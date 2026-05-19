using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
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
/// Integration tests for the transient-cancellation auto-retry path. When the
/// pipeline catches an <see cref="PhaseCancellationException"/> whose source
/// couldn't be attributed to operator cancel / host shutdown / configured
/// timeout, it should:
///   1. Increment <see cref="WorkItem.TransientCancelRetries"/>
///   2. Reset the item to a recoverable pre-phase state
///   3. Re-enqueue (when ITaskQueue is wired)
///   4. Eventually surface as Failed with failureKind=cancelled after the cap
/// </summary>
[Collection("Pipeline integration")]
public sealed class TransientCancellationRetryTests : IDisposable
{
    private readonly string _workspace;

    public TransientCancellationRetryTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-transient-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task UnattributedCancellation_AutoRetries_IncrementingCounter()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, maxRetries: 3);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        // Configure the agent to cancel its OWN inner token directly (mimicking
        // a leaked/external supervisor CTS) so the pipeline sees an
        // unattributed OCE. The agent never sets its phase as a known source.
        harness.Agent.Behaviour = HoldThenCancelInternally;

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(1, after!.TransientCancelRetries);
        // Auto-retry resets the work phase to Queued (mirrors WorkItemRetrier).
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(CancellationSources.Unknown, after.CancellationSource);
        // Items in the auto-retry state must NOT be marked Failed.
        Assert.NotEqual(WorkItemState.Failed, after.State);
        // The queue should have been kicked so the orchestrator picks it back up.
        Assert.Contains(item.Id, harness.Queue.Enqueued);
    }

    [Fact]
    public async Task UnattributedCancellation_AfterMaxRetries_TransitionsFailedWithCancelledKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, maxRetries: 2);

        var item = NewItem() with { TransientCancelRetries = 2 }; // already at the cap
        await harness.Store.CreateAsync(item);

        harness.Agent.Behaviour = HoldThenCancelInternally;

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("cancelled", after.FailureKind);
        Assert.Equal(CancellationSources.Unknown, after.CancellationSource);
        // The clearer error message must point the operator at the real cause.
        Assert.Contains("cancellation-token leak", after.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transient-cancel retries", after.LastError!);
    }

    [Fact]
    public async Task MaxRetriesZero_DisablesAutoRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, maxRetries: 0);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        harness.Agent.Behaviour = HoldThenCancelInternally;

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("cancelled", after.FailureKind);
        Assert.Contains("auto-retry disabled", after.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfiguredTimeout_StillSurfacedAsTimeoutFailureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, maxRetries: 3);

        // WorkTimeout very short forces the configured-timeout path; the agent
        // blocks indefinitely. Pipeline should record failureKind=timeout with
        // cancellationSource=timeout:work, NOT route to the auto-retry path.
        var item = NewItem() with { WorkTimeout = TimeSpan.FromMilliseconds(250) };
        await harness.Store.CreateAsync(item);

        harness.Agent.Behaviour = async (sandbox, dir, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentResult(false, "unreachable", null, null);
        };

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("timeout", after.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("work"), after.CancellationSource);
        // The auto-retry path must not bump the counter for a deliberate timeout.
        Assert.Equal(0, after.TransientCancelRetries);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        PushUpstream = false,
    };

    private TransientHarness BuildHarness(string seedRepoUrl, int maxRetries)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ProgrammableAgent();
        var registry = new AgentRegistry([agent]);
        var queue = new RecordingTaskQueue();

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

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer, store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            taskQueue: queue,
            orchestratorOptions: new OrchestratorOptions { MaxTransientCancelRetries = maxRetries });

        return new TransientHarness(pipeline, store, gitHost, agent, queue);
    }

    private static async Task<AgentResult> HoldThenCancelInternally(ISandbox sandbox, string dir, CancellationToken ct)
    {
        // Wait for cancellation to fire, then throw a raw TaskCanceledException
        // that matches the prod smoking-gun (lastError='A task was canceled.').
        // This simulates an in-agent timer/internal CTS firing — neither operator
        // cancel nor host shutdown nor a configured per-phase timeout is set.
        await Task.Delay(50, CancellationToken.None);
        throw new TaskCanceledException();
    }
}

internal sealed class TransientHarness : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public LocalGitHost GitHost { get; }
    public ProgrammableAgent Agent { get; }
    public RecordingTaskQueue Queue { get; }

    public TransientHarness(PipelineRunner pipeline, SqliteWorkItemStore store, LocalGitHost gitHost, ProgrammableAgent agent, RecordingTaskQueue queue)
    {
        Pipeline = pipeline;
        Store = store;
        GitHost = gitHost;
        Agent = agent;
        Queue = queue;
    }

    public void Dispose() => Store.Dispose();
}

/// <summary>
/// Test agent that delegates the body of RunAsync to a swappable function.
/// Lets each test inject the failure mode without spawning a new class.
/// </summary>
internal sealed class ProgrammableAgent : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public Func<ISandbox, string, CancellationToken, Task<AgentResult>> Behaviour { get; set; } =
        (_, _, _) => Task.FromResult(new AgentResult(true, "ok", null, null));

    public Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt,
        AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
        CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
        => Behaviour(sandbox, workingDirectory, ct);
}

/// <summary>
/// In-memory queue that records every EnqueueAsync call. Used to verify that
/// the auto-retry path actually kicks the orchestrator.
/// </summary>
internal sealed class RecordingTaskQueue : ITaskQueue
{
    public List<WorkItemId> Enqueued { get; } = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<WorkItemId> _q = new();

    public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
    {
        Enqueued.Add(id);
        _q.Enqueue(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        => new(_q.TryDequeue(out var id) ? id : null);

    public int Count => _q.Count;
}
