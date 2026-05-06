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
/// Integration tests that exercise the real <see cref="PipelineRunner"/> shutdown semantics.
///
/// Host shutdown (IHostApplicationLifetime.ApplicationStopping): the pipeline must leave
/// the item in its current mid-flight state so the recovery loop can pick it up on next start.
///
/// Operator-requested cancel (DELETE /workitems/{id}): the pipeline must transition to
/// Cancelled with CancellationReason=OperatorRequested.
/// </summary>
[Collection("Pipeline integration")]
public sealed class HostShutdownCancellationTests : IDisposable
{
    private readonly string _workspace;

    public HostShutdownCancellationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-shutdown-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private ShutdownTestHarness BuildBlockingPipeline(string seedRepoUrl)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([new BlockingAgentRunner()]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 3, AuditTypes = [] },
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var upstreamFactory = new TestUpstreamFactory();

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer, store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        return new ShutdownTestHarness(pipeline, store, gitHost);
    }

    // ── Host shutdown: leave item in mid-flight state ─────────────────────────

    [Fact]
    public async Task HostShutdown_DoesNotCancelItem_LeavesWorkingState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildBlockingPipeline(seed);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        // Poll until the real PipelineRunner has committed Working state to the DB
        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Signal host shutdown without cancelling the work item token; the
        // pipeline should preempt the running agent rather than treat this as
        // operator cancellation.
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.NotNull(final.PreemptedAt);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", final.PreemptCheckpoint);

        var showRef = await TestSupport.RunGit(harness.GitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", final.PreemptCheckpoint!);
        Assert.Equal(0, showRef.code);
    }

    // ── Operator cancel: item must be Cancelled with OperatorRequested reason ──

    [Fact]
    public async Task OperatorCancel_TransitionsItem_ToCancelled_WithReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildBlockingPipeline(seed);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource(); // never fires in this test
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Signal operator cancel only — hostShutdownToken does NOT fire
        await operatorCancelCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, final.CancellationReason);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public void ResumePrompt_DoesNotEmbedScratchpadContent()
    {
        var prompt = PipelineRunner.BuildResumePrompt(
            "original prompt",
            "refs/heads/codeybox/preempt/wi");

        Assert.Contains("original prompt", prompt);
        Assert.Contains("refs/heads/codeybox/preempt/wi", prompt);
        Assert.Contains("restored work tree", prompt);
        Assert.DoesNotContain("BEGIN UNTRUSTED PREEMPT SCRATCHPAD", prompt);
        Assert.DoesNotContain("ignore previous instructions", prompt);
    }

    private static async Task WaitForStateAsync(
        SqliteWorkItemStore store, WorkItemId id, WorkItemState target, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await store.GetAsync(id);
            if (current?.State == target) return;
            await Task.Delay(25);
        }
        var actual = (await store.GetAsync(id))?.State;
        throw new TimeoutException(
            $"Item {id} did not reach state {target} within {timeout}; final state: {actual}");
    }
}

/// <summary>
/// Disposable bundle of pipeline resources used by <see cref="HostShutdownCancellationTests"/>.
/// </summary>
internal sealed class ShutdownTestHarness : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public LocalGitHost GitHost { get; }

    public ShutdownTestHarness(PipelineRunner pipeline, SqliteWorkItemStore store, LocalGitHost gitHost)
    {
        Pipeline = pipeline;
        Store = store;
        GitHost = gitHost;
    }

    public void Dispose() => Store.Dispose();
}

/// <summary>
/// Agent runner that blocks indefinitely until the cancellation token fires.
/// Holds the pipeline in the Working phase so shutdown tests can signal cancellation
/// while the item is provably mid-flight.
/// </summary>
internal sealed class BlockingAgentRunner : IAgentRunner
{
    public AgentKind Kind { get; init; } = AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new AgentResult(false, "unreachable", null, null);
    }
}
