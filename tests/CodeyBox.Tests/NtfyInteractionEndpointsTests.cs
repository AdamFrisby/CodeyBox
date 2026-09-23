using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Notifications;
using CodeyBox.NtfyPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
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
/// End-to-end verification of the ntfy bidirectional loop: the signed
/// callback a rendered <c>http</c> action button emits answers the bound
/// question exactly once through the shared pipeline, while tampered,
/// replayed, or stale deliveries fail cleanly before touching state.
/// ntfy holds no sandbox and no platform signature exists — the recorded
/// shape is pinned by feeding the provider's own published action
/// (body + MAC header) back through the real endpoint.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class NtfyInteractionEndpointsTests : IDisposable
{
    private const string SecretEnvVar = "CODEYBOX_TEST_NTFY_E2E_SECRET";
    private const string Secret = "test-ntfy-interaction-secret-value";
    private const string Topic = "codeybox-e2e-topic";

    private readonly NtfyEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public NtfyInteractionEndpointsTests()
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
            ProjectId = new ProjectId(NtfyEndpointFactory.ProjectId),
            Title = "ntfy test item",
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

    private static string Sign(string body) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    private async Task<HttpResponseMessage> PostNtfyAsync(string body, string? signature = null, bool tamper = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/ntfy");
        request.Headers.Add("X-CodeyBox-Signature", signature ?? Sign(body));
        var send = tamper ? body + " " : body;
        request.Content = new StringContent(send, Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    /// <summary>Runs a real notification through the registered plugin and
    /// returns the (body, signature) pair an <c>http</c> action button would
    /// carry — the exact recorded wire shape an ntfy client POSTs back.</summary>
    private async Task<(string Body, string Signature)> PublishAndExtractButtonAsync(WorkItem item, string answerLabel)
    {
        var notification = NotificationInteractionHelper.ForQuestion(
            item.Id.ToString(), "q-001", "Which approach?", [answerLabel],
            "https://codeybox.example.invalid");
        await _factory.NtfyProvider.SendAsync(notification, CancellationToken.None);

        var publish = _factory.NtfyOutbound.Requests.Last();
        using var doc = JsonDocument.Parse(publish.Body);
        var action = doc.RootElement.GetProperty("actions").EnumerateArray()
            .First(a => a.GetProperty("action").GetString() == "http");
        var body = action.GetProperty("body").GetString()!;
        var signature = action.GetProperty("headers")
            .GetProperty("X-CodeyBox-Signature").GetString()!;
        return (body, signature);
    }

    [Fact]
    public async Task PublishedButton_AnswersBoundQuestionExactlyOnce_AndClosesLoop()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var (body, signature) = await PublishAndExtractButtonAsync(item, "Use rollbacks");

        var first = await PostNtfyAsync(body, signature);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("answered", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Same button re-pressed (or redelivered): claimed, no state touch.
        var replay = await PostNtfyAsync(body, signature);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("duplicate", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", question!.State);
        Assert.Equal("Use rollbacks", question.AnswerText);
        Assert.Equal("ntfy:subscriber", question.AnsweredBy);

        // Loop-close: the provider republished over the question's sequence id.
        var update = _factory.NtfyOutbound.Requests.Last();
        using var upd = JsonDocument.Parse(update.Body);
        Assert.Equal($"codeybox-{item.Id}-q-001", upd.RootElement.GetProperty("sequence_id").GetString());
        Assert.Contains("Decided: Use rollbacks — by ntfy:subscriber",
            upd.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TamperedBody_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var (body, signature) = await PublishAndExtractButtonAsync(item, "Use rollbacks");

        var resp = await PostNtfyAsync(body, signature, tamper: true);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
        Assert.Null(question.AnswerText);
    }

    [Fact]
    public async Task InvalidSignature_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var (body, _) = await PublishAndExtractButtonAsync(item, "Use rollbacks");

        var resp = await PostNtfyAsync(body, signature: "sha256=deadbeef");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task SignedBodyForOtherTopic_RejectedByChannelAllowlist()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        // A validly signed payload bound to a different topic than the
        // configured allowlist fails authorisation, not verification.
        var body = NtfyMessageBuilder.BuildInteractionBody(
            item.Id.ToString(), "q-001", "Use rollbacks",
            $"{item.Id}:q-001", "some-other-topic", DateTimeOffset.UtcNow);

        var resp = await PostNtfyAsync(body, Sign(body));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
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
        // Each press is a freshly minted interaction id (the publish timestamp
        // differs) so dedup does not swallow the second one.
        var (body1, sig1) = await PublishAndExtractButtonAsync(item, "Use rollbacks");
        var first = await PostNtfyAsync(body1, sig1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var body2 = NtfyMessageBuilder.BuildInteractionBody(
            item.Id.ToString(), "q-001", "Use rollbacks",
            $"{item.Id}:q-001", Topic, DateTimeOffset.UtcNow.AddMinutes(1));
        var retry = await PostNtfyAsync(body2, Sign(body2));

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var resp = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already answered", resp.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_ListsNtfyAsInteractive()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("inboundProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "ntfy"
                && p.GetProperty("scheme").GetString() == "ntfy-hmac");
        Assert.Contains(body.GetProperty("renderProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "ntfy"
                && p.GetProperty("supportsInteractions").GetBoolean());
    }

    [Fact]
    public async Task NtfyVerifier_RejectsMissingSignatureHeader()
    {
        var opts = new InteractionProviderOptions
        {
            Provider = "ntfy",
            Scheme = "ntfy-hmac",
            SigningSecretEnvVar = SecretEnvVar,
        };
        var verifier = new NtfyInteractionVerifier("ntfy", () => opts);
        var body = Encoding.UTF8.GetBytes("""{"interactionId":"x"}""");

        var result = await verifier.VerifyAsync(body, new Dictionary<string, string>(), CancellationToken.None);

        Assert.False(result.Valid);
        Assert.Contains("signature", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NtfyVerifier_RejectsMismatchedSignature()
    {
        var opts = new InteractionProviderOptions
        {
            Provider = "ntfy",
            Scheme = "ntfy-hmac",
            SigningSecretEnvVar = SecretEnvVar,
        };
        var verifier = new NtfyInteractionVerifier("ntfy", () => opts);
        var body = Encoding.UTF8.GetBytes("""{"interactionId":"x"}""");
        var wrong = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("other-secret"), body)).ToLowerInvariant();

        var result = await verifier.VerifyAsync(
            body, new Dictionary<string, string> { ["X-CodeyBox-Signature"] = wrong }, CancellationToken.None);

        Assert.False(result.Valid);
        Assert.Equal("signature mismatch", result.FailureReason);
    }

    [Fact]
    public async Task NtfyVerifier_AcceptsValidSignature()
    {
        var opts = new InteractionProviderOptions
        {
            Provider = "ntfy",
            Scheme = "ntfy-hmac",
            SigningSecretEnvVar = SecretEnvVar,
        };
        var verifier = new NtfyInteractionVerifier("ntfy", () => opts);
        var body = Encoding.UTF8.GetBytes("""{"interactionId":"x"}""");
        var headers = new Dictionary<string, string> { ["X-CodeyBox-Signature"] = Sign("""{"interactionId":"x"}""") };

        var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

        Assert.True(result.Valid);
    }

    [Fact]
    public async Task NtfyVerifier_SignatureHeaderOverrideDoesNotApply()
    {
        // The plugin mints buttons with the contract header and cannot see
        // this host's options, so a SignatureHeader override must not change
        // which header the ntfy verifier reads.
        var opts = new InteractionProviderOptions
        {
            Provider = "ntfy",
            Scheme = "ntfy-hmac",
            SigningSecretEnvVar = SecretEnvVar,
            SignatureHeader = "X-Custom-Signature",
        };
        var verifier = new NtfyInteractionVerifier("ntfy", () => opts);
        var body = """{"interactionId":"x"}""";
        var headers = new Dictionary<string, string> { ["X-CodeyBox-Signature"] = Sign(body) };

        var result = await verifier.VerifyAsync(
            Encoding.UTF8.GetBytes(body), headers, CancellationToken.None);

        Assert.True(result.Valid);
    }
}

internal sealed class NtfyEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project-ntfy";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-ntfy-");
    private string _dbPath => _scratch.DbPath("ntfy.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public CapturingHttpHandler NtfyOutbound { get; } = new();
    public NtfyNotificationProvider NtfyProvider { get; }

    public NtfyEndpointFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        QuestionStore = new SqliteWorkItemQuestionStore(_dbPath);
        NtfyProvider = new NtfyNotificationProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:Plugins:codeybox.ntfy:Enabled"] = "true",
                    ["CodeyBox:Plugins:codeybox.ntfy:BaseUrl"] = "https://ntfy.example.invalid",
                    ["CodeyBox:Plugins:codeybox.ntfy:DefaultTopic"] = "codeybox-e2e-topic",
                    ["CodeyBox:Plugins:codeybox.ntfy:InteractionSecretEnvVar"] = "CODEYBOX_TEST_NTFY_E2E_SECRET",
                    ["CodeyBox:Plugins:codeybox.ntfy:PublicBaseUrl"] = "https://codeybox.example.invalid",
                })
                .Build(),
            new HttpClient(NtfyOutbound),
            NullLogger<NtfyNotificationProvider>.Instance);
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
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "ntfy",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "ntfy-hmac",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = "CODEYBOX_TEST_NTFY_E2E_SECRET",
                ["CodeyBox:Notifications:Interactions:Providers:0:AllowedChannels:0"] = "codeybox-e2e-topic",
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
                    DisplayName = "ntfy Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));

            services.AddSingleton<INotificationProvider>(NtfyProvider);
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
