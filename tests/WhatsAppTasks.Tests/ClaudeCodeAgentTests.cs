using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class ClaudeCodeAgentTests
{
    private static readonly ScheduleDefinition Daily = new(ScheduleKind.Daily, new TimeOnly(10, 30), new DateOnly(2026, 1, 1), new HashSet<DayOfWeek>(), 1);

    [Fact]
    public void Arguments_never_bypass_permissions_and_add_allowed_tools_only_when_present()
    {
        Assert.Equal(new[] { "-p", "--output-format", "json" }, ClaudeCodeRunner.BuildArguments(""));
        Assert.Equal(new[] { "-p", "--output-format", "json" }, ClaudeCodeRunner.BuildArguments("   "));
        var withTools = ClaudeCodeRunner.BuildArguments("Read Bash(git log:*)");
        Assert.Equal(new[] { "-p", "--output-format", "json", "--allowedTools", "Read Bash(git log:*)" }, withTools);
        Assert.DoesNotContain(withTools, x => x.Contains("dangerously", StringComparison.OrdinalIgnoreCase) || x.Contains("bypass", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hola\"}", true, "hola")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"hola\"}", false, "")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":false,\"result\":\"hola\"}", false, "")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"  \"}", false, "")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false}", false, "")]
    [InlineData("not json", false, "")]
    [InlineData("[1]", false, "")]
    [InlineData("", false, "")]
    public void Result_parsing_accepts_only_a_successful_non_empty_result(string json, bool success, string text)
    {
        var parsed = ClaudeCodeRunner.ParseResult(json);
        Assert.Equal(success, parsed.Success); Assert.Equal(text, parsed.Text);
        if (!success) Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public async Task Runner_fails_without_starting_when_directory_or_executable_is_missing()
    {
        var missingDirectory = await new ClaudeCodeRunner("claude.exe").RunAsync("p", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "", 1);
        Assert.False(missingDirectory.Success); Assert.Contains("working directory", missingDirectory.Error);
        var missingExecutable = await new ClaudeCodeRunner(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")).RunAsync("p", Path.GetTempPath(), "", 1);
        Assert.False(missingExecutable.Success); Assert.Contains("could not be started", missingExecutable.Error);
    }

    [Fact]
    public async Task Runner_kills_the_process_tree_on_timeout_or_cancellation()
    {
        var script = Path.Combine(Path.GetTempPath(), $"sleep-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew(); var started = DateTime.Now;
            var result = await new ClaudeCodeRunner(script).RunAsync("p", Path.GetTempPath(), "", TimeSpan.FromMilliseconds(500));
            Assert.False(result.Success); Assert.Contains("timed out", result.Error); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
            await Task.Delay(500); Assert.Empty(System.Diagnostics.Process.GetProcessesByName("PING").Where(p => Environment.TickCount64 > 0 && StartedAfter(p, started)));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeCodeRunner(script).RunAsync("p", Path.GetTempPath(), "", TimeSpan.FromMinutes(1), cancel.Token));
        }
        finally { File.Delete(script); }
    }

    private const string OrphanScript = "@echo off\r\nstart \"\" /b ping -n 25 127.0.0.1 >nul\r\necho {\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\r\nexit /b 0\r\n";

    [Fact]
    public async Task Runner_times_out_and_cancels_even_when_a_descendant_keeps_the_pipes_open()
    {
        var script = Path.Combine(Path.GetTempPath(), $"orphan-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(script, OrphanScript);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await new ClaudeCodeRunner(script).RunAsync("p", Path.GetTempPath(), "", TimeSpan.FromSeconds(2));
            Assert.False(result.Success); Assert.Contains("timed out", result.Error); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
            watch.Restart(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeCodeRunner(script).RunAsync("p", Path.GetTempPath(), "", TimeSpan.FromMinutes(5), cancel.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
        }
        finally { try { File.Delete(script); } catch (IOException) { } }
    }

    [Fact]
    public async Task Executor_marks_the_execution_cancelled_when_the_runner_honours_cancellation()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var task = await StoreAsync(repository); var evolution = new FakeEvolution();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var record = await new MessageExecutor(repository, evolution, new NoAnthropic(), new MessageSplitter(), new NoDelay(), new WaitingRunner()).ExecuteAsync(task, DateTimeOffset.UtcNow, cancel.Token);
            Assert.Equal(ExecutionStatus.Cancelled, record.Status); Assert.Empty(evolution.Sent);
            Assert.Equal(ExecutionStatus.Cancelled, (await repository.GetExecutionAsync(record.Id))!.Status);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Repository_round_trips_agent_fields_validates_them_and_keeps_other_kinds_unchanged()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var now = DateTimeOffset.UtcNow;
            var id = await repository.CreateTaskAsync(Agent(now));
            var stored = (await repository.GetTaskAsync(id))!;
            Assert.Equal(MessageKind.ClaudeCodeAgent, stored.MessageKind); Assert.Equal("revisa", stored.AnthropicPrompt); Assert.Equal(@"C:\wrk", stored.AgentWorkingDirectory); Assert.Equal("Read Bash", stored.AgentAllowedTools); Assert.Equal(15, stored.AgentTimeoutMinutes);
            var fixedId = await repository.CreateTaskAsync(new ScheduledTask(0, "Fija", true, "52", "5512345678", MessageKind.Fixed, "hola", "", Daily, now, now));
            var fixedTask = (await repository.GetTaskAsync(fixedId))!; Assert.Equal(10, fixedTask.AgentTimeoutMinutes); Assert.Equal("", fixedTask.AgentWorkingDirectory);
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(Agent(now) with { AnthropicPrompt = " " }));
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(Agent(now) with { AgentWorkingDirectory = "" }));
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(Agent(now) with { AgentTimeoutMinutes = 0 }));
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(Agent(now) with { AgentTimeoutMinutes = 121 }));
            await repository.UpdateTaskAsync(stored with { AgentTimeoutMinutes = 30, AgentAllowedTools = "" });
            var updated = (await repository.GetTaskAsync(id))!; Assert.Equal(30, updated.AgentTimeoutMinutes); Assert.Equal("", updated.AgentAllowedTools);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Existing_tasks_table_without_agent_columns_is_migrated_and_old_rows_are_read()
    {
        var path = TemporaryDatabase();
        try
        {
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await db.OpenAsync(); await using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE Tasks (Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL,Enabled INTEGER NOT NULL,CountryCode TEXT NOT NULL,PhoneNumber TEXT NOT NULL,MessageKind INTEGER NOT NULL,FixedMessage TEXT NOT NULL,AnthropicPrompt TEXT NOT NULL,Schedule TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL); INSERT INTO Tasks(Name,Enabled,CountryCode,PhoneNumber,MessageKind,FixedMessage,AnthropicPrompt,Schedule,CreatedUtc,UpdatedUtc) VALUES('Vieja',1,'52','5512345678',0,'hola','','{\"Kind\":1,\"LocalTime\":\"10:30:00\",\"AnchorDate\":\"2026-01-01\",\"DaysOfWeek\":[],\"IntervalWeeks\":1}','2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');";
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var first = new SqliteTaskRepository(path))
            {
                var old = (await first.GetTasksAsync()).Single(); Assert.Equal("Vieja", old.Name); Assert.Equal("hola", old.FixedMessage); Assert.Equal(10, old.AgentTimeoutMinutes); Assert.Equal("", old.AgentWorkingDirectory);
            }
            await using var second = new SqliteTaskRepository(path); // idempotent migration
            Assert.Single(await second.GetTasksAsync());
            Assert.True(await second.CreateTaskAsync(Agent(DateTimeOffset.UtcNow)) > 0);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Executor_sends_the_agent_result_and_passes_folder_tools_and_timeout()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var task = await StoreAsync(repository);
            var runner = new FakeRunner(new GeneratedMessageResult(true, "resultado del agente", "")); var evolution = new FakeEvolution();
            var record = await new MessageExecutor(repository, evolution, new NoAnthropic(), new MessageSplitter(), new NoDelay(), runner).ExecuteAsync(task, DateTimeOffset.UtcNow);
            Assert.Equal(ExecutionStatus.Succeeded, record.Status); Assert.Equal(MessageKind.ClaudeCodeAgent, record.MessageKind); Assert.Equal(new[] { "resultado del agente" }, evolution.Sent);
            Assert.Equal(("revisa", @"C:\wrk", "Read Bash", 15), runner.Call);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Executor_does_not_send_and_records_a_short_error_when_the_agent_fails_or_is_missing()
    {
        var path = TemporaryDatabase();
        try
        {
            await using var repository = new SqliteTaskRepository(path); var task = await StoreAsync(repository);
            var evolution = new FakeEvolution();
            var failed = await new MessageExecutor(repository, evolution, new NoAnthropic(), new MessageSplitter(), new NoDelay(), new FakeRunner(new GeneratedMessageResult(false, "", "Claude Code timed out after 15 minutes."))).ExecuteAsync(task, DateTimeOffset.UtcNow);
            Assert.Equal(ExecutionStatus.Failed, failed.Status); Assert.Equal("Claude Code timed out after 15 minutes.", failed.Error); Assert.Empty(evolution.Sent);
            var missing = await new MessageExecutor(repository, evolution, new NoAnthropic(), new MessageSplitter(), new NoDelay()).ExecuteAsync(task, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Equal(ExecutionStatus.Failed, missing.Status); Assert.False(string.IsNullOrWhiteSpace(missing.Error)); Assert.Empty(evolution.Sent);
        }
        finally { DeleteDatabase(path); }
    }

    private static bool StartedAfter(System.Diagnostics.Process p, DateTime since) { try { return p.StartTime >= since.AddSeconds(-1) && !p.HasExited; } catch { return false; } }
    private static ScheduledTask Agent(DateTimeOffset now) => new(0, "Agente", true, "52", "5512345678", MessageKind.ClaudeCodeAgent, "", "revisa", Daily, now, now) { AgentWorkingDirectory = @"C:\wrk", AgentAllowedTools = "Read Bash", AgentTimeoutMinutes = 15 };
    private static async Task<ScheduledTask> StoreAsync(SqliteTaskRepository repository) => (await repository.GetTaskAsync(await repository.CreateTaskAsync(Agent(DateTimeOffset.UtcNow))))!;
    private static string TemporaryDatabase() => Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-Agent-{Guid.NewGuid():N}.db");
    private static void DeleteDatabase(string path) { SqliteConnection.ClearAllPools(); foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate); }

    private sealed class FakeRunner(GeneratedMessageResult result) : IClaudeCodeRunner
    {
        public (string Prompt, string Directory, string Tools, int Timeout)? Call { get; private set; }
        public Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, int timeoutMinutes, CancellationToken cancellationToken = default) { Call = (prompt, workingDirectory, allowedTools, timeoutMinutes); return Task.FromResult(result); }
    }

    private sealed class WaitingRunner : IClaudeCodeRunner
    {
        public async Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, int timeoutMinutes, CancellationToken cancellationToken = default) { await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken); return new(true, "x", ""); }
    }

    private sealed class FakeEvolution : IEvolutionClient
    {
        public List<string> Sent { get; } = [];
        public Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default) { Sent.Add(text); return Task.FromResult(new SendResult(true, "")); }
    }

    private sealed class NoAnthropic : IAnthropicClient { public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException(); }
    private sealed class NoDelay : IAsyncDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
}
