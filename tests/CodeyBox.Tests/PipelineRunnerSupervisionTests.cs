using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class PipelineRunnerSupervisionTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-pipeline-supervision-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkPhase_SupervisionPublishesCommandStreamsAndDrainsQueuedInjection()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var notifier = new AutoQueueInjectionNotifier();
        var supervision = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 },
            notifier);
        notifier.Service = supervision;

        // A clean merge runs host-side with no agent, so there is no merge-phase
        // supervision session. Induce a README conflict (work writes README; the
        // auditor advances main's README during audit) so the merge runs the
        // agentic resolver, which opens a supervised "merge" session.
        var mergeConflictAuditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [mergeConflictAuditor],
            agentSupervision: supervision);
        mergeConflictAuditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "autonomous\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("operator.txt", "human\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "main side\nautonomous\n",
        });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Supervision pipeline test",
            Prompt = "write a file",
            State = WorkItemState.Queued,
            WorkBranch = "feature/supervision-pipeline",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkTimeout = TimeSpan.FromMinutes(5),
            MergeTimeout = TimeSpan.FromMinutes(5),
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains(notifier.StartedSessions, s => s.Phase == "work");
        // The merge supervision session now comes from the agentic conflict
        // resolver (Phase "conflict-merge", Source "agentic-conflict-resolver").
        Assert.Contains(
            notifier.StartedSessions,
            s => s.Phase == "conflict-merge" && s.Source == "agentic-conflict-resolver");
        Assert.Contains(notifier.Commands, c => c.Kind == "autonomous" && c.Phase == "work");
        Assert.Contains(notifier.Commands, c => c.Kind == "human-injection" && c.Phase == "work");
        var completed = Assert.Single(notifier.CompletedInjections);
        Assert.True(completed.Success);
        Assert.Equal("pipeline-test", completed.Actor);

        var page = await supervision.ListSessionsAsync(new AgentSupervisionListQuery());
        var session = Assert.Single(page.Sessions, s => s.Phase == "work");
        Assert.Equal("completed", session.State);
        Assert.Contains(session.RecentCommands, c => c.Kind == "autonomous");
        Assert.Contains(session.RecentCommands, c => c.Kind == "human-injection");
    }

    private sealed class AutoQueueInjectionNotifier : IAgentSupervisionNotifier
    {
        private bool _queued;
        public AgentSupervisionService? Service { get; set; }
        public List<AgentSupervisionSessionSnapshot> StartedSessions { get; } = [];
        public List<AgentSupervisionCommandEvent> Commands { get; } = [];
        public List<AgentSupervisionInjectionCompletedEvent> CompletedInjections { get; } = [];

        public async Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default)
        {
            StartedSessions.Add(session);
            if (_queued || Service is null)
                return;
            _queued = true;
            var receipt = await Service.EnqueueInjectionAsync(
                session.SessionId,
                new AgentSupervisionInjectionRequest("operator follow-up", "pipeline-test"),
                ct);
            Assert.True(receipt.Accepted, receipt.Error);
        }

        public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
        public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default)
        {
            CompletedInjections.Add(injection);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Tool auditor that advances <c>main</c>'s copy of a file during the audit
    /// phase, so a work branch touching the same file merges with a conflict —
    /// routing the merge phase through the agentic conflict resolver, which opens
    /// a supervised "merge" session.
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }
}
