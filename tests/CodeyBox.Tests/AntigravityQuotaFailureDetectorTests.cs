using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AntigravityQuotaFailureDetectorTests
{
    private readonly AntigravityQuotaFailureDetector _detector = new();

    [Fact]
    public void Detect_WeeklyLockoutWithStructuredReset_ParksUntilLockoutEnd()
    {
        // The AI Pro weekly cap surfaces with a structured quota_metadata.lockout_until
        // alongside human-readable text. A 7-day lockout must NOT churn the breaker;
        // it must surface the absolute reset so the work item parks WaitingForQuotaReset
        // until then.
        var lockoutUntil = DateTimeOffset.UtcNow.AddDays(6).AddHours(23);
        var stdout =
            "{\"type\":\"result\",\"status\":\"error\","
            + "\"quota_metadata\":{\"lockout_until\":\"" + lockoutUntil.ToString("o") + "\"},"
            + "\"error\":{\"message\":\"Weekly limit reached — account locked until " + lockoutUntil.ToString("o") + "\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, lockoutUntil.AddMinutes(-1), lockoutUntil.AddMinutes(1));
    }

    [Fact]
    public void Detect_ResourceExhaustedStderr_ClassifiesRateLimit()
    {
        var detection = _detector.Detect(stderr: "RESOURCE_EXHAUSTED reset after 1h17m", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_ConsumerQuota429_ParsesRelativeResetToPreciseWindow()
    {
        // agy's consumer-tier 429 reports its rolling-window reset as a RELATIVE
        // duration ("Resets in 8m14s"), not an absolute ISO timestamp. The item
        // must park in WaitingForQuotaReset with resetAt ≈ now + 494s so it
        // retries right after the window clears — not on a coarse default backoff.
        var before = DateTimeOffset.UtcNow;
        var stderr = "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";

        var detection = _detector.Detect(stderr, stdout: null);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        // 8m14s = 494s. Bracket by the wall-clock reads to stay flake-free.
        Assert.InRange(
            detection.ResetAt!.Value,
            before.AddSeconds(494),
            after.AddSeconds(494));
    }

    [Fact]
    public void Detect_CapturedTerminalRegionWithRelativeReset_ParksWithPreciseReset()
    {
        // End-to-end over the shape the runner folds into Stderr on a terminal
        // agy 429: ExtractTerminalErrorRegion slices the glog tail, then Detect
        // classifies it and parses the relative reset off the same terminal line.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";
        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);
        Assert.NotNull(region);

        var before = DateTimeOffset.UtcNow;
        var detection = _detector.Detect(stderr: region, stdout: null);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, before.AddSeconds(494), after.AddSeconds(494));
    }

    [Fact]
    public void Detect_RenderedGoogleApi429Message_ClassifiesRateLimit()
    {
        // REGRESSION (hidden 429): agy renders the cloudcode-pa / Generative
        // Language 429 as the canonical Google-API error MESSAGE — "Resource has
        // been exhausted (e.g. check quota)." — which carries neither the
        // "RESOURCE_EXHAUSTED" status token nor the phrase "quota exceeded"
        // ("check quota" != "quota exceeded"). Before this shape was recognised,
        // a real hidden 429 slipped through Detect as null and the run terminated
        // as a generic "agent exited 1" failure with no quota record / no park.
        var detection = _detector.Detect(
            stderr: "Error: Resource has been exhausted (e.g. check quota).",
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_GrpcResourceExhaustedStatus_ClassifiesRateLimit()
    {
        // The camel-case gRPC status string ("code = ResourceExhausted") does not
        // contain the screaming-snake "RESOURCE_EXHAUSTED" token, so it needs its
        // own marker to be classified rather than falling through as a generic
        // failure.
        var detection = _detector.Detect(
            stderr: "rpc error: code = ResourceExhausted desc = quota",
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void ExtractTerminalErrorRegion_RenderedGoogleApi429Message_IsSurfaced()
    {
        // The SAME marker set gates the glog->Stderr fold. A terminal agy 429 that
        // logs only the rendered Google-API message must be extracted so the
        // runner folds it into result.Stderr and the pipeline can park it — before
        // the message shape was a marker, this returned null and the fold produced
        // nothing.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "Error: Resource has been exhausted (e.g. check quota).";

        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);

        Assert.NotNull(region);
        Assert.Contains("Resource has been exhausted", region!);
        Assert.DoesNotContain("Model resolved", region);
        var detection = _detector.Detect(stderr: region, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_ResourceExhaustedWithoutResetDuration_ClassifiesWithNullReset()
    {
        // A RESOURCE_EXHAUSTED 429 that carries NO parseable reset duration must
        // still classify as a rate-limit — with a null ResetAt — so the router
        // falls back to its default backoff rather than null-crashing or silently
        // dropping the detection. (Exercises the `reset = … ?? null` fallback.)
        var detection = _detector.Detect(
            stderr: "RESOURCE_EXHAUSTED (code 429): Individual quota reached", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Null(detection.ResetAt);
    }

    [Fact]
    public void ResetParser_RelativeDuration_ComputesFromInjectedClock()
    {
        // The relative reset is now + duration off an INJECTED clock, so the parked
        // resetAt is deterministic (no wall-clock bracketing needed). "8m14s" = 494s.
        var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var reset = QuotaResetParser.TryParseResetAt(
            new[] { "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)" },
            utcNow: now);

        Assert.Equal(now.AddSeconds(494), reset);
    }

    [Fact]
    public void ResetParser_ZeroDurationPrefixBeforeRealDuration_DoesNotShadowRealWindow()
    {
        // A non-duration "resets in a moment" prefix yields an all-zero first match;
        // scanning must continue to the real "retry after 8m" later in the SAME
        // string instead of bailing to the coarse default backoff.
        var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var reset = QuotaResetParser.TryParseResetAt(
            new[] { "quota resets in a moment; retry after 8m" },
            utcNow: now);

        Assert.Equal(now.AddMinutes(8), reset);
    }

    [Fact]
    public void Detect_CleanNoOpRunWithoutQuotaMarker_ReturnsNoDetection()
    {
        // A genuine no-op — agy ran, emitted its ordinary diagnostics, and made no
        // file changes with NO 429 anywhere — must NOT be misread as a quota park.
        // It has to fall through to the "produced no changes" terminal failure.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "read 12 files, wrote 0 files\n"
            + "done";

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog));
        Assert.Null(_detector.Detect(stderr: glog, stdout: null));
    }

    [Fact]
    public void Detect_AbsoluteLockoutInPlainText_StillExtractsReset()
    {
        var when = DateTimeOffset.UtcNow.AddHours(48);
        var stderr = $"agent_error: account locked until {when:o} due to weekly cap";

        var detection = _detector.Detect(stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_AgentProseWithQuotaKeywordsInStdout_ReturnsNull()
    {
        // Self-referential false positive prevention: agent authoring quota code
        // or writing git commit messages containing "quota exceeded" or "RESOURCE_EXHAUSTED"
        // in stdout must NEVER be classified as a provider quota failure.
        const string stdout = """
            Antigravity agent starting...
            Running: git commit -m "fix(quota): handle RESOURCE_EXHAUSTED and quota exceeded errors"
            [main 1a2b3c4] fix(quota): handle RESOURCE_EXHAUSTED and quota exceeded errors
             1 file changed, 10 insertions(+)
            I have completed the task and verified that Resource has been exhausted error handling works.
            """;

        var detection = _detector.Detect(stderr: null, stdout: stdout);
        Assert.Null(detection);
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_FiltersUnstructuredProse_PreservesNdjsonErrors()
    {
        const string mixedStdout = """
            Starting agent work...
            git commit -m "fix quota exceeded handling"
            {"type":"conversation","message":"I am working on RESOURCE_EXHAUSTED handling"}
            {"event":"result","result":{"status":"ERROR","error":"Individual quota reached. Resets in 1h8m20s."}}
            Finished commit.
            """;

        var scoped = _detector.ScopeStdoutForQuotaDetection(mixedStdout);
        Assert.NotNull(scoped);
        Assert.DoesNotContain("git commit", scoped!);
        Assert.DoesNotContain("Starting agent work", scoped!);
        Assert.DoesNotContain("Finished commit", scoped!);
        Assert.DoesNotContain("type\":\"conversation", scoped!);
        Assert.Contains("Individual quota reached", scoped!);

        var detection = _detector.Detect(stderr: null, stdout: scoped);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_WhenNoNdjsonErrors_ReturnsNull()
    {
        const string stdout = """
            Fixing bug in repository...
            Running git commit -m "handle quota exceeded"
            Done!
            """;

        var scoped = _detector.ScopeStdoutForQuotaDetection(stdout);
        Assert.Null(scoped);
    }

    [Fact]
    public void Detect_Unauthorized_FlagsAsUnauthorized()
    {
        var detection = _detector.Detect(stderr: "API Error: 401 invalid token", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_GenericRuntimeError_ReturnsNull()
    {
        var stdout = """{"type":"result","status":"error","error":{"message":"compilation failed: missing semicolon"}}""";
        Assert.Null(_detector.Detect(stderr: null, stdout));
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: "", stdout: ""));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_TerminalMarkerAtEnd_ReturnsFromLastMarkerToEnd()
    {
        // The terminal 429 sits at the end of the cumulative glog; the region is the
        // slice from the last marker to end — the leading non-marker noise is dropped
        // so only the terminal cause reaches the classifiers.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "applyAuthResult: authMethod=consumer\n"
            + "RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";

        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);

        Assert.NotNull(region);
        Assert.Contains("RESOURCE_EXHAUSTED", region);
        Assert.Contains("Resets in 8m14s", region); // reset hint preserved for Detect()
        Assert.DoesNotContain("Model resolved", region!);
        // And the sliced region still classifies as a rate-limit via Detect().
        var detection = _detector.Detect(stderr: region, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void ExtractTerminalErrorRegion_MarkerOutsideTailWindow_ReturnsNull()
    {
        // A recovered-then-cleared 429 followed by lots of further work (the run then
        // ends on a markerless failure) must NOT be surfaced: the marker scrolled out
        // of the tail window, so the region is null and nothing reaches the classifiers.
        var lines = new List<string> { "RESOURCE_EXHAUSTED (code 429): recovered after retry" };
        for (var i = 0; i < 60; i++) lines.Add($"tool call {i}");
        lines.Add("Error: timed out waiting for response");

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(string.Join("\n", lines)));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_NoMarker_ReturnsNull()
    {
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(
            "Model resolved: gemini-3.5-flash\nwrote 3 files\ndone"));
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(null));
        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(""));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_BareCode429EchoWithoutAnchor_IsNotSurfaced()
    {
        // FORGERY guard: the glog interleaves verbatim tool output, so a bare
        // 'code 429' echo with no same-line exhaustion anchor must not become
        // a terminal region — otherwise it folds into Stderr as trusted quota
        // and suppresses the auth gate.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "tool output: agy failed (code 429): try again later";

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_BareQuotaPhraseEchoWithoutFraming_IsNotSurfaced()
    {
        // FORGERY guard: a tool-output line echoing 'individual quota reached'
        // with no provider error framing (no error/429/Resets-in/lock context)
        // must not surface — the real provider lines always carry framing.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "tool output: individual quota reached";

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog));
    }

    [Fact]
    public void ExtractTerminalErrorRegion_QuotaPhraseWithResetFraming_IsSurfaced()
    {
        // The real per-model refusal carries its reset hint on the same line,
        // which counts as provider framing — it must still surface.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h8m20s.";

        var region = AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog);

        Assert.NotNull(region);
        Assert.Contains("Individual quota reached", region!);
    }

    [Fact]
    public void IndividualQuotaReached_IsDetected_AndCarriesItsOwnReset()
    {
        // Verbatim from a live agy result frame. It says "quota reached", not "quota exceeded", so it
        // matched none of the original patterns and the run hard-FAILED instead of parking — despite
        // the message stating exactly when it recovers.
        const string message =
            "Individual quota reached. Please upgrade your subscription to increase your limits. "
            + "Resets in 1h8m20s.";

        var detection = new AntigravityQuotaFailureDetector().Detect(message, null);

        Assert.NotNull(detection);
        // A short rolling window, not a weekly lockout.
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void IndividualQuotaReached_IsDetectedFromAStreamJsonResultFrame()
    {
        // This is how it actually reaches us: inside agy's NDJSON result frame, not on stderr.
        const string stdout =
            """{"event":"result","result":{"status":"ERROR","error":"Individual quota reached. Resets in 1h8m20s."}}""";

        var detection = new AntigravityQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_HostSideExit3Shape_ResultErrorWith113hReset_ParsesPreciseReset()
    {
        // Host-side truth for the sandbox-masked failure: result status ERROR with
        // 'RESOURCE_EXHAUSTED (code 429): Individual quota reached ... Resets in 113h',
        // agy exit 3, cli.log 'Individual quota reached'. Must classify as quota-exhausted
        // with the parsed reset — never auth_required.
        const string stdout =
            """{"type":"result","status":"ERROR","error":"RESOURCE_EXHAUSTED (code 429): Individual quota reached. Resets in 113h."}""";

        var before = DateTimeOffset.UtcNow;
        var detection = new AntigravityQuotaFailureDetector().Detect(null, stdout);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, before.AddHours(113), after.AddHours(113));
    }

    [Fact]
    public void Detect_Code429WithExhaustionAnchor_ClassifiesRateLimit()
    {
        // "code 429" only counts alongside an exhaustion anchor in the same surface,
        // so prose discussing a bare code cannot force a quota verdict.
        var detection = new AntigravityQuotaFailureDetector().Detect(
            stderr: "agy failed (code 429): quota reset soon", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_BareCode429WithoutExhaustionAnchor_IsNotQuota()
    {
        var detection = new AntigravityQuotaFailureDetector().Detect(
            stderr: "agy failed (code 429): try again later", stdout: null);

        Assert.Null(detection);
    }

    [Fact]
    public void Detect_BareIndividualQuotaReachedWithoutAnchor_IsNotQuota()
    {
        // FORGERY guard: the per-model phrase only counts with a strong
        // provider anchor on the same surface. A bare echo with no reset/429/
        // RESOURCE_EXHAUSTED/lockout context is model-relayed prose.
        Assert.Null(new AntigravityQuotaFailureDetector().Detect(
            stderr: "tool output: individual quota reached", stdout: null));
    }

    [Fact]
    public void Detect_IndividualQuotaReachedWithResetAnchor_IsQuota()
    {
        var detection = new AntigravityQuotaFailureDetector().Detect(
            stderr: "Individual quota reached. Please upgrade your subscription. Resets in 1h8m20s.",
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void ExtractTerminalErrorRegion_QuotaEchoWithOnlyGenericErrorFraming_IsNotSurfaced()
    {
        // Generic prose words ('error') are NOT provider framing: a forged
        // tool-output line must carry a strong token (429, Resets in,
        // RESOURCE_EXHAUSTED, lockout, ...) to surface.
        var glog = "Model resolved: gemini-3.5-flash\n"
            + "tool output: individual quota reached error";

        Assert.Null(AntigravityQuotaFailureDetector.ExtractTerminalErrorRegion(glog));
    }
}
