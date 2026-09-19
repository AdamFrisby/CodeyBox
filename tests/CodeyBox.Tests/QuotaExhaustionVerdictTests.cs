using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Verdict-attribution tests for the quota-exhaustion classifier boundary:
/// only provider quota/rate-limit signals may install an exhaustion verdict,
/// a cached verdict records the evidence that produced it, and the operator
/// reset clears cached verdicts without a restart (audited).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class QuotaExhaustionVerdictTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-quota-verdict-").FullName;
    private readonly TestSink _sink = new();

    public QuotaExhaustionVerdictTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static AgentMembership TestMember() => new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static AgentClassRouter BuildRouter(params IAgentQuotaProbe[] probes) =>
        new(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [TestMember()],
            }],
            probes,
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "verdict attribution",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = AgentKind.Claude,
        PushUpstream = false,
    };

    // ── Evidence narrowing ──────────────────────────────────────────────

    [Theory]
    [InlineData(QuotaFailureKind.RateLimitExceeded)]
    [InlineData(QuotaFailureKind.LimitReached)]
    public void ExhaustionEvidence_AcceptsQuotaSignals(QuotaFailureKind kind)
    {
        var evidence = new QuotaExhaustionEvidence(kind, "work:claude/default", "TerminalQuotaError");
        Assert.Equal(kind, evidence.Signal);
    }

    [Fact]
    public void ExhaustionEvidence_RejectsNonQuotaSignals()
    {
        Assert.Throws<ArgumentException>(() => new QuotaExhaustionEvidence(QuotaFailureKind.Unauthorized, "test", "test verdict"));
        Assert.Throws<ArgumentException>(() => new QuotaExhaustionEvidence((QuotaFailureKind)42, "test", "test verdict"));
    }

    [Fact]
    public void RouterMarkExhausted_RequiresEvidence()
    {
        var router = BuildRouter();

        Assert.Throws<ArgumentNullException>(() =>
            router.MarkExhausted(
                TestMember(),
                TimeSpan.FromMinutes(30),
                null,
                null!));
    }

    [Fact]
    public async Task CachedVerdict_RecordsEvidenceNamingOrigin()
    {
        var router = BuildRouter();
        var member = TestMember();
        router.MarkExhausted(
            member,
            TimeSpan.FromMinutes(30),
            null,
            new QuotaExhaustionEvidence(
                QuotaFailureKind.RateLimitExceeded, "work:claude/default", "TerminalQuotaError"));
        Assert.True(router.IsExhausted(member, DateTimeOffset.UtcNow));

        var cleared = router.ClearExhaustionForAgent(AgentKind.Claude);

        var entry = Assert.Single(cleared);
        var evidence = entry.Value.Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, evidence.Signal);
        Assert.Contains("work:claude/default", evidence.ToString(), StringComparison.Ordinal);
        Assert.Contains("TerminalQuotaError", evidence.ToString(), StringComparison.Ordinal);
        Assert.False(router.IsExhausted(member, DateTimeOffset.UtcNow));
        await Task.CompletedTask;
    }

    // ── Store narrowing: 401s are never recorded as quota observations ──

    [Fact]
    public async Task RecordIfQuotaFailure_IgnoresUnauthorizedOutput()
    {
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteQuotaFailureStore(failuresDb);
        // A detector surfacing a provider 401 as an Unauthorized detection
        // (the DotNetOpencode/Goose pattern-table shape): the auth event must
        // never land in the quota-observation store.
        var classifier = new CompositeQuotaFailureClassifier([new UnauthorizedDetector()]);
        var now = DateTimeOffset.UtcNow;

        await classifier.RecordIfQuotaFailureAsync(
            store,
            AgentKind.Claude,
            modelId: null,
            summary: "agent exited 1",
            stderr: "HTTP 401 Unauthorized: invalid API key",
            observedAt: now,
            retention: TimeSpan.FromHours(1),
            CancellationToken.None);

        Assert.False(await store.HasRecentAsync(
            AgentKind.Claude, null, TimeSpan.FromMinutes(10), now, CancellationToken.None));
    }

    [Fact]
    public async Task RecordIfQuotaFailure_RecordsGenuineRateLimit()
    {
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteQuotaFailureStore(failuresDb);
        var classifier = new CompositeQuotaFailureClassifier([new ClaudeQuotaFailureDetector()]);
        var now = DateTimeOffset.UtcNow;

        await classifier.RecordIfQuotaFailureAsync(
            store,
            AgentKind.Claude,
            modelId: null,
            summary: "agent exited 1",
            stderr: "API Error: 429 Too Many Requests, please retry after 60s",
            observedAt: now,
            retention: TimeSpan.FromHours(1),
            CancellationToken.None);

        Assert.True(await store.HasRecentAsync(
            AgentKind.Claude, null, TimeSpan.FromMinutes(10), now, CancellationToken.None));
    }

    private sealed class UnauthorizedDetector : IAgentQuotaFailureDetector
    {
        public AgentKind Kind => AgentKind.Claude;

        public QuotaDetection? Detect(string? stderr, string? stdout) =>
            !string.IsNullOrEmpty(stderr) && stderr.Contains("401", StringComparison.Ordinal)
                ? new QuotaDetection(QuotaFailureKind.Unauthorized)
                : null;
    }

    // ── Pipeline: a 401 never benches quota state and never parks ───────

    [Fact]
    public async Task UnauthorizedAgentOutput_NeverBenchesQuotaOrParks()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var failuresDb = Path.Combine(_workspace, "quota-failures-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var quotaFailures = new SqliteQuotaFailureStore(failuresDb);
        var router = BuildRouter();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            classRouter: router,
            quotaFailures: quotaFailures);

        tp.Agent.WorkResults.Enqueue(new AgentResult(
            false,
            "agent failed",
            null,
            "API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Auth handling owns the outcome — but it must never be a quota park,
        // and no quota-side state may record the 401.
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual("quota", final.FailureKind);
        var member = TestMember();
        Assert.False(router.IsExhausted(member, DateTimeOffset.UtcNow));
        Assert.False(await quotaFailures.HasRecentAsync(
            AgentKind.Claude, null, TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    // ── Pipeline: repo-seeding failure is infrastructure, never quota ────

    [Fact]
    public async Task RepoSeedingFailure_FailsInfrastructure_LeavesQuotaUntouched()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var router = BuildRouter();
        var badProject = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = Path.Combine(_workspace, "does-not-exist-" + Guid.NewGuid().ToString("N")[..8] + ".git"),
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            NetworkProfiles = new ProjectNetworkProfiles(),
            Upstream = ProjectUpstream.Noop,
            Audit = new ProjectAudit { MaxIterations = 1 },
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            classRouter: router,
            projectRepository: new InMemoryProjectRepository(badProject));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // The clone/fetch runs on the orchestrator host against the git
        // remote — no agent, no provider quota. It must surface as
        // infrastructure (transient network blip), never as a quota park,
        // and must leave every quota gate untouched.
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, final.State);
        Assert.Contains("ensure-repository", final.LastError ?? "", StringComparison.Ordinal);
        var member = TestMember();
        Assert.False(router.IsExhausted(member, DateTimeOffset.UtcNow));
    }

    // ── Operator reset: clears cached verdicts, audited ─────────────────

    [Fact]
    public void OperatorReset_ClearsCachedVerdicts_AndAuditsOperator()
    {
        var router = BuildRouter();
        var member = TestMember();
        router.MarkExhausted(
            member,
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow.AddHours(1),
            new QuotaExhaustionEvidence(
                QuotaFailureKind.LimitReached, "work:claude/default", "TerminalQuotaError"));
        var probe = new ResettableProbe(AgentKind.Claude, availablePct: 0.0);
        probe.MarkExhausted();
        Assert.True(router.IsExhausted(member, DateTimeOffset.UtcNow));

        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var reset = new AgentAvailabilityReset(
            registry,
            new InVmSmokeCache(TimeSpan.FromMinutes(15)),
            registry,
            router,
            [probe]);

        var result = reset.ResetQuotaExhaustion(AgentKind.Claude, "operator-test");

        Assert.Equal(1, result.RouterEntriesCleared);
        Assert.Equal(1, result.ProbeEntriesCleared);
        Assert.False(router.IsExhausted(member, DateTimeOffset.UtcNow));
        Assert.Equal(0, probe.RuntimeExhaustedCount);
        var evidence = Assert.Single(result.ClearedEvidence);
        Assert.Contains("LimitReached", evidence, StringComparison.Ordinal);

        var cleared = Assert.Single(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "agent.quota_exhaustion_cleared");
        Assert.Equal("operator-test", GetScalar<string>(cleared, "ClearedBy"));
        Assert.Equal("claude", GetScalar<string>(cleared, "Agent"));
    }

    [Fact]
    public void OperatorReset_WithNoActiveVerdicts_AuditsEmptyClear()
    {
        var router = BuildRouter();
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var reset = new AgentAvailabilityReset(
            registry,
            new InVmSmokeCache(TimeSpan.FromMinutes(15)),
            registry,
            router,
            []);

        var result = reset.ResetQuotaExhaustion(AgentKind.Claude, "operator-test");

        Assert.Equal(0, result.RouterEntriesCleared);
        Assert.Empty(result.ClearedEvidence);
        Assert.Single(_sink.Events, e =>
            GetScalar<string>(e, "EventName") == "agent.quota_exhaustion_cleared");
    }

    private static T? GetScalar<T>(LogEvent evt, string name) =>
        evt.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? (T?)scalar.Value
            : default;

    private sealed class ResettableProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe, IAgentQuotaExhaustionReset
    {
        public AgentKind Kind { get; } = kind;
        public int RuntimeExhaustedCount { get; private set; }

        public void MarkExhausted() => RuntimeExhaustedCount++;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });

        public int ClearRuntimeExhaustion(AgentKind kind)
        {
            if (kind != Kind)
                return 0;
            var cleared = RuntimeExhaustedCount;
            RuntimeExhaustedCount = 0;
            return cleared;
        }
    }
}
