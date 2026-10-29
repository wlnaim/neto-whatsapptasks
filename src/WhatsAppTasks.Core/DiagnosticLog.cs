using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace WhatsAppTasks.Core;

public enum AppLogOperation { Startup, GroupSynchronization, MessageSend, Scheduler, Ui }
public enum AppLogEvent
{
    StartupStarted, StartupCompleted, StartupFailed,
    GroupSyncStarted, GroupFetchStarted, GroupFetchCompleted, GroupFetchFailed,
    GroupPayloadParsed, GroupValidationFailed,
    GroupReplaceStarted, GroupReplaceCompleted, GroupReplaceFailed,
    MessageSendFailed, SchedulerFailed, UiOperationFailed, LoggerFailed,
    GroupSkipped
}
public enum GroupField { None, Id, Subject }
public enum GroupSkipReason { None, NonObject, MissingId, InvalidIdType, InvalidJid, MissingSubject, InvalidSubjectType, EmptySubject, InvalidFieldEncoding }

public sealed record LogMetadata(
    int? DurationMs = null, int? HttpStatus = null, int? Index = null,
    GroupField Field = GroupField.None, JsonValueKind? JsonType = null,
    bool? HasField = null, int? Length = null, bool? JidValid = null,
    bool? NameEmpty = null, int? Count = null,
    int? SqliteErrorCode = null, int? SqliteExtendedErrorCode = null)
{
    public GroupSkipReason SkipReason { get; init; } = GroupSkipReason.None;
    public int? SkippedCount { get; init; }
}

public interface IAppLogScope : IDisposable { string CorrelationId { get; } }
public interface IAppLogger : IDisposable
{
    IAppLogScope BeginScope(AppLogOperation operation, string? correlationId = null);
    void Write(AppLogEvent eventType, LogMetadata? metadata = null, Exception? exception = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public static NullAppLogger Instance { get; } = new();
    private NullAppLogger() { }
    public IAppLogScope BeginScope(AppLogOperation operation, string? correlationId = null) => new Scope(ValidCorrelationId(correlationId) ? correlationId! : Guid.NewGuid().ToString("N"));
    public void Write(AppLogEvent eventType, LogMetadata? metadata = null, Exception? exception = null) { }
    public void Dispose() { }
    private sealed class Scope(string correlationId) : IAppLogScope { public string CorrelationId { get; } = correlationId; public void Dispose() { } }
    private static bool ValidCorrelationId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);
}

/// <summary>Best-effort local JSONL diagnostics with a fixed, non-payload schema.</summary>
public sealed class JsonlAppLogger : IAppLogger
{
    private const long DefaultMaxFileBytes = 5 * 1024 * 1024;
    private const int DefaultRetentionDays = 7;
    private const int DefaultMaxFiles = 56;
    private const int MaxStackLength = 2048;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly int _maxFiles;
    private readonly object _writeLock = new();
    private readonly AsyncLocal<ScopeState?> _current = new();
    private bool _disposed;

    public JsonlAppLogger(string logsDirectory, TimeProvider? clock = null, long maxFileBytes = DefaultMaxFileBytes, int retentionDays = DefaultRetentionDays, int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        if (maxFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        _directory = logsDirectory;
        _clock = clock ?? TimeProvider.System;
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
        _maxFiles = maxFiles;
    }

    public IAppLogScope BeginScope(AppLogOperation operation, string? correlationId = null)
    {
        var prior = _current.Value;
        var id = correlationId is null ? prior?.CorrelationId ?? Guid.NewGuid().ToString("N") : ValidCorrelationId(correlationId) ? correlationId : Guid.NewGuid().ToString("N");
        var scope = new ScopeState(operation, id);
        _current.Value = scope;
        return new Scope(this, prior, scope.CorrelationId);
    }

    public void Write(AppLogEvent eventType, LogMetadata? metadata = null, Exception? exception = null)
    {
        try
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                Directory.CreateDirectory(_directory);
                DeleteExpiredFiles();
                var state = _current.Value;
                var sqlite = exception as SqliteException;
                metadata = sqlite is null
                    ? metadata is null ? null : metadata with { SqliteErrorCode = null, SqliteExtendedErrorCode = null }
                    : (metadata ?? new LogMetadata()) with { SqliteErrorCode = sqlite.SqliteErrorCode, SqliteExtendedErrorCode = sqlite.SqliteExtendedErrorCode };
                var entry = new Entry(_clock.GetUtcNow(), state?.Operation ?? AppLogOperation.Startup,
                    state?.CorrelationId ?? Guid.NewGuid().ToString("N"), eventType, metadata,
                    exception is null ? null : exception.GetType().FullName, exception?.HResult,
                    exception is null ? null : SafeStack(exception),
                    (exception as HttpRequestException)?.HttpRequestError);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
                using var stream = new FileStream(SelectPath(), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
            }
        }
        catch { /* Logging must never change the application result. */ }
    }

    public void Dispose() { lock (_writeLock) _disposed = true; }

    private string SelectPath()
    {
        var day = _clock.GetUtcNow().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < _maxFiles; index++)
        {
            var suffix = index == 0 ? "" : $"-{index}";
            var path = Path.Combine(_directory, $"whatsapptasks-{day}{suffix}.jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileBytes) return path;
        }
        var oldest = Directory.EnumerateFiles(_directory, "whatsapptasks-*.jsonl").Select(path => new FileInfo(path)).OrderBy(x => x.LastWriteTimeUtc).FirstOrDefault();
        if (oldest is not null)
        {
            File.Delete(oldest.FullName);
            return oldest.FullName;
        }
        return Path.Combine(_directory, $"whatsapptasks-{day}-{_maxFiles - 1}.jsonl");
    }

    private void DeleteExpiredFiles()
    {
        var threshold = _clock.GetUtcNow().UtcDateTime.AddDays(-_retentionDays);
        var files = Directory.EnumerateFiles(_directory, "whatsapptasks-*.jsonl").Select(path => new FileInfo(path)).OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
        foreach (var file in files.Skip(_maxFiles).Concat(files.Where(x => x.LastWriteTimeUtc < threshold)))
        {
            try { file.Delete(); } catch { }
        }
    }

    private static string SafeStack(Exception exception)
    {
        var frames = new StackTrace(exception, false).GetFrames() ?? [];
        var text = string.Join("\n", frames.Take(16).Select(frame =>
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType?.Name ?? "Unknown";
            var name = method?.Name ?? "Unknown";
            var file = frame.GetFileName() is { Length: > 0 } value ? Path.GetFileName(value) : null;
            return file is null ? $"{type}.{name}" : $"{type}.{name}@{file}:{frame.GetFileLineNumber()}";
        }));
        return text.Length <= MaxStackLength ? text : text[..MaxStackLength];
    }

    private void Restore(ScopeState? prior) => _current.Value = prior;
    private static bool ValidCorrelationId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);
    private sealed record ScopeState(AppLogOperation Operation, string CorrelationId);
    private sealed class Scope(JsonlAppLogger owner, ScopeState? prior, string correlationId) : IAppLogScope
    {
        private JsonlAppLogger? _owner = owner;
        public string CorrelationId { get; } = correlationId;
        public void Dispose() { var owner = Interlocked.Exchange(ref _owner, null); owner?.Restore(prior); }
    }
    private sealed record Entry(DateTimeOffset TimestampUtc, AppLogOperation Operation, string CorrelationId, AppLogEvent Event, LogMetadata? Metadata, string? ExceptionType, int? HResult, string? Stack, HttpRequestError? HttpRequestError);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
