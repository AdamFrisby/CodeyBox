using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the audit event emitted when <see cref="PipelineRunner"/>'s
/// pickup-time rebase resolver reroutes from a quota-blocked primary to a
/// viable class-chain fallback. The event must carry the actual blocking-gate
/// detail (e.g. <c>quota exhausted (1.0%)</c>) instead of a hard-coded
/// "primary unavailable" / "credential missing" template — otherwise a
/// regression that drops <c>primaryRejectionDetail</c> would still pass the
/// outcome-only routing tests in <see cref="RebaseResolverAgentRoutingTests"/>
/// (work item reaches Done on the fallback) while leaving operators with a
/// reroute reason that points debugging at the wrong subsystem.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class RebaseResolverRerouteAuditTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-rebase-audit-").FullName;
    private readonly TestSink _sink = new();

    public RebaseResolverRerouteAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PrimaryQuotaExhausted_ReroutesWithQuotaReasonInAuditEvent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        codex.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        var quotaProbes = new IAgentQuotaProbe[]
        {
            new ScriptedQuotaProbe(AgentKind.Claude, availablePct: 1.0),
            new ScriptedQuotaProbe(AgentKind.Codex, availablePct: 80.0),
        };

        using var fix = BuildFixture(seed, [claude, codex], quotaProbes: quotaProbes);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var rerouteEvents = _sink.Events
            .Where(e => e.Properties.TryGetValue("EventName", out var ev)
                && ev is ScalarValue sv
                && (string?)sv.Value == "rebase_resolver.rerouted")
            .ToList();

        // The pipeline may invoke the resolver more than once (initial rebase
        // + a post-resolution check), so assert AT LEAST one rerouted event —
        // every one must carry the same quota-flavoured reason.
        Assert.NotEmpty(rerouteEvents);
        foreach (var evt in rerouteEvents)
        {
            Assert.Equal("claude", GetScalar<string>(evt, "RejectedAgent"));
            Assert.Equal("codex", GetScalar<string>(evt, "ChosenAgent"));
            var reason = GetScalar<string>(evt, "Reason");
            Assert.NotNull(reason);
            // The reason must reflect the quota gate that actually rejected
            // the primary, not the generic "primary unavailable" template the
            // pre-fix code emitted. "%" matches EvaluateCandidateQuotaAsync's
            // "quota exhausted (1.0%)" format string.
            Assert.Contains("quota", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%", reason);
            // It must NOT carry the legacy hard-coded template — a regression
            // that drops the per-gate detail and falls back to the template
            // would fail this assertion.
            Assert.DoesNotContain("primary unavailable", reason);
        }
    }

    // ── Harness (mirrors RebaseResolverAgentRoutingTests) ────────────────────

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<ScriptedAgent> agents,
        IReadOnlyList<IAgentQuotaProbe>? quotaProbes = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();
        var registry = new AgentRegistry(agents);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = agents
                .Select((agent, idx) => new AgentMembership
                {
                    Agent = agent.Kind,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100 - idx,
                })
                .ToList(),
        };
        var router = new AgentClassRouter(
            [frontier],
            probes: [],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = agents[0].Kind,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1 },
        };
        var projects = new InMemoryProjectRepository(project);

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new PermissiveCredentialProvider(),
            prs,
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            classRouter: router,
            quotaProbes: quotaProbes,
            quotaOptions: quotaProbes is null
                ? null
                : new QuotaRouterOptions { MinQuotaPct = 10.0 });

        return new RoutingFixture(pipeline, store, gitHost);
    }

    private sealed class ScriptedQuotaProbe : IAgentQuotaProbe
    {
        private readonly double _availablePct;
        public ScriptedQuotaProbe(AgentKind kind, double availablePct)
        {
            Kind = kind;
            _availablePct = availablePct;
        }
        public AgentKind Kind { get; }
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _availablePct });
        public Task MarkExhaustedAsync(AgentMembership member, TimeSpan ttl, DateTimeOffset? resetAt = null, CancellationToken ct = default)
            => Task.CompletedTask;
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

    private async Task<string> CommitToBareBranchAsync(
        string barePath, string branch, string fileName, string contents, string subject)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        var fullPath = Path.Combine(clone, fileName);
        await File.WriteAllTextAsync(fullPath, contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        var (_, stdout, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return stdout.Trim();
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t) return t;
        return default;
    }

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string>(),
                Files: new Dictionary<string, string>()));
    }

    private sealed record RoutingFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        LocalGitHost GitHost) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }
}
