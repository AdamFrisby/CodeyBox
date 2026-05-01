using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that PipelineRunner emits timing rows through ITimingStore during
/// a real end-to-end run.  Uses the same Process-sandbox + scripted-agent
/// infrastructure as PipelineIntegrationTests.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerTimingTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerTimingTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-timing-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task SuccessfulRun_EmitsTimingRowsForWorkPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("timing-test.txt", "timing\n"));

        var item = NewItem("feature/timing");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var steps = timings.CompletedRows.Select(r => r.Step).ToHashSet();
        Assert.Contains("git.clone_into_sandbox", steps);
        Assert.Contains("agent.exec", steps);
        Assert.Contains("git.commit", steps);
        Assert.Contains("git.push_back_to_bare_repo", steps);
    }

    [Fact]
    public async Task SuccessfulRun_AllRowsHaveDurationSet()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("x.txt", "x\n"));

        var item = NewItem("feature/duration");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.All(timings.CompletedRows, row =>
        {
            Assert.NotNull(row.DurationMs);
            Assert.True(row.DurationMs >= 0);
        });
    }

    [Fact]
    public async Task SuccessfulRun_WorkPhaseRowsHaveCorrectPhaseLabel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("phase.txt", "work\n"));

        var item = NewItem("feature/phase-check");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var workRows = timings.CompletedRows.Where(r => r.Phase == "work").ToList();
        Assert.NotEmpty(workRows);
        Assert.All(workRows, r => Assert.Equal("work", r.Phase));
    }

    [Fact]
    public async Task SuccessfulRun_TimingRowsLinkedToCorrectWorkItem()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("id-check.txt", "id\n"));

        var item = NewItem("feature/id-check");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.All(timings.CompletedRows, r => Assert.Equal(item.Id, r.WorkItemId));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkItem NewItem(string branch) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Timing test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task FakeVmProvider_EmitsVmLifecycleTimingRowsInOrder()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        var inner = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var sandboxes = new FakeVmSandboxProvider(inner, timings);
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings, sandboxes);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("vm-test.txt", "vm\n"));

        var item = NewItem("feature/vm-lifecycle");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var vmRows = timings.CompletedRows
            .Where(r => r.Step is "vm.clone" or "vm.start" or "vm.dispose")
            .OrderBy(r => r.StartedAt)
            .ToList();

        Assert.Contains(vmRows, r => r.Step == "vm.clone");
        Assert.Contains(vmRows, r => r.Step == "vm.start");
        Assert.Contains(vmRows, r => r.Step == "vm.dispose");

        var cloneIdx = vmRows.FindIndex(r => r.Step == "vm.clone");
        var startIdx = vmRows.FindIndex(r => r.Step == "vm.start");
        var disposeIdx = vmRows.FindIndex(r => r.Step == "vm.dispose");

        Assert.True(cloneIdx < startIdx, "vm.clone must precede vm.start");
        Assert.True(startIdx < disposeIdx, "vm.start must precede vm.dispose");
    }

    private static TestPipeline BuildPipelineWithTimings(
        string workspace,
        string seedRepoUrl,
        RecordingTimingStore timingStore,
        ISandboxProvider? sandboxProvider = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        ISandboxProvider sandboxes = sandboxProvider ?? new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });

        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var upstreamFactory = new TestUpstreamFactory();

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            },
            NullLogger<PipelineRunner>.Instance,
            timingStore: timingStore);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    // ── Fake VM sandbox provider ─────────────────────────────────────────────

    /// <summary>
    /// Wraps ProcessSandboxProvider and emits vm.clone, vm.start, and vm.dispose
    /// timing rows so tests can assert the VM lifecycle ordering without a real
    /// Multipass installation.
    /// </summary>
    internal sealed class FakeVmSandboxProvider : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner;
        private readonly ITimingStore _timings;

        public FakeVmSandboxProvider(ProcessSandboxProvider inner, ITimingStore timings)
        {
            _inner = inner;
            _timings = timings;
        }

        public string Name => "fake-vm";

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var itemId = spec.TimingWorkItemId;
            var phase = spec.TimingPhase ?? "work";

            if (itemId.HasValue)
            {
                await using (var cloneScope = await TimingScope.BeginAsync(_timings, itemId.Value, phase, "vm.clone"))
                    await Task.Delay(1, ct);
                await using (var startScope = await TimingScope.BeginAsync(_timings, itemId.Value, phase, "vm.start"))
                    await Task.Delay(1, ct);
            }

            var inner = await _inner.CreateAsync(spec, ct);
            return itemId.HasValue
                ? new TimedFakeSandbox(inner, _timings, itemId.Value, phase)
                : inner;
        }
    }

    internal sealed class TimedFakeSandbox : ISandbox
    {
        private readonly ISandbox _inner;
        private readonly ITimingStore _timings;
        private readonly WorkItemId _itemId;
        private readonly string _phase;

        public string Id => _inner.Id;

        public TimedFakeSandbox(ISandbox inner, ITimingStore timings, WorkItemId itemId, string phase)
        {
            _inner = inner;
            _timings = timings;
            _itemId = itemId;
            _phase = phase;
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => _inner.ExecAsync(exec, ct);

        public async ValueTask DisposeAsync()
        {
            await using (var disposeScope = await TimingScope.BeginAsync(_timings, _itemId, _phase, "vm.dispose"))
                await _inner.DisposeAsync();
        }
    }

    // ── Recording store ───────────────────────────────────────────────────────

    internal sealed class RecordingTimingStore : ITimingStore
    {
        private readonly Dictionary<string, TimingRecord> _inFlight = new();
        private readonly List<TimingRecord> _completed = new();

        public IReadOnlyList<TimingRecord> CompletedRows
        {
            get { lock (_completed) return [.. _completed]; }
        }

        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
        {
            lock (_inFlight) _inFlight[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
        {
            TimingRecord? rec;
            lock (_inFlight)
            {
                _inFlight.TryGetValue(id, out rec);
                _inFlight.Remove(id);
            }
            if (rec is not null)
            {
                var completed = rec with { EndedAt = endedAt, DurationMs = durationMs };
                lock (_completed) _completed.Add(completed);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimingRecord>>(
                CompletedRows.Where(r => r.WorkItemId == id).ToList());

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
            int workItemLimit,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var r in CompletedRows)
                yield return r;
        }
    }
}
