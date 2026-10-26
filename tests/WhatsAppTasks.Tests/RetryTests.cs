using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class RetryTests
{
    [Fact]
    public async Task Retry_claim_uses_original_snapshot_and_only_sends_the_pending_part()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 1, new string('x', 301));
            await repository.DeleteTaskAsync(root.TaskId); // Retry is historical and must not consult the Task.
            var plan = await repository.ClaimManualRetryAsync(root.Id);
            Assert.True(plan.Accepted); Assert.Equal(1, plan.FirstPendingPart); Assert.Equal(root.Id, plan.RetryExecution!.RetryRootExecutionId); Assert.Equal(root.Id, plan.RetryExecution.RetryOfExecutionId);
            var client = new RecordingClient(); var executor = new MessageExecutor(repository, client, new NeverAnthropic(), new MessageSplitter(), new RecordingDelay());
            var completed = await executor.ExecuteManualRetryAsync(plan);
            Assert.Equal(ExecutionStatus.Succeeded, completed.Status); Assert.Equal(2, completed.SentPartCount); Assert.Equal(new[] { 1 }, client.Lengths);
            Assert.False((await repository.ClaimManualRetryAsync(root.Id)).Accepted);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_chain_allows_an_ancestor_but_prevents_a_concurrent_duplicate()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 0, "first" + new string('y', 300));
            var first = await repository.ClaimManualRetryAsync(root.Id);
            Assert.True(first.Accepted);
            var competing = await repository.ClaimManualRetryAsync(root.Id);
            Assert.False(competing.Accepted);
            await repository.CompleteExecutionAsync(first.RetryExecution! with { FinishedUtc = DateTimeOffset.UtcNow, Status = ExecutionStatus.Partial, SentPartCount = 1, Error = "temporary" });
            var fromAncestor = await repository.ClaimManualRetryAsync(root.Id);
            Assert.True(fromAncestor.Accepted); Assert.Equal(1, fromAncestor.FirstPendingPart); Assert.Equal(root.Id, fromAncestor.RetryExecution!.RetryOfExecutionId);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_from_descendant_uses_root_snapshot_and_high_water_mark()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 0, new string('z', 601));
            var first = await repository.ClaimManualRetryAsync(root.Id); await repository.CompleteExecutionAsync(first.RetryExecution! with { FinishedUtc = DateTimeOffset.UtcNow, Status = ExecutionStatus.Partial, SentPartCount = 2, Error = "temporary" });
            var descendant = await repository.ClaimManualRetryAsync(first.RetryExecution!.Id);
            Assert.True(descendant.Accepted); Assert.Equal(2, descendant.FirstPendingPart); Assert.Equal(root.Id, descendant.RootExecution!.Id); Assert.Equal(root.Id, descendant.RetryExecution!.RetryRootExecutionId); Assert.Equal(first.RetryExecution.Id, descendant.RetryExecution.RetryOfExecutionId);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_uses_descendant_snapshot_when_retention_has_purged_root()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 0, new string('q', 301)); var first = await repository.ClaimManualRetryAsync(root.Id);
            await repository.CompleteExecutionAsync(first.RetryExecution! with { FinishedUtc = DateTimeOffset.UtcNow, Status = ExecutionStatus.Partial, SentPartCount = 1, Error = "temporary" });
            await using (var db = new SqliteConnection($"Data Source={path}")) { await db.OpenAsync(); await using var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM Executions WHERE Id=$id"; delete.Parameters.AddWithValue("$id", root.Id); await delete.ExecuteNonQueryAsync(); }
            var fallback = await repository.ClaimManualRetryAsync(first.RetryExecution.Id);
            Assert.True(fallback.Accepted); Assert.Equal(1, fallback.FirstPendingPart); Assert.Equal(first.RetryExecution.Message, fallback.RootExecution!.Message);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_persists_partial_progress_and_next_retry_starts_after_confirmed_part()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 0, new string('p', 601)); var plan = await repository.ClaimManualRetryAsync(root.Id);
            var delay = new CapturingDelay(); var failed = await new MessageExecutor(repository, new FailingOnSecondClient(), new NeverAnthropic(), new MessageSplitter(), delay).ExecuteManualRetryAsync(plan);
            Assert.Equal(ExecutionStatus.Partial, failed.Status); Assert.Equal(1, failed.SentPartCount); Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, delay.Delays);
            var next = await repository.ClaimManualRetryAsync(root.Id);
            Assert.True(next.Accepted); Assert.Equal(1, next.FirstPendingPart); Assert.Equal(1, next.RetryExecution!.RetryStartPartIndex);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_rejects_failed_anthropic_execution_without_saved_text()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var now = DateTimeOffset.UtcNow;
            var task = new ScheduledTask(0, "IA", true, "52", "9831735992", MessageKind.AnthropicPrompt, "", "redacta", new ScheduleDefinition(ScheduleKind.Daily, TimeOnly.FromDateTime(now.LocalDateTime), DateOnly.FromDateTime(now.LocalDateTime), new HashSet<DayOfWeek>(), 1), now, now);
            var taskId = await repository.CreateTaskAsync(task); var claim = await repository.ClaimExecutionAsync(new(taskId, now.AddMinutes(1)), (await repository.GetTaskAsync(taskId))!);
            await repository.CompleteExecutionAsync(new ExecutionRecord(claim, taskId, "IA", now, now, ExecutionStatus.Failed, MessageKind.AnthropicPrompt, "529831735992", "", 0, 0, "generation failed"));
            Assert.False((await repository.ClaimManualRetryAsync(claim)).Accepted);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Retry_cancellation_after_a_confirmed_part_releases_chain_at_next_pending_part()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(repository, 0, new string('c', 601)); var plan = await repository.ClaimManualRetryAsync(root.Id);
            using var cancellation = new CancellationTokenSource(); var cancelled = await new MessageExecutor(repository, new RecordingClient(), new NeverAnthropic(), new MessageSplitter(), new CancellingDelay(cancellation)).ExecuteManualRetryAsync(plan, cancellation.Token);
            Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status); Assert.Equal(1, cancelled.SentPartCount);
            Assert.Equal(1, (await repository.ClaimManualRetryAsync(root.Id)).FirstPendingPart);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Scheduler_caller_cancellation_after_claim_closes_retry_without_sending()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var inner = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(inner, 0, new string('k', 301));
            var gated = new GatedRepository(inner); var client = new RecordingClient(); await using var scheduler = new BackgroundTaskScheduler(gated, client, new NeverAnthropic(), new MessageSplitter(), new RecordingDelay());
            await scheduler.StartAsync(); using var cancellation = new CancellationTokenSource(); var retry = scheduler.RetryExecutionAsync(root.Id, cancellation.Token);
            await gated.ClaimEntered.Task; cancellation.Cancel(); gated.ReleaseClaim(); var result = await retry;
            Assert.False(result.Accepted); Assert.Empty(client.Lengths);
            var history = await inner.GetHistorySinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1)); Assert.Contains(history, x => x.RetryOfExecutionId == root.Id && x.Status == ExecutionStatus.Cancelled);
            await scheduler.StopAsync();
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Scheduler_stop_waits_for_gated_claim_then_cancels_tracked_retry_and_rejects_later_clicks()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var inner = new SqliteTaskRepository(path); var root = await CreatePartialRootAsync(inner, 0, new string('s', 301));
            var gated = new GatedRepository(inner); var client = new CancellationAwareClient(); await using var scheduler = new BackgroundTaskScheduler(gated, client, new NeverAnthropic(), new MessageSplitter(), new RecordingDelay());
            await scheduler.StartAsync(); var retry = scheduler.RetryExecutionAsync(root.Id); await gated.ClaimEntered.Task;
            var stop = scheduler.StopAsync(); Assert.False(stop.IsCompleted); gated.ReleaseClaim(); await retry; await stop;
            var closed = await inner.GetHistorySinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1)); Assert.Contains(closed, x => x.RetryOfExecutionId == root.Id && x.Status == ExecutionStatus.Cancelled);
            var before = closed.Count; var rejected = await scheduler.RetryExecutionAsync(root.Id);
            Assert.False(rejected.Accepted); Assert.Equal(before, (await inner.GetHistorySinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1))).Count);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Restart_closes_running_retry_and_releases_its_chain_lock()
    {
        var path = TemporaryDatabase();
        try
        {
            ExecutionRecord root;
            await using (var firstRepository = new SqliteTaskRepository(path)) { root = await CreatePartialRootAsync(firstRepository, 0, new string('r', 301)); Assert.True((await firstRepository.ClaimManualRetryAsync(root.Id)).Accepted); }
            await using var reopened = new SqliteTaskRepository(path);
            var retry = await reopened.ClaimManualRetryAsync(root.Id);
            Assert.True(retry.Accepted);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Existing_sqlite_execution_schema_is_migrated_additively()
    {
        var path = TemporaryDatabase();
        try
        {
            await using (var db = new SqliteConnection($"Data Source={path}"))
            {
                await db.OpenAsync(); await using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE Executions (Id INTEGER PRIMARY KEY AUTOINCREMENT,TaskId INTEGER NOT NULL,DueAtUtc TEXT NOT NULL,TaskName TEXT NOT NULL,StartedUtc TEXT NOT NULL,FinishedUtc TEXT NULL,Status INTEGER NOT NULL,MessageKind INTEGER NOT NULL,Recipient TEXT NOT NULL,Message TEXT NOT NULL,SentPartCount INTEGER NOT NULL,TotalPartCount INTEGER NOT NULL,Error TEXT NOT NULL,UNIQUE(TaskId,DueAtUtc)); INSERT INTO Executions(TaskId,DueAtUtc,TaskName,StartedUtc,FinishedUtc,Status,MessageKind,Recipient,Message,SentPartCount,TotalPartCount,Error) VALUES(1,'2026-10-24T00:00:00.0000000+00:00','legado','2026-10-24T00:00:00.0000000+00:00',NULL,0,0,'529831735992','legado',0,1,'');";
                await command.ExecuteNonQueryAsync();
            }
            await using (var repository = new SqliteTaskRepository(path)) { Assert.Single(await repository.GetHistorySinceAsync(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero))); }
            await using var verified = new SqliteConnection($"Data Source={path}"); await verified.OpenAsync(); await using var columns = verified.CreateCommand(); columns.CommandText = "PRAGMA table_info(Executions)"; await using var reader = await columns.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(1));
            Assert.Contains("RetryOfExecutionId", names); Assert.Contains("RetryRootExecutionId", names); Assert.Contains("RetryStartPartIndex", names);
        }
        finally { DeleteDatabase(path); }
    }

    private static async Task<ExecutionRecord> CreatePartialRootAsync(SqliteTaskRepository repository, int sent, string message)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new ScheduledTask(0, "Histórica", true, "52", "9831735992", MessageKind.Fixed, message, "", new ScheduleDefinition(ScheduleKind.Daily, TimeOnly.FromDateTime(now.LocalDateTime), DateOnly.FromDateTime(now.LocalDateTime), new HashSet<DayOfWeek>(), 1), now, now);
        var taskId = await repository.CreateTaskAsync(task); var saved = (await repository.GetTaskAsync(taskId))!; var claim = await repository.ClaimExecutionAsync(new(taskId, now.AddMinutes(1)), saved);
        var total = new MessageSplitter().Split(message).Count;
        var root = new ExecutionRecord(claim, taskId, saved.Name, now, DateTimeOffset.UtcNow, sent == 0 ? ExecutionStatus.Failed : ExecutionStatus.Partial, MessageKind.Fixed, "529831735992", message, sent, total, "failure");
        await repository.CompleteExecutionAsync(root); return root;
    }

    private static string TemporaryDatabase() => Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-Retry-{Guid.NewGuid():N}.db");
    private static void DeleteDatabase(string path) { SqliteConnection.ClearAllPools(); foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate); }
    private sealed class RecordingClient : IEvolutionClient { public List<int> Lengths { get; } = []; public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) { Lengths.Add(text.Length); return Task.FromResult(new SendResult(true, "")); } }
    private sealed class NeverAnthropic : IAnthropicClient { public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => throw new Xunit.Sdk.XunitException("Retry must not invoke Anthropic."); }
    private sealed class RecordingDelay : IAsyncDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class CapturingDelay : IAsyncDelay { public List<TimeSpan> Delays { get; } = []; public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { Delays.Add(delay); return Task.CompletedTask; } }
    private sealed class CancellingDelay(CancellationTokenSource source) : IAsyncDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { source.Cancel(); return Task.FromCanceled(cancellationToken); } }
    private sealed class FailingOnSecondClient : IEvolutionClient { private int _calls; public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) => Task.FromResult(++_calls == 2 ? new SendResult(false, "temporary") : new SendResult(true, "")); }
    private sealed class CancellationAwareClient : IEvolutionClient { public async Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return new SendResult(true, ""); } }
    private sealed class GatedRepository(ITaskRepository inner) : ITaskRepository
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseClaim() => _release.TrySetResult();
        public Task<IReadOnlyList<ScheduledTask>> GetTasksAsync(CancellationToken cancellationToken = default) => inner.GetTasksAsync(cancellationToken);
        public Task<ScheduledTask?> GetTaskAsync(long id, CancellationToken cancellationToken = default) => inner.GetTaskAsync(id, cancellationToken);
        public Task<long> CreateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => inner.CreateTaskAsync(task, cancellationToken);
        public Task UpdateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => inner.UpdateTaskAsync(task, cancellationToken);
        public Task DeleteTaskAsync(long id, CancellationToken cancellationToken = default) => inner.DeleteTaskAsync(id, cancellationToken);
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => inner.SaveSettingsAsync(settings, cancellationToken);
        public Task<IReadOnlyList<ExecutionRecord>> GetHistorySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) => inner.GetHistorySinceAsync(sinceUtc, cancellationToken);
        public Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, CancellationToken cancellationToken = default) => inner.ClaimExecutionAsync(occurrence, task, cancellationToken);
        public Task CompleteExecutionAsync(ExecutionRecord record, CancellationToken cancellationToken = default) => inner.CompleteExecutionAsync(record, cancellationToken);
        public Task PurgeHistoryOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default) => inner.PurgeHistoryOlderThanAsync(thresholdUtc, cancellationToken);
        public async Task<ManualRetryPlan> ClaimManualRetryAsync(long sourceExecutionId, CancellationToken cancellationToken = default) { ClaimEntered.TrySetResult(); await _release.Task.WaitAsync(cancellationToken); return await inner.ClaimManualRetryAsync(sourceExecutionId, cancellationToken); }
    }
}
