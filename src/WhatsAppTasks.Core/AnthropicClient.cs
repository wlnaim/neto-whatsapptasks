using System.Net.Http.Json;
using System.Text.Json;

namespace WhatsAppTasks.Core;

public sealed class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _http; private readonly ITaskRepository _repository;
    public AnthropicClient(HttpClient http, ITaskRepository repository) { _http = http; _repository = repository; }
    public async Task<GeneratedMessageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(settings.AnthropicApiKeyFilePath) || string.IsNullOrWhiteSpace(settings.AnthropicModel)) return new(false, "", "Anthropic settings are incomplete.");
        try
        {
            var key = (await File.ReadAllTextAsync(settings.AnthropicApiKeyFilePath, cancellationToken)).Trim();
            if (key.Length == 0) return new(false, "", "The Anthropic API key file is empty.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent.Create(new { model = settings.AnthropicModel, max_tokens = 8192, messages = new[] { new { role = "user", content = prompt } } }) };
            request.Headers.TryAddWithoutValidation("x-api-key", key); request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, "", $"Anthropic request failed with HTTP {(int)response.StatusCode}.");
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken); using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal)) return new(false, "", "Anthropic did not complete the generated message.");
            var result = doc.RootElement.GetProperty("content").EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "text").Select(x => x.GetProperty("text").GetString() ?? "");
            var text = string.Concat(result); return text.Length == 0 ? new(false, "", "Anthropic returned no text.") : new(true, text, "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException) { return new(false, "", "Anthropic request could not be completed."); }
    }
}
