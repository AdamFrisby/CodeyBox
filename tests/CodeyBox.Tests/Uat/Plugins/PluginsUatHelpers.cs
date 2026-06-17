using CodeyBox.Audit.Presets;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Tests;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.Plugins;

internal static class PluginsUatHelpers
{
    public static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    public static PluginLoader Loader(PluginOptions options, IConfiguration? config = null)
        => new(options, config ?? EmptyConfig(), NullLogger<PluginLoader>.Instance);

    public static WorkItem NewItem(string workBranch = "feature/plugin-uat") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("plugin-uat-project"),
        Title = "plugins UAT",
        Prompt = "exercise plugin UAT",
        Agent = AgentKind.Claude,
        WorkBranch = workBranch,
    };

    public static PluginPipelineContext BuildPluginAuditPipeline(
        string workspace,
        string seedRepoUrl,
        IAuditor pluginAuditor,
        ProjectAudit audit,
        IAuditReportStore? auditReportStore = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("plugin-uat-project"),
            DisplayName = "Plugin UAT Project",
            RepositoryUrl = seedRepoUrl,
            DefaultAgent = AgentKind.Claude,
            Upstream = ProjectUpstream.Noop,
            Audit = audit,
        });
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [pluginAuditor],
            NullLogger<ProjectAuditorComposer>.Instance);
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            registry,
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            composer,
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditReports: auditReportStore,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new PluginPipelineContext(pipeline, store, agent, gitHost, gitRoot);
    }

    public static UpstreamRemoteFactory UpstreamFactory(
        IEnumerable<IUpstreamRemote> pluginRemotes,
        ILogger<UpstreamRemoteFactory>? logger = null)
        => new(
            gitHost: new FakeGitHost(),
            httpClientFactory: new FakeHttpClientFactory(new FakeHttpMessageHandler()),
            githubLog: NullLogger<GitHubUpstreamRemote>.Instance,
            sandboxes: null!,
            agents: null!,
            credentials: null!,
            generatorLog: NullLogger<LlmPullRequestDescriptionGenerator>.Instance,
            pluginRemotes: pluginRemotes,
            factoryLog: logger ?? NullLogger<UpstreamRemoteFactory>.Instance);
}

internal sealed class PluginPipelineContext : IDisposable
{
    public PluginPipelineContext(
        PipelineRunner pipeline,
        SqliteWorkItemStore store,
        ScriptedAgent agent,
        LocalGitHost gitHost,
        string gitRoot)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        GitHost = gitHost;
        GitRoot = gitRoot;
    }

    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public LocalGitHost GitHost { get; }
    public string GitRoot { get; }

    public void Dispose() => Store.Dispose();
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

internal sealed class CapturingAuditReportStore : IAuditReportStore
{
    public List<AuditReport> Reports { get; } = [];

    public Task CreateAsync(AuditReport report, CancellationToken ct = default)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditReport>>(
            Reports.Where(r => r.WorkItemId == workItemId)
                .OrderBy(r => r.Iteration)
                .ThenBy(r => r.AuditorName, StringComparer.Ordinal)
                .ToList());

    public Task<string?> GetRawOutputAsync(
        string workItemId,
        int iteration,
        string auditorName,
        CancellationToken ct = default)
        => Task.FromResult(Reports.FirstOrDefault(r =>
            r.WorkItemId == workItemId &&
            r.Iteration == iteration &&
            r.AuditorName == auditorName)?.RawOutput);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult(0);
}
