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

    [Fact]
    public async Task PrimaryExit0AuthPromptThenFallbackSucceeds_PrimaryIsBenchedAndAlerted()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codexAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };

        // Exit-0 auth prompt with no tree changes: the resolver should detect
        // the login prompt before verification and advance to the fallback
        // candidate without retrying the unauthenticated primary.
        claudeAgent.AgenticConflictResults.Enqueue(new AgentResult(true, "ok", transcript, null));
        codexAgent.ConflictResolutionPlan.Enqueue(files =>
        {
            Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "main branch change\nwork branch change\n",
            };
        });

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        // A corroborating in-VM smoke gate: the forced probe confirms the
        // stdout-only auth evidence, so the fleet-wide bench is authorised.
        // Without corroboration the bench fails CLOSED (covered separately).
        var smokeGate = new AuthCorroboratingInVmSmokeGate();
        smokeGate.AttachAuthRegistry(availability, webhooks);
        using var fix = BuildFixture(
            seed,
            [claudeAgent, codexAgent],
            new CapturingAuditReportStore(),
            webhooks,
            availability,
            new AgenticConflictResolver(
                new AgenticConflictResolverOptionsSnapshot(
                    new AgenticConflictResolverOptions
                    {
                        MaxIterations = 2,
                        MaxAttemptsPerAgent = 1,
                    })),
            inVmSmokeGate: smokeGate);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Single(claudeAgent.AgenticConflictInvocations);
        Assert.Single(codexAgent.AgenticConflictInvocations);

        var availabilityVerdict = availability.GetAvailability(AgentKind.Claude);
        Assert.False(availabilityVerdict.Available);
        Assert.Contains("auth required from agent output", availabilityVerdict.Reason);
        // Phase attribution rides the availability reason (the pipeline's
        // PublishSideEffectsAsync overwrites the auth-required reason with the
        // phase-scoped detail after corroboration).
        Assert.Contains("rebase-resolver", availabilityVerdict.Reason);
        Assert.True(availability.GetAvailability(AgentKind.Codex).Available);

        // The corroborating in-VM probe publishes the smoke_failed webhook for
        // the benched primary; its later pipeline-side republish is deduped
        // because the agent is already excluded.
        var failed = Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("claude", details.AgentKind);
    }

    [Fact]
    public async Task PickupRebaseResolverAuthPromptOnEveryCandidate_BenchesAndAlertsEveryCandidate()
    {
        // Both candidates emit exit-0 auth prompts. The resolver records an
        // AgenticConflictResolverAuthFailureEvidence entry per candidate, then
        // HandleAgenticResolverAuthRequiredOutputAsync MUST publish side
        // effects for EVERY entry before throwing. A regression to
        // throw-on-first-iteration would bench only the first candidate and
        // leave the rest of the class routable in a whole-class outage —
        // exactly the bug the loop rewrite at PipelineRunner.cs:5570 fixes.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        var codexAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Codex };
        claudeAgent.AgenticConflictResults.Enqueue(new AgentResult(true, "ok", transcript, null));
        codexAgent.AgenticConflictResults.Enqueue(new AgentResult(true, "ok", transcript, null));

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        // Corroborating gate authorises the fleet-wide bench for each candidate
        // (the forced probe confirms every candidate's stdout-only auth
        // evidence). This keeps the publish-per-candidate coverage intact under
        // the fail-closed-unless-corroborated policy.
        var smokeGate = new AuthCorroboratingInVmSmokeGate();
        smokeGate.AttachAuthRegistry(availability, webhooks);
        using var fix = BuildFixture(
            seed,
            [claudeAgent, codexAgent],
            new CapturingAuditReportStore(),
            webhooks,
            availability,
            new AgenticConflictResolver(
                new AgenticConflictResolverOptionsSnapshot(
                    new AgenticConflictResolverOptions
                    {
                        MaxIterations = 4,
                        MaxAttemptsPerAgent = 1,
                    })),
            inVmSmokeGate: smokeGate);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Both candidates were tried (resolver did not short-circuit after the
        // first auth failure).
        Assert.Single(claudeAgent.AgenticConflictInvocations);
        Assert.Single(codexAgent.AgenticConflictInvocations);

        // BOTH agents must be benched under the auth-required source — not
        // just the primary that threw.
        Assert.False(availability.GetAvailability(AgentKind.Claude).Available);
        Assert.False(availability.GetAvailability(AgentKind.Codex).Available);
        Assert.True(availability.GetAuthRequiredAvailability(AgentKind.Claude).AuthRequired);
        Assert.True(availability.GetAuthRequiredAvailability(AgentKind.Codex).AuthRequired);

        // One webhook per benched candidate — single-throw-then-skip would
        // emit only one event for the primary.
        var smokeFails = webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .Select(e => Assert.IsType<AgentSmokeFailedDetails>(e.Details))
            .ToList();
        Assert.Contains(smokeFails, d => d.AgentKind == "claude");
        Assert.Contains(smokeFails, d => d.AgentKind == "codex");

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, final.FailureKind);
    }

    [Fact]
    public async Task PickupRebaseResolverAuthPromptWithoutFallback_FailsAsAuthRequired()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var claudeAgent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        claudeAgent.AgenticConflictResults.Enqueue(new AgentResult(true, "ok", transcript, null));

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        // Corroborating gate authorises the fleet-wide bench for the sole
        // candidate's stdout-only auth evidence.
        var smokeGate = new AuthCorroboratingInVmSmokeGate();
        smokeGate.AttachAuthRegistry(availability, webhooks);
        using var fix = BuildFixture(
            seed,
            [claudeAgent],
            new CapturingAuditReportStore(),
            webhooks,
            availability,
            new AgenticConflictResolver(
                new AgenticConflictResolverOptionsSnapshot(
                    new AgenticConflictResolverOptions { MaxIterations = 3 })),
            inVmSmokeGate: smokeGate);

        var item = NewItem(AgentKind.Claude) with { State = WorkItemState.WorkComplete };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "work branch change\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main readme");

        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, final.FailureKind);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Contains("rebase-resolver", final.LastError);
        Assert.Single(claudeAgent.AgenticConflictInvocations);
        Assert.False(availability.GetAvailability(AgentKind.Claude).Available);
        Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private CascadeFixture BuildFixture(
        string seedRepoUrl,
        IReadOnlyList<IAgentRunner> agents,
        IAuditReportStore auditReportStore,
        IWebhookDispatcher? webhooks = null,
        AgentAvailabilityRegistry? availability = null,
        AgenticConflictResolver? agenticConflictResolver = null,
        IInVmSmokeGate? inVmSmokeGate = null)
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
        var webhookDispatcher = webhooks ?? new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhookDispatcher, projects);
        var authAvailability = availability
            ?? new AgentAvailabilityRegistry(
                new AvailabilityOptions(),
                TimeProvider.System,
                NullLogger<AgentAvailabilityRegistry>.Instance);

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
            webhookDispatcher,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            auditReports: auditReportStore,
            availability: availability,
            authAvailability: authAvailability,
            agenticConflictResolver: agenticConflictResolver,
            inVmSmokeGate: inVmSmokeGate,
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
