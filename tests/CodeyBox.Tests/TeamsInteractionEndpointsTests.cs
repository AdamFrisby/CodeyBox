using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.TeamsPlugin;
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
/// End-to-end verification of the Teams bidirectional loop: a bearer-signed
/// Bot Framework activity answers the bound question exactly once through
/// the shared pipeline, while tampered, expired, or stale deliveries fail
/// cleanly before touching state.
///
/// A live-platform test cannot exist here: Teams has no sandbox API and CI
/// cannot hold tenant bot credentials, so the suite runs against the
/// recorded activity shape in <c>Fixtures/Teams/submit-activity.json</c>
/// (also pinned by <c>TeamsInteractionFoundationTests</c>) with RSA-signed
/// bearer tokens minted in-test. See
/// <c>docs/extending/teams-notifications.md</c> for the documented reason.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class TeamsInteractionEndpointsTests : IDisposable
{
    private const string AppIdEnvVar = "CODEYBOX_TEST_TEAMS_E2E_APP_ID";
    private const string AppId = "test-teams-e2e-app-id";
    private const string BotTokenEnvVar = "CODEYBOX_TEST_TEAMS_E2E_BOT_TOKEN";


    private readonly TeamsEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public TeamsInteractionEndpointsTests()
    {
        Environment.SetEnvironmentVariable(AppIdEnvVar, AppId);
        Environment.SetEnvironmentVariable(BotTokenEnvVar, "test-bot-token");
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(AppIdEnvVar, null);
        Environment.SetEnvironmentVariable(BotTokenEnvVar, null);
    }

    private async Task<WorkItem> CreateWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(TeamsEndpointFactory.ProjectId),
            Title = "Teams test item",
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

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MintBearerToken(DateTimeOffset? expiresAt = null, string? audience = AppId)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "RS256", kid = TeamsEndpointFactory.KeyId, typ = "JWT" })));
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                iss = TeamsInteractionVerifier.ExpectedIssuer,
                aud = audience,
                exp = (expiresAt ?? now.AddHours(1)).ToUnixTimeSeconds(),
                iat = now.ToUnixTimeSeconds(),
            })));
        var signingInput = $"{header}.{payload}";
        var signature = TeamsEndpointFactory.SigningRsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string TeamsActivity(WorkItem item, string activityId, string answer, string? questionId = null)
    {
        var q = questionId ?? "q-001";
        var value = JsonSerializer.Serialize(new
        {
            w = item.Id.ToString(),
            q,
            a = answer,
            c = $"{item.Id}:{q}",
        });
        return JsonSerializer.Serialize(new
        {
            type = "message",
            id = activityId,
            serviceUrl = "https://smba.trafficmanager.net/teams/",
            from = new { id = "29:alice-user-id", name = "Alice" },
            conversation = new { id = "19:channel-id@thread.tacv2" },
            recipient = new { id = "28:bot-id" },
            text = answer,
            value = JsonSerializer.Deserialize<JsonElement>(value),
        });
    }

    private async Task<HttpResponseMessage> PostTeamsAsync(string activityJson, string? bearerToken = null, bool tamper = false)
    {
        var body = Encoding.UTF8.GetBytes(activityJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/teams");
        request.Headers.Add("Authorization", "Bearer " + (bearerToken ?? MintBearerToken()));
        var send = tamper ? [.. body, (byte)' '] : body;
        request.Content = new ByteArrayContent(send);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ValidSignedSubmit_AnswersBoundQuestionExactlyOnce()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var first = await PostTeamsAsync(TeamsActivity(item, "activity-once-1", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("answered", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var replay = await PostTeamsAsync(TeamsActivity(item, "activity-once-1", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("duplicate", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", question!.State);
        Assert.Equal("Use rollbacks", question.AnswerText);
        Assert.Equal("teams:29:alice-user-id (Alice)", question.AnsweredBy);
        Assert.Empty(_factory.TeamsOutbound.Requests);
    }

    [Fact]
    public async Task TamperedBearerToken_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var valid = MintBearerToken();
        var vparts = valid.Split('.');
        var vmid = vparts[2][10] == 'A' ? 'B' : 'A';
        var tampered = $"{vparts[0]}.{vparts[1]}.{vparts[2][..10]}{vmid}{vparts[2][11..]}";

        var resp = await PostTeamsAsync(TeamsActivity(item, "activity-tamper-1", "Use rollbacks"), bearerToken: tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
        Assert.Null(question.AnswerText);
    }

    [Fact]
    public async Task ExpiredBearerToken_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var expired = MintBearerToken(expiresAt: DateTimeOffset.UtcNow.AddHours(-2));

        var resp = await PostTeamsAsync(TeamsActivity(item, "activity-expired-1", "Use rollbacks"), bearerToken: expired);

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
        // submit against the answered first question reports its own state.
        var first = await PostTeamsAsync(TeamsActivity(item, "activity-aa-1", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var retry = await PostTeamsAsync(TeamsActivity(item, "activity-aa-2", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already answered", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_ListsTeamsAsInteractive()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("inboundProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "teams");
        Assert.Contains(body.GetProperty("renderProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "teams"
                && p.GetProperty("supportsInteractions").GetBoolean());
    }
}

internal sealed class TeamsStubKeyProvider : IBotFrameworkSigningKeyProvider
{
    private readonly RSA _rsa;
    public TeamsStubKeyProvider(RSA rsa) => _rsa = rsa;
    public Task<IReadOnlyList<BotFrameworkSigningKey>> GetKeysAsync(CancellationToken ct, bool forceRefresh = false)
        => Task.FromResult<IReadOnlyList<BotFrameworkSigningKey>>(
            new[] { new BotFrameworkSigningKey(TeamsEndpointFactory.KeyId, _rsa.ExportParameters(false)) });
}

internal sealed class TeamsEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project-teams";

    public static readonly RSA SigningRsa = RSA.Create(2048);
    public const string KeyId = "e2e-test-key";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-teams-");
    private string _dbPath => _scratch.DbPath("teams.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public CapturingHttpHandler TeamsOutbound { get; } = new();

    public TeamsEndpointFactory()
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
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "teams",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "botframework-jwt",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = "CODEYBOX_TEST_TEAMS_E2E_APP_ID",
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
                    DisplayName = "Teams Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));

            // The host's real Teams verifier stays in place; only the
            // Microsoft JWKS fetch is stubbed so no network is needed.
            services.RemoveAll<IBotFrameworkSigningKeyProvider>();
            services.AddSingleton<IBotFrameworkSigningKeyProvider>(
                new TeamsStubKeyProvider(SigningRsa));

            var outbound = TeamsOutbound;
            services.AddSingleton<INotificationProvider>(_ =>
                new TeamsNotificationProvider(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["CodeyBox:Plugins:codeybox.teams:Enabled"] = "true",
                            ["CodeyBox:Plugins:codeybox.teams:AppIdEnvVar"] = "CODEYBOX_TEST_TEAMS_E2E_APP_ID",
                            ["CodeyBox:Plugins:codeybox.teams:AppPasswordEnvVar"] = "CODEYBOX_TEST_TEAMS_E2E_BOT_TOKEN",
                            ["CodeyBox:Plugins:codeybox.teams:ServiceUrl"] = "https://smba.trafficmanager.net/teams/",
                            ["CodeyBox:Plugins:codeybox.teams:ConversationId"] = "19:channel-id@thread.tacv2",
                        })
                        .Build(),
                    new HttpClient(outbound),
                    NullLogger<TeamsNotificationProvider>.Instance));
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
