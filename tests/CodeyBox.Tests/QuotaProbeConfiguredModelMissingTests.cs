using System.Net;
using System.Net.Http;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
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
    //
    // Gemini's primary signal is now the live :generateContent ping (a single
    // call for a fixed ModelId, or a fan-out for the auto sentinel) — the
    // bucket-gating ApplyMemberGate flow is no longer the source of "unknown"
    // for a typoed model id. Coverage for the typo / 404 case lives with the
    // live-call tests in GeminiQuotaProbeAutoFanOutTests; the only Gemini
    // surface still exercised here is the no-ModelId legacy fallback that
    // reaches retrieveUserQuota.

    [Fact]
    public async Task Gemini_NoConfiguredModelId_PreservesGlobalMostConstrained()
    {
        var probe = BuildGeminiProbe(GeminiResponseFlashLitePro);

        // ModelId-less member: probe falls through to retrieveUserQuota and
        // reports the overall mostConstrained pct.
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

    // ── Cursor ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cursor_ConfiguredModelMissingFromResponse_ReportsUnknownWithDiagnosticNotes()
    {
        // remaining/limit drive the headline (see CursorQuotaProbe HEADLINE-METRIC):
        // 900/1000 -> 90% available. autoBucketModels populates perModel with
        // composer-2, so the configured ModelId "composer-99-unknown" doesn't
        // match and ApplyMemberGate emits the diagnostic Notes string.
        var body = """
        {
          "planUsage": { "remaining": 900, "limit": 1000, "autoPercentUsed": 10, "apiPercentUsed": 0 },
          "autoBucketModels": ["composer-2"]
        }
        """;
        var probe = BuildCursorProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Cursor,
            Billing = AgentBilling.Subscription,
            ModelId = "composer-99-unknown",
            QualityScore = 98,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("composer-99-unknown", snapshot.Notes ?? "");
        Assert.Contains("not in quota response", snapshot.Notes ?? "");
    }

    [Fact]
    public async Task Cursor_ConfiguredModelPresentInPerModel_UsesParsedQuota()
    {
        // 700/1000 -> 70% overall available. autoBucketModels lists composer-2.5
        // (DefaultRoutedModelId), and autoPercentUsed=25 -> auto bucket at 75%
        // which is then capped by overall to 70%. Configured ModelId matches a
        // populated perModel key, so ApplyMemberGate leaves the snapshot intact.
        var body = """
        {
          "planUsage": { "remaining": 700, "limit": 1000, "autoPercentUsed": 25, "apiPercentUsed": 0 },
          "autoBucketModels": ["composer-2.5"]
        }
        """;
        var probe = BuildCursorProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Cursor,
            Billing = AgentBilling.Subscription,
            ModelId = CursorQuotaProbe.DefaultRoutedModelId,
            QualityScore = 98,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(snapshot.AvailablePct >= 0);
        Assert.True(snapshot.PerModel.ContainsKey(CursorQuotaProbe.DefaultRoutedModelId));
        Assert.Null(snapshot.Notes);
    }

    [Fact]
    public async Task Cursor_NoConfiguredModelId_PreservesOverallAvailability()
    {
        var body = """{"planUsage":{"remaining":700,"limit":1000}}""";
        var probe = BuildCursorProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Cursor,
            Billing = AgentBilling.Subscription,
            QualityScore = 98,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(70, snapshot.AvailablePct);
        Assert.Null(snapshot.Notes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GeminiQuotaProbe BuildGeminiProbe(string body, TimeSpan? cacheTtl = null)
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, body, _ => { });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new GeminiQuotaProbe(
            factory,
            () => new AgentQuotaCredentials("test-token"),
            cacheTtl ?? TimeSpan.FromMinutes(1),
            NullLogger<GeminiQuotaProbe>.Instance);
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

    private static CursorQuotaProbe BuildCursorProbe(string body)
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, body, _ => { });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new CursorQuotaProbe(
            factory,
            token: "test-token",
            cacheTtl: TimeSpan.FromMinutes(1),
            NullLogger<CursorQuotaProbe>.Instance);
    }

}
