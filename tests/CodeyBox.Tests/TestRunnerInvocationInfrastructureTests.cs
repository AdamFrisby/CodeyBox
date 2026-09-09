using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// A test-gate invocation error (the runner refused its arguments, so zero
/// tests executed) is an infrastructure fault: it must fail the item as
/// infrastructure, must not be handed to the rework agent as a code finding,
/// and must leave the item's rework budget intact.
///
/// Regression test for the 2026-09-07 incident, where
/// <c>dotnet test --no-build</c> against absent build artifacts surfaced as a
/// blocking <c>command exited 1</c> finding, burned a rework iteration on an
/// unfixable environment fault, and parked the item for operator review.
/// </summary>
[Collection("Pipeline integration")]
public sealed class TestRunnerInvocationInfrastructureTests : IDisposable
{
    private readonly string _workspace;

    public TestRunnerInvocationInfrastructureTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-testgate-infra-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task TestGateInvocationError_FailsAsInfrastructureWithoutRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new InvocationFailingTestGateAuditor();
        var involvement = new InMemoryAgentInvolvementStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 3,
            involvement: involvement);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "test gate invocation error",
            Prompt = "change the repo",
            WorkBranch = "feature/testgate-infra",
            BaseBranch = "main",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Infrastructure, not a code verdict: surfaced for operator attention
        // with the command and its output, not as findings.
        var final = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        Assert.Contains("could-not-verify", final.LastError ?? string.Empty, StringComparison.Ordinal);

        // The gate was attempted exactly once — never skipped, never retried
        // as a code finding.
        Assert.Equal(1, auditor.Calls);

        // No rework iteration was driven: exactly one agent turn (initial
        // work), no rework prompts, no rework involvement, and no second
        // dispatch row against the 3-iteration budget.
        Assert.Single(tp.Agent.WorkPrompts);
        var rows = await involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.DoesNotContain(rows, r => string.Equals(r.Phase, "rework", StringComparison.Ordinal));
        var iterations = await tp.Store.GetIterationsAsync(item.Id, CancellationToken.None);
        Assert.DoesNotContain(iterations, i => i.Iteration > 1);
    }

    /// <summary>
    /// Stands in for the <c>csharp:test-pass</c> gate after its classifier
    /// raises a runner-invocation refusal as <see cref="AuditUnavailableException"/>.
    /// Throws the incident-shaped fault instead of returning findings so the
    /// pipeline must route it as infrastructure.
    /// </summary>
    private sealed class InvocationFailingTestGateAuditor : IAuditor
    {
        public string Name => "csharp:test-pass";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool CanShortCircuitOnBlockingFinding => true;
        public AuditorRole Role => AuditorRole.BuildTestGate;
        public BuildTestGateEvidence BuildTestGateEvidence => BuildTestGateEvidence.Test;
        public int Calls { get; private set; }

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            Calls++;
            throw new AuditUnavailableException(
                "could-not-verify: test runner invocation failed for 'csharp:test-pass' (exit 1): "
                + "The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid. "
                + "(command: dotnet test --no-build)",
                1,
                "The argument /work/tests/CodeyBox.Tests/bin/Debug/net10.0/CodeyBox.Tests.dll is invalid. "
                + "Please use the /help option to check the list of valid arguments.");
        }
    }
}
