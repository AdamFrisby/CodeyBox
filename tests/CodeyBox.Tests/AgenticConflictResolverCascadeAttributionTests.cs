using CodeyBox.Agents;
using CodeyBox.Audit;
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
/// Pins the cascade-winner attribution downstream of the agentic conflict
/// resolver: when the primary runner FAILS the in-VM resolution and a
/// class-fallback runner WINS, the chosen runner replaces the primary for
/// every subsequent attribution step — security review invocation, commit
/// trailer composition, etc.
///
/// <para>
/// This is the post-rework analog of the deleted
/// <c>ClaudeTextOnlyFails_AdvisoryReviewUsesCascadeWinner</c> test from the
/// old <c>RebaseResolverAgentRoutingTests</c>. The old test exercised the
/// text-only resolver cascade; this one exercises the new in-VM agentic
/// resolver cascade through the pickup-rebase code path
/// (<c>PipelineRunner.RebaseCheckedOutBranchWithScopeFenceAsync</c>,
/// lines ~1334-1387 and ~1082-1107).
/// </para>
///
/// <para>
/// Observable evidence the chosen-resolver swap took effect: the advisory
/// merge security review (a text-only post-resolution check) is invoked on
/// the CASCADE WINNER, not the primary. The deleted test relied on the same
/// signal. If a regression replaces <c>chosenResolver</c> with the original
/// <c>runner</c> at the RecordMergeSecurityReviewAsync call site, the
/// advisory review will hit the primary instead and this test fails.
/// </para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class AgenticConflictResolverCascadeAttributionTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-cascade-attribution-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PrimaryFailsResolution_AdvisoryReviewRoutesToCascadeWinner()
    {
        // Set up two scripted agents — Claude as primary, Codex as fallback —
        // both in a single class. Configure Claude's first agentic-resolution
        // attempt to fail (returns AgentResult.Success=false). The resolver
        // then walks to the next candidate (Codex), which succeeds.
        //
        // After pickup-rebase finishes, the orchestrator invokes
        // RecordMergeSecurityReviewAsync with the chosen-resolver runner. If
        // chosenResolver was correctly swapped to Codex, Codex's text-only
        // path receives the "# Advisory merge security review" prompt; if a
        // regression reverted to the primary, Claude's text-only path
        // receives it instead.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codexAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        // Claude attempt fails → resolver advances to Codex. AgenticConflictResults
        // is consumed BEFORE the conflict-plan handler, so a single failure
        // entry skips Claude's plan handler entirely.
        claudeAgent.AgenticConflictResults.Enqueue(
            new AgentResult(false, "scripted primary failure", null, "boom"));

        // Codex resolves the conflict on its first attempt.
        codexAgent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("README.md", file.Path);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "main branch change\nwork branch change\n",
            };
        });

        var auditStore = new CapturingAuditReportStore();
        using var fix = BuildFixture(seed, [claudeAgent, codexAgent], auditStore);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Resolver visited Claude (which failed) and Codex (which succeeded).
        Assert.Single(claudeAgent.AgenticConflictInvocations);
        Assert.Single(codexAgent.AgenticConflictInvocations);
        Assert.Empty(codexAgent.ConflictResolutionPlan);

        // The advisory merge security review must have routed to the cascade
        // winner (Codex) — NOT to the primary (Claude). This is the direct
        // pin on PipelineRunner's chosenResolver / chosenMergeRunner swap:
        // the trailer-composition + security-review block uses the swapped
        // runner, so a regression that left chosenResolver at the primary
        // would land the advisory review on Claude instead.
        Assert.Contains(
            codexAgent.TextOnlyInvocations,
            p => p.StartsWith("# Advisory merge security review", StringComparison.Ordinal));
        Assert.DoesNotContain(
            claudeAgent.TextOnlyInvocations,
            p => p.StartsWith("# Advisory merge security review", StringComparison.Ordinal));

        // The advisory review actually executed and persisted a report (extra
        // assertion that the chosen-resolver dispatch reached the recorder,
        // not just any text-only path).
        Assert.NotEmpty(auditStore.Reports);
        Assert.Contains(auditStore.Reports, r => r.AuditorName == "merge-security-review");
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private CascadeFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<IAgentRunner> agents,
        IAuditReportStore auditReportStore)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry(agents);

        var classMembers = agents
            .Select((a, idx) => new AgentMembership
            {
                Agent = a.Kind,
                Billing = AgentBilling.Subscription,
                // Descending QualityScore by config order keeps the router's
                // tie-break deterministic: primary first, fallbacks after.
                QualityScore = 100 - idx,
            })
            .ToList();
        var agentClass = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members = classMembers,
        };
        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [agentClass],
            probes: [],
            quotaOptions,
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
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

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
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            auditReports: auditReportStore,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new CascadeFixture(pipeline, store, gitHost);
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

    private async Task CommitToBareBranchAsync(
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
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private sealed class PermissiveCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    private sealed record CascadeFixture(
        PipelineRunner Pipeline,
        SqliteWorkItemStore Store,
        LocalGitHost GitHost) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
