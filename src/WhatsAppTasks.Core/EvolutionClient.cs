using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhatsAppTasks.Core;

public sealed class EvolutionClient : IEvolutionClient
{
    private readonly HttpClient _http; private readonly ITaskRepository _repository;
    public EvolutionClient(HttpClient http, ITaskRepository repository) { _http = http; _repository = repository; }
    public async Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(recipient) || recipient.Any(c => c is < '0' or > '9')) return new(false, "The recipient is invalid.");
        var settings = await _repository.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.EvolutionBaseUrl) || string.IsNullOrWhiteSpace(settings.EvolutionInstanceName) || string.IsNullOrWhiteSpace(settings.ProtectedEvolutionApiKey)) return new(false, "Evolution connection settings are incomplete.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.EvolutionBaseUrl.TrimEnd('/')}/message/sendText/{Uri.EscapeDataString(settings.EvolutionInstanceName)}") { Content = JsonContent.Create(new { number = recipient, text }) };
            request.Headers.TryAddWithoutValidation("Apikey", SettingsProtection.Unprotect(settings.ProtectedEvolutionApiKey));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode ? new(true, "") : new(false, await DescribeFailureAsync(response, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "Evolution no respondió dentro del tiempo límite. La entrega no pudo confirmarse; revisa WhatsApp antes de reintentar."); }
        catch (CryptographicException) { return new(false, "The protected Evolution API key cannot be read for this Windows user."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(false, "No se pudo comunicar con Evolution. La entrega no pudo confirmarse; revisa la conexión y WhatsApp antes de reintentar."); }
        catch (Exception ex) when (ex is UriFormatException or FormatException) { return new(false, "La configuración de conexión con Evolution no es válida."); }
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var prefix = $"Evolution rechazó el envío (HTTP {(int)response.StatusCode}). ";
        // Never log arbitrary provider text: error bodies can echo credentials or message payloads.
        // Extract only recognized, structured causes from a bounded response.
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = new byte[16_385];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                    if (read == 0) break;
                    length += read;
                }
                if (length < bytes.Length)
                {
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
                    if (json.RootElement.TryGetProperty("response", out var detail))
                    {
                        if (NumberDoesNotExist(detail))
                            return prefix + "Evolution indica que el destinatario no existe en WhatsApp (exists: false). Verifica el número y código de país.";
                        if (HasRequiredTextOrNumberError(detail))
                            return prefix + "Evolution indica que falta un campo obligatorio del mensaje (number o text). Verifica la compatibilidad del servicio.";
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or InvalidOperationException) { }
        }
        return prefix + (response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Verifica el destinatario y los datos de la solicitud; el servicio no proporcionó una causa reconocida.",
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "Verifica las credenciales de Evolution y sus permisos.",
            System.Net.HttpStatusCode.NotFound => "Verifica la URL base, el nombre de la instancia y el endpoint de envío.",
            System.Net.HttpStatusCode.TooManyRequests => "Se alcanzó el límite de solicitudes. Espera antes de reintentar.",
            _ when (int)response.StatusCode >= 500 => "El servicio presentó un error interno. Revisa su estado antes de reintentar.",
            _ => "Revisa el estado y la configuración del servicio."
        });
    }

    private static bool NumberDoesNotExist(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Any(NumberDoesNotExist),
        JsonValueKind.Object => (element.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.False)
            || (element.TryGetProperty("message", out var message) && NumberDoesNotExist(message)),
        _ => false
    };

    private static bool HasRequiredTextOrNumberError(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Any(HasRequiredTextOrNumberError),
        JsonValueKind.Object => element.TryGetProperty("message", out var message) && HasRequiredTextOrNumberError(message),
        JsonValueKind.String => element.GetString() is { } value &&
            (value.Contains("requires property \"number\"", StringComparison.OrdinalIgnoreCase)
             || value.Contains("requires property \"text\"", StringComparison.OrdinalIgnoreCase)),
        _ => false
    };
}
