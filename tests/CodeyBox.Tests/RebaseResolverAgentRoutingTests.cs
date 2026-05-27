using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Pickup-time rebase resolver must consult an agent's text-only credential
/// viability before invoking <see cref="ITextOnlyAgentRunner.RunTextOnlyAsync"/>.
/// Before this fix the resolver hard-routed to whichever agent the work item
/// had been routed to at pickup-time — so an OAuth-only Gemini configuration
/// (no <c>GEMINI_API_KEY</c>) failed every pickup-time rebase with a misleading
/// <see cref="WorkItemState.MergeConflictResolutionFailed"/>, even when the
/// project's class chain had Claude/Codex available with working credentials.
///
/// <para>
/// Coverage targets the bug's acceptance criteria:
/// </para>
/// <list type="number">
///   <item>Gemini missing API_KEY + Claude/Codex with OAuth → rebase routes to
///         Claude or Codex and completes (Gemini's text-only path never fires).</item>
///   <item>Every class member missing text-only credentials → item fails with
///         <c>failureKind=agent_unavailable</c>, not
///         <see cref="WorkItemState.MergeConflictResolutionFailed"/>.</item>
/// </list>
/// </summary>
[Collection("Pipeline integration")]
public sealed class RebaseResolverAgentRoutingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-rebase-route-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task GeminiMissingApiKey_ResolverRoutesToClaude()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gemini = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Gemini,
            TextOnlyUnavailabilityReason = "GEMINI_API_KEY is required",
        };
        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
        };

        // Resolver-side conflict plan goes on Claude — the first class member
        // we expect the router to land on after stepping past Gemini.
        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        using var fix = BuildFixture(seed, [gemini, claude, codex]);

        var item = NewItem(AgentKind.Gemini) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Resolver never invoked Gemini — its text-only credential is missing.
        Assert.Empty(gemini.TextOnlyInvocations);
        // Resolver did invoke Claude (the next class member with viable creds)
        // exactly once for the conflict-resolution prompt.
        Assert.Single(claude.TextOnlyInvocations);
        Assert.StartsWith("# Merge conflict resolver", claude.TextOnlyInvocations[0]);
        Assert.Empty(claude.ConflictResolutionPlan);
        // Codex was the third-rank fallback; never reached.
        Assert.Empty(codex.TextOnlyInvocations);
    }

    [Fact]
    public async Task AllAgentsMissingTextOnlyCredentials_FailsWithAgentUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gemini = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Gemini,
            TextOnlyUnavailabilityReason = "GEMINI_API_KEY is required",
        };
        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
            TextOnlyUnavailabilityReason = "CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is required",
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
            TextOnlyUnavailabilityReason = "OPENAI_API_KEY is required for text-only calls",
        };

        using var fix = BuildFixture(seed, [gemini, claude, codex]);

        var item = NewItem(AgentKind.Gemini) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");
        var preRebaseTip = await RevParseAsync(barePath, item.WorkBranch!);

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Distinct from MergeConflictResolutionFailed: the resolver never ran.
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("agent_unavailable", final.FailureKind);
        Assert.Contains("no text-only-capable agent has viable credentials", final.LastError);
        Assert.Contains("gemini:", final.LastError);
        Assert.Contains("claude:", final.LastError);
        Assert.Contains("codex:", final.LastError);
        // None of the runners' text-only paths were exercised.
        Assert.Empty(gemini.TextOnlyInvocations);
        Assert.Empty(claude.TextOnlyInvocations);
        Assert.Empty(codex.TextOnlyInvocations);
        // Work branch is untouched — same protection as the merge-failure path.
        Assert.Equal(preRebaseTip, await RevParseAsync(barePath, item.WorkBranch!));
    }

    [Fact]
    public async Task CleanRebase_NoConflicts_SucceedsEvenWhenNoResolverHasViableCredentials()
    {
        // Guards the lazy-resolution branch in RebaseCheckedOutBranchWithScopeFenceAsync:
        // resolverPair is materialised on FIRST conflict, not up-front. A regression
        // that hoists ResolveTextOnlyRebaseResolverAsync above the conflict-detection
        // loop would make every clean pickup-time rebase fail with
        // failureKind=agent_unavailable in an OAuth-only-Gemini-no-fallback setup —
        // exactly the misleading-failure shape the routing fix is supposed to
        // eliminate for the no-conflict case.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gemini = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Gemini,
            TextOnlyUnavailabilityReason = "GEMINI_API_KEY is required",
        };
        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
            TextOnlyUnavailabilityReason = "CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is required",
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
            TextOnlyUnavailabilityReason = "OPENAI_API_KEY is required for text-only calls",
        };

        using var fix = BuildFixture(seed, [gemini, claude, codex]);

        var item = NewItem(AgentKind.Gemini) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        // Work branch and main touch DIFFERENT files — rebase has no conflicts,
        // so the lazy resolverPair is never materialised and the missing
        // text-only credentials never matter.
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "feature.md", "work added a feature file\n", "work changes feature");
        await CommitToSeedAsync(seed, "docs.md", "main added a docs file\n", "main changes docs");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // The crucial assertion: a clean rebase with no viable text-only
        // resolver MUST NOT fail with agent_unavailable. The lazy-resolution
        // branch lets the rebase finish without ever consulting the router.
        Assert.NotEqual("agent_unavailable", final!.FailureKind);
        Assert.NotEqual(WorkItemState.Failed, final.State);
        // None of the runners' text-only paths were exercised — there was
        // no conflict, so the resolver never needed to run.
        Assert.Empty(gemini.TextOnlyInvocations);
        Assert.Empty(claude.TextOnlyInvocations);
        Assert.Empty(codex.TextOnlyInvocations);
    }

    [Fact]
    public async Task PrimaryAtAgentCap_WithViableFallback_ResolverRoutesToFallback()
    {
        // Operator config: claude.MaxConcurrent=1 (intentional, to leave Anthropic
        // account budget for an external assistant session). When the work item
        // is on Claude and Claude's per-agent cap is at ceiling, issuing the
        // rebase resolver's text-only call against Claude would compete with
        // the in-flight work-phase budget and 429. The resolver should route
        // to the next class member (Codex here) which is below its own cap.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
        };

        // Conflict plan goes on Codex — the class member we expect the router
        // to reach after stepping past at-cap Claude.
        codex.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        // Claude at cap (running=1, cap=1). Codex has no configured cap.
        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 1 },
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };

        using var fix = BuildFixture(seed, [claude, codex],
            runningCounters: counters, agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Resolver did NOT invoke Claude — its cap was at ceiling.
        Assert.Empty(claude.TextOnlyInvocations);
        // Resolver invoked Codex (the next class member below cap) for the
        // conflict-resolution prompt.
        Assert.Single(codex.TextOnlyInvocations);
        Assert.StartsWith("# Merge conflict resolver", codex.TextOnlyInvocations[0]);
        Assert.Empty(codex.ConflictResolutionPlan);
    }

    [Fact]
    public async Task AllAgentsAtCap_FallsBackToPrimaryViableCreds()
    {
        // Permissive escape hatch: when every viable class member is at cap,
        // the resolver runs on the primary anyway. Better to attempt the call
        // (and accept a possible 429 surfaced as MergeConflictResolutionFailed)
        // than to fail every work item with agent_unavailable simply because
        // the operator's caps are tight. The all-at-cap audit event marks
        // this code path so operators can distinguish it from the clean
        // cap-rerouted case.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
        };

        // Conflict plan goes on Claude — the primary, which we expect to be
        // reused under the all-at-cap fallback.
        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        // Both Claude and Codex pinned to cap.
        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 1 },
            { AgentKind.Codex, 1 },
        };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                [AgentKind.Codex.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };

        using var fix = BuildFixture(seed, [claude, codex],
            runningCounters: counters, agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Both were at cap; primary (Claude) was used despite being at cap.
        Assert.Single(claude.TextOnlyInvocations);
        Assert.StartsWith("# Merge conflict resolver", claude.TextOnlyInvocations[0]);
        Assert.Empty(claude.ConflictResolutionPlan);
        Assert.Empty(codex.TextOnlyInvocations);
    }

    [Fact]
    public async Task NoCapConfigured_ResolverIgnoresRunningCount_UsesPrimary()
    {
        // Sanity check: when MaxConcurrent=0 (unset / "no per-agent cap"),
        // the resolver MUST still pick the primary even when running > 0.
        // Otherwise wiring IAgentRunningCounters would change behaviour in
        // any configuration that doesn't set an explicit cap.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Claude,
        };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge])
        {
            Kind = AgentKind.Codex,
        };

        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        // Running counters say "5 claude items in flight" — should be ignored
        // because no cap is configured.
        var counters = new StubAgentRunningCounters
        {
            { AgentKind.Claude, 5 },
        };
        var concurrency = new AgentConcurrencyOptions(); // empty Members — no caps.

        using var fix = BuildFixture(seed, [claude, codex],
            runningCounters: counters, agentConcurrency: concurrency);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Single(claude.TextOnlyInvocations);
        Assert.Empty(codex.TextOnlyInvocations);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private RoutingFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<ScriptedAgent> agents,
        IAgentRunningCounters? runningCounters = null,
        AgentConcurrencyOptions? agentConcurrency = null)
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
                    // Descending QualityScore by config order keeps the router's
                    // tie-break deterministic: first-listed agent ranks first.
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
            agentRunningCounters: runningCounters,
            agentConcurrency: agentConcurrency);

        return new RoutingFixture(pipeline, store, gitHost);
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
        var sha = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task<string> RevParseAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
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

    /// <summary>
    /// Implements <see cref="IAgentRunningCounters"/> for these tests with a
    /// fixed dictionary. Initializer-list friendly so each test can express
    /// the pinned in-flight count in one line.
    /// </summary>
    private sealed class StubAgentRunningCounters
        : Dictionary<AgentKind, int>, IAgentRunningCounters
    {
        public int GetRunning(AgentKind agent) => TryGetValue(agent, out var n) ? n : 0;

        public IReadOnlyDictionary<AgentKind, int> Snapshot()
            => new Dictionary<AgentKind, int>(this);
    }
}
