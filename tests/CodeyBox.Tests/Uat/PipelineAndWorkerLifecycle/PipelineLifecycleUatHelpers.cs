using CodeyBox.Audit.Presets;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Tests;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;

internal static class PipelineLifecycleUatHelpers
{
    public static UatPipelineContext BuildPipeline(
        string workspace,
        string seedRepoUrl,
        IEnumerable<IAuditor>? auditors = null,
        int maxAuditIterations = 3,
        ProjectUpstream? upstream = null,
        IUpstreamRemoteFactory? upstreamFactory = null,
        string? defaultBaseBranch = "main")
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var agentRegistry = new AgentRegistry([agent]);
        var auditorList = (auditors ?? []).ToList();
        var auditTypes = auditorList.Count > 0 ? new[] { "uat-scripted" } : [];
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Pipeline lifecycle UAT",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = defaultBaseBranch,
            DefaultAgent = AgentKind.Claude,
            Upstream = upstream ?? ProjectUpstream.Noop,
            Audit = new ProjectAudit
            {
                MaxIterations = maxAuditIterations,
                AuditTypes = auditTypes,
            },
        });
        var composer = new ProjectAuditorComposer(new UatAuditorCatalog(auditorList));
        var webhooks = new CapturingWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            sandboxes,
            gitHost,
            agentRegistry,
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            upstreamFactory ?? new NoopUpstreamFactory(),
            composer,
            store,
            webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new UatPipelineContext(pipeline, store, agent, gitHost, gitRoot, webhooks);
    }

    public static ProjectId TestProjectId { get; } = new("test-project");

    public static WorkItem NewItem(string workBranch, WorkItemState state = WorkItemState.Queued) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "Pipeline lifecycle UAT",
        Prompt = "make the requested change",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
        State = state,
    };

    public static async Task<string> RevParseAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
    }

    public static async Task CommitToBareBranchAsync(
        string workspace,
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(workspace, "bare-edit-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);

        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    public static WorkItem WorkerItem(WorkItemState state, int recoveryAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "worker recovery",
        Prompt = "recover",
        BaseBranch = "main",
        WorkBranch = "feature/recover",
        State = state,
        RecoveryAttempts = recoveryAttempts,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    public static OrchestratorService BuildReplayService(
        IWorkItemStore store,
        ITaskQueue queue,
        int maxRecoveryAttempts = 3)
        => new(
            queue,
            store,
            new RecordingPipelineRunner(),
            new CancellationRegistry(),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MaxRecoveryAttempts = maxRecoveryAttempts,
            },
            NullLogger<OrchestratorService>.Instance);

    private sealed class UatAuditorCatalog : IPresetCatalog
    {
        private readonly IReadOnlyList<IAuditor> _auditors;

        public UatAuditorCatalog(IReadOnlyList<IAuditor> auditors) => _auditors = auditors;

        public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx) => [];
        public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx) => _auditors;
        public IReadOnlyList<string> KnownLanguages => [];
        public IReadOnlyList<string> KnownAuditTypes => _auditors.Count == 0 ? [] : ["uat-scripted"];
        public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}";
        public string LlmPlanPromptFrameTemplate => CodeyBox.Audit.Llm.LlmPromptFrameTemplate.DefaultPlanFrameTemplate;
    }
}

internal sealed class UatPipelineContext : IDisposable
{
    public UatPipelineContext(
        PipelineRunner pipeline,
        SqliteWorkItemStore store,
        ScriptedAgent agent,
        LocalGitHost gitHost,
        string gitRoot,
        CapturingWebhookDispatcher webhooks)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        GitHost = gitHost;
        GitRoot = gitRoot;
        Webhooks = webhooks;
    }

    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public LocalGitHost GitHost { get; }
    public string GitRoot { get; }
    public CapturingWebhookDispatcher Webhooks { get; }

    public void Dispose() => Store.Dispose();
}

internal sealed class PassingAuditor(string name = "uat:pass") : IAuditor
{
    public string Name { get; } = name;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}

internal sealed class ScriptedUatAuditor(IEnumerable<AuditResult> results) : IAuditor
{
    private readonly Queue<AuditResult> _results = new(results);

    public string Name => "uat:scripted";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        if (_results.Count == 0)
            throw new InvalidOperationException("No scripted audit result remains.");

        return Task.FromResult(_results.Dequeue());
    }
}

internal sealed class CapturingUpstreamFactory : IUpstreamRemoteFactory
{
    public CapturingUpstreamRemote Remote { get; } = new();

    public IUpstreamRemote Create(Project project) => Remote;
}

internal sealed class CapturingUpstreamRemote : IUpstreamRemote
{
    public List<UpstreamCompletionRequest> Requests { get; } = [];
    public string Name => "uat-upstream";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = "https://example.invalid/pr/1",
            PullRequestNumber = 1,
            MergedSha = request.MergeSha,
        });
    }

    public Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch,
        string sourceBranch,
        CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class NoopUpstreamFactory : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project) => new NoopUpstreamRemote();
}

internal sealed class RecordingPipelineRunner : IPipelineRunner
{
    public List<WorkItemId> Invocations { get; } = [];

    public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        Invocations.Add(item.Id);
        return Task.CompletedTask;
    }
}
