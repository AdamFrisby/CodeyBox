using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
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

    private static AgentTurnCheckpointRef AssertDurableCheckpointRef(
        WorkItemId workItemId,
        string? value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        var checkpointRef = AgentTurnCheckpointRef.Parse(value!);
        Assert.Equal(workItemId, checkpointRef.WorkItemId);
        return checkpointRef;
    }

    private ShutdownTestHarness BuildBlockingPipeline(string seedRepoUrl)
        => BuildPipeline(seedRepoUrl, new BlockingAgentRunner());

    private ShutdownTestHarness BuildPipeline(
        string seedRepoUrl,
        IAgentRunner agent,
        PipelineOptions? options = null,
        ILogger<PipelineRunner>? logger = null,
        ISandboxProvider? sandboxProvider = null,
        IQuotaFailureClassifier? quotaClassifier = null,
        IRequiredBuildVerifier? requiredBuildVerifier = null,
        IReadOnlyList<IAuditor>? auditors = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = sandboxProvider
            ?? new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([agent]);

        var configuredAuditors = auditors ?? [];
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 3,
                AuditTypes = configuredAuditors.Count == 0 ? [] : ["scripted"],
            },
        });

        var presetCatalog = new ScriptedAuditorCatalog(configuredAuditors);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var upstreamFactory = new TestUpstreamFactory();
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer, store,
            webhooks,
            options ?? new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            logger ?? NullLogger<PipelineRunner>.Instance,
            quotaClassifier: quotaClassifier,
            requiredBuildVerifier: requiredBuildVerifier ?? TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new ShutdownTestHarness(pipeline, store, gitHost);
    }

    // ── Host shutdown: leave item in mid-flight state ─────────────────────────

    [Fact]
    public async Task HostShutdown_DoesNotCancelItem_LeavesWorkingState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        var syncObserver = new PreemptPushSyncObservingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            logger: logger,
            sandboxProvider: syncObserver);

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
        // (b) item left mid-flight — not Failed, not Cancelled — so the recovery
        // loop can pick it up on next startup.
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.NotNull(final.PreemptedAt);
        AssertDurableCheckpointRef(item.Id, final.PreemptCheckpoint);
        // (c) auto-retry is NOT triggered for host-shutdown attribution; the
        // recovery loop owns the item, and bumping TransientCancelRetries here
        // would race the host going away.
        Assert.Equal(0, final.TransientCancelRetries);
        Assert.Null(final.FailureKind);

        var showRef = await TestSupport.RunGit(harness.GitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", final.PreemptCheckpoint!);
        Assert.Equal(0, showRef.code);
        Assert.True(syncObserver.PreemptCheckpointSyncs > 0,
            "preempt checkpoint push was not followed by a sandbox host sync");

        // (a) the structured boundary log fires with Boundary=RunAsync.host-shutdown
        // so post-incident triage can correlate the catch site with the source.
        // A regression that dropped the LogBoundary call (or wired the wrong source)
        // would surface as a missing entry here.
        var boundary = Assert.Single(logger.Entries, e =>
            e.Properties.TryGetValue("Boundary", out var b) && b is string s
                && s == "RunAsync.host-shutdown");
        Assert.Equal(CancellationSources.HostShutdown, boundary.Properties["CancellationSource"]);
        Assert.Equal(true, boundary.Properties["HostShutdown"]);
    }

    [Fact]
    public async Task InfrastructureFailure_CheckpointsDirtyTurn_AndRetryResumesExactNativeSession()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent();
        using var harness = BuildPipeline(seed, agent);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
        var durableRef = AssertDurableCheckpointRef(item.Id, failed.PreemptCheckpoint);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(failed.AgentTurnResumeCheckpoint);
        Assert.Equal(AgentTurnResumePhase.Work, checkpoint.Phase);
        Assert.Equal(DurableFailureResumeAgent.SessionId, checkpoint.NativeSessionId?.Value);
        Assert.Equal(0, checkpoint.AttemptCount);

        var checkpointTree = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree",
            "-r",
            "--name-only",
            failed.PreemptCheckpoint!);
        Assert.Equal(0, checkpointTree.code);
        Assert.Contains("partial-before-infrastructure-failure.txt", checkpointTree.stdout);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad", checkpointTree.stdout, StringComparison.Ordinal);
        var privateScratchpad = await harness.Store.ReadAsync(item.Id, durableRef);
        Assert.NotNull(privateScratchpad);

        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            harness.Store,
            queue,
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(failed, trigger: "manual");
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(WorkItemState.Working, retry.ResumeState);

        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.Working, resumed.State);
        Assert.Equal(0, resumed.AgentTurnResumeCheckpoint?.AttemptCount);

        await harness.Pipeline.RunAsync(resumed, CancellationToken.None);

        var completed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(completed);
        Assert.True(
            completed.State == WorkItemState.Done,
            $"Expected Done, got {completed.State}: {completed.LastError}");
        Assert.Null(completed.PreemptedAt);
        Assert.Null(completed.PreemptCheckpoint);
        Assert.Null(completed.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, durableRef));
        Assert.Equal(1, agent.InitialWorkCalls);
        Assert.Equal(1, agent.ResumeCalls);
        Assert.Equal(DurableFailureResumeAgent.SessionId, agent.ResumedNativeSessionId?.Value);
        Assert.True(agent.SawCheckpointedPartialWork);
        Assert.True(agent.SawCheckpointScratchpad);
    }

    [Fact]
    public async Task DurableResume_RestoreTransportUnavailable_RetainsCheckpointAndPrivateArchive()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent(throwResumePreparationUnavailable: true);
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);
        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, interrupted!.PreemptCheckpoint);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));

        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(interrupted, trigger: "test-restore-outage");
        Assert.True(retry.Success, retry.Error);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        await harness.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var failedRestore = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failedRestore);
        Assert.Equal(WorkItemState.Failed, failedRestore!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failedRestore.FailureKind);
        Assert.Equal(checkpointRef.Value, failedRestore.PreemptCheckpoint);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(failedRestore.AgentTurnResumeCheckpoint);
        Assert.Equal(0, checkpoint.AttemptCount);
        Assert.Null(checkpoint.DispatchClaimId);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.Equal(1, agent.ResumeCalls);
    }

    [Fact]
    public async Task DurableResume_PromptEditedAfterRetryRefusesStaleCheckpointBeforeDispatch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent();
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);
        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, interrupted!.PreemptCheckpoint);
        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(interrupted, trigger: "test-prompt-race");
        Assert.True(retry.Success, retry.Error);
        var stalePickup = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(stalePickup);

        const string editedPrompt = "edited after the retry was queued";
        var promptUpdate = await harness.Store.TryReplacePromptAsync(
            item.Id,
            editedPrompt,
            DateTimeOffset.UtcNow);
        Assert.Equal(PromptReplaceOutcome.Updated, promptUpdate.Outcome);

        await harness.Pipeline.RunAsync(stalePickup, CancellationToken.None);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(editedPrompt, final.Prompt);
        Assert.Equal(promptUpdate.NewRevision, final.PromptRevision);
        Assert.Equal("restore", final.FailureKind);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.Equal(0, agent.ResumeCalls);
    }

    [Fact]
    public async Task DurableResume_ConcurrentPickups_InvokeResumedCliExactlyOnce()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent(blockResume: true);
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);
        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(interrupted!, trigger: "test-concurrent-pickup");
        Assert.True(retry.Success, retry.Error);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var firstPickup = harness.Pipeline.RunAsync(resumed!, timeout.Token);
        await agent.ResumeEntered.WaitAsync(TimeSpan.FromSeconds(30));
        var claimed = await harness.Store.GetAsync(item.Id);
        var firstClaimId = claimed?.AgentTurnResumeCheckpoint?.DispatchClaimId;
        Assert.NotNull(firstClaimId);

        var duplicatePickup = harness.Pipeline.RunAsync(resumed!, timeout.Token);
        await duplicatePickup.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, agent.ResumeCalls);
        var stillClaimed = await harness.Store.GetAsync(item.Id);
        Assert.Equal(firstClaimId, stillClaimed?.AgentTurnResumeCheckpoint?.DispatchClaimId);

        agent.ReleaseResume();
        await firstPickup.WaitAsync(TimeSpan.FromSeconds(30));

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final?.State);
        Assert.Null(final?.AgentTurnResumeCheckpoint);
        Assert.Equal(1, agent.ResumeCalls);
    }

    [Fact]
    public async Task QuotaFailure_CheckpointsDirtyTurn_AndParksAtExactResumeBoundary()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent(quotaFailure: true);
        using var harness = BuildPipeline(
            seed,
            agent,
            quotaClassifier: new DurableFailureResumeAgent.QuotaFailureClassifier());

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, parked.State);
        Assert.Equal("quota", parked.FailureKind);
        Assert.Equal(RetryFromPolicy.Work, parked.QuotaRetryFrom);
        Assert.Equal(RetryFromPolicy.Work, parked.QuotaRetryPhase);
        AssertDurableCheckpointRef(item.Id, parked.PreemptCheckpoint);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(parked.AgentTurnResumeCheckpoint);
        Assert.Equal(AgentTurnResumePhase.Work, checkpoint.Phase);
        Assert.Equal(DurableFailureResumeAgent.SessionId, checkpoint.NativeSessionId?.Value);
    }

    [Fact]
    public async Task InfrastructureFailure_WithoutNativeId_ResumesFromScratchpadAndDirtyTree()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent(emitNativeSessionId: false);
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed.State);
        var checkpoint = Assert.IsType<AgentTurnResumeCheckpoint>(failed.AgentTurnResumeCheckpoint);
        Assert.Null(checkpoint.NativeSessionId);

        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(failed, trigger: "manual");
        Assert.True(retry.Success, retry.Error);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        await harness.Pipeline.RunAsync(resumed, CancellationToken.None);

        var completed = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, completed?.State);
        Assert.Equal(1, agent.ResumeCalls);
        Assert.Null(agent.ResumedNativeSessionId);
        Assert.True(agent.SawCheckpointedPartialWork);
        Assert.True(agent.SawCheckpointScratchpad);
    }

    [Fact]
    public async Task DurableResume_EmptyInitialCheckpointAndNoopResume_FollowsNormalNoDiffFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent(
            writePartialBeforeFailure: false,
            writeOnResume: false);
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        Assert.Equal(WorkItemState.Failed, interrupted!.State);
        Assert.False(string.IsNullOrWhiteSpace(interrupted.PreemptCheckpoint), interrupted.LastError);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, interrupted.PreemptCheckpoint);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));

        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(interrupted, trigger: "test-resume");
        Assert.True(retry.Success, retry.Error);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        await harness.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes", final.LastError, StringComparison.Ordinal);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.Equal(1, agent.InitialWorkCalls);
        Assert.Equal(1, agent.ResumeCalls);
        Assert.False(agent.SawCheckpointedPartialWork);
        Assert.True(agent.SawCheckpointScratchpad);
    }

    [Fact]
    public async Task DurableResume_EmptyReworkCheckpointAndNoopResume_FollowsNormalNoDiffFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new EmptyReworkDurableResumeAgent();
        using var harness = BuildPipeline(
            seed,
            agent,
            auditors: [new OnceFailingAuditor()]);
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        Assert.Equal(WorkItemState.Failed, interrupted!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, interrupted.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(interrupted.PreemptCheckpoint), interrupted.LastError);
        Assert.Equal(
            AgentTurnResumePhase.Rework,
            Assert.IsType<AgentTurnResumeCheckpoint>(interrupted.AgentTurnResumeCheckpoint).Phase);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, interrupted.PreemptCheckpoint);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));

        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance,
            auditProgress: harness.Store);
        var retry = await retrier.RetryAsync(interrupted, trigger: "test-rework-resume");
        Assert.True(retry.Success, retry.Error);
        Assert.Equal(WorkItemState.Reworking, retry.ResumeState);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        await harness.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Rework agent produced no changes", final.LastError, StringComparison.Ordinal);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.Equal(1, agent.InitialWorkCalls);
        Assert.Equal(1, agent.InterruptedReworkCalls);
        Assert.Equal(1, agent.ResumeCalls);
        Assert.True(agent.SawCheckpointScratchpad);
    }

    [Fact]
    public async Task DirectRecoveryRequeue_WhenDurableResumeDisabled_DoesNotInvokeCheckpointedTurn()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent();
        using var harness = BuildPipeline(seed, agent);
        var item = NewItem();
        await harness.Store.CreateAsync(item);
        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, failed!.PreemptCheckpoint);
        var bypassedRetrier = failed with
        {
            State = WorkItemState.Working,
            FailureKind = null,
            LastError = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await harness.Store.UpdateAsync(bypassedRetrier);

        var previousLimit = SessionResumeOptions.MaxResumeAttempts;
        try
        {
            SessionResumeOptions.SetMaxResumeAttempts(0);
            await harness.Pipeline.RunAsync(bypassedRetrier, CancellationToken.None);
        }
        finally
        {
            SessionResumeOptions.SetMaxResumeAttempts(previousLimit);
        }

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Null(after.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.Equal(0, agent.ResumeCalls);
    }

    [Fact]
    public async Task PostAgentExecutionUnavailable_CheckpointsCompletedDirtyTreeBeforeFailing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var sandboxProvider = new PostAgentGitUnavailableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var harness = BuildPipeline(
            seed,
            new SuccessfulDirtyAgentRunner(),
            sandboxProvider: sandboxProvider);
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, failed.PreemptCheckpoint);
        Assert.NotNull(failed.AgentTurnResumeCheckpoint);
        Assert.Null(failed.AgentTurnResumeCheckpoint!.NativeSessionId);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));
        var tree = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree",
            "-r",
            "--name-only",
            checkpointRef.Value);
        Assert.Equal(0, tree.code);
        Assert.Contains("completed-before-git-infrastructure-failure.txt", tree.stdout);
        Assert.DoesNotContain("preempt-scratchpad", tree.stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumedAgent_PublishedChangesThenBuildInfrastructureFailure_DiscardsStaleTurnCheckpoint()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new DurableFailureResumeAgent();
        var buildVerifier = new TestRequiredBuildVerifier(
            RequiredBuildProbeResult.Applies,
            RequiredBuildVerificationResult.Unavailable("injected build sandbox outage"));
        using var harness = BuildPipeline(
            seed,
            agent,
            requiredBuildVerifier: buildVerifier);
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var interrupted = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(interrupted);
        Assert.False(string.IsNullOrWhiteSpace(interrupted!.PreemptCheckpoint), interrupted.LastError);
        var staleCheckpointRef = AssertDurableCheckpointRef(item.Id, interrupted.PreemptCheckpoint);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, staleCheckpointRef));

        var retrier = new WorkItemRetrier(
            harness.Store,
            new InMemoryTaskQueue(),
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(interrupted, trigger: "test-resume");
        Assert.True(retry.Success, retry.Error);
        var resumed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);

        await harness.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var failedBuildVerification = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failedBuildVerification);
        Assert.Equal(WorkItemState.Failed, failedBuildVerification!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failedBuildVerification.FailureKind);
        Assert.Null(failedBuildVerification.PreemptCheckpoint);
        Assert.Null(failedBuildVerification.AgentTurnResumeCheckpoint);
        Assert.Null(await harness.Store.ReadAsync(item.Id, staleCheckpointRef));
        Assert.Equal(1, agent.InitialWorkCalls);
        Assert.Equal(1, agent.ResumeCalls);
        Assert.Equal(1, buildVerifier.VerifyCalls);

        var branchTree = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree",
            "-r",
            "--name-only",
            failedBuildVerification.WorkBranch!);
        Assert.Equal(0, branchTree.code);
        Assert.Contains("partial-before-infrastructure-failure.txt", branchTree.stdout);
        Assert.Contains("resumed-after-infrastructure-failure.txt", branchTree.stdout);

        var boundaryRetry = await retrier.RetryAsync(
            failedBuildVerification,
            trigger: "test-build-recovery");
        Assert.True(boundaryRetry.Success, boundaryRetry.Error);
        Assert.Equal(WorkItemState.WorkComplete, boundaryRetry.ResumeState);
        Assert.Equal(1, agent.ResumeCalls);
    }

    [Fact]
    public async Task DirectExit137WithoutNativeId_RetainsDirtyTreeAsInfrastructureCheckpoint()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildPipeline(seed, new Exit137DirtyAgentRunner());
        var item = NewItem();
        await harness.Store.CreateAsync(item);

        await harness.Pipeline.RunAsync(item, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, failed.PreemptCheckpoint);
        Assert.Null(failed.AgentTurnResumeCheckpoint?.NativeSessionId);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));
        var tree = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree",
            "-r",
            "--name-only",
            checkpointRef.Value);
        Assert.Equal(0, tree.code);
        Assert.Contains("partial-before-exit-137.txt", tree.stdout);
    }

    [Fact]
    public async Task HostShutdown_StopAndPreserveFailure_LeavesCheckpointedItemRecoverable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            logger: logger,
            sandboxProvider: new PreserveFailingSandboxProvider(
                new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance)));

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);
        var thrownText = thrown.ToString();
        Assert.Contains("preserving the sandbox failed", thrownText);
        Assert.Contains("injected preserve failure", thrownText);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.FailureKind);
        AssertDurableCheckpointRef(item.Id, final.PreemptCheckpoint);
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("Failed preserving sandbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostShutdown_StopAndPreserveTimeout_LeavesCheckpointedItemRecoverable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            logger: logger,
            sandboxProvider: new PreserveCancelingSandboxProvider(
                new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance)));

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);
        Assert.Contains("preserving the sandbox failed", thrown.ToString());

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.FailureKind);
        AssertDurableCheckpointRef(item.Id, final.PreemptCheckpoint);
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("Timed out preserving sandbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostShutdown_PreemptHookIgnoresCancellation_RefusesCheckpointAndPreservesSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        var sandboxProvider = new SuspendableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var harness = BuildPipeline(
            seed,
            new HangingPreemptAgentRunner(),
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                ShutdownGrace = TimeSpan.FromSeconds(8),
            },
            logger,
            sandboxProvider);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipelineTask.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("preempt checkpoint could not be created", thrown.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not terminate after cancellation", thrown.ToString(), StringComparison.OrdinalIgnoreCase);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Equal(1, Assert.Single(sandboxProvider.Wrappers).StopAndPreserveCalls);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("checkpoint publication will be refused", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("preserving sandbox without publishing a checkpoint", StringComparison.Ordinal));

        var refs = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "for-each-ref",
            "--format=%(refname)",
            $"refs/heads/codeybox/preempt/{item.Id}/");
        Assert.Equal(string.Empty, refs.stdout.Trim());
    }

    [Fact]
    public async Task HostShutdown_SlowPreemptHookObservesCancellation_QuiescesThenCheckpoints()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new CancellationObservingSlowPreemptAgentRunner();
        var logger = new CapturingLogger<PipelineRunner>();
        using var harness = BuildPipeline(
            seed,
            agent,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                ShutdownGrace = TimeSpan.FromSeconds(8),
            },
            logger);

        var item = NewItem();
        await harness.Store.CreateAsync(item);
        using var hostShutdownCts = new CancellationTokenSource();
        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, CancellationToken.None, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipelineTask.WaitAsync(TimeSpan.FromSeconds(15)));

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        var checkpointRef = AssertDurableCheckpointRef(item.Id, final.PreemptCheckpoint);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.NotNull(await harness.Store.ReadAsync(item.Id, checkpointRef));
        Assert.True(agent.CancellationObserved);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("preempt signal exceeded timeout", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("checkpoint publication will be refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostShutdown_LateRepositoryScratchWriteAfterTimeout_CannotRaceCheckpointPublication()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new LateRepositoryWritingPreemptAgentRunner(TimeSpan.FromMilliseconds(750));
        var logger = new CapturingLogger<PipelineRunner>();
        var sandboxProvider = new SuspendableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var harness = BuildPipeline(
            seed,
            agent,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                ShutdownGrace = TimeSpan.FromMilliseconds(500),
            },
            logger,
            sandboxProvider);

        var item = NewItem();
        await harness.Store.CreateAsync(item);
        using var hostShutdownCts = new CancellationTokenSource();
        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, CancellationToken.None, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipelineTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await agent.LateWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Equal(1, Assert.Single(sandboxProvider.Wrappers).StopAndPreserveCalls);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("checkpoint publication will be refused", StringComparison.Ordinal));

        var refs = await TestSupport.RunGit(
            harness.GitHost.GetRepoPath(item.Id.ToString()),
            "for-each-ref",
            "--format=%(refname)",
            $"refs/heads/codeybox/preempt/{item.Id}/");
        Assert.Equal(string.Empty, refs.stdout.Trim());
    }

    // ── Operator cancel: item must be Cancelled with OperatorRequested reason ──

    [Fact]
    public async Task OperatorCancel_TransitionsItem_ToCancelled_WithReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        using var harness = BuildPipeline(seed, new BlockingAgentRunner(), logger: logger);

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
        // The cancel must be attributed to the operator, not silently reclassified
        // as a transient/host source — operators rely on this to distinguish their
        // own DELETE /workitems/{id} action from other cancellation contributors.
        Assert.Equal(CancellationSources.Operator, final.CancellationSource);
        // The auto-retry path must not fire on operator cancel; operators don't
        // want their explicit cancel re-queued.
        Assert.Equal(0, final.TransientCancelRetries);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);

        // The structured boundary log must be emitted with source=operator so
        // post-incident triage can correlate the catch site with the source.
        var boundary = Assert.Single(logger.Entries, e =>
            e.Properties.TryGetValue("Boundary", out var b) && b is string s
                && s == "RunAsync.operator-cancel");
        Assert.Equal(CancellationSources.Operator, boundary.Properties["CancellationSource"]);
        Assert.Equal(true, boundary.Properties["OperatorRequested"]);
        Assert.Equal(false, boundary.Properties["HostShutdown"]);
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

    [Fact]
    public async Task PreemptedRework_RunsResumeAgentBeforeAuditing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new CapturingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var agent = new ReworkResumeRecordingAgent();
        var registry = new AgentRegistry([agent]);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            GraphicalSandbox = true,
            NetworkProfiles = new ProjectNetworkProfiles
            {
                Rework = "rework-profile",
                AuditTool = "audit-tool-profile",
            },
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost, registry, new StaticCredentialProvider(), new InMemoryPullRequestService(),
            projects, new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = NewItem() with
        {
            State = WorkItemState.Reworking,
            WorkBranch = "codeybox/rework-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, agent.ResumeCalls);
        Assert.Contains("Interrupted Rework Resume", agent.LastResumePrompt);
        Assert.True(agent.SawScratchpad);
        var reworkSpec = Assert.Single(sandboxes.Specs, spec => spec.TimingPhase == "rework");
        Assert.Equal(SandboxProfileFlavor.Graphical, reworkSpec.Flavor);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, reworkSpec.Network.ProfileName);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_RestoresScratchpadAndResumes()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new StartupResumeRecordingAgent();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(), projects,
            new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);
        using var orchestrator = new OrchestratorService(
            queue, store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/startup-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await orchestrator.StartAsync(CancellationToken.None);
        var final = await WaitForStateAsync(store, item.Id, WorkItemState.Done, TimeSpan.FromSeconds(20));
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Equal(1, agent.ResumeCalls);
        Assert.True(agent.LegacyScratchpadFilesAbsentBeforeResume);
        Assert.True(agent.PrivateScratchpadPresentBeforeResume);
        Assert.True(agent.RestoredScratchpad);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_RemovesCheckpointScratchpadFromWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new NoopResumeAgent();
        var pipeline = BuildResumePipeline(seed, gitHost, store, agent);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/no-scratchpad-leak",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var tree = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree", "-r", "--name-only", item.WorkBranch!);
        Assert.Equal(0, tree.code);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad.md", tree.stdout);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad.tgz", tree.stdout);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_WithNoNewDiff_PushesCheckpointWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new NoopResumeAgent();
        var syncObserver = new PreemptPushSyncObservingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var pipeline = BuildResumePipeline(seed, gitHost, store, agent, syncObserver);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/no-new-diff-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed, includeScratchpad: false);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, agent.ResumeCalls);

        var branch = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", $"refs/heads/{item.WorkBranch}");
        Assert.Equal(0, branch.code);

        var tree = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree", "-r", "--name-only", item.WorkBranch!);
        Assert.Equal(0, tree.code);
        Assert.Contains("partial-rework.txt", tree.stdout);
        Assert.True(syncObserver.ResumedCheckpointWorkBranchSyncs > 0,
            "resumed checkpoint work-branch push was not followed by a sandbox host sync");
    }

    // ── R8-core regression #2: suspend handler must not race the checkpoint flow ──

    [Fact]
    public async Task HostShutdown_WhenSandboxAlreadySuspended_SkipsCheckpointAndPreserve()
    {
        // Replays the race the SandboxShutdownTeardownService → PipelineRunner
        // shutdown ordering creates: by the time the pipeline catches the
        // host-shutdown OCE, the suspend handler has already frozen the VM
        // (sandbox.IsSuspended == true). The legacy preempt-checkpoint flow
        // would then try to run `git add/commit/push` against a frozen VM and
        // hang until the host kills the process — so the pipeline must
        // short-circuit: no checkpoint commit, no StopAndPreserveAsync call,
        // just rethrow OCE and let the resume side own recovery.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var wrappingProvider = new SuspendableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var agent = new BlockingAgentRunner();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            wrappingProvider, gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            logger,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = NewItem();
        await store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Simulate the suspend service freezing the active VM before the
        // BackgroundService cancellation token propagates to the pipeline:
        // every live sandbox the pipeline created flips to IsSuspended=true.
        SuspendableSandboxWrapper? liveSandbox = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            liveSandbox = wrappingProvider.Wrappers.LastOrDefault();
            if (liveSandbox is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(liveSandbox);
        liveSandbox.IsSuspended = true;

        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipelineTask.WaitAsync(TimeSpan.FromSeconds(15)));

        // The pipeline must NOT have run the legacy checkpoint commit/push
        // flow (it would hang against a real frozen VM and would here race
        // the test). PreemptCheckpoint stays null because the suspend
        // handler owns recovery via SuspendedVmName, not via a checkpoint
        // ref. StopAndPreserveAsync must also be skipped: the suspend
        // already preserved the VM and a redundant stop call would (in the
        // real multipass case) re-issue lifecycle ops against a frozen VM.
        var final = await store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Working, final.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.PreemptedAt);
        Assert.Equal(0, liveSandbox.StopAndPreserveCalls);

        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("was taken over by SandboxShutdownTeardownService", StringComparison.Ordinal)
            && e.Message.Contains("skipping preempt-checkpoint", StringComparison.Ordinal));

        // Sanity: no preempt-checkpoint ref ever made it to origin either.
        var showRef = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "show-ref");
        Assert.DoesNotContain("codeybox/preempt", showRef.stdout);

        store.Dispose();
    }

    [Fact]
    public async Task HostShutdown_WhenSuspendTimedOutButMappingPersisted_SkipsCheckpointAndPreserve()
    {
        // Covers the suspend-TIMEOUT branch of the same race: the suspend
        // handler persisted the (work item → VM) mapping BEFORE awaiting
        // `multipass suspend`, then the per-VM timeout fired while multipassd is
        // still writing the snapshot — so IsSuspended is still FALSE but
        // SuspendedVmName is set in the store. PipelineRunner must treat the
        // persisted mapping (not just IsSuspended) as "suspend owns recovery"
        // and short-circuit the legacy checkpoint/preserve flow, otherwise the
        // git-checkpoint + multipass-stop path races the in-flight suspend.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var wrappingProvider = new SuspendableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var agent = new BlockingAgentRunner();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            wrappingProvider, gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            logger,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = NewItem();
        await store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        SuspendableSandboxWrapper? liveSandbox = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            liveSandbox = wrappingProvider.Wrappers.LastOrDefault();
            if (liveSandbox is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(liveSandbox);

        // The suspend handler timed out: mapping persisted, IsSuspended NOT set.
        Assert.False(liveSandbox.IsSuspended);
        var persisted = await store.GetAsync(item.Id);
        await store.UpdateAsync(persisted! with
        {
            SuspendedVmName = liveSandbox.Id,
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipelineTask.WaitAsync(TimeSpan.FromSeconds(15)));

        var final = await store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.PreemptedAt);
        Assert.Equal(0, liveSandbox.StopAndPreserveCalls);

        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("was taken over by SandboxShutdownTeardownService", StringComparison.Ordinal)
            && e.Message.Contains("skipping preempt-checkpoint", StringComparison.Ordinal));

        var showRef = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "show-ref");
        Assert.DoesNotContain("codeybox/preempt", showRef.stdout);

        store.Dispose();
    }

    [Fact]
    public async Task HostShutdown_WhenShutdownHandlerOwnsNonSuspendedSandbox_SkipsCheckpointAndPreserve()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var logger = new CapturingLogger<PipelineRunner>();
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var wrappingProvider = new SuspendableSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var agent = new BlockingAgentRunner();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            wrappingProvider, gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            logger,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = NewItem();
        await store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        SuspendableSandboxWrapper? liveSandbox = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            liveSandbox = wrappingProvider.Wrappers.LastOrDefault();
            if (liveSandbox is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(liveSandbox);
        Assert.False(liveSandbox.IsSuspended);

        liveSandbox.MarkOwnedByShutdownHandler();
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipelineTask.WaitAsync(TimeSpan.FromSeconds(15)));

        var final = await store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Working, final.State);
        Assert.Null(final.SuspendedVmName);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.PreemptedAt);
        Assert.Equal(0, liveSandbox.StopAndPreserveCalls);

        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("was taken over by SandboxShutdownTeardownService", StringComparison.Ordinal)
            && e.Message.Contains("skipping preempt-checkpoint", StringComparison.Ordinal));

        var showRef = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "show-ref");
        Assert.DoesNotContain("codeybox/preempt", showRef.stdout);

        store.Dispose();
    }

    [Fact]
    public async Task ProcessSandbox_StopAndPreserve_SkipsDisposeDeletion()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var pwd = await sandbox.ExecAsync(new SandboxExec { Argv = ["pwd"] });
        Assert.True(pwd.Success);
        var root = Directory.GetParent(pwd.Stdout.Trim())!.FullName;

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();
        await sandbox.DisposeAsync();

        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, ".codeybox-preempt")));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private PipelineRunner BuildResumePipeline(
        string seed,
        LocalGitHost gitHost,
        SqliteWorkItemStore store,
        IAgentRunner agent,
        ISandboxProvider? sandboxProvider = null)
    {
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        return new PipelineRunner(
            sandboxProvider ?? new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(), projects,
            new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);
    }

    private async Task CreatePreemptCheckpointAsync(LocalGitHost gitHost, WorkItem item, string seed, bool includeScratchpad = true)
    {
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed);
        var clone = Path.Combine(_workspace, "checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        var bare = gitHost.GetRepoPath(repoId);
        Assert.Equal(0, (await TestSupport.RunGit(_workspace, "clone", bare, clone)).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.name", "Test")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch!)).code);
        if (includeScratchpad)
        {
            Directory.CreateDirectory(Path.Combine(clone, ".codeybox"));
            await File.WriteAllTextAsync(Path.Combine(clone, ".codeybox", "preempt-scratchpad.md"), "resume scratchpad");
            var scratchRoot = Path.Combine(_workspace, "scratch-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(scratchRoot, "home", ".testagent"));
            await File.WriteAllTextAsync(Path.Combine(scratchRoot, "home", ".testagent", "session.txt"), "resume session");
            await RunProcessAsync(clone, "tar", "-czf", Path.Combine(clone, ".codeybox", "preempt-scratchpad.tgz"), "-C", scratchRoot, ".");
        }
        await File.WriteAllTextAsync(Path.Combine(clone, "partial-rework.txt"), "partial");
        Assert.Equal(0, (await TestSupport.RunGit(clone, "add", "-A")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "commit", "-m", "checkpoint")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{item.PreemptCheckpoint}")).code);
    }

    private static async Task<WorkItem> WaitForStateAsync(
        SqliteWorkItemStore store, WorkItemId id, WorkItemState target, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await store.GetAsync(id);
            if (current?.State == target) return current;
            await Task.Delay(25);
        }
        var actual = (await store.GetAsync(id))?.State;
        throw new TimeoutException(
            $"Item {id} did not reach state {target} within {timeout}; final state: {actual}");
    }

    private static async Task RunProcessAsync(string cwd, string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed: {stderr}");
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
        Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new AgentResult(false, "unreachable", null, null);
    }
}

internal sealed class SuccessfulDirtyAgentRunner : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;

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
        _ = prompt;
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "printf '%s\\n' complete > completed-before-git-infrastructure-failure.txt",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(
            write.Success,
            write.Success ? "completed work" : "failed to write completed work",
            write.Stdout,
            write.Stderr)
        {
            ExecutionUnavailable = write.ExecutionUnavailable,
        };
    }
}

internal sealed class Exit137DirtyAgentRunner : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;

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
        _ = prompt;
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf '%s\\n' partial > partial-before-exit-137.txt"],
            WorkingDirectory = workingDirectory,
        }, ct);
        if (!write.Success)
            return new AgentResult(false, "failed to create partial exit-137 work", write.Stdout, write.Stderr);
        return new AgentResult(false, "agent exited 137", "", "process killed");
    }
}

internal static class TestAgentTurnScratchpadCapture
{
    public static async Task CaptureAsync(ISandbox sandbox, CancellationToken ct)
    {
        var capture = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "set -euo pipefail; root=$1; tmp=$(mktemp -d \"$root/capture.XXXXXX\"); cleanup() { rm -rf -- \"$tmp\"; }; trap cleanup EXIT; : > \"$tmp/manifest.tsv\"; archive=$(mktemp \"$root/scratchpad.tgz.tmp.XXXXXX\"); tar -czf \"$archive\" -C \"$tmp\" manifest.tsv; mv -f -- \"$archive\" \"$root/scratchpad.tgz\"",
                "codeybox-test-capture",
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/",
        }, ct);
        if (!capture.Success)
            throw new InvalidOperationException("test scratchpad capture failed");
    }
}

internal sealed class EmptyReworkDurableResumeAgent :
    IAgentRunner,
    IResumableAgentRunner,
    IPreemptibleAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int InitialWorkCalls { get; private set; }
    public int InterruptedReworkCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    public bool SawCheckpointScratchpad { get; private set; }

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
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return new AgentResult(false, "unexpected merge invocation", null, null);

        if (InitialWorkCalls == 0)
        {
            InitialWorkCalls++;
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf '%s\\n' initial > initial-before-rework.txt"],
                WorkingDirectory = workingDirectory,
            }, ct);
            return new AgentResult(
                write.Success,
                write.Success ? "initial work complete" : "initial write failed",
                write.Stdout,
                write.Stderr);
        }

        InterruptedReworkCalls++;
        return new AgentResult(
            Success: false,
            Summary: "rework execution became unavailable before making changes",
            Stdout: null,
            Stderr: "sandbox execution unavailable")
        {
            ExecutionUnavailable = true,
        };
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        _ = prompt;
        _ = credential;
        _ = resume;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        ResumeCalls++;
        var scratchpad = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = workingDirectory,
        }, ct);
        SawCheckpointScratchpad = scratchpad.Success;
        return scratchpad.Success
            ? new AgentResult(true, "rework resumed without source changes", null, null)
            : new AgentResult(false, "rework scratchpad was not restored", scratchpad.Stdout, scratchpad.Stderr);
    }

    public Task RequestPreemptAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct = default)
        => TestAgentTurnScratchpadCapture.CaptureAsync(sandbox, ct);
}

internal sealed class DurableFailureResumeAgent :
    IAgentRunner,
    IResumableAgentRunner,
    IPreemptibleAgentRunner,
    ICliSessionResumableAgentRunner
{
    public const string SessionId = "durable-session-7d858a41";
    private readonly bool _quotaFailure;
    private readonly bool _emitNativeSessionId;
    private readonly bool _writePartialBeforeFailure;
    private readonly bool _writeOnResume;
    private readonly bool _blockResume;
    private readonly bool _throwResumePreparationUnavailable;
    private readonly TaskCompletionSource _resumeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resumeCalls;

    public AgentKind Kind => AgentKind.Claude;
    public bool RequiresStructuredStreamForSessionId => false;
    public IQuotaFailureClassifier SessionResumeQuotaClassifier { get; } = new NoQuotaFailureClassifier();
    public int InitialWorkCalls { get; private set; }
    public int ResumeCalls => Volatile.Read(ref _resumeCalls);
    public Task ResumeEntered => _resumeEntered.Task;
    public AgentNativeSessionId? ResumedNativeSessionId { get; private set; }
    public bool SawCheckpointedPartialWork { get; private set; }
    public bool SawCheckpointScratchpad { get; private set; }

    public DurableFailureResumeAgent(
        bool quotaFailure = false,
        bool emitNativeSessionId = true,
        bool writePartialBeforeFailure = true,
        bool writeOnResume = true,
        bool blockResume = false,
        bool throwResumePreparationUnavailable = false)
    {
        _quotaFailure = quotaFailure;
        _emitNativeSessionId = emitNativeSessionId;
        _writePartialBeforeFailure = writePartialBeforeFailure;
        _writeOnResume = writeOnResume;
        _blockResume = blockResume;
        _throwResumePreparationUnavailable = throwResumePreparationUnavailable;
    }

    public void ReleaseResume() => _resumeRelease.TrySetResult();

    public string? TryExtractSessionId(string? stdout) => null;

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
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return await MergeAsync(sandbox, workingDirectory, prompt, ct);

        InitialWorkCalls++;
        if (_writePartialBeforeFailure)
        {
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf '%s\\n' partial > partial-before-infrastructure-failure.txt"],
                WorkingDirectory = workingDirectory,
            }, ct);
            if (!write.Success)
                return new AgentResult(false, "failed to create partial work", write.Stdout, write.Stderr);
        }

        return new AgentResult(
            Success: false,
            Summary: _quotaFailure
                ? "agent reported quota exhaustion"
                : "agent execution became unavailable",
            Stdout: "native session initialized before interruption",
            Stderr: _quotaFailure
                ? QuotaFailureClassifier.Marker
                : "sandbox execution unavailable")
        {
            ExecutionUnavailable = !_quotaFailure,
            NativeSessionId = _emitNativeSessionId
                ? new AgentNativeSessionId(SessionId)
                : null,
        };
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        _ = prompt;
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        Interlocked.Increment(ref _resumeCalls);
        _resumeEntered.TrySetResult();
        if (_blockResume)
            await _resumeRelease.Task.WaitAsync(ct);
        if (_throwResumePreparationUnavailable)
            throw new AgentResumePreparationUnavailableException(255);
        ResumedNativeSessionId = resume.NativeSessionId;

        var partial = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", "partial-before-infrastructure-failure.txt"],
            WorkingDirectory = workingDirectory,
        }, ct);
        SawCheckpointedPartialWork = partial.Success;
        var scratchpad = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = workingDirectory,
        }, ct);
        SawCheckpointScratchpad = scratchpad.Success;
        if ((_writePartialBeforeFailure && !partial.Success) || !scratchpad.Success)
        {
            return new AgentResult(
                false,
                "durable checkpoint restore was incomplete",
                string.Concat(partial.Stdout, scratchpad.Stdout),
                string.Concat(partial.Stderr, scratchpad.Stderr));
        }

        if (!_writeOnResume)
            return new AgentResult(true, "resumed without source changes", null, null);

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf '%s\\n' resumed > resumed-after-infrastructure-failure.txt"],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(
            write.Success,
            write.Success ? "resumed" : "resume write failed",
            write.Stdout,
            write.Stderr);
    }

    public Task RequestPreemptAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct = default)
        => TestAgentTurnScratchpadCapture.CaptureAsync(sandbox, ct);

    private static async Task<AgentResult> MergeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", workingDirectory, "merge", "--no-ff",
                "-m", "codeybox: merge durable resume test",
                $"origin/{workBranch}",
            ],
        }, ct);
        return new AgentResult(
            result.Success,
            result.Success ? "merged" : "merge failed",
            result.Stdout,
            result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0)
            return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }

    private sealed class NoQuotaFailureClassifier : IQuotaFailureClassifier
    {
        public QuotaFailureClassification Classify(
            AgentKind agent,
            string? stderr,
            string? stdout) => QuotaFailureClassification.None;

        public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout) => null;
    }

    public sealed class QuotaFailureClassifier : IQuotaFailureClassifier
    {
        public const string Marker = "test provider quota exhausted";

        public QuotaFailureClassification Classify(
            AgentKind agent,
            string? stderr,
            string? stdout)
        {
            var detection = Detect(agent, stderr, stdout);
            return detection is null
                ? QuotaFailureClassification.None
                : QuotaFailureClassification.Quota(detection);
        }

        public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
            => string.Equals(stderr, Marker, StringComparison.Ordinal)
                ? new QuotaDetection(QuotaFailureKind.LimitReached)
                : null;
    }
}

internal sealed class HangingPreemptAgentRunner : IAgentRunner, IPreemptibleAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;

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
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AgentResult(false, "unreachable", null, null);
    }

    public Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default)
        => Task.Delay(Timeout.InfiniteTimeSpan);
}

internal sealed class LateRepositoryWritingPreemptAgentRunner : IAgentRunner, IPreemptibleAgentRunner
{
    private readonly TimeSpan _writeDelay;

    public LateRepositoryWritingPreemptAgentRunner(TimeSpan writeDelay) => _writeDelay = writeDelay;

    public AgentKind Kind => AgentKind.Claude;
    public TaskCompletionSource<bool> LateWriteCompleted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AgentResult(false, "unreachable", null, null);
    }

    public async Task RequestPreemptAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct = default)
    {
        await Task.Delay(_writeDelay, CancellationToken.None);
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; mkdir -p .codeybox/preempt-scratchpad.late; printf '%s\n' private-provider-state > .codeybox/preempt-scratchpad.late/session.txt; printf '%s\n' late > .codeybox/preempt-scratchpad.tgz",
            ],
            WorkingDirectory = workingDirectory,
        }, CancellationToken.None);
        LateWriteCompleted.TrySetResult(write.Success);
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }
}

internal sealed class CancellationObservingSlowPreemptAgentRunner : IAgentRunner, IPreemptibleAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public bool CancellationObserved { get; private set; }

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
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AgentResult(false, "unreachable", null, null);
    }

    public async Task RequestPreemptAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct = default)
    {
        await TestAgentTurnScratchpadCapture.CaptureAsync(sandbox, ct);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancellationObserved = true;
            throw;
        }
    }
}

internal sealed class StartupResumeRecordingAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }
    public bool LegacyScratchpadFilesAbsentBeforeResume { get; private set; }
    public bool PrivateScratchpadPresentBeforeResume { get; private set; }
    public bool RestoredScratchpad { get; private set; }

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
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        throw new InvalidOperationException("preempted startup work should use RunResumedAsync");
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        var legacyFilesAbsent = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "test ! -e \"$1\" && test ! -L \"$1\" && test ! -e \"$2\" && test ! -L \"$2\"",
                "legacy-scratchpad-check",
                ".codeybox/preempt-scratchpad.tgz",
                ".codeybox/preempt-scratchpad.md",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        LegacyScratchpadFilesAbsentBeforeResume = legacyFilesAbsent.Success;

        var privateScratchpad = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", resume.ScratchpadArchivePath],
            WorkingDirectory = workingDirectory,
        }, ct);
        PrivateScratchpadPresentBeforeResume = privateScratchpad.Success;
        if (!legacyFilesAbsent.Success || !privateScratchpad.Success)
        {
            return new AgentResult(
                false,
                "legacy scratchpad migration was incomplete",
                string.Concat(legacyFilesAbsent.Stdout, privateScratchpad.Stdout),
                string.Concat(legacyFilesAbsent.Stderr, privateScratchpad.Stderr));
        }

        var restore = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; tmp=$(mktemp -d); trap 'rm -rf -- \"$tmp\"' EXIT; tar -xzf \"$1\" -C \"$tmp\"; test -f \"$tmp/home/.testagent/session.txt\"; cp -a \"$tmp/home/.\" \"$HOME/\"; test -f \"$HOME/.testagent/session.txt\"; printf '%s\n' resumed > resumed-startup.txt",
                "startup-resume",
                resume.ScratchpadArchivePath,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        RestoredScratchpad = restore.Success;
        return new AgentResult(restore.Success, restore.Success ? "ok" : "restore failed", restore.Stdout, restore.Stderr);
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge startup resume test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}

internal sealed class ReworkResumeRecordingAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }
    public string LastResumePrompt { get; private set; } = string.Empty;
    public bool SawScratchpad { get; private set; }

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
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        throw new InvalidOperationException("preempted rework should use RunResumedAsync");
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        LastResumePrompt = prompt;
        var scratchpad = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", resume.ScratchpadArchivePath],
            WorkingDirectory = workingDirectory,
        }, ct);
        SawScratchpad = scratchpad.Success;

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf '%s\n' resumed > resumed-rework.txt"],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(true, "ok", "resumed", null);
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}

internal sealed class NoopResumeAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }

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
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        return Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    public Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        return Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge noop resume test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}

internal sealed class PreemptPushSyncObservingSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;
    private int _preemptCheckpointSyncs;
    private int _resumedCheckpointWorkBranchSyncs;

    public PreemptPushSyncObservingSandboxProvider(ISandboxProvider inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;
    public int PreemptCheckpointSyncs => Volatile.Read(ref _preemptCheckpointSyncs);
    public int ResumedCheckpointWorkBranchSyncs => Volatile.Read(ref _resumedCheckpointWorkBranchSyncs);

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => new PreemptPushSyncObservingSandbox(await _inner.CreateAsync(spec, ct), this);

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);

    private void RecordPreemptCheckpointSync() => Interlocked.Increment(ref _preemptCheckpointSyncs);
    private void RecordResumedCheckpointWorkBranchSync() => Interlocked.Increment(ref _resumedCheckpointWorkBranchSyncs);

    private sealed class PreemptPushSyncObservingSandbox : ISandbox
    {
        private readonly ISandbox _inner;
        private readonly PreemptPushSyncObservingSandboxProvider _owner;
        private int _pendingPreemptCheckpointPush;
        private int _pendingResumedWorkBranchPush;

        public PreemptPushSyncObservingSandbox(
            ISandbox inner,
            PreemptPushSyncObservingSandboxProvider owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var result = await _inner.ExecAsync(exec, ct);
            if (!result.Success) return result;

            if (IsGitPushToRef(exec.Argv, "refs/heads/codeybox/preempt/"))
                Interlocked.Exchange(ref _pendingPreemptCheckpointPush, 1);
            else if (IsGitPushToWorkBranch(exec.Argv))
                Interlocked.Exchange(ref _pendingResumedWorkBranchPush, 1);

            return result;
        }

        public async Task SyncStateToHostAsync(CancellationToken ct = default)
        {
            var preemptCheckpointPending = Interlocked.Exchange(ref _pendingPreemptCheckpointPush, 0) == 1;
            var resumedWorkBranchPending = Interlocked.Exchange(ref _pendingResumedWorkBranchPush, 0) == 1;

            await _inner.SyncStateToHostAsync(ct);

            if (preemptCheckpointPending) _owner.RecordPreemptCheckpointSync();
            if (resumedWorkBranchPending) _owner.RecordResumedCheckpointWorkBranchSync();
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private static bool IsGitPushToRef(IReadOnlyList<string> argv, string refPrefix)
            => argv.Contains("push", StringComparer.Ordinal)
                && argv.Any(arg => arg.StartsWith($"HEAD:{refPrefix}", StringComparison.Ordinal));

        private static bool IsGitPushToWorkBranch(IReadOnlyList<string> argv)
            => argv.Contains("push", StringComparer.Ordinal)
                && argv.Any(arg => arg.StartsWith("HEAD:", StringComparison.Ordinal)
                    && !arg.StartsWith("HEAD:refs/heads/codeybox/preempt/", StringComparison.Ordinal));
    }
}

internal sealed class PostAgentGitUnavailableSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;

    public PostAgentGitUnavailableSandboxProvider(ISandboxProvider inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => new PostAgentGitUnavailableSandbox(await _inner.CreateAsync(spec, ct));

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);

    private sealed class PostAgentGitUnavailableSandbox : ISandbox
    {
        private readonly ISandbox _inner;
        private int _injected;
        private int _agentCompleted;

        public PostAgentGitUnavailableSandbox(ISandbox inner)
        {
            _inner = inner;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _agentCompleted) != 0
                && exec.Argv.Count >= 5
                && exec.Argv[0] == "git"
                && exec.Argv.Contains("add", StringComparer.Ordinal)
                && exec.Argv.Contains("-A", StringComparer.Ordinal)
                && Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
            {
                return new SandboxExecResult(
                    1,
                    Stdout: "",
                    Stderr: "injected unavailable exec",
                    ExecutionUnavailable: true);
            }

            var result = await _inner.ExecAsync(exec, ct);
            if (result.Success
                && exec.Argv.Any(argument => argument.Contains(
                    "completed-before-git-infrastructure-failure.txt",
                    StringComparison.Ordinal)))
            {
                Interlocked.Exchange(ref _agentCompleted, 1);
            }
            return result;
        }

        public Task SyncStateToHostAsync(CancellationToken ct = default)
            => _inner.SyncStateToHostAsync(ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

internal sealed class CapturingSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;

    public CapturingSandboxProvider(ISandboxProvider inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;
    public List<SandboxSpec> Specs { get; } = [];

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        Specs.Add(spec);
        return _inner.CreateAsync(spec, ct);
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);
}

internal sealed class PreserveFailingSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;

    public PreserveFailingSandboxProvider(ISandboxProvider inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => new PreserveFailingSandbox(await _inner.CreateAsync(spec, ct));

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);
}

internal sealed class PreserveFailingSandbox : IPreemptibleSandbox
{
    private readonly ISandbox _inner;

    public PreserveFailingSandbox(ISandbox inner)
    {
        _inner = inner;
    }

    public string Id => _inner.Id;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => _inner.ExecAsync(exec, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public Task StopAndPreserveAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("injected preserve failure");
}

internal sealed class PreserveCancelingSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;

    public PreserveCancelingSandboxProvider(ISandboxProvider inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => new PreserveCancelingSandbox(await _inner.CreateAsync(spec, ct));

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);
}

internal sealed class PreserveCancelingSandbox : IPreemptibleSandbox
{
    private readonly ISandbox _inner;

    public PreserveCancelingSandbox(ISandbox inner)
    {
        _inner = inner;
    }

    public string Id => _inner.Id;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => _inner.ExecAsync(exec, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public Task StopAndPreserveAsync(CancellationToken ct = default)
        => throw new OperationCanceledException("injected preserve timeout", ct);
}

/// <summary>
/// Test wrapper that gives a non-suspending sandbox provider an
/// <see cref="ISuspendableSandbox"/> capability. Used by the R8-core test
/// that proves <see cref="PipelineRunner"/> short-circuits its preempt-checkpoint
/// flow when the suspend handler has already frozen the VM (i.e. the
/// wrapper's <see cref="SuspendableSandboxWrapper.IsSuspended"/> is true at
/// the moment the host-shutdown OCE catch runs).
/// </summary>
internal sealed class SuspendableSandboxProvider : ISandboxProvider
{
    private readonly ISandboxProvider _inner;
    private readonly List<SuspendableSandboxWrapper> _wrappers = new();
    private readonly object _gate = new();

    public SuspendableSandboxProvider(ISandboxProvider inner) { _inner = inner; }

    public string Name => _inner.Name;
    public IReadOnlyList<SuspendableSandboxWrapper> Wrappers
    {
        get { lock (_gate) return _wrappers.ToArray(); }
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var inner = await _inner.CreateAsync(spec, ct);
        var wrapper = new SuspendableSandboxWrapper(inner);
        lock (_gate) _wrappers.Add(wrapper);
        return wrapper;
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => _inner.ListAllManagedAsync(ct);
    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => _inner.DisposeLeakedAsync(name, ct);
}

internal sealed class SuspendableSandboxWrapper : IPreemptibleSandbox, ISuspendableSandbox, IShutdownTeardownSandbox
{
    private readonly ISandbox _inner;
    private bool _ownedByShutdownHandler;
    public SuspendableSandboxWrapper(ISandbox inner) { _inner = inner; }

    public string Id => _inner.Id;
    public bool IsSuspended { get; set; }
    public bool IsOwnedByShutdownHandler => IsSuspended || _ownedByShutdownHandler;
    public int StopAndPreserveCalls { get; private set; }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => _inner.ExecAsync(exec, ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public Task SuspendAsync(CancellationToken ct = default)
    {
        IsSuspended = true;
        return Task.CompletedTask;
    }

    public Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        StopAndPreserveCalls++;
        if (_inner is IPreemptibleSandbox preemptible)
            return preemptible.StopAndPreserveAsync(ct);
        return Task.CompletedTask;
    }

    public void MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;
}
