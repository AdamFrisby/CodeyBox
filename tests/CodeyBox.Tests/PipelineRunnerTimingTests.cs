using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
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

    [Fact]
    public async Task SuccessfulRun_EmitsPipelineSpansAndInvocationMetrics()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        using var tp = BuildPipelineWithTimings(_workspace, seed, timings);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("otel.txt", "otel\n"));

        // Captures must be live before RunAsync — an ActivitySource emits no
        // Activity unless a listener is sampling, and the MeterListener only sees
        // measurements recorded after Start.
        using var spans = new SpanCapture("CodeyBox.Pipeline");
        using var metrics = new MetricCapture("codeybox.agent.invocations", "codeybox.phase.duration_ms");

        var item = NewItem("feature/otel-spans");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Root span + work-phase span + at least one agent.invoke span, tagged.
        Assert.True(spans.Any("pipeline.run", ("codeybox.work_item_id", item.Id.ToString())),
            "expected a pipeline.run root span for the work item");
        Assert.True(spans.Any("phase.work", ("codeybox.phase", "work")),
            "expected a phase.work span");
        Assert.True(spans.Any("agent.invoke", ("codeybox.phase", "work"), ("codeybox.outcome", "success")),
            "expected a successful agent.invoke span in the work phase");

        // The pickup phase wraps the pre-work rebase/reset that every fresh
        // Queued run executes. Asserting it here prevents a regression that
        // dropped or renamed the BeginPhaseScope(item, "pickup") wrapper from
        // silently passing the rest of this suite.
        Assert.True(spans.Any("phase.pickup", ("codeybox.phase", "pickup")),
            "expected a phase.pickup span on a fresh Queued pipeline entry");

        // Invocation counter + phase-duration histogram fired on the real run.
        Assert.True(metrics.Any("codeybox.agent.invocations", ("phase", "work"), ("outcome", "success")),
            "expected a codeybox.agent.invocations measurement for the successful work invocation");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "work")),
            "expected a codeybox.phase.duration_ms{phase=work} measurement");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "pickup")),
            "expected a codeybox.phase.duration_ms{phase=pickup} measurement");

        // The merge phase (RealMerge, non-empty merge path) opens its own
        // phase.merge span and records a phase=merge duration sample. Asserting
        // only phase.work would let a regression that dropped or mis-tagged the
        // merge scope pass unnoticed on this full Done pipeline.
        Assert.True(spans.Any("phase.merge", ("codeybox.phase", "merge")),
            "expected a phase.merge span on the full Done pipeline");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "merge")),
            "expected a codeybox.phase.duration_ms{phase=merge} measurement");
    }

    [Fact]
    public async Task AuditRun_EmitsAuditorSubStepTimingFromRawOutput()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        var auditor = new RawOutputAuditor(
            "csharp:build-WaE",
            """
            Build succeeded.
            Time Elapsed 00:00:01.234
            """);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 1,
            timingStore: timings,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-substep.txt", "audit\n"));

        var item = NewItem("feature/audit-substep");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var subStep = Assert.Single(timings.CompletedRows, r => r.Step == "csharp.build");
        Assert.Equal(item.Id, subStep.WorkItemId);
        Assert.Equal("audit", subStep.Phase);
        Assert.Equal(1, subStep.Iteration);
        Assert.Equal(1_234, subStep.DurationMs);
        Assert.Equal("{}", subStep.MetadataJson);
        Assert.Equal(subStep.StartedAt.AddMilliseconds(1_234), subStep.EndedAt);
    }

    [Fact]
    public async Task AuditRun_EmitsFallbackToolCallTelemetryFromAuditorRawOutputThroughRunAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        var counter = new StaticToolCallCounter(
            AgentKind.Claude,
            "audit stream-json",
            new AgentToolCallCounts(
                new Dictionary<string, int> { ["AuditTool"] = 3 },
                FinalText: "audit complete"));
        var auditor = new RawOutputAuditor("quality:llm-review", "audit stream-json");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 1,
            timingStore: timings,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            toolCallCounters: new Dictionary<AgentKind, IAgentToolCallCounter>
            {
                [AgentKind.Claude] = counter,
            });

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-tool-call-telemetry.txt", "audit\n"));

        var item = NewItem("feature/audit-tool-call-telemetry");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("audit stream-json", counter.SeenStdouts);

        var auditorExec = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "audit" && r.Step == "auditor.quality:llm-review");
        var tool = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "audit" && r.Step == "agent.tool_call.AuditTool");
        Assert.Equal(item.Id, tool.WorkItemId);
        Assert.Equal(1, tool.Iteration);
        Assert.Equal(0, tool.DurationMs);
        Assert.Equal(tool.StartedAt, tool.EndedAt);
        using (var metadata = JsonDocument.Parse(tool.MetadataJson))
        {
            Assert.Equal(3, metadata.RootElement.GetProperty("count").GetInt32());
        }

        var thinking = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "audit" && r.Step == "agent.thinking_aggregate");
        Assert.Equal(item.Id, thinking.WorkItemId);
        Assert.Equal(1, thinking.Iteration);
        Assert.Equal(auditorExec.DurationMs, thinking.DurationMs);
        Assert.Equal("{}", thinking.MetadataJson);
        Assert.Equal(thinking.StartedAt.AddMilliseconds(thinking.DurationMs!.Value), thinking.EndedAt);
    }

    [Fact]
    public async Task UnstructuredWorkAgent_EmitsFallbackToolCallTelemetryThroughRunAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        var counter = new StaticToolCallCounter(
            AgentKind.Claude,
            "plain stream-json",
            new AgentToolCallCounts(
                new Dictionary<string, int> { ["Bash"] = 2 },
                FinalText: "done"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            timingStore: timings,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            toolCallCounters: new Dictionary<AgentKind, IAgentToolCallCounter>
            {
                [AgentKind.Claude] = counter,
            });

        tp.Agent.ResultStdout = "plain stream-json";
        tp.Agent.WorkPlan.Enqueue(new FileWrite("tool-call-telemetry.txt", "telemetry\n"));

        var item = NewItem("feature/tool-call-telemetry");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("plain stream-json", counter.SeenStdouts);

        var agentExec = Assert.Single(timings.CompletedRows, r => r.Phase == "work" && r.Step == "agent.exec");
        var tool = Assert.Single(timings.CompletedRows, r => r.Step == "agent.tool_call.Bash");
        Assert.Equal(item.Id, tool.WorkItemId);
        Assert.Equal("work", tool.Phase);
        Assert.Null(tool.Iteration);
        Assert.Equal(0, tool.DurationMs);
        Assert.Equal(tool.StartedAt, tool.EndedAt);
        using (var metadata = JsonDocument.Parse(tool.MetadataJson))
        {
            Assert.Equal(2, metadata.RootElement.GetProperty("count").GetInt32());
        }

        var thinking = Assert.Single(timings.CompletedRows, r => r.Step == "agent.thinking_aggregate");
        Assert.Equal(item.Id, thinking.WorkItemId);
        Assert.Equal("work", thinking.Phase);
        Assert.Null(thinking.Iteration);
        Assert.Equal(agentExec.DurationMs, thinking.DurationMs);
        Assert.Equal("{}", thinking.MetadataJson);
        Assert.Equal(thinking.StartedAt.AddMilliseconds(thinking.DurationMs!.Value), thinking.EndedAt);
    }

    [Fact]
    public async Task ConflictMerge_EmitsFallbackToolCallTelemetryFromResolverStdoutThroughRunAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var timings = new RecordingTimingStore();
        var counter = new StaticToolCallCounter(
            AgentKind.Claude,
            "merge stream-json",
            new AgentToolCallCounts(
                new Dictionary<string, int> { ["MergeTool"] = 1 },
                FinalText: "merge complete"));
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 1,
            timingStore: timings,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            toolCallCounters: new Dictionary<AgentKind, IAgentToolCallCounter>
            {
                [AgentKind.Claude] = counter,
            });
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));
        tp.Agent.AgenticConflictResultStdout = "merge stream-json";
        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("README.md", file.Path);
            Assert.Contains("main side", file.Content);
            Assert.Contains("work side", file.Content);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "main side\nwork side\n",
            };
        });

        var item = NewItem("feature/merge-tool-call-telemetry");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("merge stream-json", counter.SeenStdouts);

        var mergeExec = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "merge" && r.Step == "agent.exec");
        var tool = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "merge" && r.Step == "agent.tool_call.MergeTool");
        Assert.Equal(item.Id, tool.WorkItemId);
        Assert.Null(tool.Iteration);
        Assert.Equal(0, tool.DurationMs);
        Assert.Equal(tool.StartedAt, tool.EndedAt);
        using (var metadata = JsonDocument.Parse(tool.MetadataJson))
        {
            Assert.Equal(1, metadata.RootElement.GetProperty("count").GetInt32());
        }

        var thinking = Assert.Single(
            timings.CompletedRows,
            r => r.Phase == "merge" && r.Step == "agent.thinking_aggregate");
        Assert.Equal(item.Id, thinking.WorkItemId);
        Assert.Null(thinking.Iteration);
        Assert.Equal(mergeExec.DurationMs, thinking.DurationMs);
        Assert.Equal("{}", thinking.MetadataJson);
        Assert.Equal(thinking.StartedAt.AddMilliseconds(thinking.DurationMs!.Value), thinking.EndedAt);
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
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            },
            NullLogger<PipelineRunner>.Instance,
            timingStore: timingStore,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

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

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            _inner.DisposeLeakedAsync(name, ct);
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

    private sealed class RawOutputAuditor(string name, string rawOutput) : IAuditor
    {
        public string Name => name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

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
            return Task.FromResult(new AuditResult(true, [], rawOutput));
        }
    }

    private sealed class MainAdvancingAuditor(string workspace, string path, string content) : IAuditor
    {
        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");

            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, path), content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }

    private sealed class StaticToolCallCounter(
        AgentKind kind,
        string expectedStdout,
        AgentToolCallCounts counts) : IAgentToolCallCounter
    {
        public AgentKind Kind => kind;
        public List<string?> SeenStdouts { get; } = [];

        public AgentToolCallCounts? TryCount(string? bufferedStdout)
        {
            SeenStdouts.Add(bufferedStdout);
            return bufferedStdout == expectedStdout ? counts : null;
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
