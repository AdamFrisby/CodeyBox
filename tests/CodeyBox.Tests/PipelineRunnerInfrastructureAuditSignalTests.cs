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
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        return new TestFixture(pipeline, store, codex, webhooks, availability);
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

        public TestFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent codex,
            CapturingWebhookDispatcher webhooks,
            AgentAvailabilityRegistry registry)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Webhooks = webhooks;
            Registry = registry;
        }

        public void Dispose() => Store.Dispose();
    }
}
