using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the fail → retry → fail loop where a terminally-failed
/// work item was re-queued with its stale baseline pin intact: baseline
/// migration deliberately skips terminal items, so a pin that no longer
/// matches the item's current route could never be repaired and every retry
/// re-launched on the wrong baseline (the failure then named a missing agent
/// binary, not the stale pin, inviting another retry). The retrier now
/// re-validates the pin against live config when retrying out of a terminal
/// state: stale → cleared so the next pickup recomputes; still-current → kept.
/// Parked-state retries are the same in-flight attempt and keep their pin.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemRetrierBaselinePinTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("retry-baseline-pin");
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-retrypin-");
    private string DbPath => _scratch.DbPath("retrier-pin.db");

    private readonly StubBaselineResolver _resolver = new();
    private readonly SqliteWorkItemStore _store;
    private readonly InMemoryTaskQueue _queue = new();
    private readonly WorkItemRetrier _retrier;

    public WorkItemRetrierBaselinePinTests()
    {
        _store = new SqliteWorkItemStore(DbPath);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Retry pin",
            RepositoryUrl = "https://example.invalid/retry-pin",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
        });
        _retrier = new WorkItemRetrier(
            _store,
            _queue,
            new ThrowingGitHost(),
            NullLogger<WorkItemRetrier>.Instance,
            projects: projects,
            baselineResolver: _resolver);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestScratchDirectory.DeleteSqliteCompanions(DbPath);
        _scratch.Dispose();
    }

    /// <summary>
    /// The loop from the incident report: a Failed item whose pin no longer
    /// matches what its route resolves to must NOT carry that pin into the new
    /// attempt. With the pin cleared, the next pickup recomputes the ref from
    /// live config instead of cloning the baseline that lacks the binary.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_ClearsStaleBaselinePin()
    {
        _resolver.Current = "cb-baseline-new";
        var item = PinnedItem("cb-baseline-old");
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal(item.Id, await _queue.DequeueAsync(CancellationToken.None));
        var persisted = await _store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.BaselineImageRef);
        Assert.Null(persisted.BaselineImageAgent);
    }

    /// <summary>
    /// A pin that still equals what the current route resolves to — same ref,
    /// attributed to the agent the retried item will dispatch as — survives
    /// the retry, so a healthy in-flight pin is not churned on every retry.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_KeepsPinThatStillMatchesRoute()
    {
        _resolver.Current = "cb-baseline-old";
        var item = PinnedItem("cb-baseline-old");
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-baseline-old", persisted!.BaselineImageRef);
        Assert.Equal(AgentKind.Claude, persisted.BaselineImageAgent);
    }

    /// <summary>
    /// A pin whose ref still resolves but which was attributed to a different
    /// agent kind cannot serve the retried item's route — the ref is only
    /// valid for the agent it was resolved under. Clearing re-stamps the same
    /// route at pickup with the correct attribution.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_ClearsPinAttributedToAnotherAgent()
    {
        _resolver.Current = "cb-baseline-old";
        var item = PinnedItem("cb-baseline-old", agent: AgentKind.Devin);
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Null(persisted!.BaselineImageRef);
        Assert.Null(persisted.BaselineImageAgent);
    }

    /// <summary>
    /// A pin whose agent attribution is missing (rows predating the column)
    /// cannot be verified against the route and is cleared so pickup
    /// re-resolves and attributes it — matching the dispatch-time reconcile,
    /// which also refuses to trust an unattributed pin blindly.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_ClearsUnattributedPin()
    {
        _resolver.Current = "cb-baseline-old";
        var item = PinnedItem("cb-baseline-old") with { BaselineImageAgent = null };
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Null(persisted!.BaselineImageRef);
    }

    /// <summary>
    /// When the current route resolves to no baseline at all (baselines
    /// disabled, or the provider does not model them for this profile), any
    /// surviving pin is stale by definition and is dropped.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_ClearsPin_WhenRouteResolvesNoBaseline()
    {
        _resolver.Current = null;
        var item = PinnedItem("cb-baseline-old");
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Null(persisted!.BaselineImageRef);
    }

    /// <summary>
    /// A resolver fault maps to "no current baseline" — the same fail-open
    /// semantics as pickup-time resolution — so a stale pin cannot hide behind
    /// a transient resolver error and the retry still proceeds.
    /// </summary>
    [Fact]
    public async Task TerminalRetry_ClearsPin_WhenResolverThrows()
    {
        _resolver.ThrowOnResolve = true;
        var item = PinnedItem("cb-baseline-old");
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Null(persisted!.BaselineImageRef);
    }

    /// <summary>
    /// Retrying a PARKED (non-terminal) item resumes the same in-flight
    /// attempt, so its pin is preserved even when it no longer matches live
    /// config — the pin is precisely what holds an in-flight item on the
    /// baseline it started with when config drifts underneath.
    /// </summary>
    [Fact]
    public async Task ParkedRetry_KeepsBaselinePin()
    {
        _resolver.Current = "cb-baseline-new";
        var item = PinnedItem("cb-baseline-old", state: WorkItemState.NeedsOperatorInput);
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Equal("cb-baseline-old", persisted!.BaselineImageRef);
        Assert.Equal(AgentKind.Claude, persisted.BaselineImageAgent);
    }

    [Theory]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    [InlineData(WorkItemState.NoActionRequired)]
    public async Task TerminalRetry_ClearsStalePin_AcrossAllTerminalStates(WorkItemState terminal)
    {
        _resolver.Current = "cb-baseline-new";
        var item = PinnedItem("cb-baseline-old", state: terminal);
        await _store.CreateAsync(item);

        var result = await _retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        var persisted = await _store.GetAsync(item.Id);
        Assert.Null(persisted!.BaselineImageRef);
    }

    private static WorkItem PinnedItem(
        string pin,
        AgentKind? agent = null,
        WorkItemState state = WorkItemState.Failed)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "Pinned item",
            Prompt = "do work",
            Agent = agent ?? AgentKind.Claude,
            State = state,
            LastError = "previous failure",
            BaselineImageRef = pin,
            // The pin is always attributed to the agent that originally
            // resolved it; `agent` above is the route the item dispatches as
            // now, which may differ.
            BaselineImageAgent = AgentKind.Claude,
        };

    private sealed class StubBaselineResolver : IBaselineImageResolver
    {
        /// <summary>The ref the live config currently resolves to.</summary>
        public string? Current { get; set; }

        public bool ThrowOnResolve { get; set; }

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolver unavailable");
            return Current;
        }

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
