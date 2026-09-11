using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.OpencodeGoQuotaPlugin;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the out-of-tree opencode-go quota plugin: payload parsing
/// (including the used-vs-remaining direction), window aggregation, status
/// gating, member claiming, and transient-vs-permanent failure classification.
/// </summary>
public sealed class OpencodeGoQuotaPluginTests
{
    private const string CapturedPayload =
        """{"usage":{"rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-07T06:42:12.975Z"},"weekly":{"status":"ok","percent":0,"resetsAt":"2026-09-14T00:00:00.975Z"},"monthly":{"status":"ok","percent":0,"resetsAt":"2026-09-26T04:00:22.975Z"}}}""";

    private const string ZenBaseUrl = "https://opencode.ai/zen/go/v1";

    private static AgentMembership OpencodeMember(string? modelId) => new()
    {
        Agent = AgentKind.Opencode,
        Billing = AgentBilling.Subscription,
        ModelId = modelId,
        QualityScore = 90,
    };

    private static AgentMembership CopilotMember(string? modelId = "opencode-go/deepseek-v4-pro") => new()
    {
        Agent = AgentKind.Copilot,
        Billing = AgentBilling.Subscription,
        ModelId = modelId,
        QualityScore = 80,
    };

    private static IConfiguration BuildConfig(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static IConfiguration ZenCopilotConfig(params (string Key, string? Value)[] extra) =>
        BuildConfig(
            new (string Key, string? Value)[] { ("CodeyBox:Copilot:Provider:BaseUrl", ZenBaseUrl) }
                .Concat(extra).ToArray());

    private static OpencodeGoQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        IConfiguration config,
        ILogger<OpencodeGoQuotaProbe>? logger = null,
        TimeProvider? time = null,
        string? token = "test-key") =>
        new(
            new QuotaFakeHttpClientFactory("agent-quota", handler),
            config,
            logger,
            member => new AgentQuotaCredentials(token),
            time);

    private static HttpMessageHandler OkHandler(string body) =>
        new SequenceHandler((HttpStatusCode.OK, body));

    // ── Parser: captured payload ────────────────────────────────────────────

    [Fact]
    public void Parse_CapturedPayload_ReadsAllThreeWindows()
    {
        var snapshot = OpencodeGoUsageParser.Parse(CapturedPayload);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(100, snapshot.AvailablePct);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal<string>(
            new[] { "monthly", "rolling", "weekly" },
            snapshot.Windows.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var window in snapshot.Windows)
            Assert.Equal(100, window.AvailablePct);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-07T06:42:12.975Z"),
            snapshot.Windows.Single(w => w.Name == "rolling").ResetAt);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-14T00:00:00.975Z"),
            snapshot.Windows.Single(w => w.Name == "weekly").ResetAt);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-26T04:00:22.975Z"),
            snapshot.Windows.Single(w => w.Name == "monthly").ResetAt);
    }

    [Fact]
    public void Parse_PercentIsFractionUsed_FullDrainReadsZero()
    {
        // Direction guard: percent is the fraction USED, so a fully consumed
        // plan (percent=100) must read 0% available — never 100%.
        var drained =
            """{"usage":{"rolling":{"status":"ok","percent":100,"resetsAt":"2026-09-07T06:42:12.975Z"},"weekly":{"status":"ok","percent":100,"resetsAt":"2026-09-14T00:00:00.975Z"},"monthly":{"status":"ok","percent":100,"resetsAt":"2026-09-26T04:00:22.975Z"}}}""";

        var snapshot = OpencodeGoUsageParser.Parse(drained);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(0, snapshot.AvailablePct);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.AvailablePct));
    }

    [Fact]
    public void Parse_SnapshotIsMinimumAcrossWindows_ResetFromBindingWindow()
    {
        var mixed =
            """{"usage":{"rolling":{"status":"ok","percent":20,"resetsAt":"2026-09-07T06:42:12.975Z"},"weekly":{"status":"ok","percent":90,"resetsAt":"2026-09-14T00:00:00.975Z"},"monthly":{"status":"ok","percent":50,"resetsAt":"2026-09-26T04:00:22.975Z"}}}""";

        var snapshot = OpencodeGoUsageParser.Parse(mixed);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(10, snapshot.AvailablePct);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-14T00:00:00.975Z"),
            snapshot.ResetAt);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("exhausted")]
    [InlineData("error")]
    public void Parse_NonOkWindowStatus_DoesNotYieldConfidentReading(string status)
    {
        var body = "{\"usage\":{\"rolling\":{\"status\":\"ok\",\"percent\":0,\"resetsAt\":\"2026-09-07T06:42:12.975Z\"},"
            + "\"weekly\":{\"status\":\"" + status + "\",\"percent\":0,\"resetsAt\":\"2026-09-14T00:00:00.975Z\"},"
            + "\"monthly\":{\"status\":\"ok\",\"percent\":0,\"resetsAt\":\"2026-09-26T04:00:22.975Z\"}}}";

        var snapshot = OpencodeGoUsageParser.Parse(body);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
    }

    [Fact]
    public void Parse_MissingWindow_DoesNotInventAvailability()
    {
        var body =
            """{"usage":{"rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-07T06:42:12.975Z"},"monthly":{"status":"ok","percent":0,"resetsAt":"2026-09-26T04:00:22.975Z"}}}""";

        var snapshot = OpencodeGoUsageParser.Parse(body);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
    }

    [Fact]
    public void Parse_InvalidJson_IsPermanentUnknown()
    {
        var snapshot = OpencodeGoUsageParser.Parse("not json");

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
    }

    // ── Handles ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("opencode-go/deepseek-v4-pro")]
    [InlineData("OpenCode-Go/some-model")]
    public void Handles_ClaimsOpencodeGoMembers(string? modelId)
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        Assert.True(probe.Handles(AgentQuotaMemberKey.From(OpencodeMember(modelId))));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-4-7")]
    [InlineData("gpt-5.5")]
    public void Handles_DoesNotClaimNonGoOpencodeModels(string modelId)
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        Assert.False(probe.Handles(AgentQuotaMemberKey.From(OpencodeMember(modelId))));
    }

    [Fact]
    public void Handles_DoesNotClaimOtherAgents()
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            ZenCopilotConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        var claude = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 90,
        };

        Assert.False(probe.Handles(AgentQuotaMemberKey.From(claude)));
    }

    [Theory]
    [InlineData("https://opencode.ai/zen/go/v1")]
    [InlineData("https://opencode.ai/zen/go/v1/")]
    [InlineData("https://opencode.ai/zen/go/v2")]
    public void Handles_ClaimsCopilotWhenByokPointsAtZen(string baseUrl)
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(("CodeyBox:Copilot:Provider:BaseUrl", baseUrl)),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        Assert.True(probe.Handles(AgentQuotaMemberKey.From(CopilotMember())));
    }

    [Theory]
    [InlineData("https://other-provider.example.com/v1")]
    [InlineData("https://opencode.ai.evil.example.com/zen/go/v1")]
    [InlineData("https://opencode.ai/zen/v1")]
    [InlineData(null)]
    public void Handles_DoesNotClaimCopilotOnOtherProviders(string? baseUrl)
    {
        (string Key, string? Value)[] values = baseUrl is null
            ? Array.Empty<(string Key, string? Value)>()
            : new (string Key, string? Value)[] { ("CodeyBox:Copilot:Provider:BaseUrl", baseUrl) };
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(values),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        Assert.False(probe.Handles(AgentQuotaMemberKey.From(CopilotMember())));
    }

    [Fact]
    public void Handles_SpecificClaimBeatsBroadClaimWithoutConflict()
    {
        // The plugin narrows to opencode-go models, so against a kind-wide
        // opencode probe it wins deterministically instead of conflicting.
        var plugin = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));
        var broad = new BroadOpencodeProbe();
        var probes = AgentQuotaProbeCatalog.BuildSubscriptionProbes(
            new IAgentQuotaProbe[] { broad, plugin });

        var resolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, OpencodeMember("opencode-go/deepseek-v4-pro"), NullLogger.Instance);

        Assert.Same(plugin, resolution.Probe);
        Assert.Null(resolution.Conflict);
    }

    // ── Fetch: happy path ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAvailability_KnownReading_DerivesUsagePathFromBaseUrl()
    {
        var captured = new CapturingHandler(HttpStatusCode.OK, CapturedPayload);
        var config = ZenCopilotConfig(
            ("CodeyBox:Plugins:codeybox.opencode-go-quota:ProviderBaseUrl", "https://opencode.ai/zen/go/v1/"));
        var probe = BuildProbe(captured, config);

        var snapshot = await probe.GetAvailabilityAsync(CopilotMember(), CancellationToken.None);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(100, snapshot.AvailablePct);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.NotNull(captured.SeenRequest);
        Assert.Equal(
            "https://opencode.ai/zen/go/v1/usage",
            captured.SeenRequest!.RequestUri!.ToString());
        Assert.Equal(
            "Bearer",
            captured.SeenRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", captured.SeenRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetAvailability_CachesWithinTtl_AndRefetchesAfterExpiry()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, CapturedPayload),
            (HttpStatusCode.OK, CapturedPayload));
        var config = ZenCopilotConfig(
            ("CodeyBox:Plugins:codeybox.opencode-go-quota:CacheTtlSeconds", "60"));
        var probe = BuildProbe(handler, config, time: time);

        await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        time.Advance(TimeSpan.FromSeconds(31));
        await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);
        Assert.Equal(2, handler.CallCount);
    }

    // ── Fetch: failure classification ───────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetAvailability_ServerSideFailure_IsTransientUnknown(HttpStatusCode status)
    {
        var probe = BuildProbe(new SequenceHandler((status, "")), ZenCopilotConfig());

        var snapshot = await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }

    [Fact]
    public async Task GetAvailability_TransportError_IsTransientUnknown()
    {
        var probe = BuildProbe(new ThrowingHandler(new HttpRequestException("boom")), ZenCopilotConfig());

        var snapshot = await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }

    [Fact]
    public async Task GetAvailability_Timeout_IsTransientUnknown()
    {
        var config = ZenCopilotConfig(
            ("CodeyBox:Plugins:codeybox.opencode-go-quota:TimeoutSeconds", "0.1"));
        var probe = BuildProbe(new NeverHandler(), config);

        var snapshot = await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetAvailability_AuthFailure_IsPermanentUnknown(HttpStatusCode status)
    {
        var probe = BuildProbe(new SequenceHandler((status, "")), ZenCopilotConfig());

        var snapshot = await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
    }

    [Fact]
    public async Task GetAvailability_MissingCredential_IsNoCredentialUnknown()
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials(null));

        var snapshot = await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.NoCredential, snapshot.Unknown);
    }

    [Fact]
    public async Task GetAvailability_SustainedFailures_EscalateToWarning()
    {
        var logger = new CapturingLogger<OpencodeGoQuotaProbe>();
        var handler = new SequenceHandler(
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""));
        var config = ZenCopilotConfig(
            ("CodeyBox:Plugins:codeybox.opencode-go-quota:SustainedFailureWarningThreshold", "2"));
        var probe = BuildProbe(handler, config, logger);

        await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);

        await probe.GetAvailabilityAsync(OpencodeMember(null), CancellationToken.None);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("2 consecutive", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kind_IsOpencode()
    {
        var probe = new OpencodeGoQuotaProbe(
            new QuotaFakeHttpClientFactory("agent-quota", OkHandler(CapturedPayload)),
            BuildConfig(),
            NullLogger<OpencodeGoQuotaProbe>.Instance,
            _ => new AgentQuotaCredentials("test-key"));

        Assert.Equal(AgentKind.Opencode, probe.Kind);
    }

    // ── Test helpers ────────────────────────────────────────────────────────

    private sealed class BroadOpencodeProbe : IAgentQuotaProbe
    {
        public AgentKind Kind => AgentKind.Opencode;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            _ = member;
            _ = ct;
            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = 50 });
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? SeenRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            SeenRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _error;

        public ThrowingHandler(Exception error) => _error = error;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => Task.FromException<HttpResponseMessage>(_error);
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
