using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the session-path pre-emptive self-review turn enhancement.
///
/// <para>The feature injects ONE extra warm-session turn after the worker's
/// initial work turn (before the formal audit) using the composer-built
/// guidance from the project's active auditors. The formal audit still runs
/// independently in its own fresh sandbox and owns pass/fail. Default OFF;
/// preserve current behaviour.</para>
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PreemptiveSelfReviewSessionTurnTests : IDisposable
{
    private readonly string _workspace;

    public PreemptiveSelfReviewSessionTurnTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-preempt-self-review-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task FeatureOff_DefaultBehaviour_OnlyWorkTurnFires_NoSelfReviewTurn()
    {
        // Acceptance: "default OFF; preserve current behaviour". With the
        // feature flag off, a session item runs exactly ONE worker turn
        // (work), then the audit, exactly as before this enhancement.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);
        var auditor = new GuidanceContributingAuditor(
            "ScriptedWithGuidance",
            guidance: "- thing to check",
            plan: [new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            // Session ON, but the self-review enhancement OFF (the default).
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = false,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // ONE worker turn — the self-review enhancement did NOT fire a
        // second turn, preserving today's session-mode behaviour.
        Assert.Equal(1, sessionRunner.SendTurns);
    }

    [Fact]
    public async Task FeatureOn_WithAuditorGuidance_RunsTwoTurnsOnSameSession_BeforeAudit()
    {
        // Acceptance: with the feature ON, a session item runs an initial
        // work turn THEN a self-review turn in the SAME session BEFORE the
        // formal audit, using the composed guidance.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        // First worker turn writes the work file; second writes a self-
        // review fix on top so we observe a real diff to commit.
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
            new RecordingFileWrite("a.txt", "v1-self-review-fixed"),
        ]);
        var auditor = new GuidanceContributingAuditor(
            "ScriptedWithGuidance",
            guidance: "- guidance-marker-12345",
            plan: [new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = true,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Exactly TWO worker turns: work + self-review (same session).
        Assert.Equal(2, sessionRunner.SendTurns);

        // Both turns ran on the same handle — session continuity is the
        // whole point (cache-hot, near-free 2nd turn).
        var handleIds = sessionRunner.HandleIdsObserved.ToArray();
        Assert.Equal(2, handleIds.Length);
        Assert.Equal(handleIds[0], handleIds[1]);
        Assert.Equal(1, sessionRunner.OpenedSessions);

        // The second turn carried the auditor's composed guidance, framed
        // as the brief specifies (good-faith "fix any GENUINE issues",
        // NOT "maximise compliance").
        var prompts = sessionRunner.PromptsSent.ToArray();
        Assert.Contains("guidance-marker-12345", prompts[1]);
        Assert.Contains("GENUINE issues", prompts[1]);
        Assert.Contains("independent auditor", prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximise compliance", prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximize compliance", prompts[1], StringComparison.OrdinalIgnoreCase);

        // The auditor (which runs in a fresh sandbox via the legacy audit
        // path) saw the post-self-review committed code, but the auditor
        // itself never ran on the worker's session — auditor isolation is
        // verified by SessionMode_PreemptiveSelfReview_AuditorRunsInSeparateSandbox.
    }

    [Fact]
    public async Task SessionMode_PreemptiveSelfReview_AuditorRunsInSeparateSandbox()
    {
        // Acceptance: "the formal audit runs independently in a separate
        // fresh session and owns pass/fail". The pre-emptive self-review
        // turn must NOT degrade auditor isolation — the auditor still
        // runs on its own VM and never sees the worker's session handle.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
            new RecordingFileWrite("a.txt", "v1-self-review-fixed"),
        ]);
        var auditor = new GuidanceContributingSandboxRecordingAuditor(
            name: "ScriptedRecorder",
            guidance: "- watch for issues",
            outcomes: [new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = true,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Pre-emptive self-review fired (2 worker turns on same session).
        Assert.Equal(2, sessionRunner.SendTurns);

        // Auditor was invoked at least once and ran on a sandbox that the
        // worker session never touched. The session worker's RecordingSessionRunner
        // captures every sandbox id observed on a SendTurnAsync; the
        // auditor records every sandbox id it ran against. The two sets
        // must be disjoint — that's the "separate fresh session" contract.
        Assert.NotEmpty(auditor.SandboxIdsObserved);
        var workerSandboxIds = sessionRunner.SandboxIdsObservedOnTurns.ToHashSet(StringComparer.Ordinal);
        foreach (var auditorSandboxId in auditor.SandboxIdsObserved)
            Assert.False(workerSandboxIds.Contains(auditorSandboxId),
                $"auditor reused the worker sandbox '{auditorSandboxId}' — pre-emptive self-review broke isolation!");

        // The auditor's one-shot run path was never asked to invoke the
        // session runner. The runner's RunAsync (legacy one-shot path)
        // count stays zero — auditors talk to ScriptedAgent through the
        // legacy fresh-sandbox path.
        Assert.Equal(0, sessionRunner.OneShotRunAsyncCalls);
    }

    [Fact]
    public async Task FeatureOn_NoAuditorContributesGuidance_DoesNotFireSelfReviewTurn()
    {
        // The composer is the source of truth — if no auditor opts in via
        // SelfReviewGuidance (e.g. the cheating-auditor scenario where every
        // auditor returns null), there's nothing to feed the agent. The
        // pipeline must skip the extra turn rather than send an empty prompt.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);
        // ScriptedAuditor from PipelineRunnerClaudeSessionWiringTests inherits
        // SelfReviewGuidance = null; constructing a similar in-fixture
        // double here covers the empty-composer path.
        var auditor = new NoGuidanceAuditor("ScriptedNoGuidance", [new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = true,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // No guidance ⇒ no second turn.
        Assert.Equal(1, sessionRunner.SendTurns);
    }

    [Fact]
    public async Task FeatureOn_SelfReviewTurnFaults_DoesNotStrand_AuditStillRuns()
    {
        // The brief is strict: "the formal audit + rework loop still owns
        // convergence." A failing self-review turn must be SOFT — log, skip
        // commit, fall through to audit. The work item must still progress.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);
        // The second turn (self-review) faults — we want to see the item
        // still reach Done.
        sessionRunner.SelfReviewTurnFault = new InvalidOperationException("self-review boom");

        var auditor = new GuidanceContributingAuditor(
            "ScriptedWithGuidance",
            guidance: "- fix things",
            plan: [new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = true,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // No stranding: even though self-review faulted, audit still ran
        // and the item reached Done via the normal merge path.
        Assert.Equal(WorkItemState.Done, final!.State);

        // Two turns were attempted (work + self-review); the second one
        // threw so SendTurns increments to 2.
        Assert.Equal(2, sessionRunner.SendTurns);
    }

    [Fact]
    public void BuildPreemptiveSelfReviewPrompt_FrameIsGoodFaith_NotCompliance()
    {
        // The brief specifies the exact framing: "before an INDEPENDENT
        // auditor reviews this against the following criteria, fix any
        // GENUINE issues" — NOT "maximise compliance." Pin the framing so
        // a future edit can't quietly drift it.
        var prompt = PipelineRunner.BuildPreemptiveSelfReviewPrompt(
            guidance: "- example check",
            promptRevisionAtDispatch: 42);

        Assert.Contains("independent auditor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GENUINE issues", prompt);
        Assert.Contains("- example check", prompt);
        // Anti-compliance framing.
        Assert.DoesNotContain("maximise compliance", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximize compliance", prompt, StringComparison.OrdinalIgnoreCase);
        // Prompt-revision trailer guidance is included so any commit the
        // agent makes carries the same revision as the prior work turn.
        Assert.Contains("**42**", prompt);
    }

    [Fact]
    public void Composer_OmitsCheatingGuidance_PerOptOutAtSource()
    {
        // The brief calls out: "cheating opted out at source". The composer
        // skips any auditor whose SelfReviewGuidance returns null. A
        // double that returns null for a "cheating"-named auditor must not
        // appear in the composed output.
        var composed = CodeyBox.Projects.SelfReviewChecklistComposer.Compose(
        [
            new GuidanceContributingAuditor("review:cheating", guidance: null,
                plan: [new AuditOutcome(true, [])]),
            new GuidanceContributingAuditor("review:quality", guidance: "- be tidy",
                plan: [new AuditOutcome(true, [])]),
        ]);
        Assert.Contains("- be tidy", composed);
        Assert.DoesNotContain("cheating", composed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FeatureOn_AuditPasses_EmitsFirstAuditMetric_TaggedSelfReviewOn()
    {
        // The measurement the brief requires: first-audit pass-rate WITH
        // the pre-emptive self-review turn. We listen for the
        // codeybox.session.first_audit.outcome counter and assert the
        // emitted tag set carries self_review=on / outcome=passed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
            new RecordingFileWrite("a.txt", "v1-after-self-review"),
        ]);
        var auditor = new GuidanceContributingAuditor(
            "ScriptedWithGuidance",
            guidance: "- always check",
            plan: [new AuditOutcome(true, [])]);

        var captured = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "codeybox.session.first_audit.outcome")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < tags.Length; i++)
                dict[tags[i].Key] = tags[i].Value;
            lock (captured)
                captured.Add((value, dict));
        });
        listener.Start();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = true,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        lock (captured)
        {
            Assert.NotEmpty(captured);
            var firstAudit = captured[0];
            Assert.Equal(1, firstAudit.Value);
            Assert.Equal("on", firstAudit.Tags["self_review"]);
            Assert.Equal("passed", firstAudit.Tags["outcome"]);
        }
    }

    [Fact]
    public async Task FeatureOff_AuditPasses_EmitsFirstAuditMetric_TaggedSelfReviewOff()
    {
        // The control group for the measurement: session items WITHOUT the
        // pre-emptive self-review turn must still emit the first-audit
        // metric, tagged self_review=off, so the two distributions are
        // directly comparable in dashboards.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);
        var auditor = new GuidanceContributingAuditor(
            "ScriptedWithGuidance",
            guidance: "- always check",
            plan: [new AuditOutcome(true, [])]);

        var captured = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "codeybox.session.first_audit.outcome")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < tags.Length; i++)
                dict[tags[i].Key] = tags[i].Value;
            lock (captured)
                captured.Add((value, dict));
        });
        listener.Start();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            sessionDispatchOptions: new AgentSessionDispatchOptions
            {
                Enabled = true,
                PreemptiveSelfReviewEnabled = false,
            },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        lock (captured)
        {
            Assert.NotEmpty(captured);
            var firstAudit = captured[0];
            Assert.Equal(1, firstAudit.Value);
            Assert.Equal("off", firstAudit.Tags["self_review"]);
            Assert.Equal("passed", firstAudit.Tags["outcome"]);
        }
    }

    // ─── helpers / doubles ────────────────────────────────────────────────

    private static Project ProjectWithSessionEnabled(
        string repoUrl,
        bool enabled = true,
        bool withScriptedAuditors = false) => new()
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = repoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            ClaudeSession = new ProjectClaudeSessionConfig { Enabled = enabled },
            Audit = withScriptedAuditors
                ? new ProjectAudit { MaxIterations = 10, AuditTypes = ["scripted"] }
                : new ProjectAudit(),
        };

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "self-review test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/self-review",
        PushUpstream = false,
        Agent = AgentKind.Claude,
    };

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class GuidanceContributingAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        private readonly string? _guidance;
        public GuidanceContributingAuditor(string name, string? guidance, IEnumerable<AuditOutcome> plan)
        {
            Name = name;
            _guidance = guidance;
            _plan = new Queue<AuditOutcome>(plan);
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public string? SelfReviewGuidance => _guidance;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            var outcome = _plan.Count > 0 ? _plan.Dequeue() : new AuditOutcome(true, []);
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class NoGuidanceAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public NoGuidanceAuditor(string name, IEnumerable<AuditOutcome> plan)
        {
            Name = name;
            _plan = new Queue<AuditOutcome>(plan);
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        // SelfReviewGuidance inherits the default (null) so the composer skips it.
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            var outcome = _plan.Count > 0 ? _plan.Dequeue() : new AuditOutcome(true, []);
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class GuidanceContributingSandboxRecordingAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _outcomes;
        private readonly string? _guidance;
        public GuidanceContributingSandboxRecordingAuditor(string name, string? guidance, IEnumerable<AuditOutcome> outcomes)
        {
            Name = name;
            _guidance = guidance;
            _outcomes = new Queue<AuditOutcome>(outcomes);
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public string? SelfReviewGuidance => _guidance;
        public ConcurrentQueue<string> SandboxIdsObserved { get; } = new();
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SandboxIdsObserved.Enqueue(sandbox.Id);
            var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : new AuditOutcome(true, []);
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed record RecordingFileWrite(string FileName, string Contents);

    /// <summary>
    /// Mirrors the in-fixture RecordingSessionRunner from
    /// PipelineRunnerClaudeSessionWiringTests but exposed in this file so
    /// the self-review tests don't depend on that fixture's private types.
    /// </summary>
    private sealed class RecordingSessionRunner : IScopedSessionAgentRunner
    {
        private readonly Queue<RecordingFileWrite> _turnFiles;
        private ISandbox? _capturedSandbox;
        private string? _workingDirectory;

        public RecordingSessionRunner(IEnumerable<RecordingFileWrite> turnFiles)
        {
            _turnFiles = new Queue<RecordingFileWrite>(turnFiles);
        }

        public AgentKind Kind => AgentKind.Claude;
        public int OpenedSessions;
        public int SendTurns;
        public int OneShotRunAsyncCalls;
        public string? OpenedHandleId;
        /// <summary>If set, the second SendTurnAsync call throws this exception.</summary>
        public Exception? SelfReviewTurnFault { get; set; }
        public ConcurrentQueue<string> HandleIdsObserved { get; } = new();
        public ConcurrentQueue<string> SandboxIdsObservedOnTurns { get; } = new();
        public ConcurrentQueue<string> PromptsSent { get; } = new();

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            Interlocked.Increment(ref OneShotRunAsyncCalls);
            return Task.FromResult(new AgentResult(true, "ok", null, null));
        }

        public AgentFailureClassification ClassifyFailure(AgentResult result)
            => new(AgentFailureKind.Normal);

        public Task<AgentSessionHandle> OpenSessionAsync(
            ISandbox sandbox, string workingDirectory, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref OpenedSessions);
            _capturedSandbox = sandbox;
            _workingDirectory = workingDirectory;
            var handleId = $"claude-session-test-{OpenedSessions}";
            OpenedHandleId = handleId;
            return Task.FromResult(new AgentSessionHandle(
                Kind,
                handleId,
                new AgentSessionSandboxRef(sandbox.Id),
                workingDirectory,
                modelId,
                reasoningMode));
        }

        public Task<AgentSessionHandle> OpenSessionAsync(AgentSessionOpenRequest request, CancellationToken ct = default)
            => OpenSessionAsync(request.Sandbox, request.WorkingDirectory, request.Credential,
                request.ModelId, request.ReasoningMode, ct);

        public async Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            var turnIndex = Interlocked.Increment(ref SendTurns);
            HandleIdsObserved.Enqueue(sessionHandle.SessionId);
            PromptsSent.Enqueue(prompt);
            if (_capturedSandbox is not null)
                SandboxIdsObservedOnTurns.Enqueue(_capturedSandbox.Id);

            if (turnIndex == 2 && SelfReviewTurnFault is not null)
                throw SelfReviewTurnFault;

            if (_turnFiles.Count == 0)
                return new AgentResult(true, "ok", null, null);

            var file = _turnFiles.Dequeue();
            var path = $"{_workingDirectory}/{file.FileName}";
            var result = await _capturedSandbox!.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = file.Contents,
            }, ct);
            return result.Success
                ? new AgentResult(true, "ok", null, null)
                : new AgentResult(false, "fail", result.Stdout, result.Stderr);
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
            => Task.CompletedTask;

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            if (_capturedSandbox is not null)
                await _capturedSandbox.DisposeAsync();
        }
    }
}
