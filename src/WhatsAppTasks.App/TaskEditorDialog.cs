using WhatsAppTasks.Core;

namespace WhatsAppTasks.App;

internal sealed class TaskEditorDialog : Form
{
    private static readonly string[] DayNames = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];
    private readonly TextBox _name = new() { Width = 430 };
    private readonly TextBox _country = DigitsBox();
    private readonly TextBox _phone = DigitsBox();
    private readonly RadioButton _phoneRecipient = new() { Text = "Número telefónico", Checked = true, AutoSize = true };
    private readonly RadioButton _groupRecipient = new() { Text = "Grupo de WhatsApp", AutoSize = true };
    private readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, Width = 360 };
    private readonly FlowLayoutPanel _phoneFields = new() { AutoSize = true, WrapContents = false };
    private readonly FlowLayoutPanel _groupFields = new() { AutoSize = true, WrapContents = false };
    private readonly IReadOnlyList<WhatsAppGroup> _groups;
    private readonly ComboBox _schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Width = 150 };
    private readonly DateTimePicker _time = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, ShowCheckBox = true, Width = 130 };
    private readonly CheckedListBox _days = new() { CheckOnClick = true, Height = 92, Width = 360 };
    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 520, Value = 1, Width = 120 };
    private readonly RadioButton _fixedMode = new() { Text = "Mensaje fijo", Checked = true, AutoSize = true };
    private readonly RadioButton _promptMode = new() { Text = "Redactar con Anthropic", AutoSize = true };
    private readonly RadioButton _agentMode = new() { Text = "Agente Claude Code", AutoSize = true };
    private readonly TextBox _agentDir = new() { Width = 340 };
    private readonly Button _browse = new() { Text = "Examinar...", AutoSize = true };
    private readonly TextBox _agentTools = new() { Width = 430 };
    private readonly NumericUpDown _agentTimeout = new() { Minimum = 1, Maximum = 120, Value = 10, Width = 120 };
    private readonly TextBox _fixedMessage = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 95, Width = 430 };
    private readonly TextBox _prompt = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 95, Width = 430 };
    private readonly Button _save = new() { Text = "Guardar", AutoSize = true, DialogResult = DialogResult.None };
    private readonly bool _readOnly;
    private readonly bool _isEnabled;

    public TaskEditorDialog(ScheduledTask? task = null, bool readOnly = false, IReadOnlyList<WhatsAppGroup>? groups = null)
    {
        _readOnly = readOnly;
        _isEnabled = task?.IsEnabled ?? true;
        _groups = groups ?? [];
        Text = readOnly ? "Detalle de tarea" : task is null ? "Nueva tarea" : "Editar tarea";
        Icon = AppIcon.Create();
        Width = 690; Height = 800; MinimumSize = new Size(610, 580); StartPosition = FormStartPosition.CenterParent;
        _schedule.Items.AddRange(Enum.GetValues<ScheduleKind>().Select(MainForm.ScheduleText).ToArray());
        _schedule.SelectedIndexChanged += (_, _) => UpdateScheduleFields();
        _fixedMode.CheckedChanged += (_, _) => UpdateMessageFields();
        _promptMode.CheckedChanged += (_, _) => UpdateMessageFields();
        _agentMode.CheckedChanged += (_, _) => UpdateMessageFields();
        _phoneRecipient.CheckedChanged += (_, _) => UpdateRecipientFields();
        _groupRecipient.CheckedChanged += (_, _) => UpdateRecipientFields();
        _browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = _agentDir.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) _agentDir.Text = dialog.SelectedPath; };
        _days.Items.AddRange(DayNames);
        foreach (var group in _groups.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) _group.Items.Add(new GroupChoice(group.Id, group.Name));
        BuildLayout();
        if (task is not null) LoadTask(task);
        else { _schedule.SelectedIndex = (int)ScheduleKind.OneTime; _date.Value = DateTime.Today; _time.Value = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute); _time.Checked = false; }
        UpdateScheduleFields(); UpdateMessageFields(); UpdateRecipientFields();
        if (readOnly) MakeReadOnly();
    }

    public ScheduledTask GetNewTask()
    {
        ValidateFields();
        var now = DateTimeOffset.UtcNow;
        return CreateTask(0, now, now);
    }

    public ScheduledTask GetUpdatedTask(ScheduledTask original)
    {
        ValidateFields();
        return CreateTask(original.Id, original.CreatedUtc, DateTimeOffset.UtcNow);
    }

    private void BuildLayout()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(panel, "Nombre de tarea", _name);
        var recipientType = new FlowLayoutPanel { AutoSize = true }; recipientType.Controls.AddRange([_phoneRecipient, _groupRecipient]);
        _phoneFields.FlowDirection = FlowDirection.TopDown;
        var countryField = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; countryField.Controls.AddRange([new Label { Text = "Código de país", AutoSize = true, Padding = new Padding(0, 7, 5, 0) }, _country]);
        var phoneField = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; phoneField.Controls.AddRange([new Label { Text = "Número telefónico", AutoSize = true, Padding = new Padding(0, 7, 5, 0) }, _phone]);
        _phoneFields.Controls.AddRange([countryField, phoneField]);
        _groupFields.Controls.AddRange([new Label { Text = "Grupo", AutoSize = true, Padding = new Padding(0, 7, 5, 0) }, _group]);
        var recipient = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }; recipient.Controls.AddRange([recipientType, _phoneFields, _groupFields]);
        AddRow(panel, "Destinatario", recipient); AddRow(panel, "Calendarización", _schedule);
        AddRow(panel, "Fecha/ancla", _date); AddRow(panel, "Hora de envío (obligatoria; selecciónala)", _time); AddRow(panel, "Días de la semana", _days); AddRow(panel, "Cada N semanas", _interval);
        var modes = new FlowLayoutPanel { AutoSize = true }; modes.Controls.AddRange([_fixedMode, _promptMode, _agentMode]); AddRow(panel, "Tipo de mensaje", modes); AddRow(panel, "Mensaje fijo", _fixedMessage); AddRow(panel, "Prompt individual (Anthropic o agente)", _prompt);
        var dirRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; dirRow.Controls.AddRange([_agentDir, _browse]); AddRow(panel, "Carpeta de trabajo del agente", dirRow); AddRow(panel, "Herramientas permitidas del agente", _agentTools); AddRow(panel, "Timeout del agente (minutos)", _agentTimeout);
        scroll.Controls.Add(panel);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = _readOnly ? "Cerrar" : "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        _save.Click += (_, _) => { try { ValidateFields(); DialogResult = DialogResult.OK; Close(); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Validación", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        footer.Controls.Add(cancel); if (!_readOnly) footer.Controls.Add(_save);
        Controls.Add(scroll); Controls.Add(footer); AcceptButton = _readOnly ? cancel : _save; CancelButton = cancel;
    }

    private void LoadTask(ScheduledTask task)
    {
        _name.Text = task.Name; _country.Text = task.CountryCode; _phone.Text = task.PhoneNumber;
        _phoneRecipient.Checked = task.RecipientKind == RecipientKind.Phone;
        _groupRecipient.Checked = task.RecipientKind == RecipientKind.Group;
        if (task.RecipientKind == RecipientKind.Group)
        {
            var choice = _group.Items.OfType<GroupChoice>().FirstOrDefault(x => string.Equals(x.Id, task.GroupJid, StringComparison.Ordinal));
            if (choice is null || (!string.IsNullOrWhiteSpace(task.GroupName) && !string.Equals(choice.Name, task.GroupName, StringComparison.Ordinal)))
            {
                choice = new GroupChoice(task.GroupJid, task.GroupName);
                _group.Items.Add(choice);
            }
            _group.SelectedItem = choice;
        }
        _schedule.SelectedIndex = (int)task.Schedule.Kind; _date.Value = task.Schedule.AnchorDate.ToDateTime(TimeOnly.MinValue); _time.Value = DateTime.Today.Add(task.Schedule.LocalTime.ToTimeSpan()); _time.Checked = true; _interval.Value = Math.Clamp(task.Schedule.IntervalWeeks, (int)_interval.Minimum, (int)_interval.Maximum);
        foreach (var day in task.Schedule.DaysOfWeek) _days.SetItemChecked((int)day, true);
        _fixedMode.Checked = task.MessageKind == MessageKind.Fixed; _agentMode.Checked = task.MessageKind == MessageKind.ClaudeCodeAgent; _promptMode.Checked = task.MessageKind == MessageKind.AnthropicPrompt; _fixedMessage.Text = task.FixedMessage; _prompt.Text = task.AnthropicPrompt;
        _agentDir.Text = task.AgentWorkingDirectory; _agentTools.Text = task.AgentAllowedTools; _agentTimeout.Value = Math.Clamp(task.AgentTimeoutMinutes, (int)_agentTimeout.Minimum, (int)_agentTimeout.Maximum);
    }

    private ScheduledTask CreateTask(long id, DateTimeOffset createdUtc, DateTimeOffset updatedUtc)
    {
        var kind = (ScheduleKind)_schedule.SelectedIndex;
        var days = _days.CheckedIndices.Cast<int>().Select(x => (DayOfWeek)x).ToHashSet();
        var selectedGroup = SelectedGroup();
        return new ScheduledTask(id, _name.Text.Trim(), _isEnabled, _phoneRecipient.Checked ? _country.Text : "", _phoneRecipient.Checked ? _phone.Text : "", _fixedMode.Checked ? MessageKind.Fixed : _agentMode.Checked ? MessageKind.ClaudeCodeAgent : MessageKind.AnthropicPrompt, _fixedMessage.Text, _prompt.Text, new ScheduleDefinition(kind, TimeOnly.FromDateTime(_time.Value), DateOnly.FromDateTime(_date.Value), days, (int)_interval.Value), createdUtc, updatedUtc)
        {
            AgentWorkingDirectory = _agentDir.Text.Trim(), AgentAllowedTools = _agentTools.Text.Trim(), AgentTimeoutMinutes = (int)_agentTimeout.Value,
            RecipientKind = _groupRecipient.Checked ? RecipientKind.Group : RecipientKind.Phone,
            GroupJid = _groupRecipient.Checked ? selectedGroup!.Id : "",
            GroupName = _groupRecipient.Checked ? selectedGroup!.Name : ""
        };
    }

    private void ValidateFields()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("Indica un nombre para la tarea.");
        if (_phoneRecipient.Checked && (!AsciiDigits(_country.Text) || !AsciiDigits(_phone.Text))) throw new ArgumentException("El código de país y el número telefónico son obligatorios y sólo aceptan números.");
        if (_groupRecipient.Checked && SelectedGroup() is null) throw new ArgumentException("Selecciona un grupo válido del catálogo cargado.");
        if (!_time.Checked) throw new ArgumentException("Selecciona la hora obligatoria de envío.");
        var kind = (ScheduleKind)_schedule.SelectedIndex;
        if (kind == ScheduleKind.WeeklyOnDays && _days.CheckedItems.Count == 0) throw new ArgumentException("Selecciona al menos un día de la semana.");
        if (kind == ScheduleKind.FutureDate && _date.Value.Date <= DateTime.Today) throw new ArgumentException("La fecha futura debe ser posterior a hoy.");
        if (_isEnabled && kind == ScheduleKind.OneTime && _date.Value.Date.Add(_time.Value.TimeOfDay) <= DateTime.Now) throw new ArgumentException("La fecha y hora de una tarea única activa deben ser futuras.");
        if (_fixedMode.Checked && string.IsNullOrWhiteSpace(_fixedMessage.Text)) throw new ArgumentException("Indica el mensaje fijo.");
        if (_promptMode.Checked && string.IsNullOrWhiteSpace(_prompt.Text)) throw new ArgumentException("Indica el prompt individual de Anthropic.");
        if (_agentMode.Checked && string.IsNullOrWhiteSpace(_prompt.Text)) throw new ArgumentException("Indica el prompt del agente Claude Code.");
        if (_agentMode.Checked && string.IsNullOrWhiteSpace(_agentDir.Text)) throw new ArgumentException("Indica la carpeta de trabajo del agente.");
    }

    private void UpdateScheduleFields()
    {
        var kind = (ScheduleKind)Math.Max(0, _schedule.SelectedIndex);
        _days.Enabled = kind == ScheduleKind.WeeklyOnDays;
        _interval.Enabled = kind == ScheduleKind.EveryNWeeks;
        _date.Enabled = kind is ScheduleKind.OneTime or ScheduleKind.FutureDate or ScheduleKind.Monthly or ScheduleKind.AlternatingWeeks or ScheduleKind.EveryNWeeks;
    }

    private void UpdateRecipientFields()
    {
        var phone = _phoneRecipient.Checked;
        _country.Enabled = _phone.Enabled = phone;
        _group.Enabled = !phone;
        _phoneFields.Visible = phone;
        _groupFields.Visible = !phone;
        _phoneFields.Parent?.PerformLayout();
    }

    private GroupChoice? SelectedGroup()
    {
        if (_group.SelectedItem is GroupChoice selected && string.Equals(selected.ToString(), _group.Text, StringComparison.CurrentCulture)) return selected;
        var choices = _group.Items.OfType<GroupChoice>().ToArray();
        var displayed = choices.SingleOrDefault(x => string.Equals(x.ToString(), _group.Text, StringComparison.CurrentCulture));
        if (displayed is not null) return displayed;
        var named = choices.Where(x => string.Equals(x.Name, _group.Text, StringComparison.CurrentCulture)).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    private void UpdateMessageFields() { _fixedMessage.Enabled = _fixedMode.Checked; _prompt.Enabled = _promptMode.Checked || _agentMode.Checked; _agentDir.Enabled = _browse.Enabled = _agentTools.Enabled = _agentTimeout.Enabled = _agentMode.Checked; }
    private void MakeReadOnly()
    {
        _browse.Enabled = false;
        foreach (var control in Descendants(this))
            if (control is TextBoxBase text) text.ReadOnly = true;
            else if (control is ComboBox or DateTimePicker or NumericUpDown or CheckedListBox or RadioButton or CheckBox) control.Enabled = false;
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
    private static void AddRow(TableLayoutPanel panel, string label, Control value) { var row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 10, 7) }, 0, row); value.Anchor = AnchorStyles.Left | AnchorStyles.Right; panel.Controls.Add(value, 1, row); }
    private static TextBox DigitsBox()
    {
        var box = new TextBox { Width = 250 };
        box.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9');
        box.TextChanged += (_, _) => { var digits = new string(box.Text.Where(x => x is >= '0' and <= '9').ToArray()); if (digits != box.Text) { var selection = box.SelectionStart; box.Text = digits; box.SelectionStart = Math.Min(selection, digits.Length); } };
        return box;
    }

    private static bool AsciiDigits(string value) => value.Length > 0 && value.All(x => x is >= '0' and <= '9');
    private sealed record GroupChoice(string Id, string Name) { public override string ToString() => $"{Name} ({Id})"; }
}
