using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.SlackPlugin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end verification of the Slack bidirectional loop: a natively
/// signed <c>block_actions</c> delivery answers the bound question exactly
/// once through the shared pipeline, while tampered, replayed, or stale
/// deliveries fail cleanly before touching state.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class SlackInteractionEndpointsTests : IDisposable
{
    private const string SecretEnvVar = "CODEYBOX_TEST_SLACK_SIGNING_SECRET";
    private const string Secret = "test-slack-signing-secret-value";
    private const string TokenEnvVar = "CODEYBOX_TEST_SLACK_E2E_BOT_TOKEN";

    private readonly SlackEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public SlackInteractionEndpointsTests()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        Environment.SetEnvironmentVariable(TokenEnvVar, "xoxb-test");
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        Environment.SetEnvironmentVariable(TokenEnvVar, null);
    }

    private async Task<WorkItem> CreateWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(SlackEndpointFactory.ProjectId),
            Title = "Slack test item",
            Prompt = "do something",
            State = WorkItemState.NeedsOperatorInput,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _factory.WorkItemStore.CreateAsync(item);
        return item;
    }

    private async Task CreateQuestionAsync(WorkItem item, string questionId = "q-001")
    {
        await _factory.QuestionStore.CreateIfNotExistsAsync(new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            QuestionId = questionId,
            QuestionText = $"What approach for {questionId}?",
        });
    }

    private static string SlackEnvelope(WorkItem item, string triggerId, string answer)
    {
        var value = SlackBlockKit.EncodeButtonValue(
            item.Id.ToString(), "q-001", answer, $"{item.Id}:q-001");
        Assert.NotNull(value);
        var escaped = value!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var json = """{"type":"block_actions","team":{"id":"T123"},"user":{"id":"U123","username":"alice"},"trigger_id":"TRIGGER","channel":{"id":"C999"},"actions":[{"action_id":"codeybox_answer","block_id":"codeybox_actions_3","text":{"type":"plain_text","text":"OPTION"},"value":"VALUE","type":"button","action_ts":"1758640010.002300"}]}"""
            .Replace("TRIGGER", triggerId, StringComparison.Ordinal)
            .Replace("OPTION", answer, StringComparison.Ordinal)
            .Replace("VALUE", escaped, StringComparison.Ordinal);
        return "payload=" + Uri.EscapeDataString(json);
    }

    private static string Sign(string timestamp, byte[] body)
    {
        var basis = Encoding.UTF8.GetBytes($"v0:{timestamp}:{Encoding.UTF8.GetString(body)}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), basis);
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> PostSlackAsync(string formBody, string? timestamp = null, bool tamper = false)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes(formBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/slack");
        request.Headers.Add("X-Slack-Signature", Sign(ts, body));
        request.Headers.Add("X-Slack-Request-Timestamp", ts);
        var send = tamper ? [.. body, (byte)' '] : body;
        request.Content = new ByteArrayContent(send);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ValidSignedSlackAction_AnswersBoundQuestionExactlyOnce()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var form = SlackEnvelope(item, "TRIG-ONCE-1", "Use rollbacks");

        var first = await PostSlackAsync(form);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("answered", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var replay = await PostSlackAsync(form);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("duplicate", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", question!.State);
        Assert.Equal("Use rollbacks", question.AnswerText);
        Assert.Equal("slack:U123 (alice)", question.AnsweredBy);
        Assert.Empty(_factory.SlackOutbound.Requests);
    }

    [Fact]
    public async Task TamperedBody_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostSlackAsync(SlackEnvelope(item, "TRIG-TAMPER-1", "Use rollbacks"), tamper: true);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
        Assert.Null(question.AnswerText);
    }

    [Fact]
    public async Task ReplayedTimestamp_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var oldTs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();

        var resp = await PostSlackAsync(SlackEnvelope(item, "TRIG-REPLAY-1", "Use rollbacks"), timestamp: oldTs);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task AlreadyAnsweredQuestion_FailsCleanlyWithReason()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");
        await CreateQuestionAsync(item, "q-002");

        // The second question keeps the item awaiting input, so a repeated
        // press against the answered first question reports its own state.
        var first = await PostSlackAsync(SlackEnvelope(item, "TRIG-AA-1", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var retry = await PostSlackAsync(SlackEnvelope(item, "TRIG-AA-2", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already answered", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_ListsSlackAsInteractive()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("inboundProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "slack");
        Assert.Contains(body.GetProperty("renderProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "slack"
                && p.GetProperty("supportsInteractions").GetBoolean());
    }
}

internal sealed class SlackEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project-slack";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-slack-");
    private string _dbPath => _scratch.DbPath("slack.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public CapturingHttpHandler SlackOutbound { get; } = new();

    public SlackEndpointFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        QuestionStore = new SqliteWorkItemQuestionStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                ["CodeyBox:Notifications:Interactions:Enabled"] = "true",
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "slack",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "slack-v0",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = "CODEYBOX_TEST_SLACK_SIGNING_SECRET",
                ["CodeyBox:Notifications:Interactions:Providers:0:ReplayWindow"] = "00:05:00",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<IWorkItemQuestionStore>();
            services.AddSingleton<IWorkItemQuestionStore>(QuestionStore);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new CodeyBox.Core.ProjectId(ProjectId),
                    DisplayName = "Slack Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));

            var outbound = SlackOutbound;
            services.AddSingleton<INotificationProvider>(_ =>
                new SlackNotificationProvider(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["CodeyBox:Plugins:codeybox.slack:Enabled"] = "true",
                            ["CodeyBox:Plugins:codeybox.slack:BotTokenEnvVar"] = "CODEYBOX_TEST_SLACK_E2E_BOT_TOKEN",
                            ["CodeyBox:Plugins:codeybox.slack:DefaultChannel"] = "C999",
                        })
                        .Build(),
                    new HttpClient(outbound),
                    NullLogger<SlackNotificationProvider>.Instance));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            QuestionStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
            TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
            _scratch.Dispose();
        }
        base.Dispose(disposing);
    }
}
