namespace WhatsAppTasks.Core;

public enum ScheduleKind { OneTime, Daily, WeeklyOnDays, EveryDay, FutureDate, Monthly, AlternatingWeeks, EveryNWeeks }
public enum MessageKind { Fixed, AnthropicPrompt, ClaudeCodeAgent }
public enum RecipientKind { Phone, Group }
public enum ExecutionStatus { Succeeded, Failed, Partial, Cancelled, Skipped, Running }

public sealed record ScheduledTask(long Id, string Name, bool IsEnabled, string CountryCode, string PhoneNumber,
    MessageKind MessageKind, string FixedMessage, string AnthropicPrompt, ScheduleDefinition Schedule,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc)
{
    public string AgentWorkingDirectory { get; init; } = "";
    public string AgentAllowedTools { get; init; } = "";
    public int AgentTimeoutMinutes { get; init; } = 10;
    public RecipientKind RecipientKind { get; init; } = RecipientKind.Phone;
    public string GroupJid { get; init; } = "";
    public string GroupName { get; init; } = "";
}

public sealed record ScheduleDefinition(ScheduleKind Kind, TimeOnly LocalTime, DateOnly AnchorDate,
    IReadOnlySet<DayOfWeek> DaysOfWeek, int IntervalWeeks);

public sealed record AppSettings(string EvolutionBaseUrl, string EvolutionInstanceName, string ProtectedEvolutionApiKey,
    string AnthropicApiKeyFilePath, string AnthropicModel)
{
    public int MaximumMessageLength { get; init; } = 300;
    public static AppSettings Empty { get; } = new("http://localhost:8080", "", "", "", "claude-haiku-5-5");
}

public sealed record ExecutionRecord(long Id, long TaskId, string TaskName, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, ExecutionStatus Status, MessageKind MessageKind, string Recipient, string Message,
    int SentPartCount, int TotalPartCount, string Error)
{
    public long? RetryRootExecutionId { get; init; }
    public long? RetryOfExecutionId { get; init; }
    public int? RetryStartPartIndex { get; init; }
    public int ChunkSize { get; init; } = 300;
    public RecipientKind RecipientKind { get; init; } = RecipientKind.Phone;
    public string RecipientName { get; init; } = "";
}
public sealed record WhatsAppGroup(string Id, string Name);
public sealed record GroupCatalogFetchResult(bool Success, string Error, IReadOnlyList<WhatsAppGroup> Groups)
{
    public int SkippedCount { get; init; }
}
public sealed record DueOccurrence(long TaskId, DateTimeOffset DueAtUtc);
public sealed record SendResult(bool Success, string Error);
public sealed record GeneratedMessageResult(bool Success, string Text, string Error);
public sealed record ManualRetryPlan(bool Accepted, string Error, ExecutionRecord? RootExecution, ExecutionRecord? RetryExecution, int FirstPendingPart);
public sealed record RetryResult(bool Accepted, string Error, ExecutionRecord? Execution);

public interface ITaskRepository
{
    Task<IReadOnlyList<ScheduledTask>> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<ScheduledTask?> GetTaskAsync(long id, CancellationToken cancellationToken = default);
    Task<long> CreateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default);
    Task UpdateTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default);
    Task SetTaskEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task DeleteTaskAsync(long id, CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WhatsAppGroup>> GetWhatsAppGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WhatsAppGroup>>([]);
    Task ReplaceWhatsAppGroupsAsync(IReadOnlyList<WhatsAppGroup> groups, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<IReadOnlyList<ExecutionRecord>> GetHistorySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
    Task<ExecutionRecord?> GetExecutionAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult<ExecutionRecord?>(null);
    Task<ManualRetryPlan> ClaimManualRetryAsync(long sourceExecutionId, CancellationToken cancellationToken = default) => Task.FromResult(new ManualRetryPlan(false, "Manual retry is unavailable.", null, null, 0));
    Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, CancellationToken cancellationToken = default);
    Task<long> ClaimExecutionAsync(DueOccurrence occurrence, ScheduledTask task, int chunkSize, CancellationToken cancellationToken = default) => ClaimExecutionAsync(occurrence, task, cancellationToken);
    Task CompleteExecutionAsync(ExecutionRecord record, CancellationToken cancellationToken = default);
    Task PurgeHistoryOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default);
}

public interface ITaskScheduler : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DueOccurrence>> GetDueOccurrencesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface IEvolutionClient { Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default); Task<GroupCatalogFetchResult> GetGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GroupCatalogFetchResult(false, "Group catalog is unavailable.", [])); }
public interface IAnthropicClient { Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default); }
public interface IClaudeCodeRunner { Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, int timeoutMinutes, CancellationToken cancellationToken = default); }
public interface IMessageSplitter { IReadOnlyList<string> Split(string text, int maximumLength = 300); }
public interface IAsyncDelay { Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken); }
public interface IManualRetryService { Task<RetryResult> RetryAsync(long sourceExecutionId, CancellationToken cancellationToken = default); }
