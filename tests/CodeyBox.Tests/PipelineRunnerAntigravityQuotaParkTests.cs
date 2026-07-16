using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end regression coverage for the antigravity hidden-429 dispatch gap:
/// a real agy 429 was surfacing as a rendered Google-API message
/// (<c>Resource has been exhausted (e.g. check quota).</c>) that the quota
/// detector did not recognise, so the run terminated as a generic
/// <c>agent exited 1</c> failure — <b>no</b> <c>quota_failures</c> record and
/// <b>no</b> <see cref="WorkItemState.WaitingForQuotaReset"/> park — which
/// starved the observed-failures dispatch gate and let the router keep
/// re-dispatching to a persistently-failing agent.
///
/// <para>These tests drive the REAL pipeline path with the REAL
/// <see cref="AntigravityQuotaFailureDetector"/> and a real
/// <see cref="IQuotaFailureStore"/>: a work-agent failure whose surfaced output
/// carries the 429 must (1) record an observed quota failure for the agent and
/// (2) park the item WaitingForQuotaReset with <c>failureKind=quota</c>.</para>
/// </summary>
public sealed class PipelineRunnerAntigravityQuotaParkTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerAntigravityQuotaParkTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-antigravity-park-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // The rendered Google-API 429 message shape agy logs on a hidden consumer-tier
    // quota block: it carries NEITHER the "RESOURCE_EXHAUSTED" status token NOR the
    // phrase "quota exceeded" ("check quota" is not "quota exceeded"). This is the
    // exact shape that previously slipped through as a generic terminal failure.
    private const string RenderedHidden429Stderr =
        "agy failed\nError: Resource has been exhausted (e.g. check quota).";

    // The classic screaming-snake token shape, which the detector already caught —
    // pinned here so the full record+park wiring is covered for it too.
    private const string ResourceExhaustedTokenStderr =
        "agy failed\nRESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)";

    [Theory]
    [InlineData(RenderedHidden429Stderr)]
    [InlineData(ResourceExhaustedTokenStderr)]
    public async Task Antigravity_Hidden429_RecordsObservedFailure_AndParksWaitingForQuotaReset(string surfacedStderr)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var quotaFailures = new RecordingQuotaFailureStore();
        using var fix = BuildAntigravityOnlyPipeline(seed, quotaFailures);

        // The runner folds agy's terminal glog region into result.Stderr on
        // failure; here we inject the AgentResult the runner would return so the
        // pipeline half (detect -> record -> park) is exercised directly.
        fix.Antigravity.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: surfacedStderr));

        var item = NewItem();
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // (2) The sole antigravity member is exhausted, so the item parks
        //     WaitingForQuotaReset with failureKind=quota — NOT a generic Failed.
        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, finalItem!.State);
        Assert.Equal("quota", finalItem.FailureKind);

        // (1) A quota_failures observation was recorded for antigravity so the
        //     UseObservedFailures gate can bench the member during its window.
        var observations = await quotaFailures.ListRecentAsync(
            TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var observation = Assert.Single(observations);
        Assert.Equal(AgentKind.Antigravity, observation.Agent);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, observation.FailureKind);
        Assert.Equal(item.ProjectId, observation.ProjectId);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private AntigravityFixture BuildAntigravityOnlyPipeline(
        string seedRepoUrl,
        IQuotaFailureStore quotaFailures)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var antigravity = new ScriptableAgent(AgentKind.Antigravity);
        // Claude is registered but not a class member: a misclassification that
        // DID trigger failover would have somewhere to go, so the WaitingForQuotaReset
        // assertion genuinely pins the "no eligible member left -> park" branch.
        var claude = new ScriptableAgent(AgentKind.Claude);
        var registry = new AgentRegistry([antigravity, claude]);

        var soloAntigravity = new AgentClass
        {
            Id = "solo-antigravity",
            DisplayName = "Solo antigravity",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Antigravity, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Antigravity,
            DefaultAgentClass = "solo-antigravity",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var antigravityProbe = new RecordingProbe(AgentKind.Antigravity);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [soloAntigravity],
            [antigravityProbe, claudeProbe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance,
            quotaFailures: quotaFailures);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [antigravityProbe, claudeProbe],
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new AntigravityQuotaFailureDetector(),
                new ClaudeQuotaFailureDetector(),
            }),
            quotaFailures: quotaFailures,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new AntigravityFixture(pipeline, store, antigravity, antigravityProbe, webhooks);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "antigravity quota park test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = AgentKind.Antigravity,
        AgentClassId = null,
        PushUpstream = false,
    };

    private sealed class AntigravityFixture(
        PipelineRunner pipeline,
        SqliteWorkItemStore store,
        ScriptableAgent antigravity,
        RecordingProbe antigravityProbe,
        CapturingWebhookDispatcher webhooks) : IDisposable
    {
        public PipelineRunner Pipeline { get; } = pipeline;
        public SqliteWorkItemStore Store { get; } = store;
        public ScriptableAgent Antigravity { get; } = antigravity;
        public RecordingProbe AntigravityProbe { get; } = antigravityProbe;
        public CapturingWebhookDispatcher Webhooks { get; } = webhooks;

        public void Dispose() => Store.Dispose();
    }

    private sealed class RecordingQuotaFailureStore : IQuotaFailureStore
    {
        private readonly List<QuotaFailureObservation> _observations = [];

        public Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt));
            return Task.CompletedTask;
        }

        public Task RecordForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt, projectId));
            return Task.CompletedTask;
        }

        public async Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default) =>
            await GetMostRecentAsync(agent, modelId, window, now, ct) is not null;

        public Task<DateTimeOffset?> GetMostRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
        {
            var latest = _observations
                .Where(o => o.Agent == agent
                    && string.Equals(o.ModelId, modelId, StringComparison.Ordinal)
                    && o.ObservedAt <= now
                    && now - o.ObservedAt <= window)
                .Select(o => (DateTimeOffset?)o.ObservedAt)
                .Max();
            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotaFailureObservation>>(
                _observations.Where(o => o.ObservedAt <= now && now - o.ObservedAt <= window).ToList());

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            _observations.RemoveAll(o => o.ObservedAt < cutoff);
            return Task.CompletedTask;
        }
    }
}
