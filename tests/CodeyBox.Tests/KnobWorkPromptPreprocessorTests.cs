using System.Runtime.CompilerServices;
using CodeyBox.Core;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class KnobWorkPromptPreprocessorTests
{
    [Fact]
    public async Task ChangeScopeSurgical_AppendsFragmentToWorkPhase()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "original prompt");

        Assert.Contains("Per-item directives (knobs)", result);
        Assert.Contains("changeScope=surgical", result);
        Assert.Contains("SURGICAL", result);
        Assert.StartsWith("original prompt", result);
    }

    [Fact]
    public async Task ChangeScopeRefactor_AppendsRefactorFragment()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor,
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "do the thing");

        Assert.Contains("REFACTOR", result);
        Assert.Contains("changeScope=refactor", result);
    }

    [Fact]
    public async Task ChangeScopeModerate_ContributesNoFragment_PromptUnchanged()
    {
        // moderate matches the existing default agent behaviour; a knob "with
        // nothing to say contributes nothing", so the assembled prompt is
        // byte-identical to the input.
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueModerate,
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoKnobsSet_FallsThroughToKnobDefault_NoFragmentForChangeScopeDefault()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "stable prompt");

        Assert.Equal("stable prompt", result);
    }

    [Fact]
    public async Task ProjectDefaultIsApplied_WhenItemHasNoOverride()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var project = NewProject(projectKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
        });
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(
            NewContext(store.Item.Id, AgentPromptPhase.Work, project),
            "task");

        Assert.Contains("SURGICAL", result);
        Assert.Contains("changeScope=surgical", result);
    }

    [Fact]
    public async Task ItemOverrideWinsOverProjectDefault()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor,
        }));
        var project = NewProject(projectKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
        });
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(
            NewContext(store.Item.Id, AgentPromptPhase.Work, project),
            "task");

        Assert.Contains("REFACTOR", result);
        Assert.DoesNotContain("SURGICAL", result);
    }

    [Fact]
    public async Task NonWorkPhases_AreSkipped_PromptUnchanged()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        foreach (var phase in new[] { AgentPromptPhase.Audit, AgentPromptPhase.Merge, AgentPromptPhase.CheckAndAct })
        {
            var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, phase), "carried");
            Assert.Equal("carried", result);
        }
    }

    [Fact]
    public async Task ReworkPhase_IsSkipped_PromptUnchanged()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Rework), "rework prompt");

        Assert.Equal("rework prompt", result);
    }

    [Fact]
    public async Task SecondKnob_RegisteredWithoutPipelineEdit_HasFragmentInjected()
    {
        // Acceptance: adding a SECOND knob is just an IKnob descriptor.
        // Register a fake knob ALONGSIDE the real changeScope one, set its
        // value on the work item, and verify both fragments appear without
        // any pipeline-core edit.
        var registry = new KnobRegistry(
        [
            new ChangeScopeKnob(),
            new FakeSecondKnob(),
        ]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
            [FakeSecondKnob.KeyName] = "loud",
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "input");

        Assert.Contains("SURGICAL", result);
        Assert.Contains("fake-second knob fragment: loud", result);
        Assert.Contains("changeScope=surgical", result);
        Assert.Contains($"{FakeSecondKnob.KeyName}=loud", result);
    }

    [Fact]
    public async Task UnknownPersistedKnobKey_IsIgnoredAtPromptTime()
    {
        // The API path validates at set-time; reaching the prompt with an
        // unknown key means the registered knob set changed after the value
        // was persisted. The preprocessor must drop unknown keys instead of
        // failing the pipeline.
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["legacyKnob"] = "old-value",
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "p");

        Assert.Equal("p", result);
    }

    [Fact]
    public async Task EmptyRegistry_LeavesPromptUnchanged()
    {
        var registry = new KnobRegistry([]);
        var store = new SingleItemStore(NewItem());
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var result = await preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "stable");

        Assert.Equal("stable", result);
    }

    [Fact]
    public async Task StoreLoadFailure_PropagatesInsteadOfDroppingItemKnobs()
    {
        var registry = new KnobRegistry([new ChangeScopeKnob()]);
        var store = new FailingGetStore();
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => preprocessor.ProcessAsync(NewContext(WorkItemId.New(), AgentPromptPhase.Work), "prompt"));

        Assert.Contains("store unavailable", ex.Message);
    }

    [Fact]
    public async Task FreeFormPromptFragment_WithoutSafetyOptIn_Throws()
    {
        var registry = new KnobRegistry([new UnsafeFreeFormKnob()]);
        var store = new SingleItemStore(NewItem(knobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [UnsafeFreeFormKnob.KeyName] = "operator text",
        }));
        var preprocessor = new KnobWorkPromptPreprocessor(registry, store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => preprocessor.ProcessAsync(NewContext(store.Item.Id, AgentPromptPhase.Work), "prompt"));

        Assert.Contains("must declare finite AllowedValues", ex.Message);
    }

    [Fact]
    public void ChangeScopeKnob_GetWorkPromptFragment_UnmappedValue_ReturnsNull()
    {
        // The registry rejects out-of-range values at set-time, but a stale
        // value can reach the fragment method if the AllowedValues list changes
        // in code after a value was persisted. Pin the documented "knob with
        // nothing to say contributes nothing" behaviour for unmapped values so
        // a future edit that flips to a throw or fallback fragment surfaces here.
        var knob = new ChangeScopeKnob();
        Assert.Null(knob.GetWorkPromptFragment("yolo"));
        Assert.Null(knob.GetWorkPromptFragment(""));
        Assert.Null(knob.GetWorkPromptFragment("MODERATE"));
    }

    [Fact]
    public void ChangeScopeKnob_GetWorkPromptFragment_CanonicalValues_ReturnExpectedFragments()
    {
        // Pin the surgical / refactor mapping at the knob level so the
        // preprocessor tests aren't the only place the value→fragment
        // contract is verified.
        var knob = new ChangeScopeKnob();
        var surgical = knob.GetWorkPromptFragment(ChangeScopeKnob.ValueSurgical);
        var refactor = knob.GetWorkPromptFragment(ChangeScopeKnob.ValueRefactor);
        Assert.NotNull(surgical);
        Assert.NotNull(refactor);
        Assert.Contains("SURGICAL", surgical);
        Assert.Contains("REFACTOR", refactor);
    }

    private static WorkItem NewItem(IReadOnlyDictionary<string, string>? knobs = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("knob-test-project"),
        Title = "test",
        Prompt = "test prompt",
        Knobs = knobs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    private static Project NewProject(IReadOnlyDictionary<string, string>? projectKnobs = null) => new()
    {
        Id = new ProjectId("knob-test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        Knobs = projectKnobs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    private static PromptContext NewContext(
        WorkItemId itemId,
        AgentPromptPhase phase,
        Project? project = null) => new(
            itemId,
            AgentKind.Claude,
            phase,
            Iteration: 1,
            project ?? NewProject(),
            new NoopSandbox(),
            "/work");

    private sealed class FakeSecondKnob : IKnob
    {
        public const string KeyName = "loudness";

        public string Key => KeyName;
        public string Description => "fake second knob to prove a second knob is just a descriptor";
        public IReadOnlyList<string> AllowedValues { get; } = ["quiet", "loud"];
        public string DefaultValue => "quiet";
        public string? GetWorkPromptFragment(string value) =>
            string.Equals(value, "loud", StringComparison.OrdinalIgnoreCase)
                ? "fake-second knob fragment: loud"
                : null;
    }

    private sealed class UnsafeFreeFormKnob : IKnob
    {
        public const string KeyName = "freeFormPrompt";

        public string Key => KeyName;
        public string Description => "unsafe free-form prompt knob";
        public IReadOnlyList<string> AllowedValues => [];
        public string DefaultValue => "default";
        public string? GetWorkPromptFragment(string value) => $"raw value: {value}";
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id => "noop-sandbox";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(1, "", "noop"));
    }

    /// <summary>
    /// Minimal in-memory store: only <see cref="GetAsync"/> is used by the
    /// preprocessor; the rest throw if accidentally exercised. Mirrors the
    /// shape used by <c>HotReloadConfigTests</c>.
    /// </summary>
    private sealed class SingleItemStore : IWorkItemStore
    {
        public WorkItem Item { get; }

        public SingleItemStore(WorkItem item)
        {
            Item = item;
        }

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<WorkItem?>(id == Item.Id ? Item : null);

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw NS();
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw NS();
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw NS();
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Empty(ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Empty(ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => throw NS();
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => Empty(ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw NS();
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => Empty(ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Empty(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw NS();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Empty(ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw NS();

        private static async IAsyncEnumerable<WorkItem> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static NotImplementedException NS() => new("Not used in knob preprocessor tests.");
    }

    private sealed class FailingGetStore : IWorkItemStore
    {
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw NS();
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw NS();
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw NS();
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Empty(ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Empty(ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => throw NS();
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => Empty(ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw NS();
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw NS();
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => Empty(ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Empty(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw NS();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw NS();
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Empty(ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw NS();
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw NS();
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw NS();

        private static async IAsyncEnumerable<WorkItem> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static NotImplementedException NS() => new("Not used in knob preprocessor tests.");
    }
}
