using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

// ── BuildTestGate ordering & short-circuit ────────────────────────────────────

/// <summary>
/// The LLM auditor panel's prompt frame asserts "automated CI built the
/// project and ran the full test suite, and reported no build errors and no
/// test failures." For that claim to stay true on every iteration, two
/// invariants must hold:
///
///   * BuildTestGate-role auditors MUST run before any LLM auditor.
///   * A BuildTestGate blocking finding MUST skip the LLM panel for that
///     iteration (even when <c>StopOnFirstFailure</c> is false, the panel's
///     default).
///
/// These tests pin both invariants by driving the live audit loop with a
/// scripted gate auditor and a recording LLM auditor.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class BuildTestGateOrderingTests : IDisposable
{
    private readonly string _workspace;
    public BuildTestGateOrderingTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-gate-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task BuildTestGateFails_LlmPanelIsSkippedThisIteration_StopOnFirstFailureOff()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Iter 1: gate fails (no test) → LLM panel must be skipped.
        // Iter 2: gate passes → LLM panel runs and passes → Done.
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
        [
            new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass", AuditSeverity.Error,
                "tests failed", "MyTests.SomeTest failed")]),
            new AuditOutcome(true, []),
        ]);

        var llmRunsSeen = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRunsSeen));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [gate, llm],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = false,
            },
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The LLM auditor must have run exactly once (iter 2), never iter 1.
        Assert.Equal(1, llmRunsSeen);
        Assert.Equal([2], llm.SeenIterations);
        Assert.Equal([1, 2], gate.SeenIterations);
    }

    [Fact]
    public async Task BuildTestGateReturnsNonPassingInfoFinding_LlmPanelIsSkipped()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass",
            AuditorRole.BuildTestGate,
        [
            new AuditOutcome(false, [new AuditFinding(
                "csharp:test-pass", AuditSeverity.Info,
                "tool not installed in sandbox: dotnet",
                "The auditor command was not run because 'dotnet' is not available in the audit sandbox.")]),
        ]);

        var llmRunsSeen = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRunsSeen));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Missing-tool gate findings are non-blocking by existing policy, but
        // a non-passing BuildTestGate still cannot allow the CI-passed prompt
        // claim to reach the LLM panel.
        Assert.Equal(WorkItemState.Done, final!.State);

        Assert.Equal(0, llmRunsSeen);
        Assert.Empty(llm.SeenIterations);
        Assert.Equal([1], gate.SeenIterations);
    }

    [Fact]
    public async Task BuildTestGateOrdersAheadOfOtherToolAuditors_AndAheadOfLlmPanel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var executionOrder = new List<string>();
        var orderLock = new object();
        void Record(string n) { lock (orderLock) executionOrder.Add(n); }

        // Three auditors:
        //   - csharp:format-check (Role.None, tool) — registered first
        //   - csharp:test-pass    (BuildTestGate, tool) — registered second
        //   - security:llm-review (LLM, registered last)
        // After ordering by tier, execution order must be: gate → format → llm.
        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check", AuditorRole.None,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("format"));
        var gate = new RoleStampedScriptedAuditor(
            "csharp:test-pass", AuditorRole.BuildTestGate,
            [new AuditOutcome(true, [])],
            onRun: _ => Record("gate"));
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Record("llm"));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [format, gate, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(["gate", "format", "llm"], executionOrder);
    }

    [Fact]
    public async Task NonGateBlockingFinding_DoesNotSkipLlmPanel()
    {
        // Confirms the gate is scoped to BuildTestGate role specifically: a
        // tool auditor without the role that produces a blocking finding does
        // NOT skip the LLM panel (the prompt frame's CI claim is only about
        // build+test, not lint/format).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var format = new RoleStampedScriptedAuditor(
            "csharp:format-check", AuditorRole.None,
            [new AuditOutcome(false, [new AuditFinding(
                "csharp:format-check", AuditSeverity.Error,
                "formatting needed", "x")])]);
        var llmRuns = 0;
        var llm = new RecordingLlmAuditor("security:llm-review", _ => Interlocked.Increment(ref llmRuns));

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [format, llm],
            maxAuditIterations: 1,
            credentials: AuditCredentials());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // LLM panel still runs even though a non-gate tool auditor blocked.
        Assert.Equal(1, llmRuns);
    }

    private static ConstantCredentialProvider AuditCredentials()
        => new(new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "test-key" },
            new Dictionary<string, string>()));

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "gate test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}

// ── Anti-bias: low-quality diff still produces findings ───────────────────────

/// <summary>
/// The shared LLM prompt frame (Defaults/llm-prompt-frame.yaml) tells the
/// auditor that automated CI already built + tested the project so it should
/// not re-run. To prevent that note from biasing the reviewer toward
/// "tests pass = code is fine", the frame also includes an explicit
/// anti-bias disclaimer instructing the reviewer to judge correctness,
/// completeness, and design from the diff and surrounding code.
///
/// This test pins that the disclaimer survives in the rendered prompt and
/// drives the auditor to still produce findings on a low-quality diff.
/// It uses a simulated agent that mirrors the documented intent: when the
/// disclaimer is present in the prompt, the reviewer flags problems in a
/// trivial-but-unwired-and-undertested change.
/// </summary>
public sealed class LlmAuditorAntiBiasOnLowQualityDiffTests
{
    private const string AntiBiasMarker =
        "does NOT mean the code is correct, complete, or well-designed";
    private const string CiAlreadyRanMarker =
        "Automated CI has already built the project and run the full test suite";
    // The operative anti-rerun instruction. Asserted separately from the
    // surrounding CI-claim/anti-bias clauses so removing JUST this sentence
    // (leaving the context that hints at it) still fails the test.
    private const string DoNotRunBuildOrTestsMarker =
        "Do NOT run any build or test commands yourself";

    [Fact]
    public async Task FrameTemplate_AntiBiasDisclaimer_SurvivesPromptRendering_AndFindingsSurface()
    {
        // Frame is the SHIPPED one loaded from the embedded preset catalog —
        // any regression that removes the disclaimer, the CI-note, or the
        // operative "Do NOT run any build or test commands" instruction will
        // fail this test rather than silently un-gating the bias or letting
        // panel auditors re-run the deterministic suite.
        var frameTemplate = new PresetCatalog().LlmPromptFrameTemplate;
        Assert.Contains(CiAlreadyRanMarker, frameTemplate, StringComparison.Ordinal);
        Assert.Contains(DoNotRunBuildOrTestsMarker, frameTemplate, StringComparison.Ordinal);
        Assert.Contains(AntiBiasMarker, frameTemplate, StringComparison.Ordinal);

        var runner = new LowQualityDiffReviewRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "quality:llm-review",
            Agent = runner,
            ReviewFocus = "- code quality, wiring, test coverage",
            FrameTemplate = frameTemplate,
        });
        var ctx = new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "Add a feature flag for X.",
            AuditRunner: runner);
        var sandbox = new WritableResultFileSandbox();

        var result = await auditor.RunAsync(sandbox, "/work", ctx);

        // The CI note, the anti-rerun directive, and the anti-bias disclaimer
        // all reached the agent prompt.
        Assert.Contains(CiAlreadyRanMarker, runner.ObservedPrompt, StringComparison.Ordinal);
        Assert.Contains(DoNotRunBuildOrTestsMarker, runner.ObservedPrompt, StringComparison.Ordinal);
        Assert.Contains(AntiBiasMarker, runner.ObservedPrompt, StringComparison.Ordinal);

        // The deterministic reviewer is a prompt-wiring simulation: when the
        // anti-bias disclaimer reaches the prompt, its JSON verdict contains
        // the blocking findings this regression needs surfaced.
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
    }

    /// <summary>
    /// Stub reviewer for the prompt-frame wiring contract. It emits the
    /// low-quality-diff findings only when the anti-bias disclaimer survives
    /// rendering into the prompt; this is not a live-model behaviour test.
    /// </summary>
    private sealed class LowQualityDiffReviewRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string ObservedPrompt { get; private set; } = string.Empty;

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
        {
            ObservedPrompt = prompt;
            if (sandbox is WritableResultFileSandbox writable)
            {
                writable.ResultJson = prompt.Contains(AntiBiasMarker, StringComparison.Ordinal)
                    ? """
                      {"passed":false,"findings":[
                        {"severity":"error","title":"new code path is never wired to a caller","description":"AddFeatureFlagX is defined but no consumer invokes it; tests cover it directly only","location":"src/Foo.cs:42"},
                        {"severity":"error","title":"no test covers the disabled branch","description":"only the enabled branch is asserted; disabled path is unobserved","location":"tests/FooTests.cs:10"}
                      ]}
                      """
                    : """
                      {"passed":true,"findings":[]}
                      """;
            }
            return Task.FromResult(new AgentResult(true, "ok", "review complete", null));
        }
    }

    private sealed class WritableResultFileSandbox : ISandbox
    {
        public string Id => "low-quality-diff-sandbox";
        public string ResultJson { get; set; } = "{\"passed\":true,\"findings\":[]}";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, ResultJson, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// ── Test helpers (file-scoped) ────────────────────────────────────────────────

file sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

/// <summary>
/// Drives a scripted sequence of outcomes per iteration and lets the test
/// stamp <see cref="IAuditor.Role"/> directly — required to exercise the
/// pipeline's BuildTestGate ordering and short-circuit logic without going
/// through the YAML preset loader.
/// </summary>
file sealed class RoleStampedScriptedAuditor : IAuditor
{
    private readonly Queue<AuditOutcome> _plan;
    private readonly Action<int>? _onRun;

    public RoleStampedScriptedAuditor(
        string name,
        AuditorRole role,
        IEnumerable<AuditOutcome> plan,
        Action<int>? onRun = null)
    {
        Name = name;
        Role = role;
        _plan = new Queue<AuditOutcome>(plan);
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public AuditorRole Role { get; }
    public List<int> SeenIterations { get; } = [];

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        if (_plan.Count == 0)
            throw new InvalidOperationException($"no plan entries left for {Name}");
        SeenIterations.Add(context.Iteration);
        _onRun?.Invoke(context.Iteration);
        var outcome = _plan.Dequeue();
        return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
    }
}

/// <summary>
/// Stand-in for a production-shaped LLM auditor (Kind="llm" with agent
/// credentials and network required). Always passes; records each iteration on
/// which it was invoked, so tests can assert it was skipped.
/// </summary>
file sealed class RecordingLlmAuditor : IAuditor
{
    private readonly Action<int> _onRun;

    public RecordingLlmAuditor(string name, Action<int> onRun)
    {
        Name = name;
        _onRun = onRun;
    }

    public string Name { get; }
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;
    public List<int> SeenIterations { get; } = [];

    public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        SeenIterations.Add(context.Iteration);
        _onRun(context.Iteration);
        return Task.FromResult(new AuditResult(true, []));
    }
}
