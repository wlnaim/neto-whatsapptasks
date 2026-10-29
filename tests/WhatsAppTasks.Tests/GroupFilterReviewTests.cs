using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

/// <summary>Independent acceptance evidence for filtering individual invalid group records.</summary>
public sealed class GroupFilterReviewTests
{
    [Fact]
    public async Task Incident_index_36_empty_subject_is_skipped_and_later_valid_group_is_retained()
    {
        var root = TempDirectory();
        try
        {
            await using var repository = await RepositoryAsync(root);
            await repository.ReplaceWhatsAppGroupsAsync([new("old-1@g.us", "Anterior")]);
            using var logger = new JsonlAppLogger(Path.Combine(root, "Logs"));
            var json = "[" + string.Join(',', Enumerable.Range(0, 38).Select(index =>
                index == 36
                    ? "{\"id\":\"120363000000036@g.us\",\"subject\":\"\"}"
                    : $"{{\"id\":\"1203630000000{index:D2}@g.us\",\"subject\":\"Grupo {index}\"}}")) + "]";
            using var client = new HttpClient(new JsonHandler(HttpStatusCode.OK, json));

            var result = await new EvolutionClient(client, repository, logger).GetGroupsAsync();

            Assert.True(result.Success);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(37, result.Groups.Count);
            Assert.Contains(result.Groups, group => group.Id == "120363000000037@g.us");
            await repository.ReplaceWhatsAppGroupsAsync(result.Groups);
            var saved = await repository.GetWhatsAppGroupsAsync();
            Assert.Equal(37, saved.Count);
            Assert.DoesNotContain(saved, group => group.Id == "old-1@g.us");
            Assert.Contains(saved, group => group.Id == "120363000000037@g.us");

            var log = string.Join("\n", Directory.GetFiles(Path.Combine(root, "Logs"), "*.jsonl").Select(File.ReadAllText));
            Assert.Contains("GroupSkipped", log, StringComparison.Ordinal);
            Assert.Contains("\"index\":36", log, StringComparison.Ordinal);
            Assert.Contains("\"skipReason\":\"EmptySubject\"", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Grupo 37", log, StringComparison.Ordinal);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task All_invalid_or_empty_arrays_clear_but_global_http_failure_preserves_catalog()
    {
        var root = TempDirectory();
        try
        {
            await using var repository = await RepositoryAsync(root);
            await repository.ReplaceWhatsAppGroupsAsync([new("old-1@g.us", "Anterior")]);
            using var allInvalidClient = new HttpClient(new JsonHandler(HttpStatusCode.OK, "[{\"id\":\"120363000000001@g.us\",\"subject\":\"  \"}]"));
            var allInvalid = await new EvolutionClient(allInvalidClient, repository).GetGroupsAsync();
            Assert.True(allInvalid.Success);
            Assert.Empty(allInvalid.Groups);
            Assert.Equal(1, allInvalid.SkippedCount);
            await repository.ReplaceWhatsAppGroupsAsync(allInvalid.Groups);
            Assert.Empty(await repository.GetWhatsAppGroupsAsync());

            await repository.ReplaceWhatsAppGroupsAsync([new("old-2@g.us", "Conservar")]);
            using var failureClient = new HttpClient(new JsonHandler(HttpStatusCode.BadGateway, "{}"));
            var failed = await new EvolutionClient(failureClient, repository).GetGroupsAsync();
            Assert.False(failed.Success);
            Assert.Equal(new WhatsAppGroup("old-2@g.us", "Conservar"), Assert.Single(await repository.GetWhatsAppGroupsAsync()));

            using var emptyClient = new HttpClient(new JsonHandler(HttpStatusCode.OK, "[]"));
            var empty = await new EvolutionClient(emptyClient, repository).GetGroupsAsync();
            Assert.True(empty.Success);
            Assert.Empty(empty.Groups);
            Assert.Equal(0, empty.SkippedCount);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<SqliteTaskRepository> RepositoryAsync(string root)
    {
        var repository = new SqliteTaskRepository(Path.Combine(root, "test.db"));
        await repository.SaveSettingsAsync(new AppSettings("http://example.test:8080", "instance", SettingsProtection.Protect("test-key"), "", "model"));
        return repository;
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-GroupFilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class JsonHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
