using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void Jsonl_logger_redacts_arbitrary_inputs_and_keeps_a_safe_correlation_id()
    {
        var directory = TempDirectory();
        try
        {
            using var logger = new JsonlAppLogger(directory);
            using (logger.BeginScope(AppLogOperation.GroupSynchronization, "api-key-secret-should-not-log"))
                logger.Write(AppLogEvent.GroupFetchFailed, new(JsonType: System.Text.Json.JsonValueKind.String), new InvalidOperationException("body=secret-jid@g.us apikey=secret"));
            var text = ReadLogs(directory);
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("GroupFetchFailed", text);
            Assert.Contains("String", text);
            Assert.Matches("\\\"correlationId\\\":\\\"[0-9a-f]{32}\\\"", text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Jsonl_logger_rotates_retains_and_handles_concurrent_and_io_failures()
    {
        var directory = TempDirectory();
        try
        {
            var old = Path.Combine(directory, "whatsapptasks-old.jsonl"); File.WriteAllText(old, "old\n"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
            using (var logger = new JsonlAppLogger(directory, maxFileBytes: 80, retentionDays: 1, maxFiles: 3))
            {
                Parallel.For(0, 100, _ => logger.Write(AppLogEvent.SchedulerFailed));
            }
            var files = Directory.GetFiles(directory, "whatsapptasks-*.jsonl");
            Assert.True(files.Length <= 3); Assert.DoesNotContain(old, files);
            Assert.All(files, file => Assert.All(File.ReadAllLines(file), line => { using var document = System.Text.Json.JsonDocument.Parse(line); Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind); }));
            var maximumLineBytes = files.SelectMany(File.ReadAllLines).Max(line => Encoding.UTF8.GetByteCount(line) + 1);
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 80 + maximumLineBytes, $"{Path.GetFileName(file)} exceeded one-entry rotation overshoot."));

            var invalidDirectory = Path.Combine(directory, "not-a-directory"); File.WriteAllText(invalidDirectory, "x");
            using var failing = new JsonlAppLogger(invalidDirectory);
            var error = Record.Exception(() => failing.Write(AppLogEvent.LoggerFailed, exception: new IOException("api-key-secret")));
            Assert.Null(error);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Jsonl_logger_records_sqlite_codes_without_exception_text()
    {
        var directory = TempDirectory();
        try
        {
            using var logger = new JsonlAppLogger(directory);
            using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT secret_sqlite_failure";
            var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            logger.Write(AppLogEvent.GroupReplaceFailed, exception: exception);
            var text = ReadLogs(directory);
            Assert.Contains("SqliteException", text); Assert.Contains("\"sqliteErrorCode\":1", text);
            Assert.DoesNotContain("secret_sqlite_failure", text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Group_validation_log_has_metadata_without_group_payload()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-logging-{Guid.NewGuid():N}.db"); var directory = TempDirectory();
        try
        {
            await using var repository = new SqliteTaskRepository(database);
            await repository.SaveSettingsAsync(new AppSettings("http://example.test", "instance", SettingsProtection.Protect("api-key-secret"), "", "model"));
            using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"secret-group-jid@g.us\",\"subject\":\"Secret Group\",\"participants\":[\"person-secret\"]}]", Encoding.UTF8, "application/json") }));
            using var logger = new JsonlAppLogger(directory);
            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();
            Assert.True(result.Success); Assert.Empty(result.Groups); Assert.Equal(1, result.SkippedCount);
            var text = ReadLogs(directory);
            Assert.Contains("GroupSkipped", text); Assert.Contains("\"field\":\"Id\"", text); Assert.Contains("\"jidValid\":false", text); Assert.Contains("\"skipReason\":\"InvalidJid\"", text);
            Assert.DoesNotContain("secret-group-jid", text); Assert.DoesNotContain("Secret Group", text); Assert.DoesNotContain("person-secret", text); Assert.DoesNotContain("api-key-secret", text);
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in new[] { database, database + "-wal", database + "-shm" }) if (File.Exists(file)) File.Delete(file); DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Scheduler_failures_use_a_fresh_scheduler_scope()
    {
        var logger = new CapturingLogger();
        using var startup = logger.BeginScope(AppLogOperation.Startup, Guid.NewGuid().ToString("N"));
        await using var scheduler = new BackgroundTaskScheduler(new ThrowingTasksRepository(), new MessageExecutor(new ThrowingTasksRepository(), new NeverEvolution(), new NeverAnthropic(), new MessageSplitter(), new NoDelay()), logger: logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.NotifyResumeAsync());
        var failure = Assert.Single(logger.Events, x => x.Event == AppLogEvent.SchedulerFailed);
        Assert.Equal(AppLogOperation.Scheduler, failure.Operation);
        Assert.NotEqual(startup.CorrelationId, failure.CorrelationId);
    }

    private static string TempDirectory() { var path = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-logs-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return path; }
    private static string ReadLogs(string directory) => string.Concat(Directory.GetFiles(directory, "*.jsonl").Select(File.ReadAllText));
    private static void DeleteDirectory(string directory) { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    private sealed class NoDelay : IAsyncDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class NeverEvolution : IEvolutionClient { public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) => Task.FromResult(new SendResult(true, "")); }
    private sealed class NeverAnthropic : IAnthropicClient { public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(new GeneratedMessageResult(true, "", "")); }
    private sealed class ThrowingTasksRepository : ITaskRepository
    {
        public Task<IReadOnlyList<ScheduledTask>> GetTasksAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("repository secret");
        public Task<ScheduledTask?> GetTaskAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledTask?>(null);
        public Task<long> CreateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task UpdateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteTaskAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(AppSettings.Empty);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionRecord>> GetHistorySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExecutionRecord>>([]);
        public Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task CompleteExecutionAsync(ExecutionRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PurgeHistoryOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class CapturingLogger : IAppLogger
    {
        private AppLogOperation _operation = AppLogOperation.Startup; private string _correlation = Guid.NewGuid().ToString("N");
        public List<(AppLogOperation Operation, AppLogEvent Event, string CorrelationId)> Events { get; } = [];
        public IAppLogScope BeginScope(AppLogOperation operation, string? correlationId = null) { var prior = (_operation, _correlation); _operation = operation; _correlation = correlationId ?? Guid.NewGuid().ToString("N"); return new Scope(_correlation, () => { _operation = prior._operation; _correlation = prior._correlation; }); }
        public void Write(AppLogEvent eventType, LogMetadata? metadata = null, Exception? exception = null) => Events.Add((_operation, eventType, _correlation));
        public void Dispose() { }
        private sealed class Scope(string correlationId, Action restore) : IAppLogScope { private Action? _restore = restore; public string CorrelationId { get; } = correlationId; public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke(); }
    }
}
