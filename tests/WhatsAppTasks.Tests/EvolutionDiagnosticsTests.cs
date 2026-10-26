using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class EvolutionDiagnosticsTests
{
    [Theory]
    [InlineData("{\"response\":{\"message\":[{\"exists\":false,\"number\":\"521234567890\"}]}}")]
    [InlineData("{\"response\":{\"exists\":false}}")]
    public async Task Missing_whatsapp_number_is_explained_without_echoing_provider_values(string body)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent(body) }));
        var result = await new EvolutionClient(http, fixture.Repository).SendTextAsync("521234567890", "private message");
        Assert.False(result.Success);
        Assert.Contains("HTTP 400", result.Error);
        Assert.Contains("exists: false", result.Error);
        Assert.DoesNotContain("521234567890", result.Error);
        Assert.True(result.Error.Length <= 300);
    }

    [Theory]
    [InlineData("{\"response\":{\"message\":\"secret-test-credential private message token=hidden\"}}")]
    [InlineData("<html>secret-test-credential private message</html>")]
    [InlineData("[]")]
    public async Task Unrecognized_error_payload_is_not_stored(string body)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent(body) }));
        var result = await new EvolutionClient(http, fixture.Repository).SendTextAsync("521234567890", "private message");
        Assert.Contains("HTTP 400", result.Error);
        Assert.DoesNotContain("secret", result.Error);
        Assert.DoesNotContain("private message", result.Error);
        Assert.DoesNotContain("token", result.Error);
    }

    [Fact]
    public async Task Oversized_error_body_keeps_status_and_does_not_echo_content()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent(new string('X', 30_000)) }));
        var result = await new EvolutionClient(http, fixture.Repository).SendTextAsync("521234567890", "hola");
        Assert.Contains("HTTP 400", result.Error);
        Assert.DoesNotContain("XXXX", result.Error);
        Assert.True(result.Error.Length <= 300);
    }

    [Fact]
    public async Task Required_field_error_is_recognized_without_echoing_other_text()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"response\":{\"message\":[\"instance requires property \\\"text\\\" private message\"]}}") }));
        var result = await new EvolutionClient(http, fixture.Repository).SendTextAsync("521234567890", "hola");
        Assert.Contains("campo obligatorio", result.Error);
        Assert.DoesNotContain("private message", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "credenciales")]
    [InlineData(HttpStatusCode.NotFound, "instancia")]
    [InlineData(HttpStatusCode.TooManyRequests, "límite")]
    [InlineData(HttpStatusCode.InternalServerError, "error interno")]
    public async Task Status_codes_offer_safe_actionable_diagnostics(HttpStatusCode status, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(status) { Content = new StringContent("secret-test-credential") }));
        var result = await new EvolutionClient(http, fixture.Repository).SendTextAsync("521234567890", "hola");
        Assert.Contains($"HTTP {(int)status}", result.Error);
        Assert.Contains(expected, result.Error);
        Assert.DoesNotContain("secret", result.Error);
    }

    [Fact]
    public async Task Network_timeout_is_distinct_from_caller_cancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => throw new TaskCanceledException()));
        var client = new EvolutionClient(http, fixture.Repository);
        var result = await client.SendTextAsync("521234567890", "hola");
        Assert.Contains("tiempo límite", result.Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendTextAsync("521234567890", "hola", cancelled.Token));
    }

    [Fact]
    public async Task Scheduler_history_preserves_the_safe_evolution_diagnosis()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"response\":{\"message\":[{\"exists\":false}]}}") }));
        var now = DateTimeOffset.UtcNow;
        var task = new ScheduledTask(0, "diagnostic test", true, "52", "1234567890", MessageKind.Fixed, "hola", "",
            new(ScheduleKind.Daily, new(12, 0), DateOnly.FromDateTime(DateTime.Now), new HashSet<DayOfWeek>(), 1), now, now);
        task = task with { Id = await fixture.Repository.CreateTaskAsync(task) };
        var executor = new MessageExecutor(fixture.Repository, new EvolutionClient(http, fixture.Repository), new NoAi(), new MessageSplitter(), new NoDelay());
        var result = await executor.ExecuteAsync(task, now);
        var persisted = Assert.Single(await fixture.Repository.GetHistorySinceAsync(now.AddMinutes(-1)));
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, persisted.SentPartCount);
        Assert.Contains("exists: false", persisted.Error);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class NoAi : IAnthropicClient
    {
        public Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class NoDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Fixture(string path, SqliteTaskRepository repository) : IAsyncDisposable
    {
        public SqliteTaskRepository Repository => repository;
        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-Diagnostics-{Guid.NewGuid():N}.db");
            var repository = new SqliteTaskRepository(path);
            await repository.SaveSettingsAsync(new("http://example.test:8080", "WhatsAppService", SettingsProtection.Protect("secret-test-credential"), "", "model"));
            return new(path, repository);
        }
        public async ValueTask DisposeAsync()
        {
            await repository.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
