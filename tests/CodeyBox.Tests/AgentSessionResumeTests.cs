using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for CLI-native session resume in <see cref="CliAgentRunnerBase"/>.
/// A transient agent process crash (non-zero exit, sandbox alive, session id
/// captured on stdout) should be recovered by re-launching the same CLI with
/// <c>--resume &lt;session-id&gt;</c> in the SAME sandbox — instead of failing
/// the whole work item and re-driving from scratch.
/// </summary>
public sealed class AgentSessionResumeTests : IDisposable
{
    // Only the session-resume static is mutated here — leaving
    // AgentSuspendResilience.MaxRetries at its default lets AgentSuspendResilienceRetryTests
    // run safely in parallel with this class (it depends on the default).
    private readonly int _originalSessionResume = SessionResumeOptions.MaxResumeAttempts;

    public void Dispose()
    {
        SessionResumeOptions.SetMaxResumeAttempts(_originalSessionResume);
    }

    // ── Extractor unit tests ──────────────────────────────────────────────────

    [Fact]
    public void Extractor_FindsSessionIdInInitLine()
    {
        var stdout = """
            {"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909","tools":[]}
            {"type":"assistant","message":{"role":"assistant","content":[]}}
            """;
        Assert.Equal("e61b65a0-0f1e-4469-94f0-0be82d71b909",
            ClaudeSessionIdExtractor.Extract(stdout));
    }

    [Fact]
    public void Extractor_AcceptsCamelCaseSessionId()
    {
        var stdout = """{"type":"system","subtype":"init","sessionId":"c8e8171a-5c61-42e6-a633-936d2362886a","tools":[]}""";
        Assert.Equal("c8e8171a-5c61-42e6-a633-936d2362886a", ClaudeSessionIdExtractor.Extract(stdout));
    }

    [Fact]
    public void Extractor_IgnoresMalformedAndTruncatedLines()
    {
        var stdout = """
            not json at all
            {"broken json
            {"type":"system","subtype":"init","session_id":"d6d9e7c3-a8d7-4f86-ab19-6318a1f95a3e"}
            """;
        Assert.Equal("d6d9e7c3-a8d7-4f86-ab19-6318a1f95a3e", ClaudeSessionIdExtractor.Extract(stdout));
    }

    [Fact]
    public void Extractor_ReturnsNullWhenAbsent()
    {
        Assert.Null(ClaudeSessionIdExtractor.Extract(null));
        Assert.Null(ClaudeSessionIdExtractor.Extract(""));
        Assert.Null(ClaudeSessionIdExtractor.Extract("""{"type":"assistant"}"""));
        Assert.Null(ClaudeSessionIdExtractor.Extract("""{"type":"system","session_id":""}"""));
        Assert.Null(ClaudeSessionIdExtractor.Extract("""{"type":"assistant","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}"""));
        Assert.Null(ClaudeSessionIdExtractor.Extract("""{"type":"system","subtype":"init","session_id":"not-a-uuid"}"""));
    }

    // ── Resume eligibility ────────────────────────────────────────────────────

    [Fact]
    public void IsResumeEligible_FalseForQuotaAndAuth()
    {
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(AgentFailureKind.QuotaExhausted)));
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: AgentFailureClassifier.HardQuotaReason)));
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(AgentFailureKind.AuthError)));
    }

    [Fact]
    public void IsResumeEligible_TrueForTransientUnknownAndSoftRateLimitWithoutReset()
    {
        Assert.True(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(AgentFailureKind.TransientNetwork)));
        Assert.True(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(AgentFailureKind.Unknown)));
        Assert.True(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: AgentFailureClassifier.SoftRateLimitReason,
                QuotaFailure: AgentQuotaFailureKind.SoftRateLimit),
            stderr: "API Error: 429 rate_limit_exceeded"));
    }

    [Fact]
    public void IsResumeEligible_FalseForNormalFailure()
    {
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(AgentFailureKind.Normal)));
    }

    [Fact]
    public void IsResumeEligible_FalseForSoftRateLimitWithResetWindow()
    {
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: AgentFailureClassifier.SoftRateLimitReason,
                QuotaFailure: AgentQuotaFailureKind.SoftRateLimit),
            stderr: "API Error: 429 rate_limit_exceeded; retry after 2h"));
    }

    [Fact]
    public void IsResumeEligible_FalseForSoftRateLimitWithClassificationResetAt()
    {
        Assert.False(SessionResumeOptions.IsResumeEligible(
            new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                QuotaResetAt: DateTimeOffset.UtcNow.AddMinutes(15),
                Reason: AgentFailureClassifier.SoftRateLimitReason,
                QuotaFailure: AgentQuotaFailureKind.SoftRateLimit),
            stderr: "API Error: 429 rate_limit_exceeded"));
    }

    [Fact]
    public void SetMaxResumeAttempts_ClampsNegativeToZero()
    {
        // Operator typo via hot-reload must not produce nonsensical behaviour
        // (negative budget would be indistinguishable from "disabled" today
        // but could regress if the comparison ever became signed-aware).
        try
        {
            SessionResumeOptions.SetMaxResumeAttempts(-7);
            Assert.Equal(0, SessionResumeOptions.MaxResumeAttempts);
        }
        finally
        {
            SessionResumeOptions.SetMaxResumeAttempts(_originalSessionResume);
        }
    }

    [Fact]
    public void SetMaxResumeAttempts_ClampsLargeValues()
    {
        try
        {
            SessionResumeOptions.SetMaxResumeAttempts(10_000);
            Assert.Equal(SessionResumeOptions.MaxAllowedResumeAttempts, SessionResumeOptions.MaxResumeAttempts);
        }
        finally
        {
            SessionResumeOptions.SetMaxResumeAttempts(_originalSessionResume);
        }
    }

    [Fact]
    public void ClaudeRunner_BuildSessionResumeInvocation_RejectsEmptySessionId()
    {
        // Defensive guard: an extractor that mistakenly hands back an empty
        // string would otherwise produce an invocation with `--resume ""` and
        // the CLI would fail with an unhelpful argparse error. Reject up-front.
        var runner = new ClaudeAgentRunner();
        Assert.Throws<ArgumentException>(() => InvokeBuildResume(runner, sessionId: ""));
        Assert.Throws<ArgumentException>(() => InvokeBuildResume(runner, sessionId: "   "));
    }

    private static object InvokeBuildResume(ClaudeAgentRunner runner, string sessionId)
    {
        var method = typeof(CliAgentRunnerBase).GetMethod(
            "BuildSessionResumeInvocation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            return method.Invoke(runner, [sessionId, "prompt", null, null, null, false])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    // ── Integration with ClaudeAgentRunner ────────────────────────────────────

    [Fact]
    public async Task ClaudeRunner_CrashWithCapturedSessionId_RetriesWithResumeFlag()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        // No legacy MaxRetries mutation — resume runs BEFORE the legacy retry
        // check, so on success at attempt 2 the legacy branch is never reached.

        var sessionId = "e61b65a0-0f1e-4469-94f0-0be82d71b909";
        var sandbox = new ResumeRecordingSandbox(call => call == 1
            // First call: agent crashed mid-run but emitted its init line.
            ? new SandboxExecResult(1,
                Stdout: $$"""
                    {"type":"system","subtype":"init","session_id":"{{sessionId}}","tools":[]}
                    """,
                Stderr: "ECONNRESET")
            : new SandboxExecResult(0, "ok", ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox,
            "/work",
            "prompt",
            credential: null,
            modelId: "claude-sonnet-4-6",
            reasoningMode: "high",
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(2, sandbox.ClaudeInvocations.Count);

        var first = sandbox.ClaudeInvocations[0];
        Assert.DoesNotContain("--resume", first);

        var second = sandbox.ClaudeInvocations[1];
        var resumeIdx = IndexOf(second, "--resume");
        Assert.True(resumeIdx >= 0, "resume retry must pass --resume flag");
        Assert.Equal(sessionId, second[resumeIdx + 1]);
        AssertFlagValue(second, "--output-format", "stream-json");
        Assert.Contains("--verbose", second);
        AssertFlagValue(second, "--model", "claude-sonnet-4-6");
        AssertFlagValue(second, "--effort", "high");

        var secondExec = sandbox.ClaudeExecs[1];
        Assert.Equal(ClaudeAgentRunner.SessionResumePrompt, secondExec.Stdin);
        Assert.NotEqual("prompt", secondExec.Stdin);
    }

    [Fact]
    public async Task ClaudeRunner_UnstructuredStdoutSessionId_DoesNotResume()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
            Stderr: ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: false);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
    }

    [Fact]
    public async Task ClaudeRunner_NormalFailureWithCapturedSessionId_DoesNotResume()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
            Stderr: "test failures: 3/100 assertions failed"));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
    }

    [Fact]
    public async Task ClaudeRunner_CrashWithoutCapturedSessionId_DoesNotResume()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        // The failure shape below ("no init line was emitted", exit 1) is
        // classified as Normal — neither TransientNetwork nor Unknown — so
        // AgentSuspendResilience.ShouldRetry returns false regardless of
        // MaxRetries. No legacy mutation needed to assert only one call.
        var sandbox = new ResumeRecordingSandbox(_ =>
            new SandboxExecResult(1, "no init line was emitted", ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
    }

    [Fact]
    public async Task ClaudeRunner_HardQuotaFailure_DoesNotResumeHammer()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        // Quota classification short-circuits BOTH the resume eligibility check
        // AND AgentSuspendResilience.ShouldRetry, so no legacy mutation is
        // needed to assert only one call.

        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            // The init line was emitted, so a session id IS available — but
            // the failure shape is a hard usage-cap quota event. A resume would
            // immediately re-fail; we must NOT consume the resume budget.
            Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
            Stderr: "usage_limit reached: weekly cap"));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
    }

    [Fact]
    public async Task ClaudeRunner_SoftRateLimitWithCapturedSessionId_RetriesWithResumeFlag()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sessionId = "e61b65a0-0f1e-4469-94f0-0be82d71b909";
        var sandbox = new ResumeRecordingSandbox(call => call == 1
            ? new SandboxExecResult(1,
                Stdout: $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
                Stderr: "API Error: 429 rate_limit_exceeded")
            : new SandboxExecResult(0, "ok", ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(2, sandbox.ClaudeInvocations.Count);
        Assert.Contains("--resume", sandbox.ClaudeInvocations[1]);
    }

    [Fact]
    public async Task ClaudeRunner_SoftRateLimitWithResetWindow_DoesNotResumeHammer()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
            Stderr: "API Error: 429 rate_limit_exceeded; retry after 2h"));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
    }

    [Fact]
    public async Task ClaudeRunner_MissingWorkdirBeforeResume_DoesNotLaunchResume()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(
            _ => new SandboxExecResult(1,
                Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
                Stderr: "ECONNRESET"),
            exec => exec.Argv.Count > 0 && exec.Argv[0] == "sh"
                ? new SandboxExecResult(1, "", "missing workdir")
                : new SandboxExecResult(0, "", ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
    }

    [Fact]
    public async Task ClaudeRunner_SandboxDeathBeforeResume_BubblesOut()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(
            _ => new SandboxExecResult(1,
                Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
                Stderr: "ECONNRESET"),
            exec => exec.Argv.Count > 0 && exec.Argv[0] == "sh"
                ? throw new InvalidOperationException("sandbox died")
                : new SandboxExecResult(0, "", ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClaudeAgentRunner().RunAsync(
                sandbox, "/work", "prompt", credential: null, captureStructuredStream: true));

        Assert.Contains("sandbox died", ex.Message);
        Assert.Single(sandbox.ClaudeInvocations);
    }

    [Fact]
    public async Task ClaudeRunner_BoundedByMaxResumeAttempts_FailsCleanlyAfterN()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);

        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            // Every attempt crashes with the init line on stdout, simulating
            // a process that consistently dies mid-stream.
            Stdout: """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
            Stderr: "ECONNRESET"));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        // 1 original + 2 resume attempts = 3 claude invocations.
        Assert.Equal(3, sandbox.ClaudeInvocations.Count);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
        Assert.Contains("--resume", sandbox.ClaudeInvocations[1]);
        Assert.Contains("--resume", sandbox.ClaudeInvocations[2]);
    }

    [Fact]
    public async Task ClaudeRunner_ResumeExhaustionOnTransientFailure_DoesNotRestartFromScratch()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);

        var sessionId = "e61b65a0-0f1e-4469-94f0-0be82d71b909";
        var sandbox = new ResumeRecordingSandbox(_ => new SandboxExecResult(1,
            Stdout: $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
            Stderr: "ECONNRESET"));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.False(result.Success);
        // Regression guard for original, --resume, --resume, original.
        Assert.Equal(3, sandbox.ClaudeInvocations.Count);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
        Assert.Contains("--resume", sandbox.ClaudeInvocations[1]);
        Assert.Contains("--resume", sandbox.ClaudeInvocations[2]);
    }

    [Fact]
    public async Task ClaudeRunner_MaxResumeZero_FallsBackToLegacyRetryOnly()
    {
        SessionResumeOptions.SetMaxResumeAttempts(0);
        // AgentSuspendResilience.MaxRetries is left at its default of 1; the
        // ECONNRESET shape below classifies as TransientNetwork and triggers
        // exactly one legacy retry, which is the behaviour this test pins.

        var sandbox = new ResumeRecordingSandbox(call => call == 1
            ? new SandboxExecResult(1, """{"type":"system","subtype":"init","session_id":"e61b65a0-0f1e-4469-94f0-0be82d71b909"}""",
                "ECONNRESET")
            : new SandboxExecResult(0, "ok", ""));

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.True(result.Success);
        // No --resume because the resume budget is 0; the legacy
        // suspend-resilience path takes over on the transient-network shape.
        Assert.Equal(2, sandbox.ClaudeInvocations.Count);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[0]);
        Assert.DoesNotContain("--resume", sandbox.ClaudeInvocations[1]);
    }

    [Fact]
    public async Task ClaudeRunner_RunResumedAsync_CrashWithCapturedSessionId_RetriesWithResumeFlag()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sessionId = "c8e8171a-5c61-42e6-a633-936d2362886a";
        var sandbox = new ResumeRecordingSandbox(call => call == 1
            ? new SandboxExecResult(1,
                Stdout: $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
                Stderr: "ECONNRESET")
            : new SandboxExecResult(0, "ok", ""));

        var result = await new ClaudeAgentRunner().RunResumedAsync(
            sandbox,
            "/work",
            "prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.True(result.Success);
        Assert.Equal(2, sandbox.ClaudeInvocations.Count);

        var first = sandbox.ClaudeInvocations[0];
        Assert.DoesNotContain("--resume", first);
        AssertFlagValue(first, "--output-format", "stream-json");
        Assert.Contains("--verbose", first);

        var second = sandbox.ClaudeInvocations[1];
        var resumeIdx = IndexOf(second, "--resume");
        Assert.True(resumeIdx >= 0, "checkpoint-restored crash must retry with --resume");
        Assert.Equal(sessionId, second[resumeIdx + 1]);
        Assert.Equal(ClaudeAgentRunner.SessionResumePrompt, sandbox.ClaudeExecs[1].Stdin);
    }

    [Fact]
    public async Task ClaudeRunner_RunResumedAsync_WhenStructuredStreamUnsupported_DoesNotForceFlags()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        var sandbox = new ResumeRecordingSandbox(
            _ => new SandboxExecResult(0, "ok", ""),
            structuredStreamHelpResult: new SandboxExecResult(0, "Usage: claude", ""));

        var result = await new ClaudeAgentRunner().RunResumedAsync(
            sandbox,
            "/work",
            "prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.True(result.Success);
        Assert.Single(sandbox.ClaudeInvocations);
        var first = sandbox.ClaudeInvocations[0];
        Assert.DoesNotContain("--output-format", first);
        Assert.DoesNotContain("stream-json", first);
        Assert.DoesNotContain("--verbose", first);
        Assert.Contains("structured stream capture was disabled", result.Stderr);
    }

    [Fact]
    public async Task NonResumableRunner_KeepsLegacyRetryBehaviour()
    {
        SessionResumeOptions.SetMaxResumeAttempts(2);
        // Legacy MaxRetries left at its default of 1.

        var sandbox = new ResumeRecordingSandbox(call => call == 1
            ? new SandboxExecResult(1,
                """{"type":"system","subtype":"init","session_id":"sid"}""",
                "ECONNRESET")
            : new SandboxExecResult(0, "ok", ""));

        // FakeRunner.SupportsSessionResume is false, so the captured session
        // id MUST be ignored and the legacy single-shot re-invocation path
        // takes over as before — argv unchanged on retry.
        var runner = new NonResumableTestRunner();
        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.True(result.Success);
        Assert.Equal(2, sandbox.AllExecs.Count);
        Assert.DoesNotContain("--resume", sandbox.AllExecs[0].Argv);
        Assert.DoesNotContain("--resume", sandbox.AllExecs[1].Argv);
    }

    [Fact]
    public async Task ClaudeRunner_SessionIdCapturedOnFirstAttempt_IsReusedAcrossLaterCrashes()
    {
        // A second attempt that crashes WITHOUT emitting a fresh init line
        // (e.g. the resumed CLI died before its own init replay) must still
        // use the session id captured on attempt 1 for the third attempt.
        SessionResumeOptions.SetMaxResumeAttempts(3);

        var sessionId = "c8e8171a-5c61-42e6-a633-936d2362886a";
        var sandbox = new ResumeRecordingSandbox(call => call switch
        {
            1 => new SandboxExecResult(1,
                $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
                "ECONNRESET"),
            2 => new SandboxExecResult(1, "no init this time", "ECONNRESET"),
            _ => new SandboxExecResult(0, "ok", ""),
        });

        var result = await new ClaudeAgentRunner().RunAsync(
            sandbox, "/work", "prompt", credential: null, captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Equal(3, sandbox.ClaudeInvocations.Count);
        var second = sandbox.ClaudeInvocations[1];
        var third = sandbox.ClaudeInvocations[2];
        Assert.Equal(sessionId, second[IndexOf(second, "--resume") + 1]);
        Assert.Equal(sessionId, third[IndexOf(third, "--resume") + 1]);
    }

    private static int IndexOf(IReadOnlyList<string> argv, string token)
    {
        for (var i = 0; i < argv.Count; i++)
            if (string.Equals(argv[i], token, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string expected)
    {
        var idx = IndexOf(argv, flag);
        Assert.True(idx >= 0, $"argv must contain {flag}");
        Assert.True(idx + 1 < argv.Count, $"{flag} must have a value");
        Assert.Equal(expected, argv[idx + 1]);
    }

    // ── Test harness ──────────────────────────────────────────────────────────

    private sealed class ResumeRecordingSandbox(
        Func<int, SandboxExecResult> onClaudeCall,
        Func<SandboxExec, SandboxExecResult>? onOtherExec = null,
        SandboxExecResult? structuredStreamHelpResult = null) : ISandbox
    {
        public string Id => "codeybox-resume-test";

        public List<SandboxExec> AllExecs { get; } = new();

        /// <summary>argv lists captured from each claude / agent-binary invocation, in order.</summary>
        public List<IReadOnlyList<string>> ClaudeInvocations { get; } = new();
        public List<SandboxExec> ClaudeExecs { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            // The base runner runs auxiliary execs (auth materialisation,
            // preempt hooks etc); only count actual agent-binary calls so the
            // ClaudeInvocations count is meaningful regardless of base-class
            // bookkeeping.
            if (exec.Argv.Count > 0
                && exec.Argv[0] == ClaudeAgentRunner.DefaultBinary
                && exec.Argv.Contains("--help"))
            {
                return Task.FromResult(structuredStreamHelpResult ?? new SandboxExecResult(0, "--output-format stream-json --verbose", ""));
            }

            if (exec.Argv.Count > 0
                && ((exec.Argv[0] == ClaudeAgentRunner.DefaultBinary && exec.Argv.Contains("--print"))
                    || exec.Argv[0] == "fake-agent"))
            {
                ClaudeExecs.Add(exec);
                ClaudeInvocations.Add(exec.Argv);
                return Task.FromResult(onClaudeCall(ClaudeInvocations.Count));
            }
            if (onOtherExec is not null)
                return Task.FromResult(onOtherExec(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// CLI-base subclass that does NOT opt into session resume — used to prove
    /// the legacy retry path is preserved for runners whose CLI has no
    /// <c>--resume</c> mode. Reuses an AgentKind that the legacy
    /// <see cref="AgentSuspendResilience"/> already opted into so the
    /// transient-network retry path engages and we can observe argv stability
    /// across attempts.
    /// </summary>
    private sealed class NonResumableTestRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind { get; } = new("opencode");

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["fake-agent", "run"], Stdin: prompt);
    }
}
