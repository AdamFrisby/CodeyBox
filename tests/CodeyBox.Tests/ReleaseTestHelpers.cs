using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Shared helpers for release-related tests. Provides stub implementations
/// for the heavy infrastructure (sandbox, git host, agents) that is never
/// invoked by pure state-machine tests.
/// </summary>
internal static class ReleaseTestHelper
{
    public static Project EnabledProject(string id = "test-project") => new()
    {
        Id = new ProjectId(id),
        DisplayName = "Test",
        RepositoryUrl = "file:///tmp/noop",
        ReleaseConfig = new ProjectReleaseConfig
        {
            Enabled = true,
            AutoSyncMainInterval = null,
        },
    };

    public static Project EnabledProjectWithDeepAuditors(
        string auditorName,
        int maxIterations = 3,
        string id = "test-project") => new()
        {
            Id = new ProjectId(id),
            DisplayName = "Test",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                AutoSyncMainInterval = null,
                DeepAuditors = [auditorName],
                DeepAuditMaxIterations = maxIterations,
            },
        };

    public static ReleaseService BuildService(
        IReleaseStore releaseStore,
        IWorkItemStore workItemStore,
        IProjectRepository projects,
        IWebhookDispatcher webhooks,
        IEnumerable<IDeepAuditor>? deepAuditors = null,
        ITaskQueue? taskQueue = null,
        ISandboxProvider? sandboxes = null,
        IGitHost? gitHost = null,
        IUpstreamRemoteFactory? upstreamFactory = null,
        IChangelogGenerator? changelog = null,
        IAgentRegistry? agents = null,
        IAgentStreamStore? agentStreams = null,
        PipelineOptions? pipelineOptions = null)
    {
        return new ReleaseService(
            releaseStore,
            workItemStore,
            projects,
            webhooks,
            sandboxes ?? new NullSandboxProvider(),
            gitHost ?? new NullGitHost(),
            agents ?? new EmptyAgentRegistry(),
            new StaticCredentialProvider(),
            upstreamFactory ?? new TestUpstreamFactory(),
            deepAuditors ?? [],
            changelog ?? new NullChangelogGenerator(),
            pipelineOptions ?? new PipelineOptions { SandboxImageReference = "none", AgentAllowedHosts = [] },
            taskQueue ?? new InMemoryTaskQueue(),
            new NullHostApplicationLifetime(),
            NullLogger<ReleaseService>.Instance,
            agentStreams);
    }

    public static Release SeedRelease(
        ReleaseState state,
        string projectId = "test-project",
        string? failedReason = null,
        string? branchName = null) => new()
        {
            Id = ReleaseId.New(),
            ProjectId = new ProjectId(projectId),
            Name = $"v1.0-{Guid.NewGuid():N}",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            FailedReason = failedReason,
            BranchName = branchName,
        };
}

internal sealed class NullHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

internal sealed class NullSandboxProvider : ISandboxProvider
{
    public string Name => "null";
    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => throw new NotSupportedException("NullSandboxProvider does not support CreateAsync");
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class NullGitHost : IGitHost
{
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}

internal sealed class EmptyAgentRegistry : IAgentRegistry
{
    public bool TryGet(AgentKind kind, out IAgentRunner runner)
    {
        runner = null!;
        return false;
    }
    public IReadOnlyCollection<AgentKind> Available => [];
}

/// <summary>Fake sandbox that returns a pre-configured output for exec calls.</summary>
internal sealed class ScriptedSandbox : ISandbox
{
    private readonly Queue<SandboxExecResult> _results;

    public string Id => "scripted";

    public ScriptedSandbox(params SandboxExecResult[] results)
    {
        _results = new Queue<SandboxExecResult>(results);
    }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        var r = _results.Count > 0
            ? _results.Dequeue()
            : new SandboxExecResult(0, "", "");
        return Task.FromResult(r);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Configurable fake upstream remote for merge tests.</summary>
internal sealed class FakeMergeUpstreamRemote : IUpstreamRemote
{
    public string Name => "fake-merge";
    public bool MergeResult { get; set; } = true;
    public List<(string Target, string Source)> MergeAttempts { get; } = [];

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true });

    public Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        MergeAttempts.Add((targetBranch, sourceBranch));
        return Task.FromResult(MergeResult);
    }
}

internal sealed class FakeMergeUpstreamFactory : IUpstreamRemoteFactory
{
    private readonly FakeMergeUpstreamRemote _remote;
    public FakeMergeUpstreamFactory(FakeMergeUpstreamRemote remote) => _remote = remote;
    public IUpstreamRemote Create(Project project) => _remote;
}

/// <summary>Stub git host that returns predictable values without touching the filesystem.</summary>
internal sealed class StubGitHost : IGitHost
{
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult($"stub-repo-{id}");
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => Task.CompletedTask;
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}

/// <summary>Upstream remote that records CreateTagAndReleaseAsync calls and completes successfully.</summary>
internal sealed class CapturingUpstreamRemote : IUpstreamRemote
{
    public string Name => "capturing";
    public List<(string Tag, string Sha, string? Notes)> TagAndReleaseRequests { get; } = [];
    public List<UpstreamCompletionRequest> CompletionRequests { get; } = [];

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest req, CancellationToken ct = default)
    {
        CompletionRequests.Add(req);
        return Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true, MergedSha = "abc123" });
    }

    public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<string?> CreateTagAndReleaseAsync(string tagName, string sha, string? releaseNotes, CancellationToken ct = default)
    {
        TagAndReleaseRequests.Add((tagName, sha, releaseNotes));
        return Task.FromResult<string?>("https://github.com/example/repo/releases/tag/" + tagName);
    }
}

/// <summary>Upstream factory that always returns the same pre-built remote.</summary>
internal sealed class FixedUpstreamFactory : IUpstreamRemoteFactory
{
    private readonly IUpstreamRemote _remote;
    public FixedUpstreamFactory(IUpstreamRemote remote) => _remote = remote;
    public IUpstreamRemote Create(Project project) => _remote;
}

/// <summary>
/// Deep auditor that returns pre-scripted results in order (one per RunAsync call).
/// When the queue is exhausted returns a passing result. Used to simulate convergence
/// scenarios without real LLM or sandbox dependencies.
/// </summary>
internal sealed class ScriptedDeepAuditor : IDeepAuditor
{
    private readonly Queue<AuditResult> _results;
    private readonly AuditCapabilities _required;

    public string Name { get; }
    public string Kind => "test";
    public AuditCapabilities Required => _required;
    public List<DeepAuditContext> Contexts { get; } = [];

    public ScriptedDeepAuditor(string name, params AuditResult[] results)
        : this(name, AuditCapabilities.None, results)
    {
    }

    public ScriptedDeepAuditor(string name, AuditCapabilities required, params AuditResult[] results)
    {
        Name = name;
        _required = required;
        _results = new Queue<AuditResult>(results);
    }

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        Contexts.Add(context);
        var result = _results.Count > 0 ? _results.Dequeue() : new AuditResult(true, []);
        return Task.FromResult(result);
    }
}

/// <summary>
/// Task queue that immediately marks enqueued work items as Done in the store.
/// Eliminates the 10-second polling delay in WaitForWorkItemTerminalAsync during
/// deep-audit loop tests.
/// </summary>
internal sealed class AutoCompleteTaskQueue : ITaskQueue
{
    private readonly IWorkItemStore _store;
    public AutoCompleteTaskQueue(IWorkItemStore store) => _store = store;

    public int Count => 0;

    public async ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is not null)
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }

    public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        => ValueTask.FromResult<WorkItemId?>(null);
}

/// <summary>
/// Git host stub that returns minimal valid values for all methods used by
/// RunDeepAuditIterationAsync and TransitionReleasedAsync, without touching
/// the real filesystem.
/// </summary>
internal sealed class DeepAuditTestGitHost : IGitHost
{
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult($"stub-repo-{id}");
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => new(
            CloneUrlInsideSandbox: "file:///dev/null",
            Mounts: [],
            Network: SandboxNetworkPolicy.Denied);

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}

/// <summary>
/// Sandbox that always returns exit code 0 for any command. Used so git clone/
/// checkout calls in RunDeepAuditIterationAsync succeed without a real repo.
/// </summary>
internal sealed class AlwaysSucceedSandbox : ISandbox
{
    public string Id => "always-succeed";

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => Task.FromResult(new SandboxExecResult(0, "", ""));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Sandbox provider that always returns an AlwaysSucceedSandbox.</summary>
internal sealed class AlwaysSucceedSandboxProvider : ISandboxProvider
{
    public string Name => "always-succeed";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => Task.FromResult<ISandbox>(new AlwaysSucceedSandbox());
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>No-op changelog generator for tests that don't exercise release note generation.</summary>
internal sealed class NullChangelogGenerator : IChangelogGenerator
{
    public Task<ChangelogEntry> GenerateAsync(ChangelogRequest request, CancellationToken ct)
        => Task.FromResult(new ChangelogEntry
        {
            ToTag = request.ToTag,
            Markdown = string.Empty,
            CategoryToPrNumbers = new Dictionary<string, IReadOnlyList<int>>(),
        });
}
