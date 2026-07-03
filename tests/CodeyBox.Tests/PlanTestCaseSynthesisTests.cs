using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class PlanTestCaseSynthesisTests
{
    // ---- Classification --------------------------------------------------

    [Theory]
    [InlineData("Add a unit test for the parser", AutomationKind.Unit)]
    [InlineData("Unit-test the classifier edge cases", AutomationKind.Unit)]
    [InlineData("Integration test covering the SQLite store", AutomationKind.Integration)]
    [InlineData("End-to-end replay of the checkout flow", AutomationKind.E2eReplay)]
    [InlineData("e2e browser test via Playwright", AutomationKind.E2eReplay)]
    [InlineData("Record a Cypress replay for the login page", AutomationKind.E2eReplay)]
    [InlineData("Manually verify the build passes with warnings-as-errors", AutomationKind.Manual)]
    [InlineData("Confirm the README renders", AutomationKind.Manual)]
    public void ClassifyAutomationKind_maps_markers(string scenario, AutomationKind expected)
        => Assert.Equal(expected, PlanTestCaseSynthesizer.ClassifyAutomationKind(scenario));

    [Fact]
    public void ClassifyAutomationKind_prefers_broader_scope_when_multiple_markers_present()
    {
        // Both "unit" and "integration" present -> integration (broader) wins.
        Assert.Equal(
            AutomationKind.Integration,
            PlanTestCaseSynthesizer.ClassifyAutomationKind("unit and integration tests for the pipeline"));
        // e2e outranks everything.
        Assert.Equal(
            AutomationKind.E2eReplay,
            PlanTestCaseSynthesizer.ClassifyAutomationKind("unit + integration + e2e coverage"));
    }

    [Fact]
    public void ClassifyAutomationKind_does_not_false_positive_on_substrings()
    {
        // "opportunity" / "community" embed "unit" but are not unit tests.
        Assert.Equal(
            AutomationKind.Manual,
            PlanTestCaseSynthesizer.ClassifyAutomationKind("Assess the opportunity for the community feature"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ClassifyAutomationKind_defaults_blank_to_manual(string scenario)
        => Assert.Equal(AutomationKind.Manual, PlanTestCaseSynthesizer.ClassifyAutomationKind(scenario));

    // ---- Deterministic id -------------------------------------------------

    [Fact]
    public void DeriveId_is_deterministic_and_ordinal_scoped()
    {
        var wid = WorkItemId.New();
        Assert.Equal(PlanTestCaseSynthesizer.DeriveId(wid, 0), PlanTestCaseSynthesizer.DeriveId(wid, 0));
        Assert.NotEqual(PlanTestCaseSynthesizer.DeriveId(wid, 0), PlanTestCaseSynthesizer.DeriveId(wid, 1));
    }

    [Fact]
    public void DeriveId_differs_by_work_item()
    {
        var a = PlanTestCaseSynthesizer.DeriveId(WorkItemId.New(), 0);
        var b = PlanTestCaseSynthesizer.DeriveId(WorkItemId.New(), 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveId_is_guid_n_format()
    {
        var id = PlanTestCaseSynthesizer.DeriveId(WorkItemId.New(), 3);
        Assert.True(Guid.TryParseExact(id, "N", out _));
    }

    // ---- Name building ----------------------------------------------------

    [Fact]
    public void BuildName_collapses_whitespace_and_bounds_length()
    {
        Assert.Equal("a b c", PlanTestCaseSynthesizer.BuildName("  a\n  b\t c  "));
        var name = PlanTestCaseSynthesizer.BuildName(new string('x', 500));
        Assert.Equal(120, name.Length);
    }

    // ---- Reconcile: emit-from-plan ---------------------------------------

    [Fact]
    public async Task Reconcile_emits_one_test_case_per_scenario_with_correct_kind()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var plan = PlanJson(
            "Unit test the reconciler",
            "Integration test the SQLite path",
            "End-to-end replay of the plan-approval flow",
            "Manually eyeball the log output");

        var result = await new PlanTestCaseReconciler(store).ReconcileAsync(wid, plan, Now);

        Assert.Equal(4, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);

        var cases = store.ForWorkItem(wid).ToList();
        Assert.Equal(4, cases.Count);
        Assert.All(cases, c => Assert.Equal(wid.ToString(), c.SourceWorkItemId));

        // Look up by deterministic ordinal id so the assertion doesn't depend on
        // store iteration order.
        var expected = new[]
        {
            AutomationKind.Unit,
            AutomationKind.Integration,
            AutomationKind.E2eReplay,
            AutomationKind.Manual,
        };
        for (var i = 0; i < expected.Length; i++)
        {
            var tc = await store.GetAsync(PlanTestCaseSynthesizer.DeriveId(wid, i));
            Assert.NotNull(tc);
            Assert.Equal(expected[i], tc!.AutomationKind);
        }
    }

    [Fact]
    public async Task Reconcile_creates_e2e_cases_without_a_committed_replay()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();

        await new PlanTestCaseReconciler(store).ReconcileAsync(
            wid, PlanJson("e2e replay of the dashboard"), Now);

        var only = Assert.Single(store.ForWorkItem(wid));
        Assert.Equal(AutomationKind.E2eReplay, only.AutomationKind);
        Assert.Null(only.ExecutableArtifactJson);
    }

    // ---- Reconcile: idempotent -------------------------------------------

    [Fact]
    public async Task Reconcile_is_idempotent_for_an_unchanged_plan()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var plan = PlanJson("Unit test A", "Integration test B");
        var reconciler = new PlanTestCaseReconciler(store);

        var first = await reconciler.ReconcileAsync(wid, plan, Now);
        var second = await reconciler.ReconcileAsync(wid, plan, Now.AddMinutes(5));

        Assert.Equal(2, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, store.ForWorkItem(wid).Count());
    }

    // ---- Reconcile: plan-rework ------------------------------------------

    [Fact]
    public async Task Reconcile_updates_changed_scenarios_in_place_without_duplicating()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var reconciler = new PlanTestCaseReconciler(store);

        await reconciler.ReconcileAsync(wid, PlanJson("Unit test A", "Integration test B"), Now);
        // Ordinal 0 changes kind + text; ordinal 1 unchanged.
        var result = await reconciler.ReconcileAsync(
            wid, PlanJson("End-to-end replay of A", "Integration test B"), Now.AddMinutes(1));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Removed);

        var cases = store.ForWorkItem(wid).OrderBy(c => c.CreatedAt).ToList();
        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, c => c.AutomationKind == AutomationKind.E2eReplay);
    }

    [Fact]
    public async Task Reconcile_prunes_scenarios_dropped_by_a_shorter_plan()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var reconciler = new PlanTestCaseReconciler(store);

        await reconciler.ReconcileAsync(wid, PlanJson("Unit A", "Unit B", "Unit C"), Now);
        var result = await reconciler.ReconcileAsync(wid, PlanJson("Unit A"), Now.AddMinutes(1));

        Assert.Equal(2, result.Removed);
        Assert.Single(store.ForWorkItem(wid));
    }

    [Fact]
    public async Task Reconcile_appends_scenarios_added_by_a_longer_plan()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var reconciler = new PlanTestCaseReconciler(store);

        await reconciler.ReconcileAsync(wid, PlanJson("Unit A"), Now);
        var result = await reconciler.ReconcileAsync(wid, PlanJson("Unit A", "Unit B"), Now.AddMinutes(1));

        Assert.Equal(1, result.Created);
        Assert.Equal(2, store.ForWorkItem(wid).Count());
    }

    [Fact]
    public async Task Reconcile_preserves_a_committed_replay_filled_in_by_authoring()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        var reconciler = new PlanTestCaseReconciler(store);

        await reconciler.ReconcileAsync(wid, PlanJson("e2e replay of checkout"), Now);
        var id = PlanTestCaseSynthesizer.DeriveId(wid, 0);

        // Simulate the separate authoring orchestration committing a replay.
        var authored = (await store.GetAsync(id))! with { ExecutableArtifactJson = "{\"steps\":[]}" };
        await store.UpdateAsync(authored);

        // Plan-rework rewrites the same ordinal's prose; the committed replay must survive.
        await reconciler.ReconcileAsync(wid, PlanJson("e2e replay of checkout and cart"), Now.AddMinutes(1));

        var reconciled = await store.GetAsync(id);
        Assert.NotNull(reconciled);
        Assert.Equal("{\"steps\":[]}", reconciled!.ExecutableArtifactJson);
        Assert.Contains("cart", reconciled.Description);
    }

    [Fact]
    public async Task Reconcile_leaves_manually_authored_cases_untouched()
    {
        var wid = WorkItemId.New();
        var store = new InMemoryTestCaseStore();
        // A hand-authored case with a random id (as the create API assigns).
        var manual = new TestCase
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Hand-written case",
            Description = "authored via API",
            SourceWorkItemId = wid.ToString(),
        };
        await store.CreateAsync(manual);

        await new PlanTestCaseReconciler(store).ReconcileAsync(wid, PlanJson("Unit A"), Now);

        Assert.Equal(2, store.ForWorkItem(wid).Count());
        Assert.NotNull(await store.GetAsync(manual.Id));
    }

    // ---- helpers ----------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static string PlanJson(params string[] testStrategy) => JsonSerializer.Serialize(new
    {
        approach = "do the thing",
        files = new[] { "src/Foo.cs" },
        testStrategy,
        risks = new[] { "none" },
        satisfiesTask = "it does",
    });
}

/// <summary>Minimal in-memory <see cref="ITestCaseStore"/> for reconcile tests.</summary>
internal sealed class InMemoryTestCaseStore : ITestCaseStore
{
    private readonly Dictionary<string, TestCase> _byId = new(StringComparer.Ordinal);

    public IEnumerable<TestCase> ForWorkItem(WorkItemId wid)
        => _byId.Values.Where(c => c.SourceWorkItemId == wid.ToString());

    public Task CreateAsync(TestCase testCase, CancellationToken ct = default)
    {
        if (!_byId.TryAdd(testCase.Id, testCase))
            throw new InvalidOperationException($"Duplicate test case id '{testCase.Id}'.");
        return Task.CompletedTask;
    }

    public async Task BulkCreateAsync(IReadOnlyList<TestCase> testCases, CancellationToken ct = default)
    {
        foreach (var tc in testCases)
            await CreateAsync(tc, ct);
    }

    public Task<bool> UpdateAsync(TestCase testCase, CancellationToken ct = default)
    {
        if (!_byId.ContainsKey(testCase.Id))
            return Task.FromResult(false);
        _byId[testCase.Id] = testCase;
        return Task.FromResult(true);
    }

    public Task<TestCase?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id, out var tc) ? tc : null);

    public async IAsyncEnumerable<TestCase> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var tc in _byId.Values.OrderBy(c => c.CreatedAt))
        {
            await Task.Yield();
            yield return tc;
        }
    }

    public async IAsyncEnumerable<TestCase> ListByWorkItemAsync(
        string workItemId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var tc in _byId.Values.Where(c => c.SourceWorkItemId == workItemId).OrderBy(c => c.CreatedAt))
        {
            await Task.Yield();
            yield return tc;
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_byId.Remove(id));
}
