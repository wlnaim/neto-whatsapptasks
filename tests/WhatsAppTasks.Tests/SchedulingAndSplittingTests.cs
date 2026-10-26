using WhatsAppTasks.Core;
using Xunit;
using Microsoft.Data.Sqlite;

namespace WhatsAppTasks.Tests;

public sealed class SchedulingAndSplittingTests
{
    private static readonly TimeOnly TenThirty = new(10, 30);

    [Fact]
    public void OneTime_and_future_date_only_return_a_future_occurrence()
    {
        var after = new DateTime(2026, 1, 10, 9, 0, 0);
        var oneTime = Schedule(ScheduleKind.OneTime, new DateOnly(2026, 1, 10));
        var future = Schedule(ScheduleKind.FutureDate, new DateOnly(2026, 1, 11));

        Assert.Equal(new DateTime(2026, 1, 10, 10, 30, 0), ScheduleCalculator.GetNextLocal(oneTime, after));
        Assert.Equal(new DateTime(2026, 1, 11, 10, 30, 0), ScheduleCalculator.GetNextLocal(future, after));
        Assert.Null(ScheduleCalculator.GetNextLocal(oneTime, new DateTime(2026, 1, 10, 10, 30, 0)));
    }

    [Theory]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.EveryDay)]
    public void Daily_labels_schedule_the_next_local_day_when_todays_time_passed(ScheduleKind kind)
    {
        var next = ScheduleCalculator.GetNextLocal(Schedule(kind), new DateTime(2026, 1, 10, 11, 0, 0));
        Assert.Equal(new DateTime(2026, 1, 11, 10, 30, 0), next);
    }

    [Fact]
    public void Weekly_on_selected_days_uses_the_next_selected_day()
    {
        var schedule = Schedule(ScheduleKind.WeeklyOnDays, days: new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday });
        var next = ScheduleCalculator.GetNextLocal(schedule, new DateTime(2026, 1, 10, 11, 0, 0)); // Saturday
        Assert.Equal(new DateTime(2026, 1, 12, 10, 30, 0), next);
    }

    [Fact]
    public void Monthly_uses_last_day_when_anchor_day_does_not_exist()
    {
        var schedule = Schedule(ScheduleKind.Monthly, new DateOnly(2026, 1, 31));
        var next = ScheduleCalculator.GetNextLocal(schedule, new DateTime(2026, 2, 1, 0, 0, 0));
        Assert.Equal(new DateTime(2026, 2, 28, 10, 30, 0), next);
    }

    [Fact]
    public void Monthly_does_not_run_before_its_future_anchor_date()
    {
        var schedule = Schedule(ScheduleKind.Monthly, new DateOnly(2026, 4, 30));
        var next = ScheduleCalculator.GetNextLocal(schedule, new DateTime(2026, 1, 10, 11, 0, 0));
        Assert.Equal(new DateTime(2026, 4, 30, 10, 30, 0), next);
    }

    [Fact]
    public void Monthly_supports_future_anchors_beyond_twenty_five_months()
    {
        var schedule = Schedule(ScheduleKind.Monthly, new DateOnly(2028, 6, 30));
        var next = ScheduleCalculator.GetNextLocal(schedule, new DateTime(2026, 1, 10, 11, 0, 0));
        Assert.Equal(new DateTime(2028, 6, 30, 10, 30, 0), next);
    }

    [Fact]
    public void Alternating_and_every_n_weeks_use_the_anchor_interval()
    {
        var anchor = new DateOnly(2026, 1, 1);
        var after = new DateTime(2026, 1, 11, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0), ScheduleCalculator.GetNextLocal(Schedule(ScheduleKind.AlternatingWeeks, anchor), after));
        Assert.Equal(new DateTime(2026, 1, 22, 10, 30, 0), ScheduleCalculator.GetNextLocal(Schedule(ScheduleKind.EveryNWeeks, anchor, interval: 3), after));
    }

    [Fact]
    public void Recurrence_validation_requires_weekdays_and_positive_every_n_interval()
    {
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(Schedule(ScheduleKind.WeeklyOnDays)));
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(Schedule(ScheduleKind.EveryNWeeks, interval: 0)));
    }

    [Fact]
    public void Ambiguous_dst_wall_time_uses_the_first_utc_occurrence()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var localFold = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);

        Assert.True(eastern.IsAmbiguousTime(localFold));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), ScheduleCalculator.LocalToUtc(localFold, eastern));
    }

    [Fact]
    public void Next_utc_after_the_second_dst_fold_occurrence_skips_to_the_next_recurrence()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var schedule = Schedule(ScheduleKind.Daily, new DateOnly(2026, 11, 1)) with { LocalTime = new TimeOnly(1, 30) };
        var afterSecondFold = new DateTimeOffset(2026, 11, 1, 6, 15, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.GetNextUtc(schedule, afterSecondFold, eastern);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_utc_advances_a_spring_dst_gap_and_preserves_normal_daily_recurrence()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var gapSchedule = Schedule(ScheduleKind.Daily, new DateOnly(2026, 3, 8)) with { LocalTime = new TimeOnly(2, 30) };
        var normalSchedule = Schedule(ScheduleKind.Daily, new DateOnly(2026, 3, 9)) with { LocalTime = new TimeOnly(9, 30) };

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), ScheduleCalculator.GetNextUtc(gapSchedule, new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), eastern));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 30, 0, TimeSpan.Zero), ScheduleCalculator.GetNextUtc(normalSchedule, new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), eastern));
    }

    [Fact]
    public void Splitter_reconstructs_unicode_text_and_never_exceeds_300_utf16_code_units()
    {
        var original = new string('a', 299) + "😀" + new string('b', 301);
        var parts = new MessageSplitter().Split(original);

        Assert.All(parts, part => Assert.InRange(part.Length, 1, 300));
        Assert.Equal(original, string.Concat(parts));
        Assert.DoesNotContain(parts, part => char.IsHighSurrogate(part[^1]));
    }

    [Fact]
    public async Task Sqlite_repository_validates_recipients_persists_tasks_and_claims_each_due_once()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteTaskRepository(database);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var invalid = TaskRecord(country: "52+", phone: "123", now);
                await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(invalid));

                var taskId = await repository.CreateTaskAsync(TaskRecord("52", "1234567890", now));
                var persisted = await repository.GetTaskAsync(taskId);
                Assert.NotNull(persisted);
                Assert.Equal("521234567890", persisted.CountryCode + persisted.PhoneNumber);

                var due = new DueOccurrence(taskId, now.AddMinutes(1));
                var claimId = await repository.ClaimExecutionAsync(due, persisted);
                Assert.True(claimId > 0);
                Assert.Equal(0, await repository.ClaimExecutionAsync(due, persisted));
                var claimed = Assert.Single(await repository.GetHistorySinceAsync(now.AddHours(-1)));
                Assert.Equal(ExecutionStatus.Running, claimed.Status);
                Assert.Null(claimed.FinishedUtc);

                await repository.CompleteExecutionAsync(new ExecutionRecord(claimId, taskId, persisted.Name, now, now,
                    ExecutionStatus.Succeeded, MessageKind.Fixed, "521234567890", "mensaje completo", 1, 1, ""));
                var history = await repository.GetHistorySinceAsync(now.AddHours(-1));
                var completed = Assert.Single(history);
                Assert.Equal("mensaje completo", completed.Message);
                Assert.Equal(ExecutionStatus.Succeeded, completed.Status);

                await repository.PurgeHistoryOlderThanAsync(now.AddMinutes(2));
                Assert.Empty(await repository.GetHistorySinceAsync(now.AddHours(-1)));
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Executor_sends_300_character_parts_in_order_with_five_second_delays_and_records_partial_failure()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteTaskRepository(database);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var task = TaskRecord("52", "1234567890", now) with { FixedMessage = new string('x', 301) };
                var id = await repository.CreateTaskAsync(task);
                task = (await repository.GetTaskAsync(id))!;
                var evolution = new RecordingEvolutionClient(failOnCall: 2);
                var delay = new RecordingDelay();
                var executor = new MessageExecutor(repository, evolution, new StubAnthropic(), new MessageSplitter(), delay);

                var result = await executor.ExecuteAsync(task, now.AddMinutes(1));

                Assert.Equal(ExecutionStatus.Partial, result.Status);
                Assert.Equal(1, result.SentPartCount);
                Assert.Equal(2, result.TotalPartCount);
                Assert.Equal(new[] { 300, 1 }, evolution.Sent.Select(x => x.Length));
                Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, delay.Delays);
                Assert.All(evolution.Recipients, recipient => Assert.Equal("521234567890", recipient));
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Executor_cancellation_after_a_sent_part_persists_cancelled_progress_without_sending_more()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteTaskRepository(database);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var task = TaskRecord("52", "1234567890", now) with { FixedMessage = new string('x', 301) };
                var id = await repository.CreateTaskAsync(task);
                task = (await repository.GetTaskAsync(id))!;
                using var cancellation = new CancellationTokenSource();
                var evolution = new RecordingEvolutionClient();
                var executor = new MessageExecutor(repository, evolution, new StubAnthropic(), new MessageSplitter(), new CancellingDelay(cancellation));

                var result = await executor.ExecuteAsync(task, now.AddMinutes(1), cancellation.Token);

                Assert.Equal(ExecutionStatus.Cancelled, result.Status);
                Assert.Equal(1, result.SentPartCount);
                Assert.Single(evolution.Sent);
                var stored = Assert.Single(await repository.GetHistorySinceAsync(now.AddHours(-1)));
                Assert.Equal(ExecutionStatus.Cancelled, stored.Status);
                Assert.Equal(1, stored.SentPartCount);
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Executor_stops_remaining_parts_when_the_task_is_edited_while_waiting()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteTaskRepository(database);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var task = TaskRecord("52", "1234567890", now) with { FixedMessage = new string('x', 301) };
                var id = await repository.CreateTaskAsync(task);
                task = (await repository.GetTaskAsync(id))!;
                var evolution = new RecordingEvolutionClient();
                var edited = task with { Name = "Actualizada", UpdatedUtc = task.UpdatedUtc.AddSeconds(1) };
                var executor = new MessageExecutor(repository, evolution, new StubAnthropic(), new MessageSplitter(), new MutatingDelay(() => repository.UpdateTaskAsync(edited)));

                var result = await executor.ExecuteAsync(task, now.AddMinutes(1));

                Assert.Equal(ExecutionStatus.Partial, result.Status);
                Assert.Equal(1, result.SentPartCount);
                Assert.Single(evolution.Sent);
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Anthropic_client_uses_messages_contract_concatenates_text_blocks_and_rejects_truncation()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        var keyFile = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.key");
        try
        {
            await File.WriteAllTextAsync(keyFile, "test-key");
            var repository = new SqliteTaskRepository(database);
            try
            {
                await repository.SaveSettingsAsync(new AppSettings("http://localhost:8080", "instance", "", keyFile, "model-test"));
                var completeHandler = new CapturingHandler("{\"content\":[{\"type\":\"text\",\"text\":\"Hola \"},{\"type\":\"tool_use\"},{\"type\":\"text\",\"text\":\"mundo\"}],\"stop_reason\":\"end_turn\"}");
                using var completeHttp = new HttpClient(completeHandler);
                var complete = await new AnthropicClient(completeHttp, repository).GenerateAsync("redacta algo");
                Assert.True(complete.Success);
                Assert.Equal("Hola mundo", complete.Text);
                Assert.Equal(HttpMethod.Post, completeHandler.Method);
                Assert.Equal("https://api.anthropic.com/v1/messages", completeHandler.Url);
                Assert.Equal("2023-06-01", completeHandler.AnthropicVersion);
                Assert.Equal("test-key", completeHandler.ApiKey);
                Assert.Contains("\"model\":\"model-test\"", completeHandler.Body);
                Assert.Contains("\"max_tokens\":", completeHandler.Body);

                var truncatedHandler = new CapturingHandler("{\"content\":[{\"type\":\"text\",\"text\":\"incompleto\"}],\"stop_reason\":\"max_tokens\"}");
                using var truncatedHttp = new HttpClient(truncatedHandler);
                var truncated = await new AnthropicClient(truncatedHttp, repository).GenerateAsync("redacta algo");
                Assert.False(truncated.Success);
                Assert.Empty(truncated.Text);
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm", keyFile })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Evolution_client_posts_the_documented_path_header_and_payload_without_a_real_send()
    {
        var database = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteTaskRepository(database);
            try
            {
                await repository.SaveSettingsAsync(new AppSettings("http://example.test:8080/", "WhatsApp Service", SettingsProtection.Protect("test-key"), "", "model"));
                var handler = new CapturingHandler("{}", System.Net.HttpStatusCode.Accepted);
                using var http = new HttpClient(handler);

                var result = await new EvolutionClient(http, repository).SendTextAsync("521234567890", "hola");

                Assert.True(result.Success);
                Assert.Equal(HttpMethod.Post, handler.Method);
                Assert.Equal("http://example.test:8080/message/sendText/WhatsApp%20Service", handler.Url);
                Assert.Equal("test-key", handler.ApiKey);
                Assert.Contains("\"number\":\"521234567890\"", handler.Body);
                Assert.Contains("\"text\":\"hola\"", handler.Body);
            }
            finally { await repository.DisposeAsync(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Scheduler_skips_missed_work_after_a_resume_gap_and_purges_during_steady_operation()
    {
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime;
        var scheduledLocal = local.AddMinutes(2);
        var task = TaskRecord("52", "1234567890", now) with
        {
            Schedule = new ScheduleDefinition(ScheduleKind.Daily, TimeOnly.FromDateTime(scheduledLocal), DateOnly.FromDateTime(scheduledLocal), new HashSet<DayOfWeek>(), 1)
        };
        var repository = new SchedulerRepository(task);
        var evolution = new RecordingEvolutionClient();
        await using var scheduler = new BackgroundTaskScheduler(repository, evolution, new StubAnthropic(), new MessageSplitter(), new RecordingDelay());

        await scheduler.RunOnceAsync(now);
        await scheduler.RunOnceAsync(now.AddSeconds(30));
        Assert.True(repository.PurgeThresholds.Count >= 2);

        await scheduler.RunOnceAsync(now.AddMinutes(4));
        await Task.Delay(40);
        Assert.Empty(evolution.Sent);
    }

    [Fact]
    public async Task Scheduler_stop_completes_without_propagating_its_own_lifetime_cancellation()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new SchedulerRepository(TaskRecord("52", "1234567890", now));
        await using var scheduler = new BackgroundTaskScheduler(repository, new RecordingEvolutionClient(), new StubAnthropic(), new MessageSplitter(), new RecordingDelay());

        await scheduler.StartAsync();
        await repository.TasksRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();
    }

    private static ScheduleDefinition Schedule(
        ScheduleKind kind,
        DateOnly? anchor = null,
        IReadOnlySet<DayOfWeek>? days = null,
        int interval = 1) => new(kind, TenThirty, anchor ?? new DateOnly(2026, 1, 1), days ?? new HashSet<DayOfWeek>(), interval);

    private static ScheduledTask TaskRecord(string country, string phone, DateTimeOffset now) => new(
        0, "Prueba", true, country, phone, MessageKind.Fixed, "mensaje", "",
        Schedule(ScheduleKind.Daily), now, now);

    private sealed class RecordingEvolutionClient(int failOnCall = -1) : IEvolutionClient
    {
        public List<string> Sent { get; } = [];
        public List<string> Recipients { get; } = [];
        public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default)
        {
            Recipients.Add(recipient); Sent.Add(text);
            return Task.FromResult(Sent.Count == failOnCall ? new SendResult(false, "network failure") : new SendResult(true, ""));
        }
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { Delays.Add(delay); return Task.CompletedTask; }
    }

    private sealed class CancellingDelay(CancellationTokenSource cancellation) : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }
    }

    private sealed class MutatingDelay(Func<Task> mutate) : IAsyncDelay
    {
        private bool _mutated;
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (!_mutated) { _mutated = true; await mutate(); }
        }
    }

    private sealed class StubAnthropic : IAnthropicClient
    {
        public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(new GeneratedMessageResult(true, "generado", ""));
    }

    private sealed class CapturingHandler(string responseJson, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? ApiKey { get; private set; }
        public string? AnthropicVersion { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method; Url = request.RequestUri?.AbsoluteUri;
            ApiKey = GetHeader(request, "x-api-key") ?? GetHeader(request, "Apikey");
            AnthropicVersion = GetHeader(request, "anthropic-version");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json") };
        }

        private static string? GetHeader(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }

    private sealed class SchedulerRepository(ScheduledTask task) : ITaskRepository
    {
        private readonly ScheduledTask _task = task with { Id = task.Id == 0 ? 7 : task.Id };
        public TaskCompletionSource TasksRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<DateTimeOffset> PurgeThresholds { get; } = [];
        public Task<IReadOnlyList<ScheduledTask>> GetTasksAsync(CancellationToken cancellationToken = default) { TasksRead.TrySetResult(); return Task.FromResult<IReadOnlyList<ScheduledTask>>([_task]); }
        public Task<ScheduledTask?> GetTaskAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledTask?>(_task.Id == id ? _task : null);
        public Task<long> CreateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(AppSettings.Empty);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionRecord>> GetHistorySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExecutionRecord>>([]);
        public Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task CompleteExecutionAsync(ExecutionRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PurgeHistoryOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default) { PurgeThresholds.Add(thresholdUtc); return Task.CompletedTask; }
    }
}
