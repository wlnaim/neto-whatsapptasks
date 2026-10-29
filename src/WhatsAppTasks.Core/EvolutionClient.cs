using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhatsAppTasks.Core;

public sealed class EvolutionClient : IEvolutionClient
{
    private readonly HttpClient _http; private readonly ITaskRepository _repository; private readonly IAppLogger _logger;
    public EvolutionClient(HttpClient http, ITaskRepository repository, IAppLogger? logger = null) { _http = http; _repository = repository; _logger = logger ?? NullAppLogger.Instance; }
    public async Task<SendResult> SendTextAsync(string recipient, string text, CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope(AppLogOperation.MessageSend);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!IsPhoneRecipient(recipient) && !IsGroupJid(recipient)) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: 0)); return new(false, "The recipient is invalid."); }
        var settings = await _repository.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.EvolutionBaseUrl) || string.IsNullOrWhiteSpace(settings.EvolutionInstanceName) || string.IsNullOrWhiteSpace(settings.ProtectedEvolutionApiKey)) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds)); return new(false, "Evolution connection settings are incomplete."); }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.EvolutionBaseUrl.TrimEnd('/')}/message/sendText/{Uri.EscapeDataString(settings.EvolutionInstanceName)}") { Content = JsonContent.Create(new { number = recipient, text }) };
            request.Headers.TryAddWithoutValidation("Apikey", SettingsProtection.Unprotect(settings.ProtectedEvolutionApiKey));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return new(true, "");
            _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds, HttpStatus: (int)response.StatusCode));
            return new(false, await DescribeFailureAsync(response, cancellationToken));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); throw; }
        catch (OperationCanceledException ex) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "Evolution no respondió dentro del tiempo límite. La entrega no pudo confirmarse; revisa WhatsApp antes de reintentar."); }
        catch (CryptographicException ex) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "The protected Evolution API key cannot be read for this Windows user."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "No se pudo comunicar con Evolution. La entrega no pudo confirmarse; revisa la conexión y WhatsApp antes de reintentar."); }
        catch (Exception ex) when (ex is UriFormatException or FormatException) { _logger.Write(AppLogEvent.MessageSendFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "La configuración de conexión con Evolution no es válida."); }
    }

    public async Task<GroupCatalogFetchResult> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope(AppLogOperation.GroupSynchronization);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var skipped = 0;
        _logger.Write(AppLogEvent.GroupSyncStarted);
        AppSettings settings;
        try { settings = await _repository.GetSettingsAsync(cancellationToken); }
        catch (Exception ex) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); throw; }
        if (string.IsNullOrWhiteSpace(settings.EvolutionBaseUrl) || string.IsNullOrWhiteSpace(settings.EvolutionInstanceName) || string.IsNullOrWhiteSpace(settings.ProtectedEvolutionApiKey)) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds)); return new(false, "Evolution connection settings are incomplete.", []); }
        try
        {
            _logger.Write(AppLogEvent.GroupFetchStarted);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.EvolutionBaseUrl.TrimEnd('/')}/group/fetchAllGroups/{Uri.EscapeDataString(settings.EvolutionInstanceName)}?getParticipants=false");
            request.Headers.TryAddWithoutValidation("Apikey", SettingsProtection.Unprotect(settings.ProtectedEvolutionApiKey));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds, HttpStatus: (int)response.StatusCode)); return new(false, $"Evolution group catalog request failed with HTTP {(int)response.StatusCode}.", []); }
            _logger.Write(AppLogEvent.GroupFetchCompleted, new(DurationMs: (int)stopwatch.ElapsedMilliseconds, HttpStatus: (int)response.StatusCode));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return InvalidRoot(document.RootElement);
            var groups = new List<WhatsAppGroup>();
            var items = document.RootElement.EnumerateArray().ToArray();
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (item.ValueKind != JsonValueKind.Object) { Skip(index, GroupSkipReason.NonObject, GroupField.None, item, null, null, null); continue; }
                if (!item.TryGetProperty("id", out var id)) { Skip(index, GroupSkipReason.MissingId, GroupField.Id, null, false, null, null); continue; }
                if (id.ValueKind != JsonValueKind.String) { Skip(index, GroupSkipReason.InvalidIdType, GroupField.Id, id, true, null, null); continue; }
                string jid;
                try { jid = id.GetString()!; }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Skip(index, GroupSkipReason.InvalidFieldEncoding, GroupField.Id, id, true, null, null); continue; }
                var jidValid = IsGroupJid(jid);
                if (!jidValid) { Skip(index, GroupSkipReason.InvalidJid, GroupField.Id, id, true, jid.Length, false); continue; }
                if (!item.TryGetProperty("subject", out var subject)) { Skip(index, GroupSkipReason.MissingSubject, GroupField.Subject, null, false, null, null); continue; }
                if (subject.ValueKind != JsonValueKind.String) { Skip(index, GroupSkipReason.InvalidSubjectType, GroupField.Subject, subject, true, null, null); continue; }
                string name;
                try { name = subject.GetString()!; }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Skip(index, GroupSkipReason.InvalidFieldEncoding, GroupField.Subject, subject, true, null, null); continue; }
                if (string.IsNullOrWhiteSpace(name)) { Skip(index, GroupSkipReason.EmptySubject, GroupField.Subject, subject, true, name.Length, null); continue; }
                groups.Add(new(jid, name));
            }
            _logger.Write(AppLogEvent.GroupPayloadParsed, new(Count: groups.Count) { SkippedCount = skipped });
            return new(true, "", groups) { SkippedCount = skipped };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); throw; }
        catch (OperationCanceledException ex) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "Evolution group catalog request timed out.", []); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or CryptographicException or UriFormatException or FormatException) { _logger.Write(AppLogEvent.GroupFetchFailed, new(DurationMs: (int)stopwatch.ElapsedMilliseconds), ex); return new(false, "Evolution group catalog could not be loaded.", []); }

        GroupCatalogFetchResult InvalidRoot(JsonElement value)
        {
            _logger.Write(AppLogEvent.GroupValidationFailed, new(Field: GroupField.None, JsonType: value.ValueKind));
            return new(false, "Evolution returned an invalid group catalog.", []);
        }

        void Skip(int itemIndex, GroupSkipReason reason, GroupField field, JsonElement? value, bool? hasField, int? length, bool? jidValid)
        {
            var nameEmpty = reason == GroupSkipReason.EmptySubject;
            skipped++;
            _logger.Write(AppLogEvent.GroupSkipped, new(Index: itemIndex, Field: field, JsonType: value?.ValueKind, HasField: hasField, Length: length, JidValid: jidValid, NameEmpty: nameEmpty) { SkipReason = reason });
        }
    }

    private static bool IsPhoneRecipient(string? value) => !string.IsNullOrEmpty(value) && value.All(c => c is >= '0' and <= '9');
    private static bool IsGroupJid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith("@g.us", StringComparison.Ordinal)) return false;
        var prefix = value[..^5];
        var separator = prefix.IndexOf('-');
        if (separator < 0) return prefix.Length > 0 && prefix.All(c => c is >= '0' and <= '9');
        return separator > 0 && separator == prefix.LastIndexOf('-') && separator < prefix.Length - 1 && prefix.All(c => c is >= '0' and <= '9' or '-');
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
