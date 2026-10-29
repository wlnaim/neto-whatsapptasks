using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

public sealed class GroupFilteringTests
{
    [Fact]
    public async Task Valid_array_omits_index_36_empty_name_and_keeps_later_groups()
    {
        var database = TempDatabase(); var logs = TempDirectory();
        try
        {
            await using var repository = new SqliteTaskRepository(database); await repository.SaveSettingsAsync(Settings());
            var values = Enumerable.Range(0, 36).Select(index => new { id = $"1203630000000{index:D2}@g.us", subject = $"Nombre {index}" }).Cast<object>().ToList();
            values.Add(new { id = "120363000000036@g.us", subject = "" });
            values.Add(new { id = "120363000000037@g.us", subject = "Posterior exacto" });
            using var logger = new JsonlAppLogger(logs); using var client = Client(JsonSerializer.Serialize(values));
            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();
            Assert.True(result.Success, result.Error); Assert.Equal(1, result.SkippedCount); Assert.Equal(37, result.Groups.Count);
            Assert.Equal(new WhatsAppGroup("120363000000037@g.us", "Posterior exacto"), result.Groups[^1]);
            var text = ReadLogs(logs); Assert.Contains("GroupSkipped", text); Assert.Contains("\"index\":36", text); Assert.Contains("\"skipReason\":\"EmptySubject\"", text); Assert.Contains("\"skippedCount\":1", text); Assert.DoesNotContain("Posterior exacto", text);
        }
        finally { Delete(database, logs); }
    }

    [Fact]
    public async Task All_invalid_records_are_skipped_and_a_valid_array_can_clear_the_catalog()
    {
        var database = TempDatabase(); var logs = TempDirectory();
        try
        {
            await using var repository = new SqliteTaskRepository(database); await repository.SaveSettingsAsync(Settings()); await repository.ReplaceWhatsAppGroupsAsync([new("old@g.us", "Anterior")]);
            const string body = "[null,{}, {\"id\":42,\"subject\":\"x\"},{\"id\":\"bad\",\"subject\":\"x\"},{\"id\":\"１２３@g.us\",\"subject\":\"x\"},{\"id\":\"120363000000001@g.us\"},{\"id\":\"120363000000002@g.us\",\"subject\":null},{\"id\":\"120363000000003@g.us\",\"subject\":8},{\"id\":\"120363000000004@g.us\",\"subject\":\"  \"}]";
            using var logger = new JsonlAppLogger(logs); using var client = Client(body);
            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();
            Assert.True(result.Success, result.Error); Assert.Empty(result.Groups); Assert.Equal(9, result.SkippedCount);
            await repository.ReplaceWhatsAppGroupsAsync(result.Groups); Assert.Empty(await repository.GetWhatsAppGroupsAsync());
            var text = ReadLogs(logs);
            foreach (var reason in new[] { "NonObject", "MissingId", "InvalidIdType", "InvalidJid", "MissingSubject", "InvalidSubjectType", "EmptySubject" }) Assert.Contains(reason, text);
            Assert.DoesNotContain("old@g.us", text); Assert.DoesNotContain("Anterior", text);
        }
        finally { Delete(database, logs); }
    }

    [Fact]
    public async Task Invalid_escaped_surrogate_is_skipped_without_losing_later_valid_groups()
    {
        var database = TempDatabase(); var logs = TempDirectory();
        try
        {
            await using var repository = new SqliteTaskRepository(database); await repository.SaveSettingsAsync(Settings());
            const string body = "[{\"id\":\"120363000000001@g.us\",\"subject\":\"Antes\"},{\"id\":\"120363000000002@g.us\",\"subject\":\"\\uD800\"},{\"id\":\"120363000000003@g.us\",\"subject\":\"Después\"}]";
            using var logger = new JsonlAppLogger(logs); using var client = Client(body);
            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();
            Assert.True(result.Success, result.Error); Assert.Equal(1, result.SkippedCount); Assert.Equal(2, result.Groups.Count);
            Assert.Equal(new WhatsAppGroup("120363000000003@g.us", "Después"), result.Groups[1]);
            Assert.Contains("\"skipReason\":\"InvalidFieldEncoding\"", ReadLogs(logs));
        }
        finally { Delete(database, logs); }
    }

    private static AppSettings Settings() => new("http://example.test", "instance", SettingsProtection.Protect("test-key"), "", "model");
    private static HttpClient Client(string body) => new(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
    private static string TempDatabase() => Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-filter-{Guid.NewGuid():N}.db");
    private static string TempDirectory() { var path = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-filter-logs-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return path; }
    private static string ReadLogs(string directory) => string.Concat(Directory.GetFiles(directory, "*.jsonl").Select(File.ReadAllText));
    private static void Delete(string database, string directory) { SqliteConnection.ClearAllPools(); foreach (var file in new[] { database, database + "-wal", database + "-shm" }) if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}
