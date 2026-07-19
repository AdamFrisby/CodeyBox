using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Cursor;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the cursor "out of usage" exhaustion case (item
/// <c>7ad83daf</c>): cursor's stderr "You're out of usage. Switch to Auto, or
/// ask your admin to increase your limit to continue." was previously classified
/// as <c>failureKind=other</c> and hard-failed the work item, even when the same
/// agent class held another eligible member that still had quota.
///
/// <para>The fix recognises the stderr signature in
/// <see cref="CursorQuotaFailureDetector"/> as a quota-shaped failure so the
/// existing dispatch wrapper (<c>PipelineRunner.InvokeAgentWithQuotaFallbackAsync</c>)
/// fails the item over to the next eligible class member instead of marking it
/// Failed. When no other eligible member remains, the item parks as
/// <see cref="WorkItemState.WaitingForQuotaReset"/> rather than hard-failing.</para>
/// </summary>
public sealed class PipelineRunnerCursorQuotaFallbackTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerCursorQuotaFallbackTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-cursor-fallback-").FullName;

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

    private const string CursorOutOfUsageStderr =
        "You're out of usage. Switch to Auto, or ask your admin to increase your limit to continue.";

    [Fact]
    public async Task Cursor_OutOfUsageStderr_FailsOverToClaude_NotHardFailure()
    {
        // Given the observed cursor exhaustion stderr and a second eligible
        // class member (Claude) with quota available, the work item must fail
        // over to Claude rather than landing in Failed/other.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildCursorFirstPipeline(seed);

        fix.Cursor.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: CursorOutOfUsageStderr));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Both agents were touched in this iteration: cursor failed quota and
        // claude completed the write. CallCount==1 on claude pins the fallback
        // path actually ran instead of cursor being retried in place.
        Assert.True(fix.Cursor.CallCount >= 1);
        Assert.Equal(1, fix.Claude.CallCount);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);
        // Most importantly: the failure was NOT classified as "other". Without
        // the cursor "out of usage" detector entry this assertion would trip
        // because the pipeline would have parked the item with FailureKind=other.
        Assert.NotEqual("other", finalItem.FailureKind);

        // Cursor's probe received the MarkExhaustedAsync write-back so a
        // follow-up pickup skips it without a fresh probe miss.
        Assert.Contains(fix.CursorProbe.MarkedExhausted, k => k == AgentKind.Cursor);

        var fallback = Assert.Single(fix.Webhooks.Events, e => e.Event == "agent.fallback");
        var details = Assert.IsType<AgentFallbackDetails>(fallback.Details);
        Assert.Equal("cursor", details.FromAgent);
        Assert.Equal("claude", details.ToAgent);
        Assert.Equal("work", details.Phase);
    }

    [Fact]
    public async Task Cursor_OutOfUsageStderr_NoOtherEligibleMember_ParksWaitingForQuotaReset()
    {
        // When cursor is the only eligible member and it reports the same
        // exhaustion stderr, the item must park as WaitingForQuotaReset
        // (failureKind=quota), NOT Failed/other.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildCursorOnlyPipeline(seed);

        fix.Cursor.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: CursorOutOfUsageStderr));

        var item = NewItem(initialAgent: AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, finalItem!.State);
        Assert.Equal("quota", finalItem.FailureKind);

        Assert.Equal(1, fix.Cursor.CallCount);
        Assert.Contains(fix.CursorProbe.MarkedExhausted, k => k == AgentKind.Cursor);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private CursorFallbackFixture BuildCursorFirstPipeline(string seedRepoUrl)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var cursor = new ScriptableAgent(AgentKind.Cursor);
        var claude = new ScriptableAgent(AgentKind.Claude);
        var registry = new AgentRegistry([cursor, claude]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Cursor, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var cursorProbe = new RecordingProbe(AgentKind.Cursor);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [frontier],
            [cursorProbe, claudeProbe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [cursorProbe, claudeProbe],
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new CursorQuotaFailureDetector(),
                new ClaudeQuotaFailureDetector(),
            }),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new CursorFallbackFixture(pipeline, store, cursor, claude, cursorProbe, claudeProbe, webhooks, fallbackHistory);
    }

    private CursorFallbackFixture BuildCursorOnlyPipeline(string seedRepoUrl)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var cursor = new ScriptableAgent(AgentKind.Cursor);
        // Claude is registered so a misclassification that *did* trigger
        // failover would have somewhere to go; the class itself only declares
        // cursor as a member, so the dispatch wrapper should still park.
        var claude = new ScriptableAgent(AgentKind.Claude);
        var registry = new AgentRegistry([cursor, claude]);

        var soloCursor = new AgentClass
        {
            Id = "solo-cursor",
            DisplayName = "Solo cursor",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Cursor, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            DefaultAgentClass = "solo-cursor",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var cursorProbe = new RecordingProbe(AgentKind.Cursor);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [soloCursor],
            [cursorProbe, claudeProbe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [cursorProbe, claudeProbe],
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new CursorQuotaFailureDetector(),
                new ClaudeQuotaFailureDetector(),
            }),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new CursorFallbackFixture(pipeline, store, cursor, claude, cursorProbe, claudeProbe, webhooks, fallbackHistory);
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "cursor fallback test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        AgentClassId = null,  // defer to project's DefaultAgentClass
        PushUpstream = false,
    };

    private sealed class CursorFallbackFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Cursor { get; }
        public ScriptableAgent Claude { get; }
        public RecordingProbe CursorProbe { get; }
        public RecordingProbe ClaudeProbe { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public InMemoryAgentFallbackHistoryStore FallbackHistory { get; }

        public CursorFallbackFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent cursor,
            ScriptableAgent claude,
            RecordingProbe cursorProbe,
            RecordingProbe claudeProbe,
            CapturingWebhookDispatcher webhooks,
            InMemoryAgentFallbackHistoryStore fallbackHistory)
        {
            Pipeline = pipeline;
            Store = store;
            Cursor = cursor;
            Claude = claude;
            CursorProbe = cursorProbe;
            ClaudeProbe = claudeProbe;
            Webhooks = webhooks;
            FallbackHistory = fallbackHistory;
        }

        public void Dispose() => Store.Dispose();
    }
}
