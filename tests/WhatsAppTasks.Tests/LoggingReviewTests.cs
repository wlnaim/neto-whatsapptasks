using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

/// <summary>Independent safe-logging acceptance tests. Every HTTP response is local and synthetic.</summary>
public sealed class LoggingReviewTests
{
    [Fact]
    public void Jsonl_logger_redacts_exception_text_rejects_untrusted_correlation_and_rotates()
    {
        var directory = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "whatsapptasks-expired.jsonl"), "{}");
            File.SetLastWriteTimeUtc(Path.Combine(directory, "whatsapptasks-expired.jsonl"), DateTime.UtcNow.AddDays(-8));
            using (var logger = new JsonlAppLogger(directory, maxFileBytes: 80, retentionDays: 7, maxFiles: 3))
            {
                using var scope = logger.BeginScope(AppLogOperation.GroupSynchronization, "APIKEY-SHOULD-NOT-APPEAR");
                Assert.DoesNotContain("APIKEY", scope.CorrelationId, StringComparison.Ordinal);
                Parallel.For(0, 20, index => logger.Write(AppLogEvent.GroupValidationFailed,
                    new(Index: index, Field: GroupField.Id, JsonType: JsonValueKind.String, Length: 18, JidValid: false),
                    new InvalidOperationException("APIKEY-SHOULD-NOT-APPEAR name-secret@group.us body-secret")));
            }

            Assert.False(File.Exists(Path.Combine(directory, "whatsapptasks-expired.jsonl")));
            var files = Directory.GetFiles(directory, "whatsapptasks-*.jsonl");
            Assert.InRange(files.Length, 1, 3);
            var text = string.Join("\n", files.Select(File.ReadAllText));
            Assert.DoesNotContain("APIKEY-SHOULD-NOT-APPEAR", text, StringComparison.Ordinal);
            Assert.DoesNotContain("name-secret@group.us", text, StringComparison.Ordinal);
            Assert.DoesNotContain("body-secret", text, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
            foreach (var line in files.SelectMany(File.ReadLines).Where(x => x.Length > 0))
                using (JsonDocument.Parse(line)) { }
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void Logger_io_failure_is_best_effort_and_never_throws()
    {
        var root = TempDirectory();
        var blocked = Path.Combine(root, "not-a-directory");
        try
        {
            File.WriteAllText(blocked, "x");
            using var logger = new JsonlAppLogger(blocked);
            var exception = Record.Exception(() => logger.Write(AppLogEvent.UiOperationFailed, exception: new IOException("secret message")));
            Assert.Null(exception);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Malformed_group_response_logs_only_shape_metadata_and_not_secrets()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "test.db");
        try
        {
            await using var repository = new SqliteTaskRepository(database);
            await repository.SaveSettingsAsync(new AppSettings("http://example.test:8080", "instance", SettingsProtection.Protect("APIKEY-DO-NOT-LOG"), "", "model"));
            using var logger = new JsonlAppLogger(Path.Combine(root, "Logs"));
            using var client = new HttpClient(new StaticHandler("[{\"id\":\"JID-DO-NOT-LOG\",\"subject\":\"NAME-DO-NOT-LOG\",\"participants\":[\"PERSON-DO-NOT-LOG\"]}]"));

            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();
            Assert.True(result.Success);
            Assert.Empty(result.Groups);
            Assert.Equal(1, result.SkippedCount);

            var content = string.Join("\n", Directory.GetFiles(Path.Combine(root, "Logs"), "*.jsonl").Select(File.ReadAllText));
            Assert.Contains("GroupSkipped", content, StringComparison.Ordinal);
            Assert.Contains("\"index\":0", content, StringComparison.Ordinal);
            Assert.Contains("\"field\":\"Id\"", content, StringComparison.Ordinal);
            Assert.Contains("\"jidValid\":false", content, StringComparison.Ordinal);
            Assert.DoesNotContain("APIKEY-DO-NOT-LOG", content, StringComparison.Ordinal);
            Assert.DoesNotContain("JID-DO-NOT-LOG", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NAME-DO-NOT-LOG", content, StringComparison.Ordinal);
            Assert.DoesNotContain("PERSON-DO-NOT-LOG", content, StringComparison.Ordinal);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Group_fetch_timeout_and_cancellation_are_logged_without_request_values()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "test.db");
        try
        {
            await using var repository = new SqliteTaskRepository(database);
            await repository.SaveSettingsAsync(new AppSettings("http://example.test:8080", "instance", SettingsProtection.Protect("APIKEY-TIMEOUT-SECRET"), "", "model"));
            using var logger = new JsonlAppLogger(Path.Combine(root, "Logs"));
            using var timeoutClient = new HttpClient(new ThrowingHandler(new OperationCanceledException("request-body-secret")));
            var timeout = await new EvolutionClient(timeoutClient, repository, logger).GetGroupsAsync();
            Assert.False(timeout.Success);

            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var cancellationClient = new HttpClient(new WaitingHandler());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EvolutionClient(cancellationClient, repository, logger).GetGroupsAsync(cancelled.Token));

            var content = string.Join("\n", Directory.GetFiles(Path.Combine(root, "Logs"), "*.jsonl").Select(File.ReadAllText));
            Assert.Contains("GroupFetchFailed", content, StringComparison.Ordinal);
            Assert.Contains("OperationCanceledException", content, StringComparison.Ordinal);
            Assert.DoesNotContain("APIKEY-TIMEOUT-SECRET", content, StringComparison.Ordinal);
            Assert.DoesNotContain("request-body-secret", content, StringComparison.Ordinal);
        }
        finally { DeleteDirectory(root); }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-Logging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
