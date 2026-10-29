using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatsAppTasks.Core;
using Xunit;

namespace WhatsAppTasks.Tests;

/// <summary>Independent acceptance evidence for the Round 2 public boundaries; no live Evolution calls.</summary>
public sealed class Round2ReviewTests
{
    [Fact]
    public async Task Group_catalog_fetch_maps_only_id_and_subject_and_replaces_only_after_valid_response()
    {
        var path = Temp();
        try
        {
            await using var repository = new SqliteTaskRepository(path);
            await repository.SaveSettingsAsync(Settings());
            var successHandler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
                "[{\"id\":\"123-456@g.us\",\"subject\":\"Equipo histórico\",\"participants\":[{\"id\":\"not-stored\"}]},{\"id\":\"120363000000001@g.us\",\"subject\":\"Equipo moderno\"}]"));
            using var client = new HttpClient(successHandler);
            var result = await new EvolutionClient(client, repository).GetGroupsAsync();

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.Groups.Count);
            var group = result.Groups[0];
            Assert.Equal("123-456@g.us", group.Id);
            Assert.Equal("Equipo histórico", group.Name);
            Assert.Equal("120363000000001@g.us", result.Groups[1].Id);
            Assert.Equal("http://example.test:8080/group/fetchAllGroups/instance?getParticipants=false", successHandler.Uri);
            Assert.Equal("test-key", successHandler.ApiKey);

            await repository.ReplaceWhatsAppGroupsAsync(result.Groups);
            Assert.Equal(result.Groups, await repository.GetWhatsAppGroupsAsync());

            await repository.ReplaceWhatsAppGroupsAsync([new("old@g.us", "Anterior")]);
            using var failureClient = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.BadGateway, "{}")));
            var failed = await new EvolutionClient(failureClient, repository).GetGroupsAsync();
            Assert.False(failed.Success);
            Assert.Equal(new WhatsAppGroup("old@g.us", "Anterior"), Assert.Single(await repository.GetWhatsAppGroupsAsync()));

            using var malformedClient = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"id\":\"not-an-array\"}")));
            var malformed = await new EvolutionClient(malformedClient, repository).GetGroupsAsync();
            Assert.False(malformed.Success);
            Assert.Equal(new WhatsAppGroup("old@g.us", "Anterior"), Assert.Single(await repository.GetWhatsAppGroupsAsync()));

            using var emptyClient = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]")));
            var empty = await new EvolutionClient(emptyClient, repository).GetGroupsAsync();
            Assert.True(empty.Success);
            await repository.ReplaceWhatsAppGroupsAsync(empty.Groups);
            Assert.Empty(await repository.GetWhatsAppGroupsAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Group_send_uses_exact_jid_while_phone_validation_remains_in_task_storage()
    {
        var path = Temp();
        try
        {
            await using var repository = new SqliteTaskRepository(path);
            await repository.SaveSettingsAsync(Settings());
            var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
            using var client = new HttpClient(handler);

            var legacy = await new EvolutionClient(client, repository).SendTextAsync("1203630-1@g.us", "hola");
            var modern = await new EvolutionClient(client, repository).SendTextAsync("120363000000001@g.us", "hola");
            Assert.True(legacy.Success, legacy.Error);
            Assert.True(modern.Success, modern.Error);
            Assert.Contains(handler.Bodies, body => body.Contains("\"number\":\"1203630-1@g.us\"", StringComparison.Ordinal));
            Assert.Contains(handler.Bodies, body => body.Contains("\"number\":\"120363000000001@g.us\"", StringComparison.Ordinal));

            var now = DateTimeOffset.UtcNow;
            var invalidPhone = new ScheduledTask(0, "Teléfono", true, "52", "abc", MessageKind.Fixed, "hola", "",
                new ScheduleDefinition(ScheduleKind.Daily, TimeOnly.FromDateTime(now.LocalDateTime), DateOnly.FromDateTime(now.LocalDateTime), new HashSet<DayOfWeek>(), 1), now, now);
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateTaskAsync(invalidPhone));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Populated_pre_round2_database_migrates_to_phone_and_300_snapshots()
    {
        var path = Temp();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Tasks (Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL,Enabled INTEGER NOT NULL,CountryCode TEXT NOT NULL,PhoneNumber TEXT NOT NULL,MessageKind INTEGER NOT NULL,FixedMessage TEXT NOT NULL,AnthropicPrompt TEXT NOT NULL,Schedule TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,AgentWorkingDirectory TEXT NOT NULL DEFAULT '',AgentAllowedTools TEXT NOT NULL DEFAULT '',AgentTimeoutMinutes INTEGER NOT NULL DEFAULT 10);
                    CREATE TABLE Settings (Id INTEGER PRIMARY KEY CHECK(Id=1),EvolutionBaseUrl TEXT NOT NULL,EvolutionInstanceName TEXT NOT NULL,ProtectedEvolutionApiKey TEXT NOT NULL,AnthropicApiKeyFilePath TEXT NOT NULL,AnthropicModel TEXT NOT NULL);
                    CREATE TABLE Executions (Id INTEGER PRIMARY KEY AUTOINCREMENT,TaskId INTEGER NOT NULL,DueAtUtc TEXT NOT NULL,TaskName TEXT NOT NULL,StartedUtc TEXT NOT NULL,FinishedUtc TEXT NULL,Status INTEGER NOT NULL,MessageKind INTEGER NOT NULL,Recipient TEXT NOT NULL,Message TEXT NOT NULL,SentPartCount INTEGER NOT NULL,TotalPartCount INTEGER NOT NULL,Error TEXT NOT NULL,RetryOfExecutionId INTEGER NULL,RetryRootExecutionId INTEGER NULL,RetryStartPartIndex INTEGER NULL,UNIQUE(TaskId,DueAtUtc));
                    INSERT INTO Tasks VALUES(1,'Anterior',1,'52','5512345678',0,'hola','', '{"Kind":1,"LocalTime":"10:30:00","AnchorDate":"2026-01-01","DaysOfWeek":[],"IntervalWeeks":1}', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00','','',10);
                    INSERT INTO Settings VALUES(1,'http://example.test:8080','instance','key','','model');
                    INSERT INTO Executions VALUES(1,1,'2026-01-01T10:30:00.0000000+00:00','Anterior','2026-01-01T10:30:00.0000000+00:00',NULL,1,0,'525512345678','hola',0,1,'',NULL,NULL,NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using var repository = new SqliteTaskRepository(path);
            var task = Assert.Single(await repository.GetTasksAsync());
            var record = Assert.Single(await repository.GetHistorySinceAsync(now.AddYears(-2)));
            var settings = await repository.GetSettingsAsync();
            Assert.Equal(RecipientKind.Phone, task.RecipientKind);
            Assert.Equal("", task.GroupJid);
            Assert.Equal(300, settings.MaximumMessageLength);
            Assert.Equal(300, record.ChunkSize);
            Assert.Equal(RecipientKind.Phone, record.RecipientKind);
            Assert.Equal("", record.RecipientName);
        }
        finally { Delete(path); }
    }

    private static AppSettings Settings() => new("http://example.test:8080", "instance", SettingsProtection.Protect("test-key"), "", "model");
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"WhatsAppTasks-R2Review-{Guid.NewGuid():N}.db");
    private static void Delete(string path) { SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string Uri { get; private set; } = "";
        public string ApiKey { get; private set; } = "";
        public string Body { get; private set; } = "";
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.TryGetValues("Apikey", out var values) ? Assert.Single(values) : "";
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken); Bodies.Add(Body);
            return response(request);
        }
    }
}
