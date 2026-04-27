using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end pipeline tests with the audit phase active. Uses the Process
/// sandbox + a fake agent + scripted auditors. Covers:
///   - audit passes first iteration → straight to merge
///   - audit fails then passes after rework
///   - audit fails max iterations → AuditFailed
///   - rework agent makes no changes → fail fast
/// </summary>
public sealed class AuditPipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;

    public AuditPipelineIntegrationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-audit-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task AuditPasses_FirstIteration_ReachesDone()
    {
        var (item, store, pipeline) = await BuildPipeline(
            agentWritesEachCall: ["initial commit"],
            auditPlan: [new AuditOutcome(Passed: true, Findings: [])]);

        await pipeline.RunAsync(item, CancellationToken.None);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsThenPassesAfterRework_ReachesDone()
    {
        var (item, store, pipeline) = await BuildPipeline(
            agentWritesEachCall: ["v1", "v2-after-rework"],
            auditPlan:
            [
                new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
                new AuditOutcome(true, []),
            ]);

        await pipeline.RunAsync(item, CancellationToken.None);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsAllIterations_ReachesAuditFailed()
    {
        var (item, store, pipeline) = await BuildPipeline(
            agentWritesEachCall: ["v1", "v2", "v3"],
            auditPlan:
            [
                new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
                new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
                new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
            ],
            maxIterations: 3);

        await pipeline.RunAsync(item, CancellationToken.None);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("did not pass after 3 iterations", final.LastError);
    }

    [Fact]
    public async Task ReworkProducesNoChanges_FailsFast()
    {
        var (item, store, pipeline) = await BuildPipeline(
            // Agent writes once on initial, then writes the SAME content on
            // rework — git sees no diff → pipeline must fail fast rather
            // than loop uselessly.
            agentWritesEachCall: ["same-content", "same-content"],
            auditPlan:
            [
                new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "fix me", "x")]),
                new AuditOutcome(true, []),
            ]);

        await pipeline.RunAsync(item, CancellationToken.None);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAuditorsRegistered_SkipsPhaseEntirely()
    {
        var (item, store, pipeline) = await BuildPipeline(
            agentWritesEachCall: ["one"],
            auditPlan: []);

        await pipeline.RunAsync(item, CancellationToken.None);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    /// <summary>
    /// Records the planned outcome of each audit iteration. Index = iteration-1.
    /// </summary>
    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private async Task<(WorkItem item, IWorkItemStore store, PipelineRunner pipeline)> BuildPipeline(
        IReadOnlyList<string> agentWritesEachCall,
        IReadOnlyList<AuditOutcome> auditPlan,
        int maxIterations = 3)
    {
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "t@l");
        await RunGit(seed, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await RunGit(seed, "add", "README.md");
        await RunGit(seed, "commit", "-m", "initial");

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var upstream = new NoopUpstreamRemote();
        var agent = new ScriptedAgent(agentWritesEachCall);
        var registry = new AgentRegistry([agent]);
        var auditor = auditPlan.Count == 0 ? null : new ScriptedAuditor(auditPlan);
        var auditorReg = new AuditorRegistry(auditor is null ? Array.Empty<IAuditor>() : [auditor]);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs, upstream, store,
            auditorReg,
            new AuditOptions { MaxIterations = maxIterations },
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            Title = "audit test",
            Prompt = "do thing",
            RepositoryUrl = seed,
            Agent = AgentKind.Claude,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            PushUpstream = false,
        };
        await store.CreateAsync(item);
        return (item, store, pipeline);
    }

    private sealed class StaticCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default) => Task.FromResult<AgentCredential?>(null);
    }

    private sealed class ScriptedAgent : IAgentRunner
    {
        private readonly IReadOnlyList<string> _writes;
        private int _calls;
        public ScriptedAgent(IReadOnlyList<string> writes) { _writes = writes; }
        public AgentKind Kind => AgentKind.Claude;

        public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential, CancellationToken ct = default)
        {
            var idx = _calls++;
            if (idx >= _writes.Count) throw new InvalidOperationException($"Agent invoked {_calls} times; only {_writes.Count} planned");
            var contents = _writes[idx];
            var path = $"{workingDirectory}/agent-output.txt";
            var r = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = contents,
            }, ct);
            return r.Success ? new AgentResult(true, "ok", null, null) : new AgentResult(false, "fail", r.Stdout, r.Stderr);
        }
    }

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly IReadOnlyList<AuditOutcome> _plan;
        private int _calls;
        public ScriptedAuditor(IReadOnlyList<AuditOutcome> plan) { _plan = plan; }
        public string Name => "Scripted";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_calls >= _plan.Count)
                throw new InvalidOperationException($"Auditor invoked {_calls + 1} times; only {_plan.Count} planned");
            var outcome = _plan[_calls++];
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private static async Task RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {await p.StandardError.ReadToEndAsync()}");
    }
}
