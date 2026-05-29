using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for pickup-time conflict-resolver routing that was
/// originally exercised through the deleted text-only rebase resolver harness.
/// The current resolver is in-VM and agentic, so these tests pin both the
/// candidate list and the full pickup-rebase path that consumes it.
///
/// <para>
/// In the <c>GlobalSerilog</c> collection (not <c>Pipeline integration</c>):
/// the routing tests emit <see cref="AuditLog"/> events during a real pipeline
/// run, and the AC4 coverage below swaps the static <see cref="Log.Logger"/> to
/// a <see cref="TestSink"/> to assert <c>rebase_resolver.agent_selected</c> /
/// <c>rebase_resolver.rerouted</c>. Both require serialising against every other
/// test that mutates the global logger so emissions and assertions stay
/// deterministic.
/// </para>
/// </summary>
[Collection("GlobalSerilog")]
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
        using var fixture = BuildCandidateFixture([claude, cursor], auditAgent: AgentKind.Cursor);

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
        using var fixture = BuildCandidateFixture(
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
        using var fixture = BuildCandidateFixture(
            [claude],
            auditAgent: AgentKind.Claude,
            quotas: new() { [AgentKind.Claude] = 6.0 });

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
                item, fixture.Project, claude, CancellationToken.None));

        Assert.Contains("all candidate agents are quota-exhausted", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has viable credentials", ex.Message, StringComparison.Ordinal);
        Assert.Contains("claude:", ex.CandidateReasons, StringComparison.Ordinal);
        Assert.Contains("quota exhausted", ex.CandidateReasons, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanRebase_NoConflicts_SucceedsEvenWhenAllCandidatesQuotaExhausted()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var gemini = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Gemini };
        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        using var fix = BuildRoutingFixture(
            seed,
            [gemini, claude, codex],
            quotas: new()
            {
                [AgentKind.Gemini] = 6.0,
                [AgentKind.Claude] = 6.0,
                [AgentKind.Codex] = 6.0,
            });

        var item = NewItem(AgentKind.Gemini) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "feature.md", "work added a feature file\n", "work changes feature");
        await CommitToSeedAsync(seed, "docs.md", "main added a docs file\n", "main changes docs");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual("agent_unavailable", final!.FailureKind);
        Assert.NotEqual(WorkItemState.Failed, final.State);
        Assert.Empty(gemini.AgenticConflictInvocations);
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Empty(codex.AgenticConflictInvocations);
    }

    [Fact]
    public async Task PrimaryAtAgentCap_WithViableFallback_ResolverRoutesToFallback()
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

        var counters = new StubAgentRunningCounters { { AgentKind.Claude, 1 } };
        var concurrency = new AgentConcurrencyOptions
        {
            Members =
            {
                [AgentKind.Claude.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 },
            },
        };

        using var fix = BuildRoutingFixture(seed, [claude, codex],
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
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Single(codex.AgenticConflictInvocations);
        Assert.StartsWith("# Conflict-resolution mode (in-sandbox agentic resolver)", codex.AgenticConflictInvocations[0]);
        Assert.Empty(codex.ConflictResolutionPlan);
    }

    [Fact]
    public async Task AllAgentsAtCap_FallsBackToPrimaryCandidate()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

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

        using var fix = BuildRoutingFixture(seed, [claude, codex],
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
        Assert.Single(claude.AgenticConflictInvocations);
        Assert.Empty(claude.ConflictResolutionPlan);
        Assert.Empty(codex.AgenticConflictInvocations);
    }

    [Fact]
    public async Task NoCapConfigured_ResolverIgnoresRunningCount_UsesPrimary()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        var counters = new StubAgentRunningCounters { { AgentKind.Claude, 5 } };

        using var fix = BuildRoutingFixture(seed, [claude, codex],
            runningCounters: counters, agentConcurrency: new AgentConcurrencyOptions());

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
        Assert.Single(claude.AgenticConflictInvocations);
        Assert.Empty(codex.AgenticConflictInvocations);
    }

    [Fact]
    public async Task AuditAgentConfigured_ResolverRoutesToAuditAgent_NotWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var cursor = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };

        cursor.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        using var fix = BuildRoutingFixture(seed, [claude, cursor], auditAgent: AgentKind.Cursor);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);

        // AC4: assert the operator-visible audit-log line names the agent the
        // resolver actually used. Wiring the sink for the full pipeline run (not
        // just a direct AuditLog call) is what catches a regression where the
        // resolver runs but the success path never emits
        // rebase_resolver.agent_selected.
        var (final, events) = await RunCapturingAuditAsync(fix, item);

        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Single(cursor.AgenticConflictInvocations);
        Assert.Empty(cursor.ConflictResolutionPlan);
        Assert.Empty(claude.AgenticConflictInvocations);

        // The diagnostic event fired exactly once (the advisory security review
        // reuses the resolver's chosen pair rather than re-selecting) and names
        // cursor — the agent the resolver actually ran.
        var selected = Assert.Single(events,
            e => GetScalar<string>(e, "EventName") == "rebase_resolver.agent_selected");
        Assert.Equal("cursor", GetScalar<string>(selected, "ChosenAgent"));
        Assert.Equal(item.Id.ToString(), GetScalar<string>(selected, "WorkItemId"));
    }

    [Fact]
    public async Task AuditAgentQuotaExhausted_WithViableFallback_ResolverReroutes()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var cursor = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };

        cursor.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        using var fix = BuildRoutingFixture(seed, [claude, cursor],
            auditAgent: AgentKind.Claude,
            quotas: new() { [AgentKind.Claude] = 6.0, [AgentKind.Cursor] = 80.0 });

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await fix.Store.CreateAsync(item);
        var (final, events) = await RunCapturingAuditAsync(fix, item);

        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Single(cursor.AgenticConflictInvocations);
        Assert.Empty(cursor.ConflictResolutionPlan);

        // The reroute event must name claude as rejected and report the *quota*
        // gate as the cause — a regression to the old credential-only message
        // would misreport this AuditAgent-quota steer as a credential problem.
        var rerouted = Assert.Single(events,
            e => GetScalar<string>(e, "EventName") == "rebase_resolver.rerouted");
        Assert.Equal("claude", GetScalar<string>(rerouted, "RejectedAgent"));
        Assert.Equal("cursor", GetScalar<string>(rerouted, "ChosenAgent"));
        Assert.Contains("quota", GetScalar<string>(rerouted, "Reason") ?? string.Empty);
        // The resolver still settled on cursor for the actual call.
        var selected = Assert.Single(events,
            e => GetScalar<string>(e, "EventName") == "rebase_resolver.agent_selected");
        Assert.Equal("cursor", GetScalar<string>(selected, "ChosenAgent"));
    }

    [Fact]
    public async Task AuditAgentQuotaExhausted_NoViableFallback_FailsCleanlyWithAgentUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };

        using var fix = BuildRoutingFixture(seed, [claude],
            auditAgent: AgentKind.Claude,
            quotas: new() { [AgentKind.Claude] = 6.0 });

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");
        var preRebaseTip = await RevParseAsync(barePath, item.WorkBranch!);

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("agent_unavailable", final.FailureKind);
        // The exception headline must reflect the actual blocking gate (quota),
        // not the legacy "missing credentials" template that misled operators
        // into a credential-debugging detour.
        Assert.Contains("all candidate agents are quota-exhausted", final.LastError);
        Assert.DoesNotContain("has viable credentials", final.LastError);
        Assert.Contains("claude:", final.LastError);
        Assert.Contains("quota exhausted", final.LastError);
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Equal(preRebaseTip, await RevParseAsync(barePath, item.WorkBranch!));
    }

    [Fact]
    public async Task MixedQuotaAndRegistrationFailures_ThrowsCombinedHeadline()
    {
        var claude = new FakeAgentRunner(AgentKind.Claude);
        using var fixture = BuildCandidateFixture(
            [claude],
            quotas: new() { [AgentKind.Claude] = 1.0 },
            classMembers: [AgentKind.Claude, AgentKind.Codex]);

        var item = NewItem(AgentKind.Claude);
        await fixture.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            fixture.Pipeline.BuildAgenticConflictCandidatesAsync(
                item, fixture.Project, claude, CancellationToken.None));

        Assert.Contains("registration and quota both blocking candidates", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("all candidate agents are quota-exhausted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("claude:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("quota exhausted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("codex:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no runner registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditAgentUnregistered_ResolverFallsBackToWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var cursor = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };

        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        using var fix = BuildRoutingFixture(seed, [claude, cursor], auditAgent: AgentKind.Gemini);

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
        Assert.Single(claude.AgenticConflictInvocations);
        Assert.Empty(claude.ConflictResolutionPlan);
        Assert.Empty(cursor.AgenticConflictInvocations);
    }

    [Fact]
    public async Task AuditAgentAtCap_WithViableFallback_ResolverRoutesToClassMember()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var cursor = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Cursor };

        claude.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [file.Path] = "main branch change\nwork branch change\n",
            };
        });

        var counters = new StubAgentRunningCounters { { AgentKind.Cursor, 1 } };
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { [AgentKind.Cursor.Value] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        };

        using var fix = BuildRoutingFixture(seed, [claude, cursor],
            runningCounters: counters,
            agentConcurrency: concurrency,
            auditAgent: AgentKind.Cursor);

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
        Assert.Empty(cursor.AgenticConflictInvocations);
        Assert.Single(claude.AgenticConflictInvocations);
        Assert.Empty(claude.ConflictResolutionPlan);
    }

    [Fact]
    public async Task PrimaryQuotaExhausted_WithViableFallback_ResolverRoutesToFallback()
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

        using var fix = BuildRoutingFixture(seed, [claude, codex],
            quotas: new() { [AgentKind.Claude] = 1.0, [AgentKind.Codex] = 80.0 });

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
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Single(codex.AgenticConflictInvocations);
        Assert.StartsWith("# Conflict-resolution mode (in-sandbox agentic resolver)", codex.AgenticConflictInvocations[0]);
        Assert.Empty(codex.ConflictResolutionPlan);
    }

    [Fact]
    public async Task AllAgentsQuotaExhausted_FailsWithAgentUnavailable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var claude = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codex = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        using var fix = BuildRoutingFixture(seed, [claude, codex],
            quotas: new() { [AgentKind.Claude] = 1.0, [AgentKind.Codex] = 2.0 });

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work changes readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");
        var preRebaseTip = await RevParseAsync(barePath, item.WorkBranch!);

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("agent_unavailable", final.FailureKind);
        Assert.Contains("all candidate agents are quota-exhausted", final.LastError);
        Assert.DoesNotContain("has viable credentials", final.LastError);
        Assert.Contains("claude:", final.LastError);
        Assert.Contains("quota exhausted", final.LastError);
        Assert.Empty(claude.AgenticConflictInvocations);
        Assert.Empty(codex.AgenticConflictInvocations);
        Assert.Equal(preRebaseTip, await RevParseAsync(barePath, item.WorkBranch!));
    }

    private CandidateFixture BuildCandidateFixture(
        IReadOnlyList<IAgentRunner> runners,
        AgentKind? auditAgent = null,
        Dictionary<AgentKind, double>? quotas = null,
        IReadOnlyList<AgentKind>? classMembers = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var registry = new AgentRegistry(runners);
        var memberKinds = classMembers ?? runners.Select(static runner => runner.Kind).ToArray();

        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = memberKinds
                .Select((kind, idx) => new AgentMembership
                {
                    Agent = kind,
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
            quotaProbes: probes,
            quotaOptions: new QuotaRouterOptions { MinQuotaPct = 10.0 },
            classRouter: router);

        return new CandidateFixture(pipeline, store, project);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the pipeline with the static <see cref="Log.Logger"/> swapped to a
    /// fresh <see cref="TestSink"/> so the resolver's <see cref="AuditLog"/>
    /// emissions (e.g. <c>rebase_resolver.agent_selected</c>) can be asserted
    /// end-to-end. Safe only because this class is in the <c>GlobalSerilog</c>
    /// collection; the previous logger is always restored.
    /// </summary>
    private static async Task<(WorkItem? Final, IReadOnlyList<LogEvent> Events)> RunCapturingAuditAsync(
        RoutingFixture fix, WorkItem item)
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            var final = await fix.Store.GetAsync(item.Id);
            return (final, sink.Events);
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private RoutingFixture BuildRoutingFixture(
        string seedRepoUrl,
        IReadOnlyList<ScriptedAgent> agents,
        IAgentRunningCounters? runningCounters = null,
        AgentConcurrencyOptions? agentConcurrency = null,
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
        var registry = new AgentRegistry(agents);

        var agentClass = new AgentClass
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
        var probes = quotas is null
            ? null
            : agents
                .Select(a => (IAgentQuotaProbe)new ConfigurableProbe(
                    a.Kind, quotas.GetValueOrDefault(a.Kind, 80.0)))
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
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = agents[0].Kind,
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
            quotaProbes: probes,
            quotaOptions: probes is null ? null : new QuotaRouterOptions { MinQuotaPct = 10.0 },
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

    private sealed record CandidateFixture(PipelineRunner Pipeline, SqliteWorkItemStore Store, Project Project) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed record RoutingFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        LocalGitHost GitHost) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(new AgentCredential(
                agent,
                EnvironmentVariables: new Dictionary<string, string>(),
                Files: new Dictionary<string, string>()));
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

    private sealed class StubAgentRunningCounters
        : Dictionary<AgentKind, int>, IAgentRunningCounters
    {
        public int GetRunning(AgentKind agent) => TryGetValue(agent, out var n) ? n : 0;

        public IReadOnlyDictionary<AgentKind, int> Snapshot()
            => new Dictionary<AgentKind, int>(this);
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
