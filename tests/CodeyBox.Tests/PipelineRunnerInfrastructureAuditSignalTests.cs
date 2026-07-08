using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class PipelineRunnerInfrastructureAuditSignalTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestSink _sink = new();

    public PipelineRunnerInfrastructureAuditSignalTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-infraaudit-").FullName;
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
    public async Task InfrastructureWorkFailure_EmitsSandboxInfraAuditSignalFromPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'codex': No such file or directory"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "infra audit signal",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Codex,
            PushUpstream = false,
        };
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        var failed = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
        Assert.Equal(AgentKind.Codex, failed.Agent);
        var snap = fix.Registry.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Codex);
        Assert.True(snap is null || snap.ConsecutiveFastFails == 0);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        var evt = Assert.Single(
            _sink.Events,
            e => GetScalar<string>(e, "EventName") == "sandbox.agent_infra_failure");
        Assert.Equal(item.Id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("codex", GetScalar<string>(evt, "Agent"));
        Assert.Equal("work", GetScalar<string>(evt, "Phase"));
        Assert.Equal("agent exited 127", GetScalar<string>(evt, "Summary"));
        Assert.Equal("agent binary was not found in the sandbox", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public async Task InfrastructureMergeFailure_EmitsSandboxInfraAuditSignalFromPipeline_WithMergePhase()
    {
        // Companion to the work-phase signal test above: the merge-phase
        // RecordAvailabilityOutcomeAsync call site must surface its own
        // sandbox.agent_infra_failure audit event tagged with Phase=merge.
        // A regression that tied the phase argument to the work-phase literal
        // (or dropped the merge call entirely) would silently lose the merge
        // infra signal, leaving sandbox/provisioning incidents on the merge
        // path invisible to dashboards.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // A clean merge runs host-side with no agent, so the merge-phase infra
        // signal only fires on the agentic conflict-resolver path. Induce a real
        // README conflict so the resolver agent runs and returns a missing-binary
        // (exit-127) failure. The work phase is already complete (AuditPassed),
        // so the single ScriptedFailures entry is consumed by the merge resolver.
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'codex': No such file or directory"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "merge infra audit signal",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Codex,
            State = WorkItemState.AuditPassed,
            WorkBranch = "feature/infra-merge-" + Guid.NewGuid().ToString("N")[..8],
            PushUpstream = false,
        };
        var repoId = await fix.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = fix.GitHost.GetRepoPath(repoId);
        await CommitWorkBranchAsync(barePath, item.WorkBranch!, "README.md", "work side\n", "work readme");
        await CommitToSeedAsync(seed, "README.md", "main side\n", "main readme");
        await fix.Store.CreateAsync(item);

        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        // Registry untouched — infra filter must also fire on the merge path.
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        var failed = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, failed!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
        Assert.Equal(AgentKind.Codex, failed.Agent);
        var snap = fix.Registry.Snapshot().SingleOrDefault(s => s.Agent == AgentKind.Codex);
        Assert.True(snap is null || snap.ConsecutiveFastFails == 0);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        // The merge-phase infra event is the assertion this test exists for.
        // Filter on Phase=merge specifically: any work-phase event in the sink
        // (none expected here, but defended) must not satisfy the assertion.
        var evt = Assert.Single(
            _sink.Events,
            e => GetScalar<string>(e, "EventName") == "sandbox.agent_infra_failure"
                 && GetScalar<string>(e, "Phase") == "merge");
        Assert.Equal(item.Id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("codex", GetScalar<string>(evt, "Agent"));
        Assert.Equal("agent binary was not found in the sandbox", GetScalar<string>(evt, "Reason"));
    }

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
        var registry = new AgentRegistry([codex]);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions
            {
                FastFailThresholdSeconds = 10,
                MaxConsecutiveFastFails = 3,
            },
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            }),
            availability: availability,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestFixture(pipeline, store, codex, webhooks, availability, gitHost);
    }

    private static async Task CommitWorkBranchAsync(
        string bareRepoPath, string workBranch, string path, string contents, string subject)
    {
        var clone = Path.Combine(Path.GetTempPath(), "codeybox-infra-merge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(clone);
        try
        {
            await TestSupport.RunGit(clone, "clone", bareRepoPath, ".");
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-b", workBranch, "origin/main");
            await File.WriteAllTextAsync(Path.Combine(clone, path), contents);
            await TestSupport.RunGit(clone, "add", path);
            await TestSupport.RunGit(clone, "commit", "-m", subject);
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{workBranch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
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
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private sealed class TestFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Codex { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public AgentAvailabilityRegistry Registry { get; }
        public LocalGitHost GitHost { get; }

        public TestFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent codex,
            CapturingWebhookDispatcher webhooks,
            AgentAvailabilityRegistry registry,
            LocalGitHost gitHost)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Webhooks = webhooks;
            Registry = registry;
            GitHost = gitHost;
        }

        public void Dispose() => Store.Dispose();
    }
}
