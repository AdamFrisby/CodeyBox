using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.DiscordPlugin;
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
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end verification of the Discord bidirectional loop: a signed
/// MESSAGE_COMPONENT delivery answers the bound question exactly once
/// through the shared pipeline and is acknowledged in-band (Discord
/// requires the acknowledgement in the HTTP response itself), while the
/// PING registration challenge, tampered, replayed, or stale deliveries
/// behave as the contract requires.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class DiscordInteractionEndpointsTests : IDisposable
{
    private const string SecretEnvVar = "CODEYBOX_TEST_DISCORD_PUBLIC_KEY";
    private const string TokenEnvVar = "CODEYBOX_TEST_DISCORD_E2E_BOT_TOKEN";
    private const string Channel = "1100000000000000001";

    // Fixed Ed25519 seed so the keypair (and every signature) is
    // deterministic across runs.
    private static readonly byte[] PrivateSeed = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private readonly DiscordEndpointFactory _factory = new();
    private readonly HttpClient _client;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public DiscordInteractionEndpointsTests()
    {
        var privateKey = new Ed25519PrivateKeyParameters(PrivateSeed, 0);
        _privateKey = privateKey;
        Environment.SetEnvironmentVariable(SecretEnvVar, PublicHex(privateKey));
        Environment.SetEnvironmentVariable(TokenEnvVar, "test-bot-token");
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        Environment.SetEnvironmentVariable(TokenEnvVar, null);
    }

    private static string PublicHex(Ed25519PrivateKeyParameters privateKey)
    {
        var publicKey = privateKey.GeneratePublicKey();
        return Convert.ToHexString(publicKey.GetEncoded()).ToLowerInvariant();
    }

    private static string Sign(Ed25519PrivateKeyParameters privateKey, string timestamp, byte[] body)
    {
        var message = Encoding.ASCII.GetBytes(timestamp).Concat(body).ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
    }

    private async Task<WorkItem> CreateWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(DiscordEndpointFactory.ProjectId),
            Title = "Discord test item",
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

    private static string ComponentEnvelope(WorkItem item, string interactionId, string answer, string channel = Channel)
    {
        var customId = DiscordButtonCodec.Encode(item.Id.ToString(), "q-001", answer);
        Assert.NotNull(customId);
        return """{"type":3,"id":"ID","application_id":"APP","channel_id":"CHANNEL","guild_id":"G1","data":{"component_type":2,"custom_id":"CUSTOM"},"member":{"user":{"id":"U123","username":"alice"}},"token":"tok","message":{"id":"M1","channel_id":"CHANNEL"},"version":1}"""
            .Replace("ID", interactionId, StringComparison.Ordinal)
            .Replace("CHANNEL", channel, StringComparison.Ordinal)
            .Replace("CUSTOM", customId, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> PostDiscordAsync(string jsonBody, string? timestamp = null, bool tamper = false)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes(jsonBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/interactions/discord");
        request.Headers.Add("X-Signature-Ed25519", Sign(_privateKey, ts, body));
        request.Headers.Add("X-Signature-Timestamp", ts);
        var send = tamper ? [.. body, (byte)' '] : body;
        request.Content = new ByteArrayContent(send);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await _client.SendAsync(request);
    }

    private static async Task<string> ContentOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("content").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task ValidSignedComponent_AnswersBoundQuestionExactlyOnce()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);
        var envelope = ComponentEnvelope(item, "9000000000000000001", "Use rollbacks");

        var first = await PostDiscordAsync(envelope);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, firstJson.GetProperty("type").GetInt32());
        var content = firstJson.GetProperty("data").GetProperty("content").GetString() ?? string.Empty;
        Assert.Contains("Decided:", content, StringComparison.Ordinal);
        Assert.Contains("Use rollbacks", content, StringComparison.Ordinal);
        Assert.Contains("discord:U123 (alice)", content, StringComparison.Ordinal);

        var replay = await PostDiscordAsync(envelope);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("Already recorded", await ContentOf(replay), StringComparison.Ordinal);

        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", question!.State);
        Assert.Equal("Use rollbacks", question.AnswerText);
        Assert.Equal("discord:U123 (alice)", question.AnsweredBy);
    }

    [Fact]
    public async Task PingChallenge_AnsweredImmediately()
    {
        var ping = """{"type":1,"id":"p1","application_id":"APP","version":1}""";

        var response = await PostDiscordAsync(ping);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task TamperedBody_RejectedBeforeSemanticProcessing()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostDiscordAsync(ComponentEnvelope(item, "9000000000000000002", "Use rollbacks"), tamper: true);

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

        var resp = await PostDiscordAsync(
            ComponentEnvelope(item, "9000000000000000003", "Use rollbacks"), timestamp: oldTs);

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

        var first = await PostDiscordAsync(ComponentEnvelope(item, "9000000000000000004", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var retry = await PostDiscordAsync(ComponentEnvelope(item, "9000000000000000005", "Use rollbacks"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Contains("Already answered", await ContentOf(retry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlistedChannel_FailsCleanlyWithReason()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item);

        var resp = await PostDiscordAsync(
            ComponentEnvelope(item, "9000000000000000006", "Use rollbacks", channel: "9999999999999999999"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("not authorised", await ContentOf(resp), StringComparison.Ordinal);
        var question = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("open", question!.State);
    }

    [Fact]
    public async Task Capabilities_ListsDiscordAsInteractive()
    {
        var resp = await _client.GetAsync("/webhooks/interactions/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("inboundProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "discord");
        Assert.Contains(body.GetProperty("renderProviders").EnumerateArray(),
            p => p.GetProperty("provider").GetString() == "discord"
                && p.GetProperty("supportsInteractions").GetBoolean());
    }
}

internal sealed class DiscordEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project-discord";

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-discord-");
    private string _dbPath => _scratch.DbPath("discord.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public CapturingHttpHandler DiscordOutbound { get; } = new();

    public DiscordEndpointFactory()
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
                ["CodeyBox:Notifications:Interactions:Providers:0:Provider"] = "discord",
                ["CodeyBox:Notifications:Interactions:Providers:0:Scheme"] = "discord-ed25519",
                ["CodeyBox:Notifications:Interactions:Providers:0:SigningSecretEnvVar"] = "CODEYBOX_TEST_DISCORD_PUBLIC_KEY",
                ["CodeyBox:Notifications:Interactions:Providers:0:ReplayWindow"] = "00:05:00",
                ["CodeyBox:Notifications:Interactions:Providers:0:AllowedChannels:0"] = "1100000000000000001",
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
                    DisplayName = "Discord Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));

            var outbound = DiscordOutbound;
            services.AddSingleton<INotificationProvider>(_ =>
                new DiscordNotificationProvider(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["CodeyBox:Plugins:codeybox.discord:Enabled"] = "true",
                            ["CodeyBox:Plugins:codeybox.discord:BotTokenEnvVar"] = "CODEYBOX_TEST_DISCORD_E2E_BOT_TOKEN",
                            ["CodeyBox:Plugins:codeybox.discord:DefaultChannelId"] = "1100000000000000001",
                        })
                        .Build(),
                    new HttpClient(outbound),
                    NullLogger<DiscordNotificationProvider>.Instance));
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
