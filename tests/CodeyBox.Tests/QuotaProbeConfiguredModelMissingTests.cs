using System.Net;
using System.Net.Http;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// When the configured <see cref="AgentMembership.ModelId"/> is not present in
/// a quota probe response, the probe must report <c>AvailablePct = -1</c> with
/// a diagnostic <c>Notes</c> string so the router falls onto its
/// <c>QuotaUnknownPolicy</c>. Otherwise a typoed model id silently falls open
/// to the global "most-constrained across visible models" answer — which is
/// ~100% when the visible buckets happen to be full, giving zero gating signal
/// for the model we'd actually invoke.
/// </summary>
public sealed class QuotaProbeConfiguredModelMissingTests
{
    private const string GeminiResponseFlashLitePro = """
        {
          "buckets": [
            {"modelId":"gemini-2.5-flash","remainingFraction":1.0,"resetTime":"2026-05-10T20:00:00Z","tokenType":"REQUESTS"},
            {"modelId":"gemini-2.5-flash-lite","remainingFraction":0.42,"resetTime":"2026-05-10T20:00:00Z","tokenType":"REQUESTS"},
            {"modelId":"gemini-2.5-pro","remainingFraction":1.0,"resetTime":"2026-05-10T20:00:00Z","tokenType":"REQUESTS"},
            {"modelId":"gemini-3.1-flash-lite","remainingFraction":1.0,"resetTime":"2026-05-10T20:00:00Z","tokenType":"REQUESTS"}
          ]
        }
        """;

    // ── Gemini ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_ConfiguredModelMissingFromBuckets_ReportsUnknownWithDiagnosticNotes()
    {
        // Repros the bug: operator set ModelId = "gemini-3-flash-preview" (a typo
        // for "gemini-3.1-flash-lite"). Buckets list the four real models. The
        // pre-fix code reported global mostConstrained ≈ 100%.
        var probe = BuildGeminiProbe(GeminiResponseFlashLitePro);

        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-3-flash-preview",
            QualityScore = 95,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("not in quota response", snapshot.Notes ?? "");
        Assert.Contains("gemini-3-flash-preview", snapshot.Notes ?? "");
        Assert.Contains("gemini-2.5-flash", snapshot.Notes ?? "");
    }

    [Fact]
    public async Task Gemini_ConfiguredModelPresentInBuckets_UsesParsedQuota()
    {
        var probe = BuildGeminiProbe(GeminiResponseFlashLitePro);

        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-2.5-flash-lite",
            QualityScore = 95,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        // ParseResponse populates perModel; the gate finds gemini-2.5-flash-lite
        // present and leaves the snapshot alone. AvailablePct stays at the
        // mostConstrained value (42%, from gemini-2.5-flash-lite).
        Assert.Equal(42, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("gemini-2.5-flash-lite"));
        Assert.Null(snapshot.Notes);
    }

    [Fact]
    public async Task Gemini_NoConfiguredModelId_PreservesGlobalMostConstrained()
    {
        var probe = BuildGeminiProbe(GeminiResponseFlashLitePro);

        // ModelId-less member: probe behaviour matches the pre-fix global cap.
        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            QualityScore = 95,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(42, snapshot.AvailablePct);
        Assert.Null(snapshot.Notes);
    }

    [Fact]
    public async Task Gemini_EmptyBuckets_StillReportsUnknownWithOriginalNotes()
    {
        // When the response shape itself is unknown (e.g. empty buckets), don't
        // override the existing notes with a "model not found" message.
        var probe = BuildGeminiProbe("""{"buckets":[]}""");

        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "anything",
            QualityScore = 95,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("no buckets", snapshot.Notes ?? "");
        Assert.DoesNotContain("not in quota response", snapshot.Notes ?? "");
    }

    [Fact]
    public async Task Gemini_MissingModelLog_FiresOncePerTokenAndModelCombo()
    {
        var sink = new CountingLoggerSink();
        var probe = BuildGeminiProbe(GeminiResponseFlashLitePro, sink, cacheTtl: TimeSpan.FromMinutes(5));

        var typoMember = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            ModelId = "gemini-3-flash-preview",
            QualityScore = 95,
        };
        var otherTypoMember = typoMember with { ModelId = "gemini-3-flash-preview-typo-2" };

        await probe.GetAvailabilityAsync(typoMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(typoMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(otherTypoMember, CancellationToken.None);

        // One log per distinct ModelId; the repeat call for the first typo is suppressed.
        Assert.Equal(2, sink.MissingModelLogCount);
    }

    // ── Claude ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claude_ConfiguredModelMissingFromResponse_ReportsUnknownWithDiagnosticNotes()
    {
        // Claude's rollup shape with only top-level rate_limit (no per-model
        // buckets). A configured ModelId that isn't surfaced should fall through
        // to the unknown path rather than using the overall cap.
        var body = """{"rate_limit":{"primary_window":{"used_percent":10}}}""";
        var probe = BuildClaudeProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            ModelId = "claude-some-typo",
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("claude-some-typo", snapshot.Notes ?? "");
        Assert.Contains("not in quota response", snapshot.Notes ?? "");
    }

    [Fact]
    public async Task Claude_ConfiguredModelPresentInPerModel_UsesParsedQuota()
    {
        // Flat shape: ParseResponse populates perModel with claude-opus-4-7,
        // claude-sonnet-4-6, claude-haiku-4-5. A configured ModelId that matches
        // is left alone.
        var body = """
        {
          "five_hour": {"utilization":10,"resets_at":"2026-05-13T12:00:00Z"},
          "seven_day": {"utilization":40,"resets_at":"2026-05-14T12:00:00Z"}
        }
        """;
        var probe = BuildClaudeProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            ModelId = "claude-opus-4-7",
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(snapshot.AvailablePct >= 0);
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-7"));
        Assert.Null(snapshot.Notes);
    }

    [Fact]
    public async Task Claude_NoConfiguredModelId_PreservesOverallAvailability()
    {
        var body = """{"rate_limit":{"primary_window":{"used_percent":30}}}""";
        var probe = BuildClaudeProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(70, snapshot.AvailablePct);
        Assert.Null(snapshot.Notes);
    }

    // ── Codex ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_ConfiguredModelMissingFromResponse_ReportsUnknownWithDiagnosticNotes()
    {
        // Overall account quota only — no additional_rate_limits, so perModel
        // is empty. A configured ModelId yields -1 with a diagnostic note.
        var body = """{"rate_limit":{"primary_window":{"used_percent":20}}}""";
        var probe = BuildCodexProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            ModelId = "gpt-typo-5.5",
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("gpt-typo-5.5", snapshot.Notes ?? "");
        Assert.Contains("not in quota response", snapshot.Notes ?? "");
    }

    [Fact]
    public async Task Codex_ConfiguredModelPresentViaAlias_UsesParsedQuota()
    {
        // GPT-5.3-Codex-Spark aliases to gpt-5.5 (Codex's DefaultRoutedModelId).
        // A member configured as gpt-5.5 should resolve via the alias map.
        var body = """
        {
          "rate_limit": {"primary_window":{"used_percent":30}},
          "additional_rate_limits": [
            {"limit_name":"GPT-5.3-Codex-Spark","rate_limit":{"primary_window":{"used_percent":50}}}
          ]
        }
        """;
        var probe = BuildCodexProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            ModelId = CodexQuotaProbe.DefaultRoutedModelId,
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(snapshot.AvailablePct >= 0);
        Assert.True(snapshot.PerModel.ContainsKey(CodexQuotaProbe.DefaultRoutedModelId));
        Assert.Null(snapshot.Notes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GeminiQuotaProbe BuildGeminiProbe(string body, CountingLoggerSink? sink = null, TimeSpan? cacheTtl = null)
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, body, _ => { });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        ILogger<GeminiQuotaProbe> log = sink is null
            ? NullLogger<GeminiQuotaProbe>.Instance
            : new SinkLogger<GeminiQuotaProbe>(sink);
        return new GeminiQuotaProbe(
            factory,
            () => new AgentQuotaCredentials("test-token"),
            cacheTtl ?? TimeSpan.FromMinutes(1),
            log);
    }

    private static ClaudeQuotaProbe BuildClaudeProbe(string body)
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, body, _ => { });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new ClaudeQuotaProbe(
            factory,
            token: "test-token",
            cacheTtl: TimeSpan.FromMinutes(1),
            NullLogger<ClaudeQuotaProbe>.Instance);
    }

    private static CodexQuotaProbe BuildCodexProbe(string body)
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, body, _ => { });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new CodexQuotaProbe(
            factory,
            token: "test-token",
            cacheTtl: TimeSpan.FromMinutes(1),
            NullLogger<CodexQuotaProbe>.Instance);
    }

    private sealed class CountingLoggerSink
    {
        public int MissingModelLogCount { get; private set; }

        public void Record(LogLevel level, string message)
        {
            if (level == LogLevel.Information && message.Contains("not in response buckets", StringComparison.Ordinal))
                MissingModelLogCount++;
        }
    }

    private sealed class SinkLogger<T> : ILogger<T>
    {
        private readonly CountingLoggerSink _sink;
        public SinkLogger(CountingLoggerSink sink) { _sink = sink; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Record(logLevel, formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
