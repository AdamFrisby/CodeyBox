using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Deployment verification chain (3/3): the operator as a reviewer through
/// the standard auditor seam. A human-kind deployment auditor parks the
/// audit iteration (no worker slot held, watchdog quiet, only the
/// deployment kept alive), notifies the operator with endpoint + expiry +
/// acceptance criteria, and on verdict resumes with approve passing,
/// reject-with-notes blocking into rework, or expiry failing closed —
/// tearing the deployment down immediately in every case.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class HumanDeploymentReviewTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestSupport.AmbientGitConfigScope _gitConfigScope;

    public HumanDeploymentReviewTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-human-review-").FullName;
        _gitConfigScope = TestSupport.AmbientGitConfigScope.Clear();
    }

    public void Dispose()
    {
        _gitConfigScope.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private sealed record Outcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class CodeAuditor(Queue<Outcome> plan) : IAuditor
    {
        public string Name => "code:scripted";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<int> SeenIterations { get; } = [];
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SeenIterations.Add(context.Iteration);
            var outcome = plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class DeploymentProbeAuditor(Queue<Outcome> plan) : IAuditor
    {
        public string Name => "deploy:smoke";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlySet<AuditTarget> Targets => AuditTargets.DeploymentOnly;
        public List<int> SeenIterations { get; } = [];
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SeenIterations.Add(context.Iteration);
            var outcome = plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class LiveFakeManager : IDeploymentManager
    {
        private readonly Dictionary<string, FakeHandle> _live = new();
        private readonly object _gate = new();
        private int _starts;
        private int _disposals;
        public int StartCount { get { lock (_gate) return _starts; } }
        public int DisposeCount { get { lock (_gate) return _disposals; } }
        public int LiveCount { get { lock (_gate) return _live.Count; } }
        public DeploymentEndpoint Endpoint { get; } = new()
        {
            Kind = DeploymentEndpointKind.Http,
            Url = "http://127.0.0.1:18080",
        };

        public Task<IDeploymentHandle> StartAsync(DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _starts++;
                var id = $"dep-{_starts}";
                var captured = id;
                var handle = new FakeHandle(Endpoint, id, () =>
                {
                    lock (_gate)
                    {
                        _disposals++;
                        _live.Remove(captured);
                    }
                });
                _live[id] = handle;
                return Task.FromResult<IDeploymentHandle>(handle);
            }
        }

        public bool TryGetActive(string deploymentId, out IDeploymentHandle? handle)
        {
            lock (_gate)
            {
                if (_live.TryGetValue(deploymentId, out var h))
                {
                    handle = h;
                    return true;
                }

                handle = null;
                return false;
            }
        }

        public IReadOnlyList<ActiveDeploymentInfo> GetActive()
        {
            lock (_gate)
                return _live.Values
                    .Select(h => new ActiveDeploymentInfo(h.Id, h.Kind, null, h.SubstrateId, DateTimeOffset.UtcNow, h.Endpoint))
                    .ToList();
        }

        private sealed class FakeHandle(DeploymentEndpoint endpoint, string id, Action onDispose) : IDeploymentHandle
        {
            private bool _disposed;
            public string Id { get; } = id;
            public string Kind => DeploymentKinds.WebApp;
            public DeploymentEndpoint Endpoint { get; } = endpoint;
            public bool IsAlive => !_disposed;
            public string? SubstrateId => "substrate-" + Id;
            public Task HealthCheckAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task<DeploymentCommandResult> ExecAsync(DeploymentCommand command, CancellationToken ct = default)
                => Task.FromResult(new DeploymentCommandResult(0, string.Empty, string.Empty));
            public ValueTask DisposeAsync()
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeSubstrates : IDeploymentSubstrateProvider
    {
        public string Name => "fake";
        public Task<IDeploymentSubstrate> CreateAsync(DeploymentSubstrateSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException("FakeManager never reaches the substrate provider.");
    }

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        private readonly List<WebhookEvent> _events = new();
        private readonly object _lock = new();
        public IReadOnlyList<WebhookEvent> Events
        {
            get { lock (_lock) return _events.ToArray(); }
        }

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            lock (_lock) _events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static DeploymentRecipe Recipe(TimeSpan? maxLifetime = null) => new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "img",
        MaxLifetime = maxLifetime ?? TimeSpan.FromMinutes(30),
    };

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "human review test",
        Prompt = "serve the widget over http",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };

    private static ProjectAudit AuditWithDeployment(bool enabled, int maxIterations = 3) => new()
    {
        MaxIterations = maxIterations,
        AuditTypes = ["scripted"],
        DeploymentAuditEnabled = enabled,
    };

    private static HumanDeploymentReviewAuditor HumanAuditor() =>
        new(new HumanDeploymentReviewOptions());

    private sealed class TestStores : IDisposable
    {
        public required TestScratchDirectory Scratch { get; init; }
        public required SqliteWorkItemQuestionStore Questions { get; init; }
        public required SqliteHumanDeploymentReviewStore Reviews { get; init; }

        public string DbPath => Scratch.DbPath("human.db");

        public void Deconstruct(
            out string dbPath,
            out SqliteWorkItemQuestionStore questions,
            out SqliteHumanDeploymentReviewStore reviews)
        {
            dbPath = DbPath;
            questions = Questions;
            reviews = Reviews;
        }

        public void Dispose()
        {
            Reviews.Dispose();
            Questions.Dispose();
            TestScratchDirectory.ClearSqlitePools();
            Scratch.Dispose();
        }
    }

    private static TestStores Stores()
    {
        var scratch = TestScratchDirectory.Create("codeybox-human-");
        var db = scratch.DbPath("human.db");
        return new TestStores
        {
            Scratch = scratch,
            Questions = new SqliteWorkItemQuestionStore(db),
            Reviews = new SqliteHumanDeploymentReviewStore(db),
        };
    }

    private async Task<HumanDeploymentReview> ParkAsync(
        TestPipeline tp,
        SqliteHumanDeploymentReviewStore reviews,
        WorkItem item,
        CancellationToken ct = default)
    {
        await tp.Pipeline.RunAsync(item, ct);
        var parked = await tp.Store.GetAsync(item.Id, ct);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);
        var review = await reviews.GetActiveForWorkItemAsync(item.Id.ToString(), ct);
        Assert.NotNull(review);
        return review!;
    }

    /// <summary>
    /// Mirrors the verdict endpoints: record the verdict, answer the backing
    /// question, and resume the item.
    /// </summary>
    private static async Task<WorkItem> ResumeWithVerdictAsync(
        TestPipeline tp,
        SqliteHumanDeploymentReviewStore reviews,
        SqliteWorkItemQuestionStore questions,
        WorkItemId id,
        bool approved,
        string? notes,
        CancellationToken ct = default)
    {
        var review = await reviews.GetActiveForWorkItemAsync(id.ToString(), ct);
        Assert.NotNull(review);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await reviews.RecordVerdictAsync(
            review!.WorkItemId, review.Iteration, approved, notes, "operator", now, ct));
        await questions.AnswerAsync(id.ToString(), review.QuestionId, approved ? "approve" : notes!, "operator", ct);
        var parked = await tp.Store.GetAsync(id, ct);
        Assert.True(await tp.Store.TryUpdateIfStateAsync(
            parked!.With(WorkItemState.WorkComplete), WorkItemState.NeedsOperatorInput, ct));
        return await tp.Store.GetAsync(id, ct) ?? parked;
    }

    [Fact]
    public async Task ParksWithoutHoldingSlot_NotifiesOperator_WatchdogsQuiet()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new LiveFakeManager();
        var webhooks = new RecordingWebhookDispatcher();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            webhookDispatcher: webhooks,
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);

        // The pipeline returns (worker slot released) while the deployment
        // stays alive: only the deployment is kept, nothing else.
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);
        Assert.Contains("human deployment review", parked.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, manager.StartCount);
        Assert.Equal(1, manager.LiveCount);
        Assert.Equal(0, manager.DisposeCount);
        Assert.Equal([1], probe.SeenIterations);

        // The review row bounds the deployment: deadline = start + recipe max.
        var review = await reviews.GetActiveForWorkItemAsync(item.Id.ToString());
        Assert.NotNull(review);
        Assert.Equal(HumanDeploymentReviewStatus.Pending, review!.Status);
        Assert.Equal("human-deployment-review-1", review.QuestionId);
        Assert.True(review.Deadline - review.RequestedAt >= TimeSpan.FromMinutes(29));

        // The backing question carries endpoint + expiry + criteria.
        var qs = await questions.ListByWorkItemAsync(item.Id.ToString());
        var q = Assert.Single(qs);
        Assert.Equal(review.QuestionId, q.QuestionId);
        Assert.Equal("open", q.State);
        Assert.Contains("http://127.0.0.1:18080", q.QuestionText, StringComparison.Ordinal);
        Assert.Contains("expires", q.QuestionText, StringComparison.OrdinalIgnoreCase);

        // The operator notification carries endpoint + expiry structurally.
        var parkedEvent = webhooks.Events.SingleOrDefault(e => e.Event == "work_item.needs_operator_input");
        Assert.NotNull(parkedEvent);
        var details = Assert.IsType<HumanReviewParkedDetails>(parkedEvent!.Details);
        Assert.Equal("http://127.0.0.1:18080", details.Endpoint);
        Assert.Equal(review.Deadline, details.ExpiresAt);
        Assert.Equal(review.QuestionId, details.QuestionId);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.question_asked");

        // A parked-on-human item is not stalled: exempt from both watchdogs.
        Assert.False(WorkerProgressWatchdog.IsWatchedState(WorkItemState.NeedsOperatorInput));
        Assert.False(WorkItemRecoveryPolicy.IsItemStaleWatchedState(WorkItemState.NeedsOperatorInput));
        Assert.DoesNotContain(
            WorkItemState.NeedsOperatorInput,
            (IEnumerable<WorkItemState>)WorkItemRecoveryPolicy.WorkerOccupiedStates);
    }

    [Fact]
    public async Task Approve_PassesAndTearsDownImmediately()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await ParkAsync(tp, reviews, item);

        var resumed = await ResumeWithVerdictAsync(tp, reviews, questions, item.Id, approved: true, notes: null);
        await tp.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Approve tears the held deployment down immediately: exactly one
        // provision, exactly one teardown, nothing live, review consumed.
        Assert.Equal(1, manager.StartCount);
        Assert.Equal(1, manager.DisposeCount);
        Assert.Equal(0, manager.LiveCount);
        var consumed = await reviews.TryGetAsync(item.Id.ToString(), 1);
        Assert.NotNull(consumed);
        Assert.Equal(HumanDeploymentReviewStatus.Approved, consumed!.Status);
        Assert.NotNull(consumed.ConsumedAt);
        // Approve contributes no findings: the probe never ran a second time.
        Assert.Equal([1], probe.SeenIterations);
    }

    [Fact]
    public async Task Reject_NotesBecomeBlockingFindings_ReworkWithFreshDeployment()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, []), new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, []), new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 2),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await ParkAsync(tp, reviews, item);

        var resumed = await ResumeWithVerdictAsync(
            tp, reviews, questions, item.Id, approved: false, notes: "smoke failed on /health");
        await tp.Pipeline.RunAsync(resumed, CancellationToken.None);

        // Reject is a blocking finding: teardown ran, rework ran, and the
        // next iteration provisioned a FRESH deployment and parked again.
        Assert.Equal(1, manager.DisposeCount);
        Assert.Equal(2, manager.StartCount);
        Assert.Equal(1, manager.LiveCount);
        var rejected = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, rejected!.State);

        var first = await reviews.TryGetAsync(item.Id.ToString(), 1);
        Assert.NotNull(first);
        Assert.Equal(HumanDeploymentReviewStatus.Rejected, first!.Status);
        Assert.Equal("smoke failed on /health", first.Notes);
        Assert.NotNull(first.ConsumedAt);
        var second = await reviews.GetActiveForWorkItemAsync(item.Id.ToString());
        Assert.NotNull(second);
        Assert.Equal(2, second!.Iteration);
        Assert.Equal(HumanDeploymentReviewStatus.Pending, second.Status);

        // The persisted iteration-1 snapshot carries the blocking verdict.
        var iterations = await tp.Store.GetIterationsAsync(item.Id);
        var attempt = iterations
            .Where(i => i.Iteration == AuditProgressIterationNumbers.WorkPhase)
            .OrderByDescending(i => i.DispatchedAt)
            .Select(i => (DateTimeOffset?)i.DispatchedAt)
            .FirstOrDefault();
        var progress = await ((IAuditProgressStore)tp.Store).GetAuditProgressAsync(item.Id, attempt);
        var iter1 = progress.Where(r => r.Iteration == 1).ToList();
        Assert.NotEmpty(iter1);
        Assert.Contains(
            iter1.SelectMany(r => r.BlockingFindingsDetails),
            f => f.Title.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                && f.Description.Contains("smoke failed on /health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expiry_FailsClosedAndTearsDown()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var fakeClock = new ControllableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(5));
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(TimeSpan.FromHours(1)),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                TimeProvider = fakeClock,
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await ParkAsync(tp, reviews, item);

        // Silence past the deadline: no verdict recorded, operator never came.
        fakeClock.Advance(TimeSpan.FromHours(2));
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.True(await tp.Store.TryUpdateIfStateAsync(
            parked!.With(WorkItemState.WorkComplete), WorkItemState.NeedsOperatorInput));

        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        // Fail-closed: blocking 'expired unreviewed' finding, teardown ran,
        // the item failed instead of shipping unverified.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("expired unreviewed", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, manager.DisposeCount);
        Assert.Equal(0, manager.LiveCount);
        var expired = await reviews.TryGetAsync(item.Id.ToString(), 1);
        Assert.NotNull(expired);
        Assert.Equal(HumanDeploymentReviewStatus.Expired, expired!.Status);
        Assert.NotNull(expired.ConsumedAt);
    }

    [Fact]
    public async Task Sweeper_ExpiresUndecidedReview_TearsDownAndRequeues()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var fakeClock = new ControllableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(5));
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(TimeSpan.FromHours(1)),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                TimeProvider = fakeClock,
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        var review = await ParkAsync(tp, reviews, item);

        fakeClock.Advance(TimeSpan.FromHours(2));
        var sweeper = new HumanDeploymentReviewSweeper(
            reviews, manager, tp.Store, questions, tp.Queue, null,
            () => new HumanDeploymentReviewOptions(),
            fakeClock,
            NullLogger<HumanDeploymentReviewSweeper>.Instance);
        var swept = await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, swept);
        Assert.Equal(1, manager.DisposeCount);
        Assert.Equal(0, manager.LiveCount);
        var expired = await reviews.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Expired, expired!.Status);
        var qs = await questions.ListByWorkItemAsync(item.Id.ToString());
        Assert.All(qs, q => Assert.NotEqual("open", q.State));
        var resumed = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
        var dequeued = await tp.Queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(item.Id, dequeued!.Value);

        // A verdict that raced the sweep keeps its authority: the sweep is a
        // CAS no-op for decided reviews.
        var secondSweep = await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(0, secondSweep);
        Assert.Equal(review.DeploymentId, expired.DeploymentId);
    }

    [Fact]
    public async Task ReparkWhileUndecided_ReusesHeldDeployment()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        var review = await ParkAsync(tp, reviews, item);

        // Operator heartbeat without a verdict (manual resume, still within
        // deadline): the resume re-parks against the SAME live deployment —
        // no second provision, no teardown.
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.True(await tp.Store.TryUpdateIfStateAsync(
            parked!.With(WorkItemState.WorkComplete), WorkItemState.NeedsOperatorInput));
        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var reparked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, reparked!.State);
        Assert.Equal(1, manager.StartCount);
        Assert.Equal(0, manager.DisposeCount);
        Assert.Equal(1, manager.LiveCount);
        var same = await reviews.GetActiveForWorkItemAsync(item.Id.ToString());
        Assert.Equal(review.DeploymentId, same!.DeploymentId);
        // The code stage did not re-run on resume: still one iteration seen.
        Assert.Equal([1], code.SeenIterations);
    }

    [Fact]
    public async Task StaleReviewAcrossWorkAttempts_ExpiresAndAuditsFresh()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var code = new CodeAuditor(new Queue<Outcome>([new(true, []), new(true, [])]));
        var probe = new DeploymentProbeAuditor(new Queue<Outcome>([new(true, []), new(true, [])]));
        var manager = new LiveFakeManager();
        using var stores = Stores();
        var (db, questions, reviews) = stores;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [code, probe, HumanAuditor()],
            projectAudit: AuditWithDeployment(enabled: true, maxIterations: 1),
            deploymentRecipe: Recipe(),
            deploymentManager: manager,
            deploymentSubstrates: new FakeSubstrates(),
            stateDbPathOverride: db,
            questionStore: questions,
            humanReviewStore: reviews);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        var first = await ParkAsync(tp, reviews, item);

        // A retry re-ran the work phase after the park: the reviewed code may
        // be gone, so the verdict must never apply. The resume expires the
        // stale review fail-closed and audits the fresh tree instead.
        await tp.Store.RecordIterationDispatchAsync(
            item.Id, AuditProgressIterationNumbers.WorkPhase, 0,
            DateTimeOffset.UtcNow.AddHours(1));
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.True(await tp.Store.TryUpdateIfStateAsync(
            parked!.With(WorkItemState.WorkComplete), WorkItemState.NeedsOperatorInput));
        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        Assert.Equal(1, manager.DisposeCount);
        Assert.Equal(2, manager.StartCount);
        // The stale review was retired fail-closed (its deployment torn
        // down) and replaced by a fresh iteration-1 review against the new
        // deployment: the code stage re-ran (two code passes seen) and no
        // verdict crossed the work-attempt boundary.
        Assert.Equal([1, 1], code.SeenIterations);
        var fresh = await reviews.GetActiveForWorkItemAsync(item.Id.ToString());
        Assert.NotNull(fresh);
        Assert.Equal(HumanDeploymentReviewStatus.Pending, fresh!.Status);
        Assert.NotEqual(first.DeploymentId, fresh.DeploymentId);
        var reparked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, reparked!.State);
    }
}
