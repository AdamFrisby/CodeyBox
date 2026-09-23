using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.GotifyPlugin;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Inbound verification for the Gotify provider — by honest absence. Gotify
/// cannot carry an authenticated interaction (messages render and can open a
/// URL on tap, but there is no signed callback the host could verify), so the
/// plugin declares notification-only and supplies no
/// <see cref="IInteractionVerifier"/>. These tests pin the consequence through
/// the real endpoint: even a correctly-signed, well-formed payload addressed
/// to <c>/webhooks/interactions/gotify</c> is refused before semantic
/// processing and the bound question is never touched.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class GotifyInteractionEndpointTests : IDisposable
{
    private const string SecretEnvVar = "CODEYBOX_TEST_GOTIFY_INTERACTION_SECRET";
    private const string Secret = "test-gotify-interaction-secret-value";

    private readonly GotifyInteractionEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public GotifyInteractionEndpointTests()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
    }

    private async Task<WorkItem> CreateWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(GotifyInteractionEndpointFactory.ProjectId),
            Title = "Test item",
            Prompt = "do something",
            State = WorkItemState.NeedsOperatorInput,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _factory.WorkItemStore.CreateAsync(item);
        await _factory.QuestionStore.CreateIfNotExistsAsync(new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            QuestionId = "q-001",
            QuestionText = "Which approach?",
        });
        return item;
    }

    private async Task<HttpResponseMessage> PostInteractionAsync(WorkItem item, bool sign)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            interactionId = "gotify-evt-1",
            workItemId = item.Id.ToString(),
            questionId = "q-001",
            answer = "Use approach B.",
            user = new { userId = "operator", login = "op" },
            correlationToken = NotificationCorrelation.TokenFor(item.Id.ToString(), "q-001"),
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/gotify");
        if (sign)
        {
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body);
            request.Headers.Add("X-CodeyBox-Signature", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
            request.Headers.Add("X-CodeyBox-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task SignedPayload_Refused_QuestionUntouched()
    {
        // No verifier is registered for "gotify": the platform cannot
        // authenticate an interaction, so the host refuses every delivery —
        // even a payload signed with the configured secret — before parsing.
        var item = await CreateWorkItemAsync();

        var resp = await PostInteractionAsync(item, sign: true);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.NotNull(question);
        Assert.Equal("open", question!.State);
        Assert.Null(question.AnswerText);
    }

    [Fact]
    public async Task UnsignedPayload_Refused_QuestionUntouched()
    {
        var item = await CreateWorkItemAsync();

        var resp = await PostInteractionAsync(item, sign: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task Capabilities_DeclaresGotifyNotificationOnly()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var renderProviders = body.GetProperty("renderProviders").EnumerateArray();
        Assert.Contains(renderProviders, p =>
            p.GetProperty("provider").GetString() == "gotify"
            && p.GetProperty("supportsInteractions").GetBoolean() == false);
    }
}

internal sealed class GotifyInteractionEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-gotify-interactions-");
    private string _dbPath => _scratch.DbPath("gotify-interactions.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }

    public GotifyInteractionEndpointFactory()
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
                // Deliberately configured but unverifiable: the plugin ships
                // no verifier because Gotify has no signed-callback scheme,
                // so the endpoint must refuse rather than fall open.
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "gotify",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "hmac-sha256",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = "CODEYBOX_TEST_GOTIFY_INTERACTION_SECRET",
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
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));

            // The render provider participates as itself so the capabilities
            // endpoint reports its honest capability through real wiring.
            services.AddSingleton<INotificationProvider>(sp =>
                new GotifyNotificationProvider(
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<ILogger<GotifyNotificationProvider>>()));
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
