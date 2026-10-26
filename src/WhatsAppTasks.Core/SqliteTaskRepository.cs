using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WhatsAppTasks.Core;

/// <summary>SQLite task/settings/history storage. Mutation and claiming share a local gate.</summary>
public sealed class SqliteTaskRepository : ITaskRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private bool _initialized;

    public SqliteTaskRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await using var db = Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,Enabled,CountryCode,PhoneNumber,MessageKind,FixedMessage,AnthropicPrompt,Schedule,CreatedUtc,UpdatedUtc,AgentWorkingDirectory,AgentAllowedTools,AgentTimeoutMinutes FROM Tasks ORDER BY Id";
        var result = new List<ScheduledTask>(); await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadTask(reader)); return result;
    }

    public async Task<ScheduledTask?> GetTaskAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await using var db = Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,Enabled,CountryCode,PhoneNumber,MessageKind,FixedMessage,AnthropicPrompt,Schedule,CreatedUtc,UpdatedUtc,AgentWorkingDirectory,AgentAllowedTools,AgentTimeoutMinutes FROM Tasks WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    public async Task<long> CreateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task); await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            await using var db = Open(); await using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO Tasks(Name,Enabled,CountryCode,PhoneNumber,MessageKind,FixedMessage,AnthropicPrompt,Schedule,CreatedUtc,UpdatedUtc,AgentWorkingDirectory,AgentAllowedTools,AgentTimeoutMinutes) VALUES($name,$enabled,$country,$phone,$kind,$fixed,$prompt,$schedule,$created,$updated,$agentDir,$agentTools,$agentTimeout); SELECT last_insert_rowid();";
            BindTask(cmd, task); return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        if (task.Id <= 0) throw new ArgumentOutOfRangeException(nameof(task)); ValidateTask(task); await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            task = task with { UpdatedUtc = DateTimeOffset.UtcNow };
            await using var db = Open(); await using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Tasks SET Name=$name,Enabled=$enabled,CountryCode=$country,PhoneNumber=$phone,MessageKind=$kind,FixedMessage=$fixed,AnthropicPrompt=$prompt,Schedule=$schedule,UpdatedUtc=$updated,AgentWorkingDirectory=$agentDir,AgentAllowedTools=$agentTools,AgentTimeoutMinutes=$agentTimeout WHERE Id=$id";
            BindTask(cmd, task); cmd.Parameters.AddWithValue("$id", task.Id);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("The task no longer exists.");
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteTaskAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try { await using var db = Open(); await using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM Tasks WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(cancellationToken); } finally { _gate.Release(); }
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await using var db = Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EvolutionBaseUrl,EvolutionInstanceName,ProtectedEvolutionApiKey,AnthropicApiKeyFilePath,AnthropicModel FROM Settings WHERE Id=1";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : AppSettings.Empty;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings); await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            await using var db = Open(); await using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings(Id,EvolutionBaseUrl,EvolutionInstanceName,ProtectedEvolutionApiKey,AnthropicApiKeyFilePath,AnthropicModel) VALUES(1,$url,$instance,$key,$path,$model) ON CONFLICT(Id) DO UPDATE SET EvolutionBaseUrl=$url,EvolutionInstanceName=$instance,ProtectedEvolutionApiKey=$key,AnthropicApiKeyFilePath=$path,AnthropicModel=$model";
            cmd.Parameters.AddWithValue("$url", settings.EvolutionBaseUrl ?? ""); cmd.Parameters.AddWithValue("$instance", settings.EvolutionInstanceName ?? ""); cmd.Parameters.AddWithValue("$key", settings.ProtectedEvolutionApiKey ?? ""); cmd.Parameters.AddWithValue("$path", settings.AnthropicApiKeyFilePath ?? ""); cmd.Parameters.AddWithValue("$model", settings.AnthropicModel ?? ""); await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ExecutionRecord>> GetHistorySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await using var db = Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,TaskId,TaskName,StartedUtc,FinishedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error,RetryOfExecutionId,RetryRootExecutionId,RetryStartPartIndex FROM Executions WHERE StartedUtc >= $since ORDER BY StartedUtc DESC"; cmd.Parameters.AddWithValue("$since", Utc(sinceUtc)); return await ReadRecordsAsync(cmd, cancellationToken);
    }

    public async Task<ExecutionRecord?> GetExecutionAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await using var db = Open(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,TaskId,TaskName,StartedUtc,FinishedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error,RetryOfExecutionId,RetryRootExecutionId,RetryStartPartIndex FROM Executions WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<ManualRetryPlan> ClaimManualRetryAsync(long sourceExecutionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            await using var db = Open(); using var tx = db.BeginTransaction();
            var source = await ReadExecutionInTransactionAsync(db, tx, sourceExecutionId, cancellationToken);
            if (source is null || source.Status is not (ExecutionStatus.Failed or ExecutionStatus.Partial or ExecutionStatus.Cancelled)) return await RollbackPlanAsync("Solo se pueden reintentar ejecuciones fallidas, parciales o canceladas.");
            var rootId = source.RetryRootExecutionId ?? source.Id;
            var root = await ReadExecutionInTransactionAsync(db, tx, rootId, cancellationToken) ?? source;
            if (string.IsNullOrEmpty(root.Message) || !Digits(root.Recipient) || root.TotalPartCount <= 0) return await RollbackPlanAsync("La ejecución no conserva contenido válido para reenviar.");
            int firstPending;
            await using (var progress = db.CreateCommand())
            {
                progress.Transaction = tx; progress.CommandText = $"SELECT COALESCE(MAX(SentPartCount),0) FROM Executions WHERE (Id=$root OR RetryRootExecutionId=$root) AND Status <> {(int)ExecutionStatus.Running}"; progress.Parameters.AddWithValue("$root", rootId);
                firstPending = Convert.ToInt32(await progress.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }
            if (firstPending < 0 || firstPending >= root.TotalPartCount) return await RollbackPlanAsync("No quedan partes pendientes para reenviar.");
            var now = DateTimeOffset.UtcNow;
            await using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Executions(TaskId,DueAtUtc,TaskName,StartedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error,RetryOfExecutionId,RetryRootExecutionId,RetryStartPartIndex) VALUES($task,$due,$name,$started,$status,$kind,$recipient,$message,$sent,$total,'',$source,$root,$start); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$task", root.TaskId); insert.Parameters.AddWithValue("$due", Utc(now)); insert.Parameters.AddWithValue("$name", root.TaskName); insert.Parameters.AddWithValue("$started", Utc(now)); insert.Parameters.AddWithValue("$status", (int)ExecutionStatus.Running); insert.Parameters.AddWithValue("$kind", (int)root.MessageKind); insert.Parameters.AddWithValue("$recipient", root.Recipient); insert.Parameters.AddWithValue("$message", root.Message); insert.Parameters.AddWithValue("$sent", firstPending); insert.Parameters.AddWithValue("$total", root.TotalPartCount); insert.Parameters.AddWithValue("$source", sourceExecutionId); insert.Parameters.AddWithValue("$root", rootId); insert.Parameters.AddWithValue("$start", firstPending);
            long id;
            try { id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); }
            catch (SqliteException) { return await RollbackPlanAsync("Ya hay un reintento en curso para esta ejecución."); }
            await tx.CommitAsync(cancellationToken);
            var retry = new ExecutionRecord(id, root.TaskId, root.TaskName, now, null, ExecutionStatus.Running, root.MessageKind, root.Recipient, root.Message, firstPending, root.TotalPartCount, "") { RetryOfExecutionId = sourceExecutionId, RetryRootExecutionId = rootId, RetryStartPartIndex = firstPending };
            return new(true, "", root, retry, firstPending);

            async Task<ManualRetryPlan> RollbackPlanAsync(string error) { await tx.RollbackAsync(cancellationToken); return new(false, error, null, null, 0); }
        }
        finally { _gate.Release(); }
    }

    public async Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            await using var db = Open(); using var tx = db.BeginTransaction();
            await using var current = db.CreateCommand(); current.Transaction = tx; current.CommandText = "SELECT UpdatedUtc FROM Tasks WHERE Id=$id AND Enabled=1"; current.Parameters.AddWithValue("$id", task.Id);
            if (await current.ExecuteScalarAsync(cancellationToken) is not string updated || updated != Utc(task.UpdatedUtc)) { await tx.RollbackAsync(cancellationToken); return 0; }
            await using var claim = db.CreateCommand(); claim.Transaction = tx;
            claim.CommandText = "INSERT OR IGNORE INTO Executions(TaskId,DueAtUtc,TaskName,StartedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error) VALUES($task,$due,$name,$started,$status,$kind,$recipient,'',0,0,''); SELECT changes();";
            claim.Parameters.AddWithValue("$task", occurrence.TaskId); claim.Parameters.AddWithValue("$due", Utc(occurrence.DueAtUtc)); claim.Parameters.AddWithValue("$name", task.Name); claim.Parameters.AddWithValue("$started", Utc(DateTimeOffset.UtcNow)); claim.Parameters.AddWithValue("$status", (int)ExecutionStatus.Running); claim.Parameters.AddWithValue("$kind", (int)task.MessageKind); claim.Parameters.AddWithValue("$recipient", task.CountryCode + task.PhoneNumber);
            if (Convert.ToInt32(await claim.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0) { await tx.RollbackAsync(cancellationToken); return 0; }
            await using var identity = db.CreateCommand(); identity.Transaction = tx; identity.CommandText = "SELECT last_insert_rowid()"; var id = Convert.ToInt64(await identity.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); await tx.CommitAsync(cancellationToken); return id;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteExecutionAsync(ExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try
        {
            await using var db = Open(); await using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Executions SET TaskName=$name,FinishedUtc=$finished,Status=$status,MessageKind=$kind,Recipient=$recipient,Message=$message,SentPartCount=$sent,TotalPartCount=$total,Error=$error WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", record.Id); cmd.Parameters.AddWithValue("$name", record.TaskName); cmd.Parameters.AddWithValue("$finished", record.FinishedUtc is null ? DBNull.Value : Utc(record.FinishedUtc.Value)); cmd.Parameters.AddWithValue("$status", (int)record.Status); cmd.Parameters.AddWithValue("$kind", (int)record.MessageKind); cmd.Parameters.AddWithValue("$recipient", record.Recipient); cmd.Parameters.AddWithValue("$message", record.Message); cmd.Parameters.AddWithValue("$sent", record.SentPartCount); cmd.Parameters.AddWithValue("$total", record.TotalPartCount); cmd.Parameters.AddWithValue("$error", record.Error ?? "");
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("The execution claim no longer exists.");
        }
        finally { _gate.Release(); }
    }

    public async Task PurgeHistoryOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken); await _gate.WaitAsync(cancellationToken); try { await using var db = Open(); await using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM Executions WHERE StartedUtc < $threshold"; cmd.Parameters.AddWithValue("$threshold", Utc(thresholdUtc)); await cmd.ExecuteNonQueryAsync(cancellationToken); } finally { _gate.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return; await _gate.WaitAsync(ct); try
        {
            if (_initialized) return; await using var db = Open(); await using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Tasks (Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL,Enabled INTEGER NOT NULL,CountryCode TEXT NOT NULL,PhoneNumber TEXT NOT NULL,MessageKind INTEGER NOT NULL,FixedMessage TEXT NOT NULL,AnthropicPrompt TEXT NOT NULL,Schedule TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,AgentWorkingDirectory TEXT NOT NULL DEFAULT '',AgentAllowedTools TEXT NOT NULL DEFAULT '',AgentTimeoutMinutes INTEGER NOT NULL DEFAULT 10); CREATE TABLE IF NOT EXISTS Settings (Id INTEGER PRIMARY KEY CHECK(Id=1),EvolutionBaseUrl TEXT NOT NULL,EvolutionInstanceName TEXT NOT NULL,ProtectedEvolutionApiKey TEXT NOT NULL,AnthropicApiKeyFilePath TEXT NOT NULL,AnthropicModel TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Executions (Id INTEGER PRIMARY KEY AUTOINCREMENT,TaskId INTEGER NOT NULL,DueAtUtc TEXT NOT NULL,TaskName TEXT NOT NULL,StartedUtc TEXT NOT NULL,FinishedUtc TEXT NULL,Status INTEGER NOT NULL,MessageKind INTEGER NOT NULL,Recipient TEXT NOT NULL,Message TEXT NOT NULL,SentPartCount INTEGER NOT NULL,TotalPartCount INTEGER NOT NULL,Error TEXT NOT NULL,RetryOfExecutionId INTEGER NULL,RetryRootExecutionId INTEGER NULL,RetryStartPartIndex INTEGER NULL,UNIQUE(TaskId,DueAtUtc)); CREATE INDEX IF NOT EXISTS IX_Executions_StartedUtc ON Executions(StartedUtc);";
            await cmd.ExecuteNonQueryAsync(ct);
            await EnsureColumnAsync(db, "Executions", "RetryOfExecutionId", "INTEGER NULL", ct); await EnsureColumnAsync(db, "Executions", "RetryRootExecutionId", "INTEGER NULL", ct); await EnsureColumnAsync(db, "Executions", "RetryStartPartIndex", "INTEGER NULL", ct);
            await EnsureColumnAsync(db, "Tasks", "AgentWorkingDirectory", "TEXT NOT NULL DEFAULT ''", ct); await EnsureColumnAsync(db, "Tasks", "AgentAllowedTools", "TEXT NOT NULL DEFAULT ''", ct); await EnsureColumnAsync(db, "Tasks", "AgentTimeoutMinutes", "INTEGER NOT NULL DEFAULT 10", ct);
            cmd.CommandText = $"CREATE UNIQUE INDEX IF NOT EXISTS IX_Executions_OneRunningRetry ON Executions(RetryRootExecutionId) WHERE RetryRootExecutionId IS NOT NULL AND Status={(int)ExecutionStatus.Running}; UPDATE Executions SET Status={(int)ExecutionStatus.Cancelled},FinishedUtc=COALESCE(FinishedUtc,StartedUtc),Error='Execution interrupted before completion.' WHERE Status={(int)ExecutionStatus.Running};";
            await cmd.ExecuteNonQueryAsync(ct); _initialized = true;
        }
        finally { _gate.Release(); }
    }

    private static async Task EnsureColumnAsync(SqliteConnection db, string table, string name, string declaration, CancellationToken ct)
    {
        await using var columns = db.CreateCommand(); columns.CommandText = $"PRAGMA table_info({table})"; await using var reader = await columns.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return;
        await using var alter = db.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {declaration}"; await alter.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private static async Task<ExecutionRecord?> ReadExecutionInTransactionAsync(SqliteConnection db, SqliteTransaction transaction, long id, CancellationToken ct)
    {
        await using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Id,TaskId,TaskName,StartedUtc,FinishedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error,RetryOfExecutionId,RetryRootExecutionId,RetryStartPartIndex FROM Executions WHERE Id=$id"; command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private void BindTask(SqliteCommand cmd, ScheduledTask task) { cmd.Parameters.AddWithValue("$name", task.Name); cmd.Parameters.AddWithValue("$enabled", task.IsEnabled ? 1 : 0); cmd.Parameters.AddWithValue("$country", task.CountryCode); cmd.Parameters.AddWithValue("$phone", task.PhoneNumber); cmd.Parameters.AddWithValue("$kind", (int)task.MessageKind); cmd.Parameters.AddWithValue("$fixed", task.FixedMessage ?? ""); cmd.Parameters.AddWithValue("$prompt", task.AnthropicPrompt ?? ""); cmd.Parameters.AddWithValue("$schedule", JsonSerializer.Serialize(ScheduleDto.From(task.Schedule), _json)); cmd.Parameters.AddWithValue("$created", Utc(task.CreatedUtc)); cmd.Parameters.AddWithValue("$updated", Utc(task.UpdatedUtc)); cmd.Parameters.AddWithValue("$agentDir", task.AgentWorkingDirectory ?? ""); cmd.Parameters.AddWithValue("$agentTools", task.AgentAllowedTools ?? ""); cmd.Parameters.AddWithValue("$agentTimeout", task.AgentTimeoutMinutes); }
    private ScheduledTask ReadTask(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3), r.GetString(4), (MessageKind)r.GetInt32(5), r.GetString(6), r.GetString(7), JsonSerializer.Deserialize<ScheduleDto>(r.GetString(8), _json)!.ToDefinition(), DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture)) { AgentWorkingDirectory = r.GetString(11), AgentAllowedTools = r.GetString(12), AgentTimeoutMinutes = r.GetInt32(13) };
    private static async Task<IReadOnlyList<ExecutionRecord>> ReadRecordsAsync(SqliteCommand cmd, CancellationToken ct) { var result = new List<ExecutionRecord>(); await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) result.Add(ReadRecord(r)); return result; }
    private static ExecutionRecord ReadRecord(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture), r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture), (ExecutionStatus)r.GetInt32(5), (MessageKind)r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetInt32(10), r.GetString(11)) { RetryOfExecutionId = r.IsDBNull(12) ? null : r.GetInt64(12), RetryRootExecutionId = r.IsDBNull(13) ? null : r.GetInt64(13), RetryStartPartIndex = r.IsDBNull(14) ? null : r.GetInt32(14) };
    private static void ValidateTask(ScheduledTask task) { ArgumentNullException.ThrowIfNull(task); if (string.IsNullOrWhiteSpace(task.Name)) throw new ArgumentException("A task name is required.", nameof(task)); if (!Digits(task.CountryCode) || !Digits(task.PhoneNumber)) throw new ArgumentException("Country code and phone number must contain ASCII digits only.", nameof(task)); ScheduleCalculator.Validate(task.Schedule); if (task.MessageKind == MessageKind.Fixed && string.IsNullOrEmpty(task.FixedMessage)) throw new ArgumentException("A fixed message is required.", nameof(task)); if (task.MessageKind == MessageKind.AnthropicPrompt && string.IsNullOrWhiteSpace(task.AnthropicPrompt)) throw new ArgumentException("An Anthropic prompt is required.", nameof(task)); if (task.MessageKind == MessageKind.ClaudeCodeAgent) { if (string.IsNullOrWhiteSpace(task.AnthropicPrompt)) throw new ArgumentException("An agent prompt is required.", nameof(task)); if (string.IsNullOrWhiteSpace(task.AgentWorkingDirectory)) throw new ArgumentException("An agent working directory is required.", nameof(task)); if (task.AgentTimeoutMinutes is < 1 or > 120) throw new ArgumentException("The agent timeout must be between 1 and 120 minutes.", nameof(task)); } }
    private static bool Digits(string? text) => !string.IsNullOrEmpty(text) && text.All(c => c is >= '0' and <= '9');
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    private sealed record ScheduleDto(ScheduleKind Kind, TimeOnly LocalTime, DateOnly AnchorDate, int[] DaysOfWeek, int IntervalWeeks) { public static ScheduleDto From(ScheduleDefinition s) => new(s.Kind, s.LocalTime, s.AnchorDate, s.DaysOfWeek.Select(x => (int)x).ToArray(), s.IntervalWeeks); public ScheduleDefinition ToDefinition() => new(Kind, LocalTime, AnchorDate, DaysOfWeek.Select(x => (DayOfWeek)x).ToHashSet(), IntervalWeeks); }
}
