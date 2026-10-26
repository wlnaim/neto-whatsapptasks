using System.Text.Json;

namespace WhatsAppTasks.Core;

public static class SettingsBootstrapper
{
    public static async Task<bool> BootstrapEvolutionFromPostmanAsync(ITaskRepository repository, string collectionPath, string anthropicKeyPath, string defaultModel = "claude-haiku-5-5", CancellationToken cancellationToken = default)
    {
        var current = await repository.GetSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(current.ProtectedEvolutionApiKey)) return false;
        await using var stream = File.OpenRead(collectionPath); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var request = document.RootElement.GetProperty("item")[0].GetProperty("request");
        var uri = new Uri(request.GetProperty("url").GetProperty("raw").GetString() ?? throw new InvalidDataException("Postman URL is missing."));
        var pieces = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 3 || pieces[0] != "message" || pieces[1] != "sendText") throw new InvalidDataException("The collection has no Evolution sendText request.");
        var header = request.GetProperty("header").EnumerateArray().FirstOrDefault(x => string.Equals(x.GetProperty("key").GetString(), "Apikey", StringComparison.OrdinalIgnoreCase));
        var key = header.ValueKind == JsonValueKind.Undefined ? null : header.GetProperty("value").GetString();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("The Postman API key is missing.");
        await repository.SaveSettingsAsync(new($"{uri.Scheme}://{uri.Authority}", pieces[2], SettingsProtection.Protect(key), anthropicKeyPath, defaultModel), cancellationToken);
        return true;
    }
}
