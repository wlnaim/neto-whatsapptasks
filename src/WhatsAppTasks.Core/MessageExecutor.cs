namespace WhatsAppTasks.Core;

public sealed class MessageExecutor
{
    private readonly ITaskRepository _repository; private readonly IEvolutionClient _evolution; private readonly IAnthropicClient _anthropic; private readonly IMessageSplitter _splitter; private readonly IAsyncDelay _delay; private readonly IClaudeCodeRunner? _claudeCode;
    public MessageExecutor(ITaskRepository repository, IEvolutionClient evolution, IAnthropicClient anthropic, IMessageSplitter splitter, IAsyncDelay delay, IClaudeCodeRunner? claudeCode = null) { _claudeCode = claudeCode; _repository = repository; _evolution = evolution; _anthropic = anthropic; _splitter = splitter; _delay = delay; }

    public async Task<ExecutionRecord> ExecuteAsync(ScheduledTask task, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        var id = await _repository.ClaimExecutionAsync(new(task.Id, dueAtUtc), task, cancellationToken);
        var recipient = task.CountryCode + task.PhoneNumber; var started = DateTimeOffset.UtcNow;
        if (id == 0) return new(0, task.Id, task.Name, started, started, ExecutionStatus.Skipped, task.MessageKind, recipient, "", 0, 0, "The task was changed, deleted, disabled, or already claimed.");
        var message = task.FixedMessage; var parts = Array.Empty<string>(); var sent = 0; var status = ExecutionStatus.Failed; var error = "";
        try
        {
            if (task.MessageKind == MessageKind.AnthropicPrompt)
            {
                var generated = await _anthropic.GenerateAsync(task.AnthropicPrompt, cancellationToken);
                if (!generated.Success) { error = SafeError(generated.Error, "Message generation failed."); return await FinishAsync(); }
                message = generated.Text;
            }
            else if (task.MessageKind == MessageKind.ClaudeCodeAgent)
            {
                if (_claudeCode is null) { error = "The Claude Code agent is not available."; return await FinishAsync(); }
                var generated = await _claudeCode.RunAsync(task.AnthropicPrompt, task.AgentWorkingDirectory, task.AgentAllowedTools, task.AgentTimeoutMinutes, cancellationToken);
                if (!generated.Success) { error = SafeError(generated.Error, "Agent execution failed."); return await FinishAsync(); }
                message = generated.Text;
            }
            parts = _splitter.Split(message).ToArray();
            await SaveProgressAsync();
            for (var index = 0; index < parts.Length; index++)
            {
                if (!await IsStillCurrentAsync()) { status = sent == 0 ? ExecutionStatus.Skipped : ExecutionStatus.Partial; error = "The task was changed, deleted, or disabled before the remaining message parts were sent."; return await FinishAsync(); }
                var result = await _evolution.SendTextAsync(recipient, parts[index], cancellationToken);
                if (!result.Success) { status = sent == 0 ? ExecutionStatus.Failed : ExecutionStatus.Partial; error = SafeError(result.Error, "Message sending failed."); return await FinishAsync(); }
                sent++;
                await SaveProgressAsync();
                if (index < parts.Length - 1) await _delay.DelayAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            status = ExecutionStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ExecutionStatus.Cancelled; error = "Execution cancelled."; }
        catch { status = sent == 0 ? ExecutionStatus.Failed : ExecutionStatus.Partial; error = "Message execution could not be completed."; }
        return await FinishAsync();

        async Task<ExecutionRecord> FinishAsync()
        {
            var record = new ExecutionRecord(id, task.Id, task.Name, started, DateTimeOffset.UtcNow, status, task.MessageKind, recipient, message, sent, parts.Length, error);
            await _repository.CompleteExecutionAsync(record, CancellationToken.None); return record;
        }

        async Task SaveProgressAsync() => await _repository.CompleteExecutionAsync(new ExecutionRecord(id, task.Id, task.Name, started, null, ExecutionStatus.Running, task.MessageKind, recipient, message, sent, parts.Length, ""), CancellationToken.None);
        async Task<bool> IsStillCurrentAsync()
        {
            var current = await _repository.GetTaskAsync(task.Id, CancellationToken.None);
            return current is not null && current.IsEnabled && current.UpdatedUtc == task.UpdatedUtc;
        }
    }

    /// <summary>Replays only pending fragments from the durable historical snapshot; it never reads a Task or Anthropic.</summary>
    public async Task<ExecutionRecord> ExecuteManualRetryAsync(ManualRetryPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.Accepted || plan.RootExecution is null || plan.RetryExecution is null) throw new ArgumentException("A claimed retry plan is required.", nameof(plan));
        var root = plan.RootExecution; var record = plan.RetryExecution; var parts = Array.Empty<string>(); var sent = plan.FirstPendingPart; var status = ExecutionStatus.Failed; var error = "";
        try
        {
            parts = _splitter.Split(root.Message).ToArray();
            if (parts.Length != root.TotalPartCount || sent < 0 || sent >= parts.Length) { error = "The saved execution parts are inconsistent."; return await FinishAsync(error); }
            for (var index = sent; index < parts.Length; index++)
            {
                var result = await _evolution.SendTextAsync(root.Recipient, parts[index], cancellationToken);
                if (!result.Success) { status = sent == plan.FirstPendingPart ? ExecutionStatus.Failed : ExecutionStatus.Partial; error = SafeError(result.Error, "Message sending failed."); return await FinishAsync(error); }
                sent++; await SaveProgressAsync();
                if (index < parts.Length - 1) await _delay.DelayAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            status = ExecutionStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ExecutionStatus.Cancelled; error = "Execution cancelled."; }
        catch { status = sent == plan.FirstPendingPart ? ExecutionStatus.Failed : ExecutionStatus.Partial; error = "Message execution could not be completed."; }
        return await FinishAsync(error);

        async Task SaveProgressAsync() => await _repository.CompleteExecutionAsync(record with { FinishedUtc = null, Status = ExecutionStatus.Running, SentPartCount = sent, TotalPartCount = parts.Length, Error = "" }, CancellationToken.None);
        async Task<ExecutionRecord> FinishAsync(string finalError)
        {
            error = finalError; var complete = record with { FinishedUtc = DateTimeOffset.UtcNow, Status = status, SentPartCount = sent, TotalPartCount = parts.Length, Error = error };
            await _repository.CompleteExecutionAsync(complete, CancellationToken.None); return complete;
        }
    }

    private static string SafeError(string value, string fallback) => string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Contains("key", StringComparison.OrdinalIgnoreCase) || value.Contains("token", StringComparison.OrdinalIgnoreCase) ? fallback : value;
}
