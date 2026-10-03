using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the control/execution seam: <see cref="PipelineControlDecisions"/>
/// (control plane) takes and returns pipeline-domain types only and never
/// touches sandbox or agent-runner types, while
/// <see cref="PipelineAgentExecutor"/> (data plane) never touches the
/// work-item store, project repository, or pipeline state. The remaining
/// tests pin the moved decision and execution primitives input-to-output so
/// a behavioural drift in either side fails here, not only in an
/// end-to-end pipeline run.
/// </summary>
public sealed class PipelineControlExecutionSplitTests
{
    private static readonly HashSet<string> ControlForbiddenNames = new(StringComparer.Ordinal)
    {
        "ISandbox", "ISandboxProvider", "IAgentRunner", "IAgentRegistry",
    };

    private static readonly HashSet<string> ExecutionForbiddenNames = new(StringComparer.Ordinal)
    {
        "WorkItem", "Project", "IWorkItemStore", "IProjectRepository",
        "IPipelineRunner", "PipelineRunner", "IWebhookDispatcher", "ITaskQueue",
    };

    private static void AssertTypeGraphClean(Type root, HashSet<string> forbiddenNames, string forbiddenNamespace)
    {
        var seen = new HashSet<Type>();
        var frontier = new Stack<Type>();
        foreach (var m in root.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            frontier.Push(m.ReturnType);
            foreach (var p in m.GetParameters())
                frontier.Push(p.ParameterType);
        }
        foreach (var f in root.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            frontier.Push(f.FieldType);

        while (frontier.Count > 0)
        {
            var t = frontier.Pop();
            if (t is null || !seen.Add(t))
                continue;
            if (t.IsGenericParameter)
                continue;
            var name = t.IsGenericType ? t.GetGenericTypeDefinition().Name.Split('`')[0] : t.Name;
            Assert.False(
                forbiddenNames.Contains(t.Name) || forbiddenNames.Contains(name),
                $"{root.Name} depends on forbidden type '{t.FullName}'.");
            Assert.False(
                string.Equals(t.Namespace, forbiddenNamespace, StringComparison.Ordinal),
                $"{root.Name} depends on forbidden namespace '{forbiddenNamespace}' via '{t.FullName}'.");
            if (t.IsGenericType)
            {
                foreach (var a in t.GetGenericArguments())
                    frontier.Push(a);
            }
        }
    }

    [Fact]
    public void ControlDecisions_HaveNoSandboxOrAgentRunnerDependencies()
    {
        AssertTypeGraphClean(
            typeof(PipelineControlDecisions),
            ControlForbiddenNames,
            "CodeyBox.Sandbox");
    }

    [Fact]
    public void AgentExecutor_HasNoControlPlaneDependencies()
    {
        AssertTypeGraphClean(
            typeof(PipelineAgentExecutor),
            ExecutionForbiddenNames,
            "CodeyBox.Projects");
    }

    private sealed class FakeAuditor(
        string name,
        string kind,
        AuditorRole role,
        bool canShortCircuit = false,
        BuildTestGateEvidence evidence = BuildTestGateEvidence.None) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool CanShortCircuitOnBlockingFinding { get; } = canShortCircuit;
        public AuditorRole Role { get; } = role;
        public BuildTestGateEvidence BuildTestGateEvidence { get; } = evidence;
        public Task<AuditResult> RunAsync(ISandbox _, string __, AuditContext ctx, CancellationToken ___ = default)
            => Task.FromResult(new AuditResult(Passed: true, Findings: [], RawOutput: "fake"));
    }

    private sealed class FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
    {
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PlainRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt,
            AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw new NotSupportedException();
    }

    private sealed class ExtractingRunner : IAgentRunner, IAgentVisibleTextExtractor
    {
        public AgentKind Kind => AgentKind.Claude;
        public string? ExtractAgentVisibleText(string rawStdout) => "visible";
        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt,
            AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw new NotSupportedException();
    }

    private static Project TestProject(int maxIterations = 3) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test",
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultBaseBranch = "main",
        DefaultAgent = AgentKind.Claude,
        Audit = new ProjectAudit { MaxIterations = maxIterations },
    };

    private static AuditResult Verdict(bool passed, params (AuditSeverity Severity, string Title)[] findings) =>
        new(Passed: passed,
            Findings: findings.Select(f => new AuditFinding("auditor", f.Severity, f.Title, "detail")).ToList(),
            RawOutput: "raw");

    private static AuditProgressSnapshot Snapshot(int iteration, int blocking, int nonBlocking = 0, string? tip = null) => new(
        Iteration: iteration,
        MaxIterations: 3,
        BlockingFindings: blocking,
        NonBlockingFindings: nonBlocking,
        BlockingFindingIds: [],
        BlockingFindingsDetails: [],
        Findings: [],
        WorkBranchTip: tip);

    [Fact]
    public void HasAuditBlockingFinding_RespectsFailingSeverity()
    {
        var project = TestProject();
        Assert.False(PipelineControlDecisions.HasAuditBlockingFinding(
            Verdict(true, (AuditSeverity.Warning, "advisory")), project));
        Assert.True(PipelineControlDecisions.HasAuditBlockingFinding(
            Verdict(false, (AuditSeverity.Error, "blocking")), project));
    }

    [Fact]
    public void IsDeclaredShortCircuitBlockingResult_FailsClosed()
    {
        Assert.False(PipelineControlDecisions.IsDeclaredShortCircuitBlockingResult(Verdict(true)));
        Assert.True(PipelineControlDecisions.IsDeclaredShortCircuitBlockingResult(Verdict(false)));
        Assert.True(PipelineControlDecisions.IsDeclaredShortCircuitBlockingResult(
            Verdict(true, (AuditSeverity.Error, "late error"))));
    }

    [Fact]
    public void AuditorOrdering_DecisionsMatchRoles()
    {
        var gate = new FakeAuditor("build", "tool", AuditorRole.BuildTestGate);
        var shortCircuit = new FakeAuditor("review", "llm", AuditorRole.None, canShortCircuit: true);
        var plain = new FakeAuditor("lint", "tool", AuditorRole.None);

        Assert.Equal(0, PipelineControlDecisions.BatchOrderingTier(gate, detectDeclaredShortCircuit: false));
        Assert.Equal(1, PipelineControlDecisions.BatchOrderingTier(shortCircuit, detectDeclaredShortCircuit: true));
        Assert.Equal(2, PipelineControlDecisions.BatchOrderingTier(shortCircuit, detectDeclaredShortCircuit: false));
        Assert.Equal(2, PipelineControlDecisions.BatchOrderingTier(plain, detectDeclaredShortCircuit: true));

        Assert.True(PipelineControlDecisions.RequiresPassedBuildTestGate(
            new FakeAuditor("review", "LLM", AuditorRole.None)));
        Assert.False(PipelineControlDecisions.RequiresPassedBuildTestGate(plain));
    }

    [Fact]
    public void MissingCompletedAuditors_ReportsOnlyUnfinished()
    {
        Assert.Empty(PipelineControlDecisions.MissingCompletedAuditors(null, ["a"]));
        Assert.Empty(PipelineControlDecisions.MissingCompletedAuditors([], ["a"]));
        Assert.Equal(
            ["b"],
            PipelineControlDecisions.MissingCompletedAuditors(["a", "b", "b"], ["a"]));
    }

    [Fact]
    public void ResolveAuditMaxIterations_ExtendsForPriorTrajectory()
    {
        var project = TestProject(maxIterations: 2);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Assert.Equal(2, PipelineControlDecisions.ResolveAuditMaxIterations(item, project, []));
        var extended = PipelineControlDecisions.ResolveAuditMaxIterations(
            item, project, [Snapshot(5, blocking: 1)]);
        Assert.Equal(7, extended);
    }

    [Fact]
    public void Convergence_DetectsDecreasingFindings()
    {
        Assert.False(PipelineControlDecisions.HasAuditConvergenceProgress([Snapshot(1, blocking: 2)]));
        Assert.False(PipelineControlDecisions.HasAuditConvergenceProgress(
            [Snapshot(1, blocking: 1), Snapshot(2, blocking: 1)]));
        Assert.True(PipelineControlDecisions.HasAuditConvergenceProgress(
            [Snapshot(1, blocking: 3), Snapshot(2, blocking: 1)]));
    }

    [Fact]
    public void FingerprintFindings_IsOrderIndependent()
    {
        static AuditFinding Finding(string title) => new("auditor", AuditSeverity.Error, title, "d", "file.cs:1");
        var forward = PipelineControlDecisions.FingerprintFindings([Finding("a"), Finding("b")]);
        var backward = PipelineControlDecisions.FingerprintFindings([Finding("b"), Finding("a")]);
        Assert.Equal(forward, backward);
        Assert.NotEqual(forward, PipelineControlDecisions.FingerprintFindings([Finding("a"), Finding("c")]));
    }

    [Fact]
    public void TruncateForEscalation_OnlyTruncatesLongValues()
    {
        Assert.Equal("short", PipelineControlDecisions.TruncateForEscalation("short"));
        var longValue = new string('x', 2500);
        var truncated = PipelineControlDecisions.TruncateForEscalation(longValue);
        Assert.EndsWith("...", truncated);
        Assert.Equal(2003, truncated.Length);
    }

    [Fact]
    public void DelegationOutcome_AdvancesOnlyOnCompleted()
    {
        Assert.True(PipelineControlDecisions.IsNonAdvancingDelegationOutcome(DelegationOutcomes.NoChanges));
        Assert.True(PipelineControlDecisions.IsNonAdvancingDelegationOutcome(DelegationOutcomes.Failed));
        Assert.False(PipelineControlDecisions.IsNonAdvancingDelegationOutcome(DelegationOutcomes.Completed));
    }

    [Fact]
    public void BuildEmptyReworkEscalationPrompt_PreservesOriginal()
    {
        var headerOnly = PipelineControlDecisions.BuildEmptyReworkEscalationPrompt("", 1, 2);
        Assert.StartsWith("[empty-rework escalation attempt 1/2]", headerOnly);
        Assert.EndsWith("do the thing", PipelineControlDecisions.BuildEmptyReworkEscalationPrompt("do the thing", 2, 2));
    }

    [Fact]
    public void ValidateAgentControlSpec_RejectsInvalidAdmission()
    {
        Assert.NotNull(PipelineControlDecisions.ValidateAgentControlSpec(new AgentControlSpec
        {
            Action = AgentControlAction.Pause,
            Agent = "  ",
            Reason = "r",
        }));
        Assert.NotNull(PipelineControlDecisions.ValidateAgentControlSpec(new AgentControlSpec
        {
            Action = AgentControlAction.Pause,
            Agent = "claude",
            Reason = null,
        }));
        Assert.NotNull(PipelineControlDecisions.ValidateAgentControlSpec(new AgentControlSpec
        {
            Action = AgentControlAction.Pause,
            Agent = "claude",
            Reason = "r",
            DurationSeconds = 0,
        }));
        Assert.Null(PipelineControlDecisions.ValidateAgentControlSpec(new AgentControlSpec
        {
            Action = AgentControlAction.Resume,
            Agent = "claude",
        }));
    }

    [Fact]
    public void AgentVisibleStdout_PrefersExtractorWhenPresent()
    {
        Assert.Equal("raw", PipelineAgentExecutor.AgentVisibleStdout(new PlainRunner(), "raw"));
        Assert.Equal("visible", PipelineAgentExecutor.AgentVisibleStdout(new ExtractingRunner(), "raw"));
    }

    [Fact]
    public void SanitiseCredentialFileName_RejectsTraversal()
    {
        Assert.Equal("token.json", PipelineAgentExecutor.SanitiseCredentialFileName("token.json"));
        Assert.ThrowsAny<Exception>(() => PipelineAgentExecutor.SanitiseCredentialFileName("../escape.json"));
    }

    [Fact]
    public async Task ExecutorRun_PropagatesFailureWithoutLeakingSecrets()
    {
        var ok = new FakeSandbox(_ => new SandboxExecResult(0, "out", ""));
        await PipelineAgentExecutor.Run(ok, "git", "status");

        var failing = new FakeSandbox(_ => new SandboxExecResult(1, "", "boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineAgentExecutor.Run(failing, "git", "status"));
        Assert.Contains("exit 1", ex.Message);

        var unavailable = new FakeSandbox(_ => new SandboxExecResult(99, "", "", ExecutionUnavailable: true));
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => PipelineAgentExecutor.RunWithCancellation(unavailable, CancellationToken.None, "git", "status"));

        var masked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineAgentExecutor.RunMasked(failing, "git", "config", "user.email", "secret@example.invalid"));
        Assert.Contains("***", masked.Message);
        Assert.DoesNotContain("secret@example.invalid", masked.Message);
    }

    [Fact]
    public async Task MaterialiseCredentialFiles_ExecutesAgainstSandbox()
    {
        var seen = new List<string>();
        var sandbox = new FakeSandbox(exec =>
        {
            seen.Add(string.Join(' ', exec.Argv));
            return new SandboxExecResult(0, "", "");
        });
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["token.json"] = "{}" });
        await PipelineAgentExecutor.MaterialiseCredentialFilesAsync(sandbox, credential, CancellationToken.None);
        Assert.NotEmpty(seen);
    }
}
