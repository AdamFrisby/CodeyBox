using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// The post-implementation e2e-replay gate: pure classification, the
/// orchestration gate over its three seams, the run-store-backed verifier, and
/// the cheap-model authoring driver's honest fail-closed behaviour.
/// </summary>
public sealed class E2eReplayGateTests
{
    private static readonly WorkItemId TheWid = new(Guid.Parse("01931f0a-0000-7000-8000-000000000001"));

    // A test case's SourceWorkItemId is the normalised WorkItemId string ("N"
    // format), exactly as PipelineRunner writes it and the gate filters on it.
    private static string Wid => TheWid.ToString();

    private static TestCase Case(
        string id,
        AutomationKind? kind = AutomationKind.E2eReplay,
        bool archived = false,
        string? artifact = null,
        bool? lastRunPassed = null)
        => new()
        {
            Id = id,
            Name = $"case-{id}",
            Description = "d",
            SourceWorkItemId = Wid,
            AutomationKind = kind,
            IsArchived = archived,
            ExecutableArtifactJson = artifact,
            LastRunPassed = lastRunPassed,
        };

    // ---- Pure policy ---------------------------------------------------

    [Fact]
    public void Classify_non_e2e_kind_is_not_applicable()
        => Assert.Equal(E2eReplayCaseNeed.NotApplicable,
            E2eReplayGatePolicy.Classify(Case("a", kind: AutomationKind.Unit, artifact: "{}", lastRunPassed: true)));

    [Fact]
    public void Classify_archived_e2e_is_not_applicable()
        => Assert.Equal(E2eReplayCaseNeed.NotApplicable,
            E2eReplayGatePolicy.Classify(Case("a", archived: true, artifact: "{}", lastRunPassed: true)));

    [Fact]
    public void Classify_declared_without_artifact_needs_authoring()
        => Assert.Equal(E2eReplayCaseNeed.NeedsAuthoring,
            E2eReplayGatePolicy.Classify(Case("a", artifact: null)));

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Classify_declared_with_blank_artifact_needs_authoring(string artifact)
        => Assert.Equal(E2eReplayCaseNeed.NeedsAuthoring,
            E2eReplayGatePolicy.Classify(Case("a", artifact: artifact)));

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Classify_declared_with_unverified_artifact_needs_verification(bool? lastRun)
        => Assert.Equal(E2eReplayCaseNeed.NeedsVerification,
            E2eReplayGatePolicy.Classify(Case("a", artifact: "{}", lastRunPassed: lastRun)));

    [Fact]
    public void Classify_declared_with_green_artifact_is_satisfied()
        => Assert.Equal(E2eReplayCaseNeed.Satisfied,
            E2eReplayGatePolicy.Classify(Case("a", artifact: "{}", lastRunPassed: true)));

    // ---- Gate orchestration -------------------------------------------

    private static WorkItemE2eReplayGate BuildGate(
        InMemoryTestCaseStore store,
        FakeVerifier verifier,
        E2eReplayAuthoringOptions? opts = null,
        FakeAuthoringDriver? driver = null)
        => new(
            store,
            verifier,
            new MutableMonitor<E2eReplayAuthoringOptions>(opts ?? new E2eReplayAuthoringOptions { Enabled = true }),
            driver,
            timeProvider: new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Disabled_gate_is_a_no_op()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: null));
        var verifier = new FakeVerifier(_ => VerifyPass());
        var gate = BuildGate(store, verifier, new E2eReplayAuthoringOptions { Enabled = false });

        var result = await gate.EvaluateAsync(TheWid);

        Assert.False(result.Enabled);
        Assert.False(result.Blocked);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Satisfied_case_is_verified_without_re_running()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: "{}", lastRunPassed: true));
        var verifier = new FakeVerifier(_ => VerifyPass());
        var gate = BuildGate(store, verifier);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.True(result.Enabled);
        Assert.False(result.Blocked);
        Assert.Equal(new[] { "a" }, result.VerifiedCaseIds);
        Assert.Equal(0, verifier.CallCount); // a green committed replay is never re-run
    }

    [Fact]
    public async Task Needs_authoring_without_driver_blocks()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: null));
        var verifier = new FakeVerifier(_ => VerifyPass());
        var gate = BuildGate(store, verifier, driver: null);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.True(result.Blocked);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("a", blocker.TestCaseId);
        Assert.Contains("driver", blocker.Reason);
        Assert.Equal(0, verifier.CallCount); // never verify a case we could not author
    }

    [Fact]
    public async Task Authors_missing_replay_then_verifies_green_and_persists()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: null));
        var driver = new FakeAuthoringDriver(_ => E2eReplayAuthoringOutcome.Success("{\"steps\":[]}", "claude-haiku-4-5-20251001"));
        var verifier = new FakeVerifier(_ => VerifyPass());
        var gate = BuildGate(store, verifier, driver: driver);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.False(result.Blocked);
        Assert.Equal(new[] { "a" }, result.VerifiedCaseIds);
        Assert.Equal(1, driver.CallCount);
        // The authored artifact was attached and the stale run outcome cleared so
        // classification treats it as freshly-authored (unverified).
        var persisted = await store.GetAsync("a");
        Assert.Equal("{\"steps\":[]}", persisted!.ExecutableArtifactJson);
        Assert.Null(persisted.LastRunPassed);
        Assert.Null(persisted.LastRunAt);
    }

    [Fact]
    public async Task Driver_that_cannot_author_blocks_with_its_reason()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: null));
        var driver = new FakeAuthoringDriver(_ => E2eReplayAuthoringOutcome.Unresolved("no recipe configured"));
        var verifier = new FakeVerifier(_ => VerifyPass());
        var gate = BuildGate(store, verifier, driver: driver);

        var result = await gate.EvaluateAsync(TheWid);

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("no recipe configured", blocker.Reason);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Broken_replay_is_reauthored_within_cap_then_passes()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: "{\"old\":true}", lastRunPassed: false));
        var driver = new FakeAuthoringDriver(_ => E2eReplayAuthoringOutcome.Success("{\"fresh\":true}", "claude-haiku-4-5-20251001"));
        // First verify (of the committed-but-broken replay) fails, second (of the
        // re-authored replay) passes.
        var verdicts = new Queue<E2eReplayVerificationOutcome>([VerifyFail(), VerifyPass()]);
        var verifier = new FakeVerifier(_ => verdicts.Dequeue());
        var gate = BuildGate(store, verifier, new E2eReplayAuthoringOptions { Enabled = true, MaxReauthorAttempts = 1 }, driver);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.False(result.Blocked);
        Assert.Equal(new[] { "a" }, result.VerifiedCaseIds);
        Assert.Equal(1, driver.CallCount); // exactly one re-author
        Assert.Equal(2, verifier.CallCount);
    }

    [Fact]
    public async Task Broken_replay_with_zero_reauthor_budget_blocks_on_first_red()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: "{\"old\":true}", lastRunPassed: false));
        var driver = new FakeAuthoringDriver(_ => E2eReplayAuthoringOutcome.Success("{\"fresh\":true}", "m"));
        var verifier = new FakeVerifier(_ => VerifyFail());
        var gate = BuildGate(store, verifier, new E2eReplayAuthoringOptions { Enabled = true, MaxReauthorAttempts = 0 }, driver);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.True(result.Blocked);
        Assert.Equal(0, driver.CallCount); // no budget: never re-author
        Assert.Contains("verification failed", Assert.Single(result.Blockers).Reason);
    }

    [Fact]
    public async Task Reauthor_attempts_are_clamped_to_the_allowed_ceiling()
    {
        // A config typo above the ceiling cannot loop the cheap-model author
        // unboundedly — the gate clamps to MaxAllowedReauthorAttempts.
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("a", artifact: "{\"old\":true}", lastRunPassed: false));
        var driver = new FakeAuthoringDriver(_ => E2eReplayAuthoringOutcome.Success("{\"x\":1}", "m"));
        var verifier = new FakeVerifier(_ => VerifyFail()); // always red
        var gate = BuildGate(store, verifier,
            new E2eReplayAuthoringOptions { Enabled = true, MaxReauthorAttempts = 9999 }, driver);

        var result = await gate.EvaluateAsync(TheWid);

        Assert.True(result.Blocked);
        // 1 initial verify + MaxAllowedReauthorAttempts re-authors (each followed
        // by a verify) = 1 + ceiling verifies; driver called exactly ceiling times.
        Assert.Equal(E2eReplayAuthoringOptions.MaxAllowedReauthorAttempts, driver.CallCount);
    }

    // ---- Verifier (real run-store polling) ----------------------------

    [Fact]
    public async Task Verifier_reports_passed_when_run_reaches_passed()
    {
        var store = new ControllableRunStore();
        var verifier = new E2eRunReplayVerifier(
            store,
            new MutableMonitor<E2eReplayAuthoringOptions>(new E2eReplayAuthoringOptions()),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch),
            runIdFactory: () => "run-1");
        // The run is already terminal-passed by the time the verifier first polls.
        store.OnFirstGet = () => store.SetStatus("run-1", E2eRunStatus.Passed,
            JsonSerializer.Serialize(new E2eRunResult { Passed = true, Summary = "ok" }));

        var outcome = await verifier.VerifyAsync("tc-1");

        Assert.True(outcome.Passed);
        Assert.Equal(E2eRunStatus.Passed, outcome.Status);
        Assert.Equal("run-1", store.CreatedRunId);
        Assert.Equal("tc-1", store.CreatedTestCaseId);
    }

    [Fact]
    public async Task Verifier_reports_failure_detail_from_result_json()
    {
        var store = new ControllableRunStore();
        var verifier = new E2eRunReplayVerifier(
            store,
            new MutableMonitor<E2eReplayAuthoringOptions>(new E2eReplayAuthoringOptions()),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch),
            runIdFactory: () => "run-1");
        store.OnFirstGet = () => store.SetStatus("run-1", E2eRunStatus.Failed,
            JsonSerializer.Serialize(new E2eRunResult { Passed = false, Summary = "step 3 selector missing", FailureKind = "assertion" }));

        var outcome = await verifier.VerifyAsync("tc-1");

        Assert.False(outcome.Passed);
        Assert.Equal(E2eRunStatus.Failed, outcome.Status);
        Assert.Contains("assertion", outcome.Detail);
        Assert.Contains("selector missing", outcome.Detail);
    }

    [Fact]
    public async Task Verifier_times_out_cancels_run_and_reports_red()
    {
        // The run never becomes terminal, so the outcome is deterministic: the
        // verifier must time out, cancel the still-queued run (releasing the pool
        // lease), and report red. A small real timeout keeps it fast and drives
        // the real Task.Delay polling loop over the system clock.
        var store = new ControllableRunStore(); // run stays Queued forever
        var opts = new E2eReplayAuthoringOptions
        {
            VerificationTimeout = TimeSpan.FromMilliseconds(100),
            VerificationPollInterval = TimeSpan.FromMilliseconds(10),
        };
        var verifier = new E2eRunReplayVerifier(
            store, new MutableMonitor<E2eReplayAuthoringOptions>(opts), timeProvider: null, runIdFactory: () => "run-1");

        var outcome = await verifier.VerifyAsync("tc-1").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Passed);
        Assert.Contains("timed out", outcome.Detail);
        Assert.True(store.CancelCalled); // the leaked pool lease is released
    }

    // ---- Authoring driver (honest fail-closed) ------------------------

    [Fact]
    public async Task Driver_reports_unresolved_when_no_target_configured()
    {
        var driver = new CheapModelCuaAuthoringDriver(
            new ThrowingHarness(),
            new FakeTargetResolver(_ => null)); // no recipe/plan for this case

        var outcome = await driver.AuthorAsync(Case("a", artifact: null), new E2eReplayAuthoringRequest());

        Assert.False(outcome.Authored);
        Assert.Null(outcome.ArtifactJson);
        Assert.Contains("no app-launch recipe", outcome.UnresolvedReason);
    }

    [Fact]
    public async Task Driver_reports_unresolved_when_harness_fails_never_fakes_success()
    {
        var target = new E2eAuthoringTarget(
            new WebAppRecipe
            {
                TargetName = "demo",
                RunCommand = new RecipeStep { Command = ["run"] },
                EntryUrl = "http://app.local/",
                BrowserCommand = ["browser"],
            },
            new E2eExplorationPlan { TargetName = "demo", Assertions = [] });
        var driver = new CheapModelCuaAuthoringDriver(
            new ThrowingHarness(), // launch blows up (sandbox provisioning failure)
            new FakeTargetResolver(_ => target));

        var outcome = await driver.AuthorAsync(Case("a", artifact: null), new E2eReplayAuthoringRequest());

        Assert.False(outcome.Authored);
        Assert.Contains("authoring failed", outcome.UnresolvedReason);
    }

    // ---- Fakes ---------------------------------------------------------

    private static E2eReplayVerificationOutcome VerifyPass()
        => new(true, E2eRunStatus.Passed, "passed");

    private static E2eReplayVerificationOutcome VerifyFail()
        => new(false, E2eRunStatus.Failed, "red");

    private sealed class FakeVerifier(Func<string, E2eReplayVerificationOutcome> verdict) : IE2eReplayVerifier
    {
        public int CallCount { get; private set; }

        public Task<E2eReplayVerificationOutcome> VerifyAsync(string testCaseId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(verdict(testCaseId));
        }
    }

    private sealed class FakeAuthoringDriver(Func<TestCase, E2eReplayAuthoringOutcome> author) : IE2eReplayAuthoringDriver
    {
        public int CallCount { get; private set; }

        public Task<E2eReplayAuthoringOutcome> AuthorAsync(TestCase testCase, E2eReplayAuthoringRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(author(testCase));
        }
    }

    private sealed class FakeTargetResolver(Func<TestCase, E2eAuthoringTarget?> resolve) : IE2eAuthoringTargetResolver
    {
        public Task<E2eAuthoringTarget?> ResolveAsync(TestCase testCase, CancellationToken ct = default)
            => Task.FromResult(resolve(testCase));
    }

    private sealed class ThrowingHarness : IAppUnderTestHarness
    {
        public Task<AppUnderTestSession> LaunchAsync(WebAppRecipe recipe, CancellationToken ct = default)
            => throw new InvalidOperationException("sandbox provisioning failed");
    }

    private sealed class MutableMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IE2eRunStore"/> the verifier drives: it only
    /// calls Create / Get / Cancel. A one-shot <see cref="OnFirstGet"/> hook lets
    /// a test flip the run terminal exactly when the verifier first polls.
    /// </summary>
    private sealed class ControllableRunStore : IE2eRunStore
    {
        private readonly Dictionary<string, E2eRun> _runs = new(StringComparer.Ordinal);
        public string? CreatedRunId { get; private set; }
        public string? CreatedTestCaseId { get; private set; }
        public bool CancelCalled { get; private set; }
        public Action? OnFirstGet { get; set; }
        private bool _firstGetSeen;

        public void SetStatus(string id, E2eRunStatus status, string? result)
            => _runs[id] = _runs[id] with { Status = status, Result = result };

        public Task CreateAsync(E2eRun run, CancellationToken ct = default)
        {
            CreatedRunId = run.Id;
            CreatedTestCaseId = run.TestCaseId;
            _runs[run.Id] = run;
            return Task.CompletedTask;
        }

        public Task<E2eRun?> GetAsync(string id, CancellationToken ct = default)
        {
            if (!_firstGetSeen)
            {
                _firstGetSeen = true;
                OnFirstGet?.Invoke();
            }
            return Task.FromResult(_runs.TryGetValue(id, out var r) ? r : null);
        }

        public Task<bool> CancelAsync(string id, CancellationToken ct = default)
        {
            CancelCalled = true;
            if (_runs.TryGetValue(id, out var r))
                _runs[id] = r with { Status = E2eRunStatus.Canceled };
            return Task.FromResult(true);
        }

        // Unused by the verifier.
        public Task BulkCreateAsync(IReadOnlyList<E2eRun> runs, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<E2eRun> ListAsync(int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<E2eRun> ListByTestCaseAsync(string testCaseId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<E2eRun> ListByBatchAsync(string batchId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<E2eRunBatchCounts?> GetBatchCountsAsync(string batchId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> HasQueuedAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<E2eRun?> ClaimNextQueuedAsync(string? sandboxId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> AssignSandboxAsync(string id, string sandboxId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<int> RequeueRunningAsync(DateTimeOffset startedBefore, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> UpdateStatusAsync(string id, E2eRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string? result, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
