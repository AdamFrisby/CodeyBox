using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests for in-iteration quota fallback inside the work phase
/// of <see cref="PipelineRunner"/>. The pipeline picks Codex first, Codex
/// returns a quota-shaped failure mid-iteration, and the wrapper retries the
/// same iteration against the next class member (Claude) without leaving the
/// item Failed. The 3-member exhaustion case parks the item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/>.
/// </summary>
public sealed class PipelineRunnerQuotaFallbackTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerQuotaFallbackTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-fallback-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task Codex_HitsQuota_FallsBackToClaude_SameIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // Codex returns quota-shaped failure on its first call; pipeline must
        // swap to Claude for the same iteration.
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "API Error: rate_limit_exceeded; please try again after 1h"));
        fix.Claude.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Codex was tried for the work phase and failed; Claude succeeded the
        // retry. Codex may also be invoked for the merge phase (work agent
        // resolution there is a separate concern — see suggestions.json), so
        // assert at-least semantics for Codex and exact for Claude.
        Assert.True(fix.Codex.CallCount >= 1, $"expected codex to be called at least once, was {fix.Codex.CallCount}");
        Assert.Equal(1, fix.Claude.CallCount);

        // Item ended up in the merged → Done flow (work phase didn't fail).
        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.NotEqual(WorkItemState.Failed, finalItem!.State);
        Assert.NotEqual(WorkItemState.WaitingForQuotaReset, finalItem.State);

        // Audit + webhook event captured.
        Assert.Contains(fix.Webhooks.Events, e => e.Event == "agent.fallback");
        var fallback = fix.Webhooks.Events.First(e => e.Event == "agent.fallback");
        var details = Assert.IsType<AgentFallbackDetails>(fallback.Details);
        Assert.Equal("codex", details.FromAgent);
        Assert.Equal("claude", details.ToAgent);
        Assert.Equal("work", details.Phase);
    }

    [Fact]
    public async Task BothMembers_Exhausted_ParksInWaitingForQuotaReset()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        var quotaErr = new AgentResult(false, "agent exited 1", null,
            "API Error: rate_limit_exceeded");
        fix.Codex.ScriptedFailures.Enqueue(quotaErr);
        fix.Claude.ScriptedFailures.Enqueue(quotaErr);

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, finalItem!.State);
        Assert.Equal("quota", finalItem.FailureKind);

        // Both members tried in this single pickup; AllExhausted audit emitted.
        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(1, fix.Claude.CallCount);

        // Both probes received the MarkExhaustedAsync write-back.
        Assert.Contains(fix.CodexProbe.MarkedExhausted, k => k == AgentKind.Codex);
        Assert.Contains(fix.ClaudeProbe.MarkedExhausted, k => k == AgentKind.Claude);
    }

    [Fact]
    public async Task NormalFailure_DoesNotTriggerFallback()
    {
        // Sanity / contrast: a non-quota failure must NOT fall back. The work
        // item fails as Failed/other (the legacy path) — burning Claude's quota
        // on a task Codex couldn't write would be wasted compute.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: "compile error: unexpected token"));

        var item = NewItem(initialAgent: AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(1, fix.Codex.CallCount);
        Assert.Equal(0, fix.Claude.CallCount);

        var finalItem = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemState.Failed, finalItem!.State);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private TestFixture BuildPipeline(string seedRepoUrl)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var codex = new ScriptableAgent(AgentKind.Codex);
        var claude = new ScriptableAgent(AgentKind.Claude);
        var registry = new AgentRegistry([codex, claude]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                // Codex first by config-order tiebreak (same effective score).
                new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var codexProbe = new RecordingProbe(AgentKind.Codex);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var router = new AgentClassRouter(
            [frontier],
            [codexProbe, claudeProbe],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [codexProbe, claudeProbe],
            classRouter: router);

        return new TestFixture(pipeline, store, codex, claude, codexProbe, claudeProbe, webhooks);
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "fallback test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        AgentClassId = "frontier",
        PushUpstream = false,
    };

    private sealed class TestFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Codex { get; }
        public ScriptableAgent Claude { get; }
        public RecordingProbe CodexProbe { get; }
        public RecordingProbe ClaudeProbe { get; }
        public CapturingWebhookDispatcher Webhooks { get; }

        public TestFixture(PipelineRunner pipeline, SqliteWorkItemStore store,
            ScriptableAgent codex, ScriptableAgent claude,
            RecordingProbe codexProbe, RecordingProbe claudeProbe,
            CapturingWebhookDispatcher webhooks)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Claude = claude;
            CodexProbe = codexProbe;
            ClaudeProbe = claudeProbe;
            Webhooks = webhooks;
        }

        public void Dispose() => Store.Dispose();
    }
}

/// <summary>
/// Test agent that returns scripted failures from <see cref="ScriptedFailures"/>
/// before falling through to a real file-write success — so we can exercise
/// the quota-fallback wrapper without standing up a full ScriptedAgent.
/// </summary>
internal sealed class ScriptableAgent : IAgentRunner, ITextOnlyAgentRunner
{
    public Queue<AgentResult> ScriptedFailures { get; } = new();
    public Queue<FileWrite> WorkPlan { get; } = new();
    public int CallCount { get; private set; }

    public AgentKind Kind { get; }

    public ScriptableAgent(AgentKind kind) { Kind = kind; }

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        CallCount++;

        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
        {
            // Run a real git merge inside the sandbox so the merge phase passes.
            var workBranchEnd = prompt.IndexOf("` into branch", StringComparison.Ordinal);
            var workBranchStart = prompt.IndexOf('`') + 1;
            var workBranch = prompt[workBranchStart..workBranchEnd];
            var rc = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "merge", "--no-ff",
                    "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}"],
            }, ct);
            return rc.Success
                ? new AgentResult(true, "merged", null, null)
                : new AgentResult(false, "merge failed", rc.Stdout, rc.Stderr);
        }

        if (ScriptedFailures.Count > 0)
            return ScriptedFailures.Dequeue();

        if (WorkPlan.Count == 0)
            return new AgentResult(false, "ScriptableAgent: no work plan and no scripted failure", null, null);

        var fw = WorkPlan.Dequeue();
        var path = $"{workingDirectory}/{fw.FileName}";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", path],
            Stdin = fw.Contents,
        }, ct);
        return write.Success
            ? new AgentResult(true, "ok", null, null)
            : new AgentResult(false, "write failed", write.Stdout, write.Stderr);
    }

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt, AgentCredential? credential,
        string? modelId = null, string? reasoningMode = null,
        CancellationToken ct = default)
        => Task.FromResult(new TextOnlyAgentResult(false, "not used", null, null));
}

/// <summary>
/// Probe that always reports plenty of quota but records calls to
/// <see cref="MarkExhaustedAsync"/> so tests can assert the pipeline propagated
/// mid-iteration exhaustion to probe-side caches.
/// </summary>
internal sealed class RecordingProbe : IAgentQuotaProbe
{
    public AgentKind Kind { get; }
    public List<AgentKind> MarkedExhausted { get; } = new();

    public RecordingProbe(AgentKind kind) { Kind = kind; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = 80.0 });

    public Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        MarkedExhausted.Add(member.Agent);
        return Task.CompletedTask;
    }
}
