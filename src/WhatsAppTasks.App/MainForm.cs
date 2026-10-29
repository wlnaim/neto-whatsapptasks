using Microsoft.Win32;
using WhatsAppTasks.Core;

namespace WhatsAppTasks.App;

internal sealed class MainForm : Form
{
    private readonly ITaskRepository _repository;
    private readonly BackgroundTaskScheduler _scheduler;
    private readonly IEvolutionClient _catalogClient;
    private readonly IAppLogger _log;
    private readonly string _logsDirectory;
    private readonly Icon _windowIcon = AppIcon.Create();
    private readonly NotifyIcon _tray;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _history = CreateGrid();
    private readonly DataGridView _tasks = CreateGrid();
    private readonly BindingSource _taskSource = new();
    private readonly List<TaskRow> _taskRows = [];
    private readonly TextBox _baseUrl = new() { Width = 430 };
    private readonly TextBox _evolutionKey = new() { Width = 430, UseSystemPasswordChar = true };
    private readonly TextBox _instance = new() { Width = 430 };
    private readonly TextBox _anthropicPath = new() { Width = 430 };
    private readonly TextBox _anthropicModel = new() { Width = 430 };
    private readonly NumericUpDown _maximumMessageLength = new() { Minimum = 1, Maximum = int.MaxValue, Value = 300, Width = 140 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 30_000 };
    private readonly ToolStripStatusLabel _schedulerHealth = new("Programador activo");
    private readonly ContextMenuStrip _historyMenu = new();
    private ToolStripMenuItem? _retryHistoryItem;
    private ExecutionRecord? _historyContextRecord;
    private bool _historyContextFromMouse;
    private readonly HashSet<long> _retryingExecutions = [];
    private bool _exiting;
    private bool _refreshing;
    private bool _loadingGroupCatalog;
    private Button? _toggleTask;
    private Button? _loadGroupCatalog;
    private readonly CancellationTokenSource _formLifetime = new();

    public event EventHandler? ExitRequested;

    public MainForm(ITaskRepository repository, BackgroundTaskScheduler scheduler, IEvolutionClient catalogClient, IAppLogger? logger = null, string? logsDirectory = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _log = logger ?? NullAppLogger.Instance;
        _logsDirectory = logsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Logs");
        Text = "WhatsApp Tasks";
        Width = 1120; Height = 720; MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Icon = _windowIcon;
        _tray = new NotifyIcon { Icon = AppIcon.Create(), Text = "WhatsApp Tasks", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.DoubleClick += (_, _) => ShowInterface();
        FormClosing += HandleFormClosing;
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshHistoryAsync();
        BuildHistoryPage();
        BuildTasksPage();
        BuildSettingsPage();
        Controls.Add(_tabs);
        var status = new StatusStrip(); status.Items.Add(_schedulerHealth); Controls.Add(status);
        _tabs.SelectedIndex = 0;
    }

    public async Task InitializeAsync()
    {
        await ReloadAllAsync();
        _refreshTimer.Start();
    }

    public void ShowInterface()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _formLifetime.Cancel();
        _refreshTimer.Stop();
        _tray.Visible = false;
        ExitRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void BuildHistoryPage()
    {
        var page = new TabPage("Historial") { Padding = new Padding(12) };
        _history.Columns.AddRange(
        [
            TextColumn("Fecha", 145), TextColumn("Estado", 95), TextColumn("Tipo", 125),
            TextColumn("Destinatario", 145), new DataGridViewTextBoxColumn { HeaderText = "Mensaje", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        ]);
        _history.MouseDown += HandleHistoryMouseDown;
        _history.KeyDown += HandleHistoryKeyDown;
        _history.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowExecutionDetail(e.RowIndex); };
        _retryHistoryItem = new ToolStripMenuItem("Reintentar");
        _retryHistoryItem.Click += async (_, _) => await RetryHistoryExecutionAsync(_retryHistoryItem.Tag as ExecutionRecord);
        _historyMenu.Items.Add(_retryHistoryItem);
        _history.ContextMenuStrip = _historyMenu;
        _historyMenu.Opening += (_, _) =>
        {
            // Keyboard menus have no CellMouseDown; use the current row. A mouse invocation keeps its row snapshot.
            if (!_historyContextFromMouse) _historyContextRecord = _history.CurrentRow?.Tag as ExecutionRecord;
            var record = _historyContextRecord;
            // Closed may precede Click; the item owns the immutable snapshot for this invocation.
            _retryHistoryItem.Tag = record;
            _retryHistoryItem.Enabled = record is not null && CanRetry(record) && !_retryingExecutions.Contains(record.Id);
        };
        _historyMenu.Closed += (_, _) => { _historyContextRecord = null; _historyContextFromMouse = false; };
        page.Controls.Add(_history);
        _tabs.TabPages.Add(page);
    }

    private void BuildTasksPage()
    {
        var page = new TabPage("Tareas") { Padding = new Padding(12) };
        _tasks.DataSource = _taskSource;
        _taskSource.DataSource = _taskRows;
        _tasks.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _tasks.MultiSelect = false;
        _tasks.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(TaskRow.Name), HeaderText = "Tarea", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(TaskRow.Schedule), HeaderText = "Programación", Width = 210 },
            new DataGridViewCheckBoxColumn { DataPropertyName = nameof(TaskRow.Enabled), HeaderText = "Activa", Width = 58 }
        ]);
        _tasks.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ViewSelectedTaskAsync(); };
        _tasks.SelectionChanged += (_, _) => UpdateTaskToggleAction();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 0, 0, 8) };
        _toggleTask = ActionButton("Suspender", async (_, _) => await ToggleSelectedTaskAsync());
        _toggleTask.Enabled = false;
        actions.Controls.AddRange([ActionButton("Nueva tarea", async (_, _) => await CreateTaskAsync()), ActionButton("Ver detalle", async (_, _) => await ViewSelectedTaskAsync()), ActionButton("Editar", async (_, _) => await EditSelectedTaskAsync()), _toggleTask, ActionButton("Eliminar", async (_, _) => await DeleteSelectedTaskAsync()), ActionButton("Actualizar", async (_, _) => await RefreshTasksAsync())]);
        page.Controls.Add(_tasks); page.Controls.Add(actions); _tabs.TabPages.Add(page);
    }

    private void BuildSettingsPage()
    {
        var page = new TabPage("Configuración") { Padding = new Padding(12) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(panel, "URL base de Evolution", _baseUrl); AddRow(panel, "ApiKey de Evolution", _evolutionKey); AddRow(panel, "Nombre del servicio", _instance);
        var pathPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; pathPanel.Controls.Add(_anthropicPath); pathPanel.Controls.Add(ActionButton("Examinar…", (_, _) => BrowseAnthropicKey()));
        AddRow(panel, "Archivo ApiKey Anthropic", pathPanel); AddRow(panel, "Modelo Anthropic", _anthropicModel); AddRow(panel, "Máximo de caracteres por mensaje", _maximumMessageLength);
        _loadGroupCatalog = ActionButton("Cargar catálogo de grupos", async (_, _) => await LoadGroupCatalogAsync());
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange([_loadGroupCatalog, ActionButton("Guardar configuración", async (_, _) => await SaveSettingsAsync())]);
        AddRow(panel, "", buttons);
        page.Controls.Add(panel); _tabs.TabPages.Add(page);
    }

    private async Task ReloadAllAsync() { await RefreshTasksAsync(); await RefreshHistoryAsync(); await LoadSettingsAsync(); }
    private async Task RefreshTasksAsync() => await RunActionAsync(async () =>
    {
        var loaded = await _repository.GetTasksAsync();
        _taskRows.Clear();
        _taskRows.AddRange(loaded.Select(x => new TaskRow(x.Id, x.Name, ScheduleText(x.Schedule.Kind), x.IsEnabled)));
        _taskSource.ResetBindings(false);
        UpdateTaskToggleAction();
    });

    private async Task RefreshHistoryAsync()
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        try
        {
            var records = await _repository.GetHistorySinceAsync(DateTimeOffset.UtcNow.AddHours(-48));
            _history.Rows.Clear();
            foreach (var record in records.OrderByDescending(x => x.StartedUtc))
            {
                var preview = record.Message.Length > 30 ? record.Message[..30] + "…" : record.Message;
                var row = _history.Rows.Add(record.StartedUtc.ToLocalTime().ToString("g"), StatusText(record.Status), MessageKindText(record.MessageKind), RecipientText(record), preview);
                _history.Rows[row].Tag = record;
            }
            _schedulerHealth.Text = _scheduler.LastError is null ? "Programador activo" : "El programador detectó un error; revisa la configuración.";
        }
        catch (Exception ex) { ShowOperationError(ex); }
        finally { _refreshing = false; }
    }

    private async Task CreateTaskAsync()
    {
        await RunActionAsync(async () =>
        {
            var groups = await _repository.GetWhatsAppGroupsAsync();
            using var editor = new TaskEditorDialog(groups: groups);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            await _repository.CreateTaskAsync(editor.GetNewTask());
            await RefreshTasksAsync();
        });
    }

    private async Task EditSelectedTaskAsync()
    {
        var selected = GetSelectedTaskId(); if (selected is null) return;
        await RunActionAsync(async () =>
        {
            var task = await _repository.GetTaskAsync(selected.Value); if (task is null) { await RefreshTasksAsync(); return; }
            var groups = await _repository.GetWhatsAppGroupsAsync();
            using var editor = new TaskEditorDialog(task, false, groups); if (editor.ShowDialog(this) != DialogResult.OK) return;
            if (MessageBox.Show(this, "¿Seguro que deseas actualizar esta tarea programada?", "Confirmar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await _repository.UpdateTaskAsync(editor.GetUpdatedTask(task)); await RefreshTasksAsync();
        });
    }

    private async Task ViewSelectedTaskAsync()
    {
        var selected = GetSelectedTaskId(); if (selected is null) return;
        await RunActionAsync(async () => { var task = await _repository.GetTaskAsync(selected.Value); if (task is null) { await RefreshTasksAsync(); return; } var groups = await _repository.GetWhatsAppGroupsAsync(); using var detail = new TaskEditorDialog(task, true, groups); detail.ShowDialog(this); });
    }

    private async Task DeleteSelectedTaskAsync()
    {
        var selected = GetSelectedTaskId(); if (selected is null) return;
        if (MessageBox.Show(this, "¿Seguro que deseas eliminar la tarea programada? Esta acción no se puede deshacer.", "Confirmar eliminación", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunActionAsync(async () => { await _repository.DeleteTaskAsync(selected.Value); await RefreshTasksAsync(); });
    }

    private void UpdateTaskToggleAction()
    {
        if (_toggleTask is null) return;
        if (_tasks.CurrentRow?.DataBoundItem is not TaskRow task)
        {
            _toggleTask.Enabled = false;
            _toggleTask.Text = "Suspender";
            return;
        }
        _toggleTask.Enabled = true;
        _toggleTask.Text = task.Enabled ? "Suspender" : "Habilitar";
    }

    private async Task ToggleSelectedTaskAsync()
    {
        var selected = GetSelectedTaskId();
        if (selected is null) return;
        await RunActionAsync(async () =>
        {
            var task = await _repository.GetTaskAsync(selected.Value);
            if (task is null) { await RefreshTasksAsync(); return; }
            var action = task.IsEnabled ? "suspender" : "habilitar";
            if (MessageBox.Show(this, $"¿Seguro que deseas {action} esta tarea programada?", "Confirmar cambio", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await _repository.SetTaskEnabledAsync(task.Id, !task.IsEnabled);
            await RefreshTasksAsync();
        });
    }

    private async Task LoadGroupCatalogAsync()
    {
        if (_loadingGroupCatalog || _exiting || IsDisposed) return;
        using var scope = _log.BeginScope(AppLogOperation.GroupSynchronization);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _log.Write(AppLogEvent.GroupSyncStarted);
        _loadingGroupCatalog = true;
        var replacing = false;
        if (_loadGroupCatalog is not null) _loadGroupCatalog.Enabled = false;
        try
        {
            var cancellationToken = _formLifetime.Token;
            var result = await _catalogClient.GetGroupsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                _log.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue)));
                MessageBox.Show(this, GroupSyncError(scope.CorrelationId), "Catálogo de grupos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            replacing = true;
            _log.Write(AppLogEvent.GroupReplaceStarted, new(Count: result.Groups.Count));
            await _repository.ReplaceWhatsAppGroupsAsync(result.Groups, cancellationToken);
            _log.Write(AppLogEvent.GroupReplaceCompleted, new(DurationMs: (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue), Count: result.Groups.Count) { SkippedCount = result.SkippedCount });
            cancellationToken.ThrowIfCancellationRequested();
            MessageBox.Show(this, $"Catálogo de grupos actualizado.\r\nImportados: {result.Groups.Count}\r\nOmitidos: {result.SkippedCount} (sin nombre o con datos inválidos).", "Catálogo de grupos", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException error) when (_formLifetime.IsCancellationRequested)
        {
            _log.Write(replacing ? AppLogEvent.GroupReplaceFailed : AppLogEvent.GroupFetchFailed, exception: error);
        }
        catch (Exception error)
        {
            _log.Write(replacing ? AppLogEvent.GroupReplaceFailed : AppLogEvent.GroupFetchFailed,
                new(DurationMs: (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue)), error);
            if (!_exiting && !IsDisposed) MessageBox.Show(this, GroupSyncError(scope.CorrelationId), "Catálogo de grupos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _loadingGroupCatalog = false;
            if (!_exiting && !IsDisposed && _loadGroupCatalog is not null) _loadGroupCatalog.Enabled = true;
        }
    }

    private string GroupSyncError(string id) => $"No se pudo cargar el catálogo de grupos. El catálogo guardado se conservó.\r\nRegistro: {id}\r\nLogs: {_logsDirectory}";

    private async Task LoadSettingsAsync() => await RunActionAsync(async () => { var settings = await _repository.GetSettingsAsync(); _baseUrl.Text = settings.EvolutionBaseUrl; _instance.Text = settings.EvolutionInstanceName; _evolutionKey.Text = SettingsProtection.Unprotect(settings.ProtectedEvolutionApiKey); _anthropicPath.Text = settings.AnthropicApiKeyFilePath; _anthropicModel.Text = settings.AnthropicModel; _maximumMessageLength.Value = Math.Clamp(settings.MaximumMessageLength, (int)_maximumMessageLength.Minimum, (int)_maximumMessageLength.Maximum); });
    private async Task SaveSettingsAsync() => await RunActionAsync(async () => { await _repository.SaveSettingsAsync(new AppSettings(_baseUrl.Text.Trim(), _instance.Text.Trim(), SettingsProtection.Protect(_evolutionKey.Text), _anthropicPath.Text.Trim(), _anthropicModel.Text.Trim()) { MaximumMessageLength = (int)_maximumMessageLength.Value }); MessageBox.Show(this, "Configuración guardada.", "Configuración", MessageBoxButtons.OK, MessageBoxIcon.Information); });
    private void BrowseAnthropicKey() { using var dialog = new OpenFileDialog { Title = "Seleccionar archivo de ApiKey Anthropic", Filter = "Archivos de texto|*.txt|Todos los archivos|*.*" }; if (dialog.ShowDialog(this) == DialogResult.OK) _anthropicPath.Text = dialog.FileName; }

    private void ShowExecutionDetail(int rowIndex)
    {
        if (_history.Rows[rowIndex].Tag is not ExecutionRecord record) return;
        var retry = record.RetryStartPartIndex is { } start
            ? $"\r\nEste reintento: {Math.Max(0, record.SentPartCount - start)}/{Math.Max(0, record.TotalPartCount - start)}\r\nConfirmadas en total: {record.SentPartCount}/{record.TotalPartCount}"
            : string.Empty;
        var ancestry = record.RetryOfExecutionId is { } parent
            ? $"\r\nReintento de ejecución: {parent}\r\nCadena raíz: {record.RetryRootExecutionId}"
            : string.Empty;
        using var form = new DetailDialog("Detalle de ejecución", $"Tarea: {record.TaskName}\r\nInicio: {record.StartedUtc.ToLocalTime():G}\r\nFin: {record.FinishedUtc?.ToLocalTime():G}\r\nEstado: {StatusText(record.Status)}\r\nTipo: {MessageKindText(record.MessageKind)}\r\nDestinatario: {RecipientText(record)}\r\nPartes enviadas: {record.SentPartCount}/{record.TotalPartCount}{retry}{ancestry}\r\nError: {record.Error}\r\n\r\nMensaje completo:\r\n{record.Message}"); form.ShowDialog(this);
    }

    private void HandleHistoryMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var rowIndex = _history.HitTest(e.X, e.Y).RowIndex;
        _historyContextFromMouse = true;
        if (rowIndex < 0)
        {
            _historyContextRecord = null;
            _history.ClearSelection();
            return;
        }
        _history.ClearSelection();
        _history.Rows[rowIndex].Selected = true;
        _historyContextRecord = _history.Rows[rowIndex].Tag as ExecutionRecord;
    }

    private void HandleHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not Keys.F10 && e.KeyCode is not Keys.Apps) return;
        if (e.KeyCode == Keys.F10 && !e.Shift) return;
        _historyContextFromMouse = false;
        _historyContextRecord = _history.CurrentRow?.Tag as ExecutionRecord;
    }

    private async Task RetryHistoryExecutionAsync(ExecutionRecord? record)
    {
        if (_exiting || IsDisposed) return;
        if (record is null || !CanRetry(record)) return;
        if (!_retryingExecutions.Add(record.Id)) return;
        try
        {
            var recipient = RecipientText(record);
            var prompt = $"¿Deseas reintentar el envío a {recipient}? Se enviarán únicamente las partes que sigan pendientes en esta cadena y se conservará el mensaje original.";
            if (MessageBox.Show(this, prompt, "Confirmar reintento", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var result = await _scheduler.RetryExecutionAsync(record.Id);
            if (!result.Accepted)
            {
                if (!_exiting && !IsDisposed && !string.IsNullOrWhiteSpace(result.Error))
                    MessageBox.Show(this, result.Error, "No se pudo iniciar el reintento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_exiting && !IsDisposed) await RefreshHistoryAsync();
        }
        catch (Exception ex) { if (!_exiting && !IsDisposed) ShowOperationError(ex); }
        finally
        {
            _retryingExecutions.Remove(record.Id);
            _historyContextRecord = null;
        }
    }

    private static bool CanRetry(ExecutionRecord record) =>
        record.Status is ExecutionStatus.Failed or ExecutionStatus.Partial or ExecutionStatus.Cancelled
        && record.TotalPartCount > record.SentPartCount
        && !string.IsNullOrWhiteSpace(record.Message);

    private static string RecipientText(ExecutionRecord record) =>
        record.RecipientKind == RecipientKind.Group && !string.IsNullOrWhiteSpace(record.RecipientName)
            ? $"{record.RecipientName} ({record.Recipient})"
            : record.Recipient;

    private async Task RunActionAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { ShowOperationError(ex); } }
    private void ShowOperationError(Exception exception)
    {
        using var scope = _log.BeginScope(AppLogOperation.Ui);
        _log.Write(AppLogEvent.UiOperationFailed, exception: exception);
        if (!IsDisposed) MessageBox.Show(this, $"No se pudo completar la operación. Revisa los datos o la configuración y vuelve a intentarlo.\r\nRegistro: {scope.CorrelationId}\r\nLogs: {_logsDirectory}", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            _exiting = true; _formLifetime.Cancel(); _refreshTimer.Stop(); _tray.Visible = false; ExitRequested?.Invoke(this, EventArgs.Empty); return;
        }
        e.Cancel = true; Hide();
    }

    private async void HandlePowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _exiting) return;
        try { await _scheduler.NotifyResumeAsync(); }
        catch { _schedulerHealth.Text = "No se pudo reanudar el programador; revisa la configuración."; }
    }
    private ContextMenuStrip BuildTrayMenu() { var menu = new ContextMenuStrip(); menu.Items.Add("Mostrar Interfaz", null, (_, _) => ShowInterface()); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Salir", null, (_, _) => RequestExit()); return menu; }
    private long? GetSelectedTaskId() => _tasks.CurrentRow?.DataBoundItem is TaskRow row ? row.Id : null;
    internal static string ScheduleText(ScheduleKind value) => value switch { ScheduleKind.OneTime => "Una sola vez", ScheduleKind.Daily => "Diariamente", ScheduleKind.WeeklyOnDays => "Cada semana en estos días", ScheduleKind.EveryDay => "Todos los días", ScheduleKind.FutureDate => "Fecha futura", ScheduleKind.Monthly => "Cada mes", ScheduleKind.AlternatingWeeks => "Una semana sí y una no", ScheduleKind.EveryNWeeks => "Cada N semanas", _ => value.ToString() };
    internal static string StatusText(ExecutionStatus value) => value switch { ExecutionStatus.Succeeded => "Exitosa", ExecutionStatus.Failed => "Fallida", ExecutionStatus.Partial => "Parcial", ExecutionStatus.Cancelled => "Cancelada", ExecutionStatus.Skipped => "Omitida", ExecutionStatus.Running => "En curso", _ => value.ToString() };
    internal static string MessageKindText(MessageKind value) => value switch { MessageKind.Fixed => "Mensaje fijo", MessageKind.ClaudeCodeAgent => "Agente Claude Code", _ => "Prompt Anthropic" };
    private static DataGridViewTextBoxColumn TextColumn(string header, int width) => new() { HeaderText = header, Width = width };
    private static Button ActionButton(string text, EventHandler handler) { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(9, 4, 9, 4) }; button.Click += handler; return button; }
    private static DataGridView CreateGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
    private static void AddRow(TableLayoutPanel panel, string label, Control input) { var row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 6) }, 0, row); input.Anchor = AnchorStyles.Left | AnchorStyles.Right; panel.Controls.Add(input, 1, row); }
    protected override void Dispose(bool disposing) { if (disposing) { _formLifetime.Cancel(); _formLifetime.Dispose(); SystemEvents.PowerModeChanged -= HandlePowerModeChanged; _refreshTimer.Dispose(); _tray.Dispose(); _historyMenu.Dispose(); _windowIcon.Dispose(); } base.Dispose(disposing); }
    private sealed record TaskRow(long Id, string Name, string Schedule, bool Enabled);
}

internal sealed class DetailDialog : Form
{
    public DetailDialog(string title, string body) { Text = title; Icon = AppIcon.Create(); Width = 720; Height = 540; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; Controls.Add(new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = body, Font = new Font("Segoe UI", 9F) }); }
}
