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

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            agentSupervision: supervision);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "autonomous\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("operator.txt", "human\n"));

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
        Assert.Contains(notifier.StartedSessions, s => s.Phase == "merge");
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
}
