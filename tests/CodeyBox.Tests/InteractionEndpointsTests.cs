using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the generic inbound interaction loop:
/// verify-first, idempotent answering through the shared pipeline,
/// clean stale-button failures, platform identity in answeredBy, and
/// honest degradation on notification-only providers.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class InteractionEndpointsTests : IDisposable
{
    private const string SecretEnvVar = "CODEYBOX_TEST_INTERACTION_SECRET";
    private const string Secret = "test-interaction-signing-secret-value";
    private const string ChatUrlEnvVar = "CODEYBOX_TEST_INTERACTION_CHAT_URL";

    private readonly InteractionEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public InteractionEndpointsTests()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        Environment.SetEnvironmentVariable(ChatUrlEnvVar, null);
    }

    private async Task<WorkItem> CreateWorkItemAsync(WorkItemState state = WorkItemState.NeedsOperatorInput)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(InteractionEndpointFactory.ProjectId),
            Title = "Test item",
            Prompt = "do something",
            State = state,
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

    private static string Sign(byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> PostInteractionAsync(object payload, bool sign = true, bool tamper = false)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/generic");
        if (sign)
        {
            request.Headers.Add("X-CodeyBox-Signature", Sign(body));
            request.Headers.Add("X-CodeyBox-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        request.Content = new ByteArrayContent(tamper ? [.. body, (byte)' '] : body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await _client.SendAsync(request);
    }

    private static object Payload(
        WorkItem item,
        string interactionId,
        string questionId = "q-001",
        string answer = "Use approach B.",
        string userId = "U123",
        string? login = "alice",
        string? correlationToken = null) => new
        {
            interactionId,
            workItemId = item.Id.ToString(),
            questionId,
            answer,
            user = new { userId, login },
            channelId = "C999",
            correlationToken,
        };

    [Fact]
    public async Task UnverifiedInteraction_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostInteractionAsync(Payload(item, "i-unverified-1"), sign: false);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.NotNull(question);
        Assert.Equal("open", question!.State);
        Assert.Null(question.AnswerText);
    }

    [Fact]
    public async Task TamperedBody_RejectedAsUnverified()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostInteractionAsync(Payload(item, "i-tampered-1"), tamper: true);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task DuplicateDelivery_AnswersExactlyOnce()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var first = await PostInteractionAsync(Payload(item, "i-dup-1", answer: "First answer."));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("answered", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var second = await PostInteractionAsync(Payload(item, "i-dup-1", answer: "Second answer."));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("duplicate", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", question!.State);
        Assert.Equal("First answer.", question.AnswerText);
    }

    [Fact]
    public async Task AlreadyAnsweredQuestion_FailsCleanlyWithReason()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");
        await CreateQuestionAsync(item, "q-002");

        var first = await PostInteractionAsync(Payload(item, "i-answered-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Fresh interaction id so dedup does not mask the stale-state check;
        // the second question keeps the work item awaiting input.
        var retry = await PostInteractionAsync(Payload(item, "i-answered-2"));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already answered", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupersededWorkItem_FailsCleanlyWithReason()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        // Resolve the only question through the REST path so the work item
        // leaves NeedsOperatorInput (the button is now stale).
        var answer = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "Decided elsewhere." });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        var stale = await PostInteractionAsync(Payload(item, "i-stale-1"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no longer", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MismatchedCorrelationToken_FailsAsStale()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostInteractionAsync(
            Payload(item, "i-corr-1", correlationToken: "some-other-item:q-999"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task PlatformIdentity_ReachesAnsweredByInAuditableForm()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostInteractionAsync(Payload(item, "i-identity-1", userId: "U123", login: "alice"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("generic:U123 (alice)", question!.AnsweredBy);
    }

    [Fact]
    public async Task UnknownProvider_Returns404WithoutProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/nope");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Capabilities_DeclaresProviderSupport()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Contains(body.GetProperty("renderProviders").EnumerateArray(),
            p => p.GetProperty("supportsInteractions").GetBoolean() == false);
    }

    [Fact]
    public async Task NotificationOnlyProvider_SurfacesAnswerLink()
    {
        Environment.SetEnvironmentVariable(ChatUrlEnvVar, "https://chat.example.invalid/hook");
        var handler = new CapturingHttpHandler();
        var provider = new ChatNotificationProvider(
            new ChatProviderOptions
            {
                Enabled = true,
                Webhooks = [new ChatWebhookOptions { Platform = ChatPlatform.Slack, UrlEnvVar = ChatUrlEnvVar }],
            },
            new HttpClient(handler),
            new CapturingLogger<ChatNotificationProvider>());

        Assert.False(provider.SupportsInteractions);
        var notification = NotificationInteractionHelper.ForQuestion(
            "w123", "q-001", "Which approach?", ["A", "B"], "https://codeybox.example.invalid");

        await provider.SendAsync(notification, CancellationToken.None);

        var posted = Assert.Single(handler.Requests);
        Assert.Contains("Answer here: https://codeybox.example.invalid/workitems/w123/questions", posted.Body);
    }

    [Fact]
    public void ProviderIgnoringActions_RendersActionableNotificationUnchanged()
    {
        var webhook = new ChatWebhookOptions { Platform = ChatPlatform.Slack };
        var plain = new Notification
        {
            ConditionId = "queue_empty",
            Title = "Queue is empty",
            Body = "All work items processed.",
            Severity = NotificationSeverity.Information,
            Timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        };
        var actionable = plain with
        {
            Actions = [new NotificationAction { Label = "A", Value = "A", WorkItemId = "w1", QuestionId = "q-001" }],
        };

        // No AnswerUrl: a provider that ignores actions renders the
        // actionable notification exactly as the plain one.
        var plainJson = JsonSerializer.Serialize(ChatNotificationProvider.BuildSlackPayload(plain, webhook));
        var actionableJson = JsonSerializer.Serialize(ChatNotificationProvider.BuildSlackPayload(actionable, webhook));
        Assert.Equal(plainJson, actionableJson);
    }

    [Fact]
    public async Task HmacVerifier_RejectsStaleTimestamp()
    {
        var opts = new InteractionProviderOptions
        {
            Provider = "generic",
            SigningSecretEnvVar = SecretEnvVar,
        };
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        var verifier = new HmacInteractionVerifier("generic", () => opts, k => Secret, clock);
        var body = Encoding.UTF8.GetBytes("""{"interactionId":"x"}""");
        var oldTs = new DateTimeOffset(2026, 9, 18, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string>
        {
            ["X-CodeyBox-Signature"] = Sign(body),
            ["X-CodeyBox-Timestamp"] = oldTs,
        };

        var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

        Assert.False(result.Valid);
        Assert.Contains("replay", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class InteractionEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-interactions-");
    private string _dbPath => _scratch.DbPath("interactions.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }

    public InteractionEndpointFactory()
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
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "generic",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "hmac-sha256",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = SecretEnvVar,
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
        });
    }

    private const string SecretEnvVar = "CODEYBOX_TEST_INTERACTION_SECRET";

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
