using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Post-merge baseline production: the merge hook must schedule exactly one
/// job per project (a second merge before the job starts supersedes it —
/// newest commit wins, never two jobs); audit setup must stage the newest
/// ancestry-reachable fresh baseline and stage nothing when only stale
/// baselines exist; and a production failure must be reported without
/// blocking merges or audits (fail-safe to the full suite).
/// </summary>
public sealed class TestSelectionBaselineProductionTests
{
    private static readonly string CommitA = new string('a', 40);
    private static readonly string CommitB = new string('b', 40);
    private static readonly string CommitC = new string('c', 40);
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Scheduler: exactly one job, newest wins ────────────────────────────

    [Fact]
    public void Schedule_SecondMergeBeforeStart_SupersedesToExactlyOneJob()
    {
        var scheduler = new TestSelectionBaselineScheduler();
        scheduler.Schedule(new TestSelectionBaselineRequest("proj", "repo", "main", CommitA));
        scheduler.Schedule(new TestSelectionBaselineRequest("proj", "repo", "main", CommitB));

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(CommitB, scheduler.GetPendingCommit("proj"));

        Assert.True(scheduler.TryTake(out var taken));
        Assert.NotNull(taken);
        Assert.Equal(CommitB, taken.Commit);
        Assert.False(scheduler.TryTake(out _));
        scheduler.Complete("proj");
    }

    [Fact]
    public void Scheduler_RunningProject_BlocksSecondTake_UntilComplete()
    {
        var scheduler = new TestSelectionBaselineScheduler();
        scheduler.Schedule(new TestSelectionBaselineRequest("proj", "repo", "main", CommitA));
        Assert.True(scheduler.TryTake(out _));
        Assert.True(scheduler.IsRunning("proj"));

        // A merge landing while the job runs waits as the single pending entry.
        scheduler.Schedule(new TestSelectionBaselineRequest("proj", "repo", "main", CommitB));
        Assert.False(scheduler.TryTake(out _));

        scheduler.Complete("proj");
        Assert.False(scheduler.IsRunning("proj"));
        Assert.True(scheduler.TryTake(out var next));
        Assert.Equal(CommitB, next!.Commit);
        scheduler.Complete("proj");
    }

    [Fact]
    public void Scheduler_PerProjectIsolation_OtherProjectsProceed()
    {
        var scheduler = new TestSelectionBaselineScheduler();
        scheduler.Schedule(new TestSelectionBaselineRequest("a", "repo", "main", CommitA));
        scheduler.Schedule(new TestSelectionBaselineRequest("b", "repo", "main", CommitB));

        Assert.True(scheduler.TryTake(out var first));
        Assert.True(scheduler.TryTake(out var second));
        Assert.NotEqual(first!.ProjectId, second!.ProjectId);
        scheduler.Complete("a");
        scheduler.Complete("b");
    }

    // ── Scheduling gate ────────────────────────────────────────────────────

    [Fact]
    public void ShouldSchedule_RequiresOptIn_EnabledFlag_AndHexSha()
    {
        Assert.True(PipelineRunner.ShouldScheduleBaselineProduction(true, true, CommitA));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(false, true, CommitA));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(true, false, CommitA));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(true, true, null));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(true, true, ""));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(true, true, "main"));
        Assert.False(PipelineRunner.ShouldScheduleBaselineProduction(true, true, "not-a-sha!!!!!!!!!!!!"));
    }

    // ── Store: keyed retention, failures never evict ────────────────────────

    [Fact]
    public async Task Store_BoundedRetention_KeepsNewestPerProject()
    {
        var store = new TestSelectionBaselineStore();
        await store.StoreAsync("proj", CommitA, T0, "{}", 1, TimeSpan.FromSeconds(1), 2);
        await store.StoreAsync("proj", CommitB, T0.AddHours(1), "{}", 2, TimeSpan.FromSeconds(1), 2);
        await store.StoreAsync("proj", CommitC, T0.AddHours(2), "{}", 3, TimeSpan.FromSeconds(1), 2);

        var listed = await store.ListAsync("proj");
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(listed, s => s.Commit == CommitA);
        Assert.NotNull(await store.GetAsync("proj", CommitC));
        Assert.Null(await store.GetAsync("proj", CommitA));

        // Other projects are unaffected by this project's eviction.
        await store.StoreAsync("other", CommitA, T0, "{}", 1, TimeSpan.FromSeconds(1), 2);
        Assert.NotNull(await store.GetAsync("other", CommitA));
    }

    [Fact]
    public async Task Store_SameCommit_ReplacesInPlace_WithoutGrowingRetention()
    {
        var store = new TestSelectionBaselineStore();
        await store.StoreAsync("proj", CommitA, T0, "{}", 1, TimeSpan.FromSeconds(1), 1);
        await store.StoreAsync("proj", CommitA, T0.AddHours(1), "{\"v\":2}", 5, TimeSpan.FromSeconds(2), 1);

        var listed = await store.ListAsync("proj");
        var single = Assert.Single(listed);
        Assert.Equal(5, single.TestCount);
        Assert.Equal(T0.AddHours(1), single.ProducedAtUtc);
    }

    // ── Staging selection: newest ancestor, stale stages nothing ────────────

    private static TestSelectionBaselineStored Baseline(string commit, DateTimeOffset produced)
        => new("proj", commit, produced, "{}", 2, 10);

    [Fact]
    public async Task SelectNewestAncestor_PicksNewestReachableFreshBaseline()
    {
        var candidates = new[]
        {
            Baseline(CommitA, T0),
            Baseline(CommitB, T0.AddHours(1)),
            Baseline(CommitC, T0.AddHours(2)),
        };
        // Only A and C are ancestors of the base; C is newer and fresh.
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { CommitA, CommitC };
        Task<bool> IsAncestor(string a, string b, CancellationToken ct)
            => Task.FromResult(ancestors.Contains(a));

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates, CommitB, IsAncestor, T0.AddHours(3), TimeSpan.FromDays(7));

        Assert.NotNull(selected);
        Assert.Equal(CommitC, selected.Commit);
    }

    [Fact]
    public async Task SelectNewestAncestor_SkipsNonAncestor_EvenWhenNewest()
    {
        var candidates = new[]
        {
            Baseline(CommitA, T0),
            Baseline(CommitB, T0.AddHours(5)),
        };
        Task<bool> IsAncestor(string a, string b, CancellationToken ct)
            => Task.FromResult(string.Equals(a, CommitA, StringComparison.Ordinal));

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates, CommitC, IsAncestor, T0.AddHours(6), TimeSpan.FromDays(7));

        Assert.NotNull(selected);
        Assert.Equal(CommitA, selected.Commit);
    }

    [Fact]
    public async Task SelectNewestAncestor_StaleBaseline_IsNotStaged()
    {
        var candidates = new[] { Baseline(CommitA, T0) };
        static Task<bool> IsAncestor(string a, string b, CancellationToken ct) => Task.FromResult(true);

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates, CommitB, IsAncestor, T0.AddDays(8), TimeSpan.FromDays(7));

        Assert.Null(selected);
    }

    [Fact]
    public async Task SelectNewestAncestor_NoAncestor_StagesNothing()
    {
        var candidates = new[] { Baseline(CommitA, T0) };
        static Task<bool> IsAncestor(string a, string b, CancellationToken ct) => Task.FromResult(false);

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates, CommitB, IsAncestor, T0.AddHours(1), TimeSpan.FromDays(7));

        Assert.Null(selected);
    }

    [Fact]
    public async Task SelectNewestAncestor_AncestryProbeThrow_SkipsCandidate()
    {
        var candidates = new[]
        {
            Baseline(CommitA, T0),
            Baseline(CommitB, T0.AddHours(1)),
        };
        Task<bool> IsAncestor(string a, string b, CancellationToken ct)
            => string.Equals(a, CommitB, StringComparison.Ordinal)
                ? throw new InvalidOperationException("unknown commit")
                : Task.FromResult(true);

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates, CommitC, IsAncestor, T0.AddHours(2), TimeSpan.FromDays(7));

        Assert.NotNull(selected);
        Assert.Equal(CommitA, selected.Commit);
    }

    // ── Stager: sandbox write + fail-safe skips ─────────────────────────────

    [Fact]
    public async Task Stager_StagesAncestorBaseline_ReadOnly_AtConfiguredPath()
    {
        var store = new TestSelectionBaselineStore();
        await store.StoreAsync("proj", CommitA, T0, "{\"marker\":1}", 7, TimeSpan.FromSeconds(1), 5);
        var git = new AncestryFakeGitHost(new Dictionary<string, string> { ["main"] = CommitB });
        git.Ancestors[(CommitA, CommitB)] = true;
        var sandbox = new RecordingSandbox();
        var stager = new TestSelectionBaselineAuditStager(
            store,
            git,
            () => new CoverageTestSelectionOptions
            {
                BaselineSandboxPath = "/opt/codeybox/test-selection/baseline.json",
                MaxBaselineAge = TimeSpan.FromDays(7),
            },
            NullLogger<TestSelectionBaselineAuditStager>.Instance,
            new FixedClock(T0.AddHours(1)));

        var staged = await stager.StageAsync(sandbox, "proj", "repo", "main");

        Assert.Equal(CommitA, staged);
        Assert.Contains(sandbox.Execs, e => e.Argv is ["mkdir", "-p", "--", "/opt/codeybox/test-selection"]);
        var write = Assert.Single(sandbox.Execs, e => e.Argv is ["tee", "--", "/opt/codeybox/test-selection/baseline.json"]);
        Assert.Equal("{\"marker\":1}", write.Stdin);
        Assert.Contains(sandbox.Execs, e => e.Argv is ["chmod", "444", "--", "/opt/codeybox/test-selection/baseline.json"]);
    }

    [Fact]
    public async Task Stager_StaleBaseline_WritesNothing()
    {
        var store = new TestSelectionBaselineStore();
        await store.StoreAsync("proj", CommitA, T0, "{}", 7, TimeSpan.FromSeconds(1), 5);
        var git = new AncestryFakeGitHost(new Dictionary<string, string> { ["main"] = CommitB });
        git.Ancestors[(CommitA, CommitB)] = true;
        var sandbox = new RecordingSandbox();
        var stager = new TestSelectionBaselineAuditStager(
            store,
            git,
            () => new CoverageTestSelectionOptions { MaxBaselineAge = TimeSpan.FromDays(7) },
            NullLogger<TestSelectionBaselineAuditStager>.Instance,
            new FixedClock(T0.AddDays(30)));

        var staged = await stager.StageAsync(sandbox, "proj", "repo", "main");

        Assert.Null(staged);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "tee");
    }

    [Fact]
    public async Task Stager_UnresolvableBase_StagesNothing_AndDoesNotThrow()
    {
        var store = new TestSelectionBaselineStore();
        await store.StoreAsync("proj", CommitA, T0, "{}", 7, TimeSpan.FromSeconds(1), 5);
        var git = new AncestryFakeGitHost(new Dictionary<string, string>());
        var sandbox = new RecordingSandbox();
        var stager = new TestSelectionBaselineAuditStager(
            store,
            git,
            () => new CoverageTestSelectionOptions(),
            NullLogger<TestSelectionBaselineAuditStager>.Instance,
            new FixedClock(T0.AddHours(1)));

        var staged = await stager.StageAsync(sandbox, "proj", "repo", "missing-branch");

        Assert.Null(staged);
        Assert.Empty(sandbox.Execs);
    }

    // ── Service: success stores, failure reports without blocking ───────────

    private static Project OptedInProject(string id = "proj") => new()
    {
        Id = new ProjectId(id),
        DisplayName = id,
        RepositoryUrl = "https://example.com/repo.git",
        TestSelectionBaselineEnabled = true,
    };

    [Fact]
    public async Task RunOne_Success_StoresBaseline_PublishesEvent_ClearsRunning()
    {
        var store = new TestSelectionBaselineStore();
        var scheduler = new TestSelectionBaselineScheduler();
        var request = new TestSelectionBaselineRequest("proj", "repo", "main", CommitA);
        scheduler.Schedule(request);
        Assert.True(scheduler.TryTake(out _));
        var runner = new ScriptedRunner(_ => Task.FromResult(
            new TestSelectionBaselineRunResult(CommitB, T0, "{\"ok\":true}", 3)));
        var webhooks = new RecordingDispatcher();
        var service = new TestSelectionBaselineProductionService(
            scheduler,
            store,
            runner,
            new StaticProjectRepository(OptedInProject()),
            new OpenProvider(),
            webhooks,
            () => new TestSelectionBaselineProductionOptions(),
            NullLogger<TestSelectionBaselineProductionService>.Instance);

        await service.RunOneAsync(request, new TestSelectionBaselineProductionOptions());

        var stored = await store.GetAsync("proj", CommitB);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.TestCount);
        Assert.False(scheduler.IsRunning("proj"));
        var produced = Assert.Single(webhooks.Events, e => e.Event == "test-selection.baseline_produced");
        var details = Assert.IsType<TestSelectionBaselineProducedDetails>(produced.Details);
        Assert.True(details.Success);
        Assert.Equal("proj", details.ProjectId);
    }

    [Fact]
    public async Task RunOne_Failure_RecordsError_PublishesEvent_DoesNotThrow_OrBlock()
    {
        var store = new TestSelectionBaselineStore();
        var scheduler = new TestSelectionBaselineScheduler();
        var request = new TestSelectionBaselineRequest("proj", "repo", "main", CommitA);
        scheduler.Schedule(request);
        Assert.True(scheduler.TryTake(out _));
        var runner = new ScriptedRunner(_ => throw new TestSelectionBaselineRunException("producer blew up"));
        var webhooks = new RecordingDispatcher();
        var service = new TestSelectionBaselineProductionService(
            scheduler,
            store,
            runner,
            new StaticProjectRepository(OptedInProject()),
            new OpenProvider(),
            webhooks,
            () => new TestSelectionBaselineProductionOptions(),
            NullLogger<TestSelectionBaselineProductionService>.Instance);

        await service.RunOneAsync(request, new TestSelectionBaselineProductionOptions());

        // Nothing stored, but the failure is reported — and the slot is free
        // for the next merge (merges and audits are never blocked).
        Assert.Empty(await store.ListAsync("proj"));
        var failure = await store.GetLastFailureAsync("proj");
        Assert.NotNull(failure);
        Assert.Contains("producer blew up", failure.Error);
        var failed = Assert.Single(webhooks.Events, e => e.Event == "test-selection.baseline_failed");
        var details = Assert.IsType<TestSelectionBaselineProducedDetails>(failed.Details);
        Assert.False(details.Success);
        Assert.NotNull(details.Error);
        Assert.False(scheduler.IsRunning("proj"));
    }

    [Fact]
    public async Task RunOne_NotOptedIn_DropsJob_WithoutRunning()
    {
        var store = new TestSelectionBaselineStore();
        var scheduler = new TestSelectionBaselineScheduler();
        var request = new TestSelectionBaselineRequest("proj", "repo", "main", CommitA);
        var runner = new ScriptedRunner(_ =>
        {
            Assert.Fail("Runner must not execute for a non-opted-in project.");
            return Task.FromResult(new TestSelectionBaselineRunResult(CommitA, T0, "{}", 0));
        });
        var project = OptedInProject() with { TestSelectionBaselineEnabled = false };
        var service = new TestSelectionBaselineProductionService(
            scheduler,
            store,
            runner,
            new StaticProjectRepository(project),
            new OpenProvider(),
            new RecordingDispatcher(),
            () => new TestSelectionBaselineProductionOptions(),
            NullLogger<TestSelectionBaselineProductionService>.Instance);

        await service.RunOneAsync(request, new TestSelectionBaselineProductionOptions());

        Assert.Empty(await store.ListAsync("proj"));
    }

    [Fact]
    public async Task ExecuteAsync_SaturatedPool_ParksJob_WithoutRunning()
    {
        var scheduler = new TestSelectionBaselineScheduler();
        scheduler.Schedule(new TestSelectionBaselineRequest("proj", "repo", "main", CommitA));
        var calls = 0;
        var runner = new ScriptedRunner(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new TestSelectionBaselineRunResult(CommitA, T0, "{}", 0));
        });
        var service = new TestSelectionBaselineProductionService(
            scheduler,
            new TestSelectionBaselineStore(),
            runner,
            new StaticProjectRepository(OptedInProject()),
            new SaturatedProvider(),
            new RecordingDispatcher(),
            () => new TestSelectionBaselineProductionOptions
            {
                SaturatedPoolRecheckDelay = TimeSpan.FromMinutes(5),
            },
            NullLogger<TestSelectionBaselineProductionService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Equal(CommitA, scheduler.GetPendingCommit("proj"));
    }

    // ── Options validation ─────────────────────────────────────────────────

    [Fact]
    public void ProductionOptions_Defaults_AreValid()
    {
        Assert.True(TestSelectionBaselineProductionOptions.IsValid(new TestSelectionBaselineProductionOptions()));
        Assert.False(TestSelectionBaselineProductionOptions.IsValid(null));
        Assert.False(TestSelectionBaselineProductionOptions.IsValid(
            new TestSelectionBaselineProductionOptions { Timeout = TimeSpan.Zero }));
        Assert.False(TestSelectionBaselineProductionOptions.IsValid(
            new TestSelectionBaselineProductionOptions { MaxRetainedPerProject = 0 }));
        Assert.False(TestSelectionBaselineProductionOptions.IsValid(
            new TestSelectionBaselineProductionOptions { ProducerBinary = "  " }));
        Assert.False(TestSelectionBaselineProductionOptions.IsValid(
            new TestSelectionBaselineProductionOptions { SaturatedPoolRecheckDelay = TimeSpan.Zero }));
    }

    [Fact]
    public void StagingIO_RejectsUnsafePaths()
    {
        Assert.True(TestSelectionBaselineStagingIO.IsStageableSandboxPath("/opt/codeybox/test-selection/baseline.json"));
        Assert.False(TestSelectionBaselineStagingIO.IsStageableSandboxPath(""));
        Assert.False(TestSelectionBaselineStagingIO.IsStageableSandboxPath("relative/path.json"));
        Assert.False(TestSelectionBaselineStagingIO.IsStageableSandboxPath("/opt/../etc/passwd"));
        Assert.False(TestSelectionBaselineStagingIO.IsStageableSandboxPath("/opt/x\0y"));
        Assert.Equal("/opt/codeybox/test-selection",
            TestSelectionBaselineStagingIO.ParentDirectory("/opt/codeybox/test-selection/baseline.json"));
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public List<SandboxExec> Execs { get; } = [];
        public string Id => "test-sandbox";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AncestryFakeGitHost(Dictionary<string, string> tips) : IGitHost
    {
        public Dictionary<(string Ancestor, string Descendant), bool> Ancestors { get; } = new();

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult("repo");

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => Task.FromResult("repo");

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
            => new("/repo", [], new SandboxNetworkPolicy());

        public Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
            => tips.TryGetValue(commitish, out var tip)
                ? Task.FromResult(tip)
                : throw new InvalidOperationException($"unknown ref {commitish}");

        public Task<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken ct = default)
            => Task.FromResult(Ancestors.TryGetValue((ancestorCommit, descendantCommit), out var ancestor) && ancestor);

        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => Task.FromResult(("", ""));
    }

    private sealed class StaticProjectRepository(Project? project) : IProjectRepository
    {
        private readonly Project? _project = project;
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) => Task.FromResult(_project);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>(_project is null ? [] : [_project]);
    }

    private sealed class ScriptedRunner(Func<TestSelectionBaselineRequest, Task<TestSelectionBaselineRunResult>> run)
        : ITestSelectionBaselineJobRunner
    {
        private readonly Func<TestSelectionBaselineRequest, Task<TestSelectionBaselineRunResult>> _run = run;
        public Task<TestSelectionBaselineRunResult> RunAsync(
            TestSelectionBaselineRequest request, TimeSpan timeout, CancellationToken ct = default)
            => _run(request);
    }

    private sealed class RecordingDispatcher : IWebhookDispatcher
    {
        public List<WebhookEvent> Events { get; } = [];
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class OpenProvider : ISandboxProvider, ISandboxAdmissionSnapshot
    {
        public string Name => "test";
        public int CurrentAdmittedSandboxes => 0;
        public int MaxConcurrentSandboxes => 4;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SaturatedProvider : ISandboxProvider, ISandboxAdmissionSnapshot
    {
        public string Name => "test";
        public int CurrentAdmittedSandboxes => 4;
        public int MaxConcurrentSandboxes => 4;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    // ── HTTP status endpoint ───────────────────────────────────────────────

    [Collection("GlobalSerilog")]
    public sealed class BaselineStatusEndpointTests
        : IClassFixture<BaselineStatusEndpointTests.BaselineStatusApiFactory>
    {
        private readonly BaselineStatusApiFactory _factory;

        public BaselineStatusEndpointTests(BaselineStatusApiFactory factory) => _factory = factory;

        [Fact]
        public async Task GetBaseline_WithStoredBaseline_ReturnsStatusShape()
        {
            var store = _factory.Services.GetRequiredService<ITestSelectionBaselineStore>();
            await store.StoreAsync("demo", new string('d', 40), DateTimeOffset.UtcNow.AddHours(-2), "{\"m\":1}", 11, TimeSpan.FromSeconds(3), 5);

            var client = _factory.CreateClient();
            var resp = await client.GetAsync("/audit/test-selection/baseline?projectId=demo");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("demo", body.GetProperty("projectId").GetString());
            Assert.Equal(new string('d', 40), body.GetProperty("latestCommit").GetString());
            Assert.Equal(11, body.GetProperty("testCount").GetInt32());
            Assert.Equal(1, body.GetProperty("retainedCount").GetInt32());
            var age = body.GetProperty("ageHours").GetDouble();
            Assert.InRange(age, 1.9, 2.1);
            Assert.False(body.GetProperty("isRunning").GetBoolean());
        }

        [Fact]
        public async Task GetBaseline_UnknownProject_Returns404()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync("/audit/test-selection/baseline?projectId=nope");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task GetBaseline_AllProjects_ReturnsArray()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync("/audit/test-selection/baseline");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Array, body.ValueKind);
            Assert.Contains(body.EnumerateArray(), e => e.GetProperty("projectId").GetString() == "demo");
        }

        public sealed class BaselineStatusApiFactory : WebApplicationFactory<Program>
        {
            private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-baseline-status-");
            private string DbPath => _scratch.DbPath("baseline-status.db");

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CodeyBox:DangerouslyDisableAuth"] = "true",
                        ["CodeyBox:StateDatabasePath"] = DbPath,
                        ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                        ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                        ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                        ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
                        ["CodeyBox:Projects:0:Id"] = "demo",
                        ["CodeyBox:Projects:0:DisplayName"] = "demo",
                        ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/demo.git",
                        ["CodeyBox:Projects:0:TestSelectionBaselineEnabled"] = "true",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _scratch.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
