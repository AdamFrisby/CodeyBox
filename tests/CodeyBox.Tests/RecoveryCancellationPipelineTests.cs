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
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests that pin <see cref="PipelineRunner"/>'s handling of a
/// recovery-initiated cancellation (the watchdog calls
/// <see cref="CancellationRegistry.CancelForRecovery"/>) versus an
/// operator-initiated cancellation (DELETE /workitems/{id}).
///
/// The pipeline must:
/// 1. <b>Recovery cancel</b> — leave the recovered durable state intact and
///    rethrow the OCE. A regression that inverts the
///    <see cref="CancellationRegistry.GetRequestKind"/> check, removes the
///    guard, or routes a recovery cancel through HandleOperatorCancelAsync
///    would silently overwrite the recovered <see cref="WorkItemState.Queued"/>
///    (or other recovery-target state) with <see cref="WorkItemState.Cancelled"/>.
/// 2. <b>Operator cancel</b> — transition to
///    <see cref="WorkItemState.Cancelled"/>, but ONLY when the guarded write
///    (TryUpdateIfStateAsync against the snapshot state) wins. If the row has
///    advanced between the snapshot read and the write — e.g. a concurrent
///    recovery already wrote the recovered state — the operator-cancel handler
///    must NOT clobber the advanced state and must NOT fire the cancellation
///    webhook.
/// </summary>
[Collection("Pipeline integration")]
public sealed class RecoveryCancellationPipelineTests : IDisposable
{
    private readonly string _workspace;

    public RecoveryCancellationPipelineTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-recovery-cancel-").FullName;
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

    // ── Test 1: PhaseCancellationException recovery branch ────────────────────
    // The watchdog cancels a wedged work item via CancelForRecovery while the
    // agent is blocked in the work-phase PhaseCancellation scope. The OCE is
    // wrapped into a PhaseCancellationException with source=Operator (because
    // PhaseCancellation can't see the registry's kind — it only sees the token
    // cancel) and hits the catch at PipelineRunner.cs:1377. The guard at
    // IsRecoveryCancellation reads the registry's kind (Recovery) and rethrows
    // without calling HandleOperatorCancelAsync, so the recovered durable state
    // is preserved.

    [Fact]
    public async Task RecoveryCancel_DuringWorkPhase_DoesNotTransitionToCancelled_NoCancelWebhook()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var webhooks = new RecordingWebhookDispatcher();
        using var harness = BuildPipeline(seed, new BlockingAgentRunner(), registry, webhooks);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var registration = registry.Register(item.Id);
        using var hostShutdownCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, registration.Token, hostShutdownCts.Token));

        // Wait for the agent to actually enter the work phase. Only then is the
        // PhaseCancellation scope open; cancelling before this races the legacy
        // OCE catch (covered by the next test).
        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Watchdog-equivalent: the per-item stale watchdog calls
        // CancelForRecovery to abort the wedged worker. The token fires; the
        // registry records kind=Recovery so the pipeline can tell this apart
        // from an operator DELETE.
        Assert.True(registry.CancelForRecovery(item.Id));
        Assert.Equal(CancellationRequestKind.Recovery, registry.GetRequestKind(item.Id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        // Recovered durable state must survive: the item is left in its mid-flight
        // state (NOT transitioned to Cancelled). The watchdog re-enqueues it
        // separately via TryUpdateIfStateAndUpdatedAtAsync; this test pins only
        // that the pipeline does NOT overwrite it.
        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Cancelled, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.Null(final.CancellationSource);

        // The operator-cancel webhook must NOT fire for a recovery cancel —
        // operators would otherwise see spurious "work_item.cancelled" events
        // every time a wedged item is recovered.
        Assert.DoesNotContain(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.cancelled", StringComparison.Ordinal));
    }

    // ── Test 2: Legacy raw-OCE recovery branch ────────────────────────────────
    // For OCEs that escape WITHOUT being wrapped in PhaseCancellationException
    // (e.g. an OCE thrown from pickup-phase work before any PhaseCancellation is
    // entered), the catch at PipelineRunner.cs:1427 is the load-bearing one.
    // The same recovery-kind guard at line 1440 must skip HandleOperatorCancelAsync.

    [Fact]
    public async Task RecoveryCancel_FromPickupPhaseLegacyOceBranch_DoesNotTransitionToCancelled_NoCancelWebhook()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var webhooks = new RecordingWebhookDispatcher();
        var item = NewItem();

        // A git-host wrapper that, on EnsureRepositoryAsync, fires
        // CancelForRecovery for the test item BEFORE delegating to the inner
        // host. The cancellation propagates through `ct` to the very next
        // pickup-phase await (BranchExistsAsync), which throws a raw OCE that
        // escapes the outer try at PipelineRunner.cs:950 WITHOUT being wrapped
        // by any PhaseCancellation — so the legacy OCE catch at line 1427 is
        // the one that fires.
        IGitHost gitHostFactory(IGitHost real) =>
            new CancelOnEnsureRepoGitHost(real, registry, item.Id);

        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            registry,
            webhooks,
            gitHostFactory: gitHostFactory);

        await harness.Store.CreateAsync(item);
        using var registration = registry.Register(item.Id);
        using var hostShutdownCts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Pipeline.RunAsync(item, registration.Token, hostShutdownCts.Token));

        // Kind was recorded as Recovery; pipeline observed it and rethrew.
        Assert.Equal(CancellationRequestKind.Recovery, registry.GetRequestKind(item.Id));

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Cancelled, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.Null(final.CancellationSource);

        Assert.DoesNotContain(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.cancelled", StringComparison.Ordinal));
    }

    // ── Test 3: HandleOperatorCancelAsync's guarded !wrote early-return ───────
    // The cancel handler reads `current` via GetAsync, then writes Cancelled
    // via TryUpdateIfStateAsync(expected=current.State). If the row has advanced
    // between the read and the write — the scenario the guard exists to prevent
    // — the write returns false and the handler must early-return without
    // emitting the cancellation audit/webhook. A regression that reverts to a
    // plain UpdateAsync would overwrite the advanced state and would NOT be
    // caught by the IsRecoveryCancellation tests above.

    [Fact]
    public async Task OperatorCancel_RowAdvancedBetweenSnapshotAndWrite_DoesNotOverwrite_NoCancelWebhook()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var webhooks = new RecordingWebhookDispatcher();
        // Inject a one-shot race ONLY when the work item is in Working state
        // (the typical state when the cancel handler runs) and the race has been
        // armed via SetArmed(true). The wrapper returns the pre-race snapshot
        // (state=Working) so HandleOperatorCancelAsync passes expected=Working
        // to TryUpdateIfStateAsync — but the persisted state has already been
        // advanced to Queued, so the guarded write returns false.
        var raceFactory = (SqliteWorkItemStore inner) => new RaceAdvancingStore(inner);

        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            registry,
            webhooks,
            storeDecorator: raceFactory);

        var raceStore = (RaceAdvancingStore)harness.PipelineStore;

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var registration = registry.Register(item.Id);
        using var hostShutdownCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, registration.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Arm the race: the NEXT GetAsync that returns a Working row (the one
        // inside HandleOperatorCancelAsync) will silently advance the persisted
        // row to Queued before returning the Working snapshot to the caller.
        raceStore.ArmRace();

        // Operator-cancel (NOT recovery): kind=Operator, so the guard at
        // IsRecoveryCancellation is FALSE and HandleOperatorCancelAsync runs.
        Assert.True(registry.Cancel(item.Id));
        Assert.Equal(CancellationRequestKind.Operator, registry.GetRequestKind(item.Id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        // The race was injected — the guard observed the state mismatch and the
        // cancel handler took the !wrote early-return.
        Assert.True(raceStore.RaceInjected,
            "Race was not injected — the test did not exercise the !wrote branch.");

        // The advanced state survives: NOT overwritten with Cancelled.
        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Queued, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.Null(final.CancellationSource);

        // The cancel webhook must NOT fire — the handler early-returned before
        // PublishAsync. A regression that reverts the guard to UpdateAsync
        // would emit "work_item.cancelled" against the advanced row, observable
        // here.
        Assert.DoesNotContain(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperatorCancel_RowRecoveredBeforeCancelHandlerRead_DoesNotOverwrite_NoCancelWebhook()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var webhooks = new RecordingWebhookDispatcher();
        var raceFactory = (SqliteWorkItemStore inner) => new RaceAdvancingStore(inner);

        using var harness = BuildPipeline(
            seed,
            new BlockingAgentRunner(),
            registry,
            webhooks,
            storeDecorator: raceFactory);

        var raceStore = (RaceAdvancingStore)harness.PipelineStore;

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var registration = registry.Register(item.Id);
        using var hostShutdownCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, registration.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        raceStore.ArmRace();
        var staleSnapshot = await raceStore.GetAsync(item.Id);
        Assert.NotNull(staleSnapshot);
        Assert.Equal(WorkItemState.Working, staleSnapshot!.State);
        Assert.True(raceStore.RaceInjected,
            "Race was not injected — the test did not exercise the recovered-before-handler branch.");

        var recoveredBeforeCancel = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(recoveredBeforeCancel);
        Assert.Equal(WorkItemState.Queued, recoveredBeforeCancel!.State);

        Assert.True(registry.Cancel(item.Id));
        Assert.Equal(CancellationRequestKind.Operator, registry.GetRequestKind(item.Id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Queued, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.Null(final.CancellationSource);

        Assert.DoesNotContain(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperatorCancel_RacingTransientAgentFailure_CancelsInsteadOfParkingTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var webhooks = new RecordingWebhookDispatcher();
        var agent = new TransientAfterCancellationAgentRunner();
        using var harness = BuildPipeline(seed, agent, registry, webhooks);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var registration = registry.Register(item.Id);
        using var hostShutdownCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, registration.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(registry.Cancel(item.Id));
        Assert.Equal(CancellationRequestKind.Operator, registry.GetRequestKind(item.Id));

        await pipelineTask;

        var final = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, final.CancellationReason);
        Assert.Equal(CancellationSources.Operator, final.CancellationSource);
        Assert.NotEqual("transient", final.FailureKind);

        Assert.Contains(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(webhooks.Events, e =>
            string.Equals(e.Event, "work_item.waiting_for_transient_retry", StringComparison.Ordinal));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private RecoveryCancelTestHarness BuildPipeline(
        string seedRepoUrl,
        IAgentRunner agent,
        CancellationRegistry registry,
        IWebhookDispatcher webhooks,
        Func<IGitHost, IGitHost>? gitHostFactory = null,
        Func<SqliteWorkItemStore, IWorkItemStore>? storeDecorator = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        IWorkItemStore pipelineStore = storeDecorator?.Invoke(store) ?? store;
        var realGitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        IGitHost gitHost = gitHostFactory?.Invoke(realGitHost) ?? realGitHost;
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registryOfAgents = new AgentRegistry([agent]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var terminalTransitions = TestSupport.CreateTerminalTransition(pipelineStore, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registryOfAgents, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            pipelineStore,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            cancellationRegistry: registry,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new RecoveryCancelTestHarness(pipeline, store, pipelineStore);
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

    private sealed class RecoveryCancelTestHarness : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public IWorkItemStore PipelineStore { get; }

        public RecoveryCancelTestHarness(PipelineRunner pipeline, SqliteWorkItemStore store, IWorkItemStore pipelineStore)
        {
            Pipeline = pipeline;
            Store = store;
            PipelineStore = pipelineStore;
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        private readonly List<WebhookEvent> _events = new();
        private readonly object _gate = new();

        public IReadOnlyList<WebhookEvent> Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            lock (_gate) _events.Add(evt);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// IGitHost wrapper that fires <see cref="CancellationRegistry.CancelForRecovery"/>
/// at <see cref="EnsureRepositoryAsync"/> entry, then throws a raw
/// <see cref="OperationCanceledException"/> for the now-cancelled token. The
/// pipeline's outer try-block calls EnsureRepositoryAsync BEFORE any
/// PhaseCancellation scope is opened, so the OCE escapes WITHOUT being wrapped
/// into <see cref="PhaseCancellationException"/> and exercises the legacy raw-OCE
/// catch at PipelineRunner.cs:1427 — not the PhaseCancellationException catch
/// at line 1377.
///
/// Only abstract IGitHost members are implemented; everything else uses the
/// interface defaults (the pipeline never reaches those calls in this test
/// because the OCE fires before any work phase begins).
/// </summary>
internal sealed class CancelOnEnsureRepoGitHost : IGitHost
{
    private readonly CancellationRegistry _registry;
    private readonly WorkItemId _targetId;
    private int _fired;

    public CancelOnEnsureRepoGitHost(IGitHost inner, CancellationRegistry registry, WorkItemId targetId)
    {
        _ = inner;
        _registry = registry;
        _targetId = targetId;
    }

    public Task<string> EnsureRepositoryAsync(
        WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, null, ct);

    public Task<string> EnsureRepositoryAsync(
        WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
    {
        if (id == _targetId && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _registry.CancelForRecovery(_targetId);
        }
        ct.ThrowIfCancellationRequested();
        // Defensive: if for any reason the ct above is not yet observed
        // cancelled (the link propagation is synchronous in practice), throw
        // the OCE explicitly so the legacy-OCE catch still fires for this test.
        throw new OperationCanceledException("test: recovery cancel injected before pickup");
    }

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) =>
        throw new NotSupportedException("pipeline should never reach this — OCE fires first");

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) =>
        Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) =>
        Task.FromResult((string.Empty, string.Empty));
}

/// <summary>
/// IWorkItemStore wrapper that, once armed, silently advances the persisted
/// row from Working → Queued on the NEXT <see cref="GetAsync"/> call that
/// returns a Working row. Returns the pre-race snapshot so callers see the
/// stale state. Used to exercise the !wrote early-return branch in
/// PipelineRunner.HandleOperatorCancelAsync (PipelineRunner.cs:11568).
/// </summary>
internal sealed class RaceAdvancingStore : IWorkItemStore
{
    private readonly SqliteWorkItemStore _inner;
    private int _armed;
    private int _injected;

    public RaceAdvancingStore(SqliteWorkItemStore inner) { _inner = inner; }

    public void ArmRace() => Interlocked.Exchange(ref _armed, 1);
    public bool RaceInjected => Volatile.Read(ref _injected) != 0;

    public async Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default)
    {
        var result = await _inner.GetAsync(id, ct).ConfigureAwait(false);
        if (Volatile.Read(ref _armed) != 0
            && Interlocked.Exchange(ref _injected, 1) == 0
            && result is { State: WorkItemState.Working })
        {
            // Advance the persisted state under the caller. The next
            // TryUpdateIfStateAsync(_, onlyIfState=Working) will find the row
            // in Queued state and return false — the exact race the !wrote
            // branch defends against.
            var advanced = result with
            {
                State = WorkItemState.Queued,
                UpdatedAt = result.UpdatedAt.AddMilliseconds(1),
            };
            await _inner.UpdateAsync(advanced, ct).ConfigureAwait(false);
        }
        return result;
    }

    public Task CreateAsync(WorkItem item, CancellationToken ct = default) => _inner.CreateAsync(item, ct);
    public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => _inner.UpdateAsync(item, ct);
    public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
        _inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
    public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
    public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
    public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
    public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
    public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.ListByStateAsync(state, ct);
    public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.CountByStateAsync(state, ct);
    public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => _inner.ReorderAsync(orderedIds, ct);
    public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
        _inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
    public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
        _inner.CountStartedInWindowAsync(projectId, since, ct);
    public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => _inner.CountInFlightAsync(projectId, ct);
    public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
        _inner.GetByExternalIdAsync(projectId, externalId, ct);
    public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
        _inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
    public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
    public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
        _inner.GetFleetStateCountsAsync(ct);
    public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
        _inner.GetFleetRecentOutcomesAsync(perProject, ct);
    public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
        _inner.GetFleetPauseStatesAsync(ct);
    public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
        _inner.ListByReplaySourceAsync(sourceId, ct);
    public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => _inner.ListSuspendedAsync(ct);
    public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => _inner.GetActiveBaselineImageRefsAsync(ct);
    public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
        _inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
    public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => _inner.OrphanReplaysAsync(sourceId, ct);
    public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => _inner.ListByReleaseAsync(releaseId, ct);
    public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
    public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
        _inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
    public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
        _inner.GetIterationsAsync(workItemId, ct);
}

internal sealed class TransientAfterCancellationAgentRunner : IAgentRunner
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentKind Kind { get; init; } = AgentKind.Claude;

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
        Started.TrySetResult();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        return new AgentResult(
            false,
            "agent exited 1",
            Stdout: null,
            Stderr: "request timed out while reading agent stream");
    }
}
