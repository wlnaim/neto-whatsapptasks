namespace WhatsAppTasks.Core;

/// <summary>Continuously dispatches due work without waiting for other sends; a detected resume gap drops missed occurrences.</summary>
public sealed class BackgroundTaskScheduler : ITaskScheduler, IManualRetryService
{
    private readonly ITaskRepository _repository; private readonly MessageExecutor _executor; private readonly TimeProvider _clock; private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly Dictionary<long, Pending> _pending = new(); private readonly List<Task> _inFlight = new();
    private CancellationTokenSource? _lifetime; private CancellationTokenSource? _workCancellation; private Task? _loop; private DateTimeOffset? _lastRunUtc; private bool _stopping;
    public Exception? LastError { get; private set; }

    public BackgroundTaskScheduler(ITaskRepository repository, MessageExecutor executor, TimeProvider? clock = null) { _repository = repository; _executor = executor; _clock = clock ?? TimeProvider.System; }
    public BackgroundTaskScheduler(ITaskRepository repository, IEvolutionClient evolution, IAnthropicClient anthropic, IMessageSplitter splitter, IAsyncDelay delay, TimeProvider? clock = null) : this(repository, new MessageExecutor(repository, evolution, anthropic, splitter, delay), clock) { }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetime is not null) return Task.CompletedTask;
        _stopping = false;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loop = LoopAsync(_lifetime.Token); return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken); try { _stopping = true; } finally { _runGate.Release(); }
        await _dispatchGate.WaitAsync(cancellationToken); CancellationTokenSource? lifetime;
        try { lifetime = _lifetime; if (lifetime is null) return; _lifetime = null; _workCancellation?.Cancel(); lifetime.Cancel(); }
        finally { _dispatchGate.Release(); }
        try { if (_loop is not null) await _loop.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        finally { _workCancellation?.Dispose(); _workCancellation = null; lifetime.Dispose(); _loop = null; }
        Task[] outstanding; lock (_inFlight) outstanding = _inFlight.ToArray();
        await Task.WhenAll(outstanding).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DueOccurrence>> GetDueOccurrencesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken); try { return _pending.Values.Where(x => x.DueAtUtc <= nowUtc).Select(x => new DueOccurrence(x.Task.Id, x.DueAtUtc)).ToArray(); } finally { _runGate.Release(); }
    }

    public async Task NotifyResumeAsync(CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken); try
        {
            _workCancellation?.Cancel(); _workCancellation = _lifetime is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _pending.Clear();
            var now = _clock.GetUtcNow(); var tasks = await _repository.GetTasksAsync(cancellationToken);
            foreach (var task in tasks.Where(x => x.IsEnabled)) AddFuture(task, now);
            _lastRunUtc = now;
        }
        catch (Exception error) { LastError = error; throw; }
        finally { _runGate.Release(); }
    }

    public Task<RetryResult> RetryAsync(long sourceExecutionId, CancellationToken cancellationToken = default) => RetryExecutionAsync(sourceExecutionId, cancellationToken);

    public async Task<RetryResult> RetryExecutionAsync(long sourceExecutionId, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(CancellationToken.None); try
        {
            await _dispatchGate.WaitAsync(CancellationToken.None); try
            {
            if (_stopping || _lifetime is null || _workCancellation is null || _workCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return new(false, "El programador se está cerrando; no se inició el reintento.", null);
            }
            var plan = await _repository.ClaimManualRetryAsync(sourceExecutionId, CancellationToken.None);
            if (!plan.Accepted || plan.RetryExecution is null) return new(false, plan.Error, null);
                if (_stopping || _lifetime is null || _workCancellation is null || _workCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    await CancelClaimedRetryAsync(plan.RetryExecution); return new(false, "El programador se está cerrando; el reintento fue cancelado.", plan.RetryExecution);
                }
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _workCancellation.Token);
                var work = Task.Run(async () => { using (linked) return await _executor.ExecuteManualRetryAsync(plan, linked.Token); }, CancellationToken.None);
                _ = work.ContinueWith(x => LastError = x.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                lock (_inFlight) { _inFlight.RemoveAll(x => x.IsCompleted); _inFlight.Add(work); }
                return new(true, "", plan.RetryExecution);
            }
            finally { _dispatchGate.Release(); }
        }
        finally { _runGate.Release(); }
    }

    private Task CancelClaimedRetryAsync(ExecutionRecord record) => _repository.CompleteExecutionAsync(record with { FinishedUtc = DateTimeOffset.UtcNow, Status = ExecutionStatus.Cancelled, Error = "Retry cancelled because the scheduler is stopping." }, CancellationToken.None);

    /// <summary>Deterministic scheduler tick for tests and for the background loop.</summary>
    public async Task RunOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken); try
        {
            var resumeGap = _lastRunUtc is { } previous && nowUtc - previous > TimeSpan.FromMinutes(1);
            var tasks = await _repository.GetTasksAsync(cancellationToken); var active = tasks.Where(x => x.IsEnabled).ToDictionary(x => x.Id); _lastRunUtc = nowUtc;
            await _repository.PurgeHistoryOlderThanAsync(nowUtc.AddDays(-7), cancellationToken);
            if (resumeGap || _pending.Count == 0) { _pending.Clear(); foreach (var task in active.Values) AddFuture(task, nowUtc); return; }
            foreach (var stale in _pending.Keys.Where(x => !active.ContainsKey(x)).ToArray()) _pending.Remove(stale);
            foreach (var task in active.Values)
                if (!_pending.TryGetValue(task.Id, out var old) || old.Task.UpdatedUtc != task.UpdatedUtc) AddFuture(task, nowUtc);
            foreach (var due in _pending.Values.Where(x => x.DueAtUtc <= nowUtc).ToArray())
            {
                AddFuture(due.Task, due.DueAtUtc.AddTicks(1));
                var token = _workCancellation?.Token ?? cancellationToken;
                var work = Task.Run(() => _executor.ExecuteAsync(due.Task, due.DueAtUtc, token), CancellationToken.None);
                _ = work.ContinueWith(x => LastError = x.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                lock (_inFlight) { _inFlight.RemoveAll(x => x.IsCompleted); _inFlight.Add(work); }
            }
        }
        finally { _runGate.Release(); }
    }

    private void AddFuture(ScheduledTask task, DateTimeOffset afterUtc)
    {
        var dueUtc = ScheduleCalculator.GetNextUtc(task.Schedule, afterUtc, TimeZoneInfo.Local);
        if (dueUtc is null) { _pending.Remove(task.Id); return; }
        _pending[task.Id] = new(task, dueUtc.Value);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!token.IsCancellationRequested)
        {
            try { await RunOnceAsync(_clock.GetUtcNow(), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { LastError = error; }
            if (!await timer.WaitForNextTickAsync(token)) break;
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _runGate.Dispose(); _dispatchGate.Dispose(); }
    private sealed record Pending(ScheduledTask Task, DateTimeOffset DueAtUtc);
}
