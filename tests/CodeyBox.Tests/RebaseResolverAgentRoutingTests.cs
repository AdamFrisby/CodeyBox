using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for pickup-time conflict-resolver routing that was
/// originally exercised through the deleted text-only rebase resolver harness.
/// The current resolver is in-VM and agentic, so these tests pin the candidate
/// list that drives both pickup-rebase and merge conflict resolution.
/// </summary>
public sealed class RebaseResolverAgentRoutingTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-rebase-route-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task AuditAgentConfigured_CandidatesStartWithAuditAgent()
    {
        var claude = new FakeAgentRunner(AgentKind.Claude);
        var cursor = new FakeAgentRunner(AgentKind.Cursor);
        using var fixture = BuildFixture([claude, cursor], auditAgent: AgentKind.Cursor);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
            item, fixture.Project, claude, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(AgentKind.Cursor, candidates[0].Runner.Kind);
        Assert.Equal(AgentKind.Claude, candidates[1].Runner.Kind);
    }

    [Fact]
    public async Task AuditAgentQuotaExhausted_WithViableFallback_RoutesToFallback()
    {
        var claude = new FakeAgentRunner(AgentKind.Claude);
        var cursor = new FakeAgentRunner(AgentKind.Cursor);
        using var fixture = BuildFixture(
            [claude, cursor],
            auditAgent: AgentKind.Claude,
            quotas: new()
            {
                [AgentKind.Claude] = 6.0,
                [AgentKind.Cursor] = 80.0,
            });

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var candidates = await fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
            item, fixture.Project, claude, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal(AgentKind.Cursor, candidates[0].Runner.Kind);
    }

    [Fact]
    public async Task AuditAgentQuotaExhausted_NoViableFallback_ThrowsAgentUnavailable()
    {
        var claude = new FakeAgentRunner(AgentKind.Claude);
        using var fixture = BuildFixture(
            [claude],
            auditAgent: AgentKind.Claude,
            quotas: new() { [AgentKind.Claude] = 6.0 });

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
                item, fixture.Project, claude, CancellationToken.None));

        Assert.Contains("no agent has viable credentials", ex.Message, StringComparison.Ordinal);
        Assert.Contains("claude:", ex.CandidateReasons, StringComparison.Ordinal);
        Assert.Contains("quota exhausted", ex.CandidateReasons, StringComparison.Ordinal);
    }

    private Fixture BuildFixture(
        IReadOnlyList<IAgentRunner> runners,
        AgentKind? auditAgent = null,
        Dictionary<AgentKind, double>? quotas = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var registry = new AgentRegistry(runners);

        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = runners
                .Select((runner, idx) => new AgentMembership
                {
                    Agent = runner.Kind,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100 - idx,
                })
                .ToList(),
        };
        var probes = quotas is null
            ? null
            : runners
                .Select(r => (IAgentQuotaProbe)new ConfigurableProbe(
                    r.Kind, quotas.GetValueOrDefault(r.Kind, 80.0)))
                .ToList();
        var router = new AgentClassRouter(
            [agentClass],
            probes: probes ?? [],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "/nonexistent",
            DefaultBaseBranch = "main",
            DefaultAgent = runners[0].Kind,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditAgent = auditAgent },
        };
        var projects = new InMemoryProjectRepository(project);

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new PermissiveCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: probes,
            auditQuotaOptions: new QuotaRouterOptions { MinQuotaPct = 10.0 },
            classRouter: router);

        return new Fixture(pipeline, store, project);
    }

    private static WorkItem NewItem(AgentKind agent)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = $"codeybox/{id.ToString()[..8]}",
            Agent = agent,
            AgentClassId = "frontier",
            PushUpstream = false,
        };
    }

    private sealed record Fixture(PipelineRunner Pipeline, SqliteWorkItemStore Store, Project Project) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public FakeAgentRunner(AgentKind kind) { Kind = kind; }
        public AgentKind Kind { get; }

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
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private sealed class ConfigurableProbe : IAgentQuotaProbe
    {
        private double _pct;

        public ConfigurableProbe(AgentKind kind, double pct)
        {
            Kind = kind;
            _pct = pct;
        }

        public AgentKind Kind { get; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _pct });

        public Task MarkExhaustedAsync(
            AgentMembership member,
            TimeSpan ttl,
            DateTimeOffset? resetAt = null,
            CancellationToken ct = default)
        {
            _pct = 0.0;
            return Task.CompletedTask;
        }
    }
}
