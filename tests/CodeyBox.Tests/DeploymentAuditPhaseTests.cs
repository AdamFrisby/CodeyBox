using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Deployment-verification chain (2/3): the audit iteration becomes a
/// cost-ordered phase ladder. Code-stage auditors run first; only a clean
/// code stage lazily provisions ONE deployment from the project's recipe,
/// deployment-targeted auditors run against the live endpoint, findings flow
/// into the normal rework loop, and teardown runs on every exit path with a
/// fresh deployment per iteration.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class DeploymentAuditPhaseTests : IDisposable
{
    private readonly string _workspace;
    public DeploymentAuditPhaseTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-deploy-audit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    private sealed record Outcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class CodeAuditor(Queue<Outcome> plan) : IAuditor
    {
        public string Name => "code:scripted";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<int> SeenIterations { get; } = [];
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SeenIterations.Add(context.Iteration);
            Assert.Null(context.DeploymentEndpoint);
            var outcome = plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class DeploymentProbeAuditor(Queue<Outcome> plan, bool blockOnCancel = false) : IAuditor
    {
        public string Name => "deploy:smoke";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlySet<AuditTarget> Targets => AuditTargets.DeploymentOnly;
        public List<DeploymentEndpoint?> SeenEndpoints { get; } = [];
        public List<AuditTarget> SeenTargets { get; } = [];
        public List<int> SeenIterations { get; } = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SeenEndpoints.Add(context.DeploymentEndpoint);
            SeenTargets.Add(context.EffectiveTarget);
            SeenIterations.Add(context.Iteration);
            _entered.TrySetResult();
            if (blockOnCancel)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            var outcome = plan.Dequeue();
            return new AuditResult(outcome.Passed, outcome.Findings);
        }
    }

    private sealed class FakeHandle(DeploymentEndpoint endpoint, string id, Action onDispose) : IDeploymentHandle
    {
        public string Id { get; } = id;
        public string Kind => DeploymentKinds.WebApp;
        public DeploymentEndpoint Endpoint { get; } = endpoint;
        public bool IsAlive => !_disposed;
        private bool _disposed;
        public string? SubstrateId => "substrate-" + Id;
        public Task HealthCheckAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeploymentCommandResult> ExecAsync(DeploymentCommand command, CancellationToken ct = default)
            => Task.FromResult(new DeploymentCommandResult(0, string.Empty, string.Empty));
        public ValueTask DisposeAsync()
        {
            _disposed = true;
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeManager : IDeploymentManager
    {
        private int _starts;
        private int _disposals;
        private readonly List<string> _handleIds = [];
        private readonly object _gate = new();
        public int StartCount { get { lock (_gate) return _starts; } }
        public int DisposeCount { get { lock (_gate) return _disposals; } }
        public IReadOnlyList<string> HandleIds { get { lock (_gate) return [.. _handleIds]; } }
        public DeploymentEndpoint Endpoint { get; } = new()
        {
            Kind = DeploymentEndpointKind.Http,
            Url = "http://127.0.0.1:18080",
        };
        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _starts++;
                var id = $"dep-{_starts}";
                _handleIds.Add(id);
                Assert.NotNull(context.SubstrateProvider);
                return Task.FromResult<IDeploymentHandle>(new FakeHandle(Endpoint, id, () =>
                {
                    lock (_gate) _disposals++;
                }));
            }
        }
        public IReadOnlyList<ActiveDeploymentInfo> GetActive() => [];
    }

    private sealed class FakeSubstrates : IDeploymentSubstrateProvider
    {
        public string Name => "fake";
        public Task<IDeploymentSubstrate> CreateAsync(DeploymentSubstrateSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException("FakeManager never reaches the substrate provider.");
    }

    private static DeploymentRecipe Recipe() => new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "img",
        MaxLifetime = TimeSpan.FromMinutes(30),
    };

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "deployment audit test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    private static ProjectAudit AuditWithDeployment(bool enabled, int maxIterations = 3) => new()
    {
        MaxIterations = maxIterations,
        AuditTypes = ["scripted"],
        DeploymentAuditEnabled = enabled,
    };

    [Fact]
    public async Task BlockingCodeStage_ProvisionsZeroDeployments()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([
            new(false, [new AuditFinding("code:scripted", AuditSeverity.Error, "broken", "x")]),
        ]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            maxAuditIterations: 1,
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(0, manager.StartCount);
        Assert.Empty(probe.SeenIterations);
    }

    [Fact]
    public async Task CleanCodeStage_ProvisionsExactlyOneDeploymentHandsEndpointAndTearsDown()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            projectAudit: AuditWithDeployment(enabled: true),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, manager.StartCount);
        var endpoint = Assert.Single(probe.SeenEndpoints);
        Assert.NotNull(endpoint);
        Assert.Equal("http://127.0.0.1:18080", endpoint!.Url);
        Assert.Equal([AuditTarget.Deployment], probe.SeenTargets);
        Assert.Equal(1, manager.DisposeCount);
    }

    [Fact]
    public async Task DeploymentFinding_FlowsIntoReworkLoopWithFreshDeploymentPerIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, []), new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([
            new(false, [new AuditFinding("deploy:smoke", AuditSeverity.Error, "health check failed", "x")]),
            new(true, []),
        ]));
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            projectAudit: AuditWithDeployment(enabled: true),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, manager.StartCount);
        Assert.Equal(2, manager.DisposeCount);
        Assert.Equal(2, manager.HandleIds.Distinct().Count());
        Assert.Equal([1, 2], probe.SeenIterations);
    }

    [Fact]
    public async Task ToggleOff_SkipsPhaseEntirely()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            projectAudit: AuditWithDeployment(enabled: false),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, manager.StartCount);
        Assert.Empty(probe.SeenIterations);
    }

    [Fact]
    public async Task NoDeploymentAuditors_SkipsPhaseEntirely()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code],
            projectAudit: AuditWithDeployment(enabled: true),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, manager.StartCount);
    }

    [Fact]
    public async Task MissingProvisioningWiring_FailsLoudlyInsteadOfFakePass()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            projectAudit: AuditWithDeployment(enabled: true),
            deploymentRecipe: Recipe());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Loudly visible infrastructure failure — never a fake pass. The
        // pipeline maps AuditUnavailableException to Failed/infrastructure.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("deployment stage", final.LastError);
        Assert.Empty(probe.SeenIterations);
    }

    [Fact]
    public async Task AbortMidDeploymentStage_TearsDownDeployment()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]), blockOnCancel: true);
        var manager = new FakeManager();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe],
            projectAudit: AuditWithDeployment(enabled: true),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        using var cts = new CancellationTokenSource();
        var runTask = tp.Pipeline.RunAsync(item, cts.Token);
        await probe.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(1, manager.StartCount);
        Assert.Equal(1, manager.DisposeCount);
    }
}
