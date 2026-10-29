using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class Round2CoreTests
{
    [Fact]
    public void Splitter_preserves_surrogate_pair_at_one_and_handles_int_maximum()
    {
        var splitter = new MessageSplitter();
        var emoji = "😀";
        Assert.Equal(new[] { emoji }, splitter.Split(emoji, 1));
        Assert.Equal("abc", string.Concat(splitter.Split("abc", int.MaxValue)));
    }

    [Fact]
    public async Task Toggle_only_changes_enabled_and_updated_timestamp_and_catalog_replaces_atomically()
    {
        var path = Temp();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var now = DateTimeOffset.UtcNow;
            var task = NewTask(now) with { MessageKind = MessageKind.ClaudeCodeAgent, FixedMessage = "", AnthropicPrompt = "haz", AgentWorkingDirectory = Path.GetTempPath(), AgentAllowedTools = "Read", AgentTimeoutMinutes = 12 };
            var id = await repository.CreateTaskAsync(task); var before = (await repository.GetTaskAsync(id))!;
            await repository.SetTaskEnabledAsync(id, false); var suspended = (await repository.GetTaskAsync(id))!;
            Assert.False(suspended.IsEnabled); Assert.True(suspended.UpdatedUtc >= before.UpdatedUtc); Assert.Equal(before.AgentWorkingDirectory, suspended.AgentWorkingDirectory); Assert.Equal(before.AgentAllowedTools, suspended.AgentAllowedTools); Assert.Equal(before.Schedule.Kind, suspended.Schedule.Kind); Assert.Equal(before.Schedule.LocalTime, suspended.Schedule.LocalTime); Assert.Equal(before.Schedule.AnchorDate, suspended.Schedule.AnchorDate); Assert.True(before.Schedule.DaysOfWeek.SetEquals(suspended.Schedule.DaysOfWeek)); Assert.Equal(before.Schedule.IntervalWeeks, suspended.Schedule.IntervalWeeks);
            Assert.Equal(0, await repository.ClaimExecutionAsync(new(id, now.AddMinutes(1)), suspended));
            await repository.ReplaceWhatsAppGroupsAsync([new("one@g.us", "Uno")]); Assert.Single(await repository.GetWhatsAppGroupsAsync());
            await repository.ReplaceWhatsAppGroupsAsync([]); Assert.Empty(await repository.GetWhatsAppGroupsAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceWhatsAppGroupsAsync([new("", "bad")])); Assert.Empty(await repository.GetWhatsAppGroupsAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Group_jid_and_chunk_size_are_snapshotted_for_send_and_retry()
    {
        var path = Temp();
        try
        {
            await using var repository = new SqliteTaskRepository(path); await repository.SaveSettingsAsync(AppSettings.Empty with { MaximumMessageLength = 2 }); var now = DateTimeOffset.UtcNow;
            var id = await repository.CreateTaskAsync(NewTask(now) with { CountryCode = "", PhoneNumber = "", RecipientKind = RecipientKind.Group, GroupJid = "12345-678@g.us", GroupName = "Grupo", FixedMessage = "abcdef" });
            var task = (await repository.GetTaskAsync(id))!; var sender = new FailingSender(2); var executor = new MessageExecutor(repository, sender, new NoAi(), new MessageSplitter(), new NoDelay());
            var failed = await executor.ExecuteAsync(task, now.AddMinutes(1));
            Assert.Equal(ExecutionStatus.Partial, failed.Status); Assert.Equal(2, failed.ChunkSize); Assert.Equal(RecipientKind.Group, failed.RecipientKind); Assert.Equal("12345-678@g.us", sender.Recipients[0]);
            await repository.SaveSettingsAsync(AppSettings.Empty with { MaximumMessageLength = 4 }); var plan = await repository.ClaimManualRetryAsync(failed.Id);
            var retrySender = new FailingSender(-1); var retried = await new MessageExecutor(repository, retrySender, new NoAi(), new MessageSplitter(), new NoDelay()).ExecuteManualRetryAsync(plan);
            Assert.Equal(ExecutionStatus.Succeeded, retried.Status); Assert.All(retrySender.Texts, text => Assert.InRange(text.Length, 1, 2)); Assert.All(retrySender.Recipients, x => Assert.Equal("12345-678@g.us", x));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(MessageKind.Fixed)]
    [InlineData(MessageKind.AnthropicPrompt)]
    [InlineData(MessageKind.ClaudeCodeAgent)]
    public async Task Group_recipient_is_used_unchanged_for_every_message_kind(MessageKind messageKind)
    {
        var path = Temp();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var now = DateTimeOffset.UtcNow;
            var task = NewTask(now) with
            {
                CountryCode = "", PhoneNumber = "", RecipientKind = RecipientKind.Group,
                GroupJid = "120363000000001@g.us", GroupName = "Grupo moderno", MessageKind = messageKind,
                FixedMessage = messageKind == MessageKind.Fixed ? "fijo" : "", AnthropicPrompt = messageKind == MessageKind.Fixed ? "" : "genera",
                AgentWorkingDirectory = messageKind == MessageKind.ClaudeCodeAgent ? Path.GetTempPath() : "",
                AgentTimeoutMinutes = messageKind == MessageKind.ClaudeCodeAgent ? 1 : 15
            };
            var id = await repository.CreateTaskAsync(task); var sender = new FailingSender(-1);
            var executor = new MessageExecutor(repository, sender, new GeneratedAi(), new MessageSplitter(), new NoDelay(), new GeneratedClaude());
            var execution = await executor.ExecuteAsync((await repository.GetTaskAsync(id))!, now.AddMinutes(1));
            Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
            Assert.Equal(new[] { "120363000000001@g.us" }, sender.Recipients);
        }
        finally { Delete(path); }
    }

    private static ScheduledTask NewTask(DateTimeOffset now) => new(0, "T", true, "52", "5555555555", MessageKind.Fixed, "hola", "", new ScheduleDefinition(ScheduleKind.Daily, TimeOnly.FromDateTime(now.LocalDateTime), DateOnly.FromDateTime(now.LocalDateTime), new HashSet<DayOfWeek>(), 1), now, now);
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-R2-{Guid.NewGuid():N}.db");
    private static void Delete(string path) { SqliteConnection.ClearAllPools(); foreach (var item in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(item)) File.Delete(item); }
    private sealed class FailingSender(int failAt) : IEvolutionClient { private int _count; public List<string> Recipients { get; } = []; public List<string> Texts { get; } = []; public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) { Recipients.Add(recipient); Texts.Add(text); return Task.FromResult(++_count == failAt ? new SendResult(false, "temporary") : new SendResult(true, "")); } }
    private sealed class NoAi : IAnthropicClient { public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException(); }
    private sealed class GeneratedAi : IAnthropicClient { public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(new GeneratedMessageResult(true, "ia", "")); }
    private sealed class GeneratedClaude : IClaudeCodeRunner { public Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, int timeoutMinutes, CancellationToken cancellationToken = default) => Task.FromResult(new GeneratedMessageResult(true, "agente", "")); }
    private sealed class NoDelay : IAsyncDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
}
