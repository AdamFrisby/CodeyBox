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
/// a quota probe response, every probe (Claude, Cursor, Codex) falls back to
/// the overall/binding-window reading (a KNOWN snapshot) so the floor is
/// enforced normally rather than degrading to Unknown (which would fail open
/// past an explicit reserve, or — under MostQuotaFirst — sort the class below
/// its known-quota peers and starve it). The fallback matters most for a newly
/// released model the backend has not yet minted a dedicated bucket for
/// (e.g. gpt-5.6-sol on launch day, where the WHAM response only lists
/// gpt-5.5 / GPT-5.3-Codex-Spark): OpenAI's rate limits are account-wide 5h /
/// weekly windows that cap every per-model bucket, so the overall reading IS
/// the binding quota for any configured codex model.
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
    public async Task Claude_ConfiguredModelMissingFromResponse_FallsBackToOverall()
    {
        // Claude's rollup shape with only top-level rate_limit (no per-model
        // buckets). A configured ModelId that isn't surfaced resolves to the
        // overall reading (a KNOWN snapshot) so the floor is enforced normally
        // rather than degrading to Unknown — the model still rides the overall
        // account quota. "claude-some-typo" carries no opus/sonnet/haiku
        // family token, so the family-bucket match is skipped and the overall
        // fallback is used.
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

        Assert.Equal(90, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("claude-some-typo"));
        Assert.Equal(90, snapshot.PerModel["claude-some-typo"].AvailablePct);
    }

    [Fact]
    public async Task Claude_ConfiguredModelAbsentFromPerModel_ResolvesToFamilyBucket()
    {
        // The configured model id "claude-opus-4-8" is NOT in the parser's
        // hardcoded configuredModels list, so it won't be an exact PerModel
        // key. But it carries the "opus" family token, and the response
        // includes a "seven_day_opus" family bucket at 0% — the probe must
        // resolve the model to that family bucket (a KNOWN 0% reading) rather
        // than degrading to Unknown. This is the root fix: claude reads
        // normally instead of Unknown-by-default, so the floor is enforced.
        var body = """
        {
          "five_hour": {"utilization":10,"resets_at":"2026-05-13T12:00:00Z"},
          "seven_day": {"utilization":40,"resets_at":"2026-05-14T12:00:00Z"},
          "seven_day_opus": {"utilization":100,"resets_at":"2026-05-14T12:00:00Z"}
        }
        """;
        var probe = BuildClaudeProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            ModelId = "claude-opus-4-8",
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        // Overall = min(five_hour=90, seven_day=60) = 60. The headline stays
        // the overall reading; the resolved per-model entry is the opus family
        // bucket (0%), which is what the router gates on for this member.
        Assert.Equal(60, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-8"));
        Assert.Equal(0, snapshot.PerModel["claude-opus-4-8"].AvailablePct);
    }

    [Fact]
    public async Task Claude_ConfiguredModelAbsentFromPerModel_NoFamilyBucket_ResolvesToOverall()
    {
        // "claude-opus-4-8" with no seven_day_opus bucket in the response —
        // the opus family is constrained only by the overall cap. The probe
        // resolves to the overall reading (a KNOWN snapshot) so the floor is
        // enforced normally rather than degrading to Unknown.
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
            ModelId = "claude-opus-4-8",
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        // Overall = min(90, 60) = 60. No opus bucket → overall fallback.
        Assert.Equal(60, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-8"));
        Assert.Equal(60, snapshot.PerModel["claude-opus-4-8"].AvailablePct);
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
    public async Task Codex_ConfiguredModelMissingFromResponse_NoPerModel_FallsBackToOverall()
    {
        // Overall account quota only — no additional_rate_limits, so perModel
        // is empty. A configured ModelId that isn't surfaced resolves to the
        // overall reading (a KNOWN snapshot) so the floor is enforced normally
        // rather than degrading to Unknown. used_percent=20 → 80% available.
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

        Assert.Equal(80, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("gpt-typo-5.5"));
        Assert.Equal(80, snapshot.PerModel["gpt-typo-5.5"].AvailablePct);
    }

    [Fact]
    public async Task Codex_NewModelAbsentFromPerModelBuckets_ResolvesToOverall()
    {
        // Regression for the gpt-5.6-sol launch-day gap: the WHAM response
        // enumerates only the previous-generation buckets (gpt-5.5,
        // GPT-5.3-Codex-Spark), NOT the newly released gpt-5.6-sol. Before the
        // overall fallback, the codex-xhigh class read AvailablePct=-1/Unknown
        // and — under MostQuotaFirst — sorted below claude/opencode, so Sol
        // never won a pickup despite ample headroom. It must now resolve to the
        // overall account reading and stay KNOWN. Overall primary_window
        // used_percent=29 → 71% available; per-model buckets are capped to it.
        var body = """
        {
          "rate_limit": {"primary_window":{"used_percent":29}},
          "additional_rate_limits": [
            {"limit_name":"gpt-5.5","rate_limit":{"primary_window":{"used_percent":0}}},
            {"limit_name":"GPT-5.3-Codex-Spark","rate_limit":{"primary_window":{"used_percent":0}}}
          ]
        }
        """;
        var probe = BuildCodexProbe(body);

        var member = new AgentMembership
        {
            Agent = AgentKind.Codex,
            Billing = AgentBilling.Subscription,
            ModelId = "gpt-5.6-sol",
            QualityScore = 120,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.True(snapshot.AvailablePct >= 0, "Sol must not degrade to Unknown");
        Assert.Equal(71, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("gpt-5.6-sol"));
        Assert.Equal(71, snapshot.PerModel["gpt-5.6-sol"].AvailablePct);
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
    public async Task Cursor_ConfiguredModelMissingFromResponse_FallsBackToOverall()
    {
        // percent-used dimensions drive the headline (see CursorQuotaProbe
        // HEADLINE-METRIC): max(10,10,0)=10 -> 90% available. autoBucketModels
        // populates perModel with composer-2, so the configured ModelId
        // "composer-99-unknown" doesn't match an auto-bucket key. The probe
        // resolves it to the overall reading (a KNOWN snapshot) so the floor
        // is enforced normally rather than degrading to Unknown.
        var body = """
        {
          "planUsage": { "totalPercentUsed": 10, "autoPercentUsed": 10, "apiPercentUsed": 0 },
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

        Assert.Equal(90, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("composer-99-unknown"));
        Assert.Equal(90, snapshot.PerModel["composer-99-unknown"].AvailablePct);
    }

    [Fact]
    public async Task Cursor_ConfiguredModelPresentInPerModel_UsesParsedQuota()
    {
        // max(total=30, auto=25, api=0) = 30 -> 70% overall available.
        // autoBucketModels lists composer-2.5 (DefaultRoutedModelId), and
        // autoPercentUsed=25 -> auto bucket at 75% which is then capped by
        // overall to 70%. Configured ModelId matches a populated perModel key,
        // so ApplyMemberGate leaves the snapshot intact.
        var body = """
        {
          "planUsage": { "totalPercentUsed": 30, "autoPercentUsed": 25, "apiPercentUsed": 0 },
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
        var body = """{"planUsage":{"totalPercentUsed":30}}""";
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
