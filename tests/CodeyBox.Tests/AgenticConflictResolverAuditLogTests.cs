using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the structured <c>agentic_conflict_resolver.attempt_failed</c> audit
/// emission for each of the three failure sites in
/// <see cref="AgenticConflictResolver.ResolveAsync"/>:
///   (1) the agent runner throws (catch block),
///   (2) the agent runner reports failure (Success=false),
///   (3) post-run verification fails (markers/unmerged remain).
///
/// Without these, a regression that drops the
/// <see cref="AuditLog.AgenticConflictResolverAttemptFailed"/> call at any of
/// the three sites would re-introduce the impossible-to-diagnose
/// "agent exited 1" shape the Part-1 capture path was added to eliminate —
/// the per-attempt trail string is computed independently from the audit
/// emission, so asserting only on result.Summary does not cover the audit
/// channel that operators actually rely on for retrospective triage.
/// </summary>
public sealed class AgenticConflictResolverAuditLogTests : IDisposable
{
    private readonly TestSink _sink = new();
    private readonly IDisposable _auditScope;

    // A dedicated Serilog logger wired to this class's own sink, threaded into
    // every resolver under test so the audit emission lands in _sink even if a
    // concurrent host bootstrap (e.g. a WebApplicationFactory test in a sibling
    // collection) reassigns/flushes the process-global Serilog.Log.Logger
    // mid-run — which previously rerouted these events off _sink and left the
    // poll in AssertSingleAttemptFailedEvent empty. That injection is why this
    // class no longer joins the GlobalSerilog serialization collection, matching
    // the MultipassDaemonRetryPolicy.AuditLogger deflake pattern. Concrete
    // Logger (not ILogger) so Dispose() can be called.
    private readonly Serilog.Core.Logger _auditLogger;

    public AgenticConflictResolverAuditLogTests()
    {
        _auditLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        // Pin this test's audit emission to our sink for the whole async flow
        // rather than relying on the process-global Log.Logger staying put: the
        // audit suite runs WebApplicationFactory<Program> host boots (which
        // rebuild Log.Logger) concurrently in other collections, and one landing
        // between an action here and its inline audit emission would otherwise
        // steal the event — leaving the sink empty. The AsyncLocal override flows
        // into every call below and is immune to those global swaps.
        _auditScope = AuditLog.PushScopedLogger(Log.Logger);
    }

    public void Dispose()
    {
        _auditScope.Dispose();
        _auditLogger.Dispose();
    }

    // A logger wired to the same sink but WITHOUT the SensitiveDataRedactionEnricher,
    // for the cases that prove redaction must have already happened inside the
    // resolver BEFORE the audit is emitted — rather than leaning on the sink-side
    // enricher to cover for it.
    private Serilog.Core.Logger CreateUnredactedAuditLogger() =>
        new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();

    [Fact]
    public async Task ResolveAsync_AgentThrows_EmitsAttemptFailedAuditWithExceptionTrace()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        var runner = new ThrowingAgentRunner(new InvalidOperationException("agent CLI exploded with diagnostics"));
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 2 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: _auditLogger);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        Assert.Contains("threw InvalidOperationException", GetScalar<string>(evt, "Reason") ?? "", StringComparison.Ordinal);
        Assert.Contains("agent CLI exploded with diagnostics",
            GetScalar<string>(evt, "Reason") ?? "", StringComparison.Ordinal);
        // The exception-trace capture lands in StderrTail; stdout is null on
        // the throw branch by construction.
        var stderrTail = GetScalar<string>(evt, "StderrTail") ?? "";
        Assert.Contains("InvalidOperationException", stderrTail, StringComparison.Ordinal);
        Assert.Contains("agent CLI exploded with diagnostics", stderrTail, StringComparison.Ordinal);
        Assert.False(evt.Properties.ContainsKey("StdoutTail"),
            "throw branch must not push StdoutTail (no AgentResult was produced)");
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal(sandbox.Id, GetScalar<string>(evt, "Sandbox"));
        Assert.Equal("/work", GetScalar<string>(evt, "WorkDir"));
        Assert.Equal(1, GetScalar<int>(evt, "Attempt"));
        Assert.Equal(2, GetScalar<int>(evt, "Max"));
    }

    [Fact]
    public async Task ResolveAsync_AgentReportsFailure_EmitsAttemptFailedAuditWithStdoutAndStderr()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        var runner = new StubFailingAgentRunner(
            stdout: "agent printed a startup banner before exiting",
            stderr: "missing ANTHROPIC_API_KEY; refusing to run");
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1, MaxAttemptsPerAgent = 1 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: _auditLogger);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        Assert.Equal("agent exited 1", GetScalar<string>(evt, "Reason"));
        Assert.Equal("agent printed a startup banner before exiting", GetScalar<string>(evt, "StdoutTail"));
        Assert.Equal("missing ANTHROPIC_API_KEY; refusing to run", GetScalar<string>(evt, "StderrTail"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal(1, GetScalar<int>(evt, "Attempt"));
        Assert.Equal(1, GetScalar<int>(evt, "Max"));
    }

    [Fact]
    public async Task ResolveAsync_AgentReportsFailure_RedactsLoggerAndFailureSummaryWithoutLoggerEnricher()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        var apiKey = "sk-ant-api03-AABBCCDDEEFFGGHHIIJJKKLLMMNNOOPPQQRRSSTT-0123456";
        var pat = "ghp_XYZabc789012345678901234567890";
        var runner = new StubFailingAgentRunner(
            stdout: $"agent printed token={pat}",
            stderr: $"agent stderr leaked api_key={apiKey}",
            summary: $"agent exited 1: api_key={apiKey}");
        var logger = new CapturingLogger<AgenticConflictResolver>();
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1, MaxAttemptsPerAgent = 1 }),
            logger,
            auditLogger: _auditLogger);

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("***", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", result.Summary, StringComparison.Ordinal);

        var warning = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("reported failure", StringComparison.Ordinal));
        Assert.Contains("***", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_AgentReportsFailure_RedactsSecretLikeStdoutAndStderrBeforeAudit()
    {
        using var unredacted = CreateUnredactedAuditLogger();

        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        const string StdoutToken = "sk-proj-resolverstdout123";
        const string StderrToken = "sk-ant-resolverstderr123";
        var runner = new StubFailingAgentRunner(
            stdout: $"agent stdout leaked {StdoutToken}",
            stderr: $"agent stderr leaked {StderrToken}");
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1, MaxAttemptsPerAgent = 1 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: unredacted);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        var stdoutTail = GetScalar<string>(evt, "StdoutTail") ?? "";
        var stderrTail = GetScalar<string>(evt, "StderrTail") ?? "";
        Assert.DoesNotContain(StdoutToken, stdoutTail, StringComparison.Ordinal);
        Assert.DoesNotContain(StderrToken, stderrTail, StringComparison.Ordinal);
        Assert.Contains("agent stdout leaked ***", stdoutTail, StringComparison.Ordinal);
        Assert.Contains("agent stderr leaked ***", stderrTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_AgentReportsFailure_TruncatesAuditStdoutAndStderrTails()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        var runner = new StubFailingAgentRunner(
            stdout: string.Join('\n', Enumerable.Range(0, 700).Select(static i => $"stdout line {i:D4} value")),
            stderr: string.Join('\n', Enumerable.Range(0, 700).Select(static i => $"stderr line {i:D4} value")));
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1, MaxAttemptsPerAgent = 1 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: _auditLogger);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Merge),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        var stdoutTail = GetScalar<string>(evt, "StdoutTail") ?? "";
        var stderrTail = GetScalar<string>(evt, "StderrTail") ?? "";
        Assert.Equal(2049, stdoutTail.Length);
        Assert.Equal(2049, stderrTail.Length);
        Assert.StartsWith("…", stdoutTail, StringComparison.Ordinal);
        Assert.StartsWith("…", stderrTail, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactAuditTail_TruncatesRedactedOutputToResolverWindow()
    {
        var method = typeof(AgenticConflictResolver).GetMethod(
            "RedactAuditTail",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var input = string.Join('\n', Enumerable.Range(0, 700).Select(static i => $"resolver tail line {i:D4} value"));
        var value = Assert.IsType<string>(method!.Invoke(null, [input]));

        Assert.Equal(4097, value.Length);
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_VerificationFails_EmitsAttemptFailedAuditWithStdoutAndStderr()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        // Agent claims success and stages the file but does NOT strip markers,
        // so post-run verification finds them. The stdout/stderr the agent
        // returned must propagate into the verification-failure audit emission
        // — that is the only diagnostic the operator gets when the model
        // reports success and quietly leaves the conflict in place.
        var runner = new MarkerLeavingAgentRunner(
            stdout: "claimed resolution complete",
            stderr: "warning: incomplete model output");
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1, MaxAttemptsPerAgent = 1 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: _auditLogger);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        var reason = GetScalar<string>(evt, "Reason") ?? "";
        Assert.StartsWith("verification:", reason, StringComparison.Ordinal);
        Assert.Contains("conflict markers remain", reason, StringComparison.Ordinal);
        Assert.Equal("claimed resolution complete", GetScalar<string>(evt, "StdoutTail"));
        Assert.Equal("warning: incomplete model output", GetScalar<string>(evt, "StderrTail"));
    }

    [Fact]
    public async Task ResolveAsync_SessionResumeExhausted_RedactsAuditStdoutAndStderrWithoutLoggerEnricher()
    {
        using var unredacted = CreateUnredactedAuditLogger();

        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nm\n=======\nw\n>>>>>>> feature\n");

        var lastResult = new AgentResult(
            false,
            "agent exited 1",
            "stdout leaked ghp_XYZabc789012345678901234567890 before resume failed",
            "stderr leaked sk-ant-api03-AABBCCDDEEFFGGHHIIJJKKLLMMNNOOPPQQRRSSTT-0123456 before resume failed");
        var runner = new ThrowingAgentRunner(new AgentSessionResumeExhaustedException(
            new AgentKind("resumable"),
            maxResumeAttempts: 2,
            lastResult))
        { Kind = new AgentKind("resumable") };
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }),
            NullLogger<AgenticConflictResolver>.Instance,
            auditLogger: unredacted);

        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        var evt = await AssertSingleAttemptFailedEvent(workItemId);
        var stdoutTail = GetScalar<string>(evt, "StdoutTail") ?? "";
        var stderrTail = GetScalar<string>(evt, "StderrTail") ?? "";
        Assert.DoesNotContain("ghp_", stdoutTail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", stderrTail, StringComparison.Ordinal);
        Assert.Contains("***", stdoutTail, StringComparison.Ordinal);
        Assert.Contains("***", stderrTail, StringComparison.Ordinal);
    }

    private async Task<LogEvent> AssertSingleAttemptFailedEvent(WorkItemId workItemId)
    {
        // The audit event reaches the in-memory sink through the global Serilog
        // pipeline, whose delivery can lag the awaited ResolveAsync completion
        // under load. Poll for it rather than reading the sink once.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        List<LogEvent> attemptFailed;
        while (true)
        {
            attemptFailed = _sink.Events
                .Where(e => GetScalar<string>(e, "EventName") == "agentic_conflict_resolver.attempt_failed")
                .ToList();
            if (attemptFailed.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                break;
            await Task.Delay(25);
        }
        var match = Assert.Single(attemptFailed);
        Assert.True(GetScalar<bool>(match, "Audit"));
        Assert.Equal(LogEventLevel.Warning, match.Level);
        Assert.Equal(workItemId.ToString(), GetScalar<string>(match, "WorkItemId"));
        return match;
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t) return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private sealed class ThrowingAgentRunner : IAgentRunner
    {
        private readonly Exception _ex;
        public ThrowingAgentRunner(Exception ex) => _ex = ex;
        public AgentKind Kind { get; init; } = new("throwing");

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw _ex;
    }

    private sealed class StubFailingAgentRunner : IAgentRunner
    {
        private readonly string _stdout;
        private readonly string _stderr;
        private readonly string _summary;
        public StubFailingAgentRunner(string stdout, string stderr, string summary = "agent exited 1")
        {
            _stdout = stdout;
            _stderr = stderr;
            _summary = summary;
        }
        public AgentKind Kind { get; init; } = new("stub-failing");

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(false, _summary, _stdout, _stderr));
    }

    /// <summary>
    /// Returns success but leaves the conflict markers in place — drives the
    /// resolver's verification-failure branch with non-empty stdout/stderr so
    /// the audit emission's capture of both fields is observable.
    /// </summary>
    private sealed class MarkerLeavingAgentRunner : IAgentRunner
    {
        private readonly string _stdout;
        private readonly string _stderr;
        public MarkerLeavingAgentRunner(string stdout, string stderr) { _stdout = stdout; _stderr = stderr; }
        public AgentKind Kind { get; init; } = new("marker-leaving");

        public async Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            // Stage the still-conflicted file via the test sandbox's git-add
            // path. Mirrors a real "lying" agent that runs `git add` without
            // first stripping the conflict markers.
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", "conflict.txt"],
            }, ct);
            return new AgentResult(true, "claimed resolution", _stdout, _stderr);
        }
    }
}
