using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Tests;
using CodeyBox.Upstream.GitHub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

internal static class UpstreamWebhooksAndReleasesHelpers
{
    public static UpstreamCompletionRequest Request(
        string repositoryId = "repo-id",
        string workBranch = "feature/uat-upstream",
        string baseBranch = "main") => new()
        {
            RepositoryId = repositoryId,
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("uat-upstream-project"),
            WorkBranch = workBranch,
            BaseBranch = baseBranch,
            MergeSha = "0123456789abcdef",
            Title = "UAT upstream change",
            Description = "Static PR body from CodeyBox",
            DiffStat = "src/App.cs | 2 ++",
            FullDiff = "diff --git a/src/App.cs b/src/App.cs\n+changed",
            AddressedFindings = ["Fix deterministic UAT finding"],
            WorkItemPrompt = "Make the upstream UAT change.",
            AgentStdout = "Agent summary tail.",
        };

    public static GitHubUpstreamRemote GitHubRemote(
        IGitHost gitHost,
        SequenceHttpMessageHandler handler,
        GitHubUpstreamOptions? options = null,
        IPullRequestDescriptionGenerator? descriptionGenerator = null)
    {
        var factory = new NamedHttpClientFactory("github-upstream", handler, "codeybox-uat");
        return new GitHubUpstreamRemote(
            gitHost,
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            options ?? GitHubOptions(),
            descriptionGenerator: descriptionGenerator);
    }

    public static GitHubUpstreamOptions GitHubOptions() => new()
    {
        Owner = "owner",
        Repository = "repo",
        Token = "uat-token-not-real",
        MergeMethod = "merge",
        AutoMerge = false,
    };

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string ComputeGitHubSignature(byte[] body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task ConfigureIdentityAsync(string repoPath)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "uat@example.invalid");
        await TestSupport.RunGit(repoPath, "config", "user.name", "UAT");
    }

    public static async Task CommitToBareBranchAsync(
        string workspace,
        string bareRepoPath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(workspace, "commit-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(workspace, "clone", bareRepoPath, clone);
        await ConfigureIdentityAsync(clone);
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }
}

internal sealed class CapturingGitHost : IGitHost
{
    public List<UpstreamPushCall> Pushes { get; } = [];
    public Exception? PushException { get; init; }

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
    {
        if (PushException is not null)
            throw PushException;

        Pushes.Add(new UpstreamPushCall(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy));
        return Task.CompletedTask;
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId,
        string baseBranch,
        string workBranch,
        CancellationToken ct = default)
        => Task.FromResult(("src/App.cs | 1 +", "diff --git a/src/App.cs b/src/App.cs\n+changed"));
}

internal sealed record UpstreamPushCall(
    string RepositoryId,
    string UpstreamUrl,
    string Branch,
    IReadOnlyDictionary<string, string> Environment,
    UpstreamPushReconcileStrategy ReconcileStrategy);

internal sealed class SequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var (name, values) in request.Headers)
            snapshot.Headers.TryAddWithoutValidation(name, values);
        Requests.Add(snapshot);

        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return _responses.Count == 0
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : _responses.Dequeue();
    }
}

internal sealed class NamedHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly string _expectedName;
    private readonly HttpMessageHandler _handler;
    private readonly string? _userAgent;

    public NamedHttpClientFactory(string expectedName, HttpMessageHandler handler, string? userAgent = null)
    {
        _expectedName = expectedName;
        _handler = handler;
        _userAgent = userAgent;
    }

    public HttpClient CreateClient(string name)
    {
        Assert.Equal(_expectedName, name);
        var client = new HttpClient(_handler, disposeHandler: false);
        if (!string.IsNullOrWhiteSpace(_userAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        return client;
    }

    public void Dispose() => _handler.Dispose();
}

internal sealed class StubPrDescriptionGenerator : IPullRequestDescriptionGenerator
{
    private readonly Func<PullRequestDescriptionRequest, CancellationToken, Task<string>> _generate;

    public StubPrDescriptionGenerator(Func<PullRequestDescriptionRequest, CancellationToken, Task<string>> generate)
        => _generate = generate;

    public List<PullRequestDescriptionRequest> Requests { get; } = [];

    public async Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return await _generate(request, ct);
    }
}

internal sealed class CapturingAgentRunner : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public string? Prompt { get; private set; }
    public AgentCredential? Credential { get; private set; }

    public Task<AgentResult> RunAsync(
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
        Prompt = prompt;
        Credential = credential;
        return Task.FromResult(new AgentResult(true, "generated", "Generated PR body", null));
    }
}

internal sealed class NullSandbox : ISandbox
{
    public string Id => "uat-null";
    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        => Task.FromResult(new SandboxExecResult(0, "", ""));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NullSandboxProvider : ISandboxProvider
{
    public string Name => "uat-null";
    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        => Task.FromResult<ISandbox>(new NullSandbox());
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class UatChangelogApiFactory : CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath;
    private readonly Project _project;

    public UatChangelogApiFactory(Project project)
    {
        _project = project;
        _dbPath = TempDatabasePath("codeybox-uat-changelog");
        WorkItems = new SqliteWorkItemStore(_dbPath);
        PullRequests = new CapturingPullRequestEnumerator();
        Generator = new CapturingChangelogGenerator();
        Queue = new CapturingTaskQueue();
    }

    public SqliteWorkItemStore WorkItems { get; }
    public CapturingPullRequestEnumerator PullRequests { get; }
    public CapturingChangelogGenerator Generator { get; }
    public CapturingTaskQueue Queue { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Temp.NewDirectoryPath("test-git-"),
                ["CodeyBox:AuditLog:Path"] = Temp.NewLogPath("test-log"),
                ["CodeyBox:AuditLog:AuditPath"] = Temp.NewLogPath("test-audit"),
                ["CodeyBox:Changelog:Enabled"] = "true",
                ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = "UAT_CHANGELOG_WEBHOOK_SECRET",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItems);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(_project));

            services.RemoveAll<IPullRequestEnumerator>();
            services.AddSingleton<IPullRequestEnumerator>(PullRequests);

            services.RemoveAll<IChangelogGenerator>();
            services.AddSingleton<IChangelogGenerator>(Generator);

            services.RemoveAll<ITaskQueue>();
            services.AddSingleton<ITaskQueue>(Queue);
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, WorkItems.Dispose);
}

internal sealed class CapturingPullRequestEnumerator : IPullRequestEnumerator
{
    public bool WasCapped { get; set; }
    public string? PreviousTag { get; set; } = "v1.0.0";
    public List<(string Owner, string Repo, string FromTag, string ToTag)> Calls { get; } = [];

    public Task<PullRequestEnumeratorResult> ListMergedBetweenAsync(
        string owner,
        string repo,
        string token,
        string fromTag,
        string toTag,
        CancellationToken ct)
    {
        Calls.Add((owner, repo, fromTag, toTag));
        return Task.FromResult(new PullRequestEnumeratorResult(
            [new MergedPullRequest(42, "Ship release item", "body", "2026-05-01T00:00:00Z", [], [])],
            WasCapped));
    }

    public Task<string?> ResolvePreviousTagAsync(
        string owner,
        string repo,
        string token,
        string currentTag,
        CancellationToken ct)
        => Task.FromResult(PreviousTag);
}

internal sealed class CapturingChangelogGenerator : IChangelogGenerator
{
    public List<ChangelogRequest> Requests { get; } = [];

    public Task<ChangelogEntry> GenerateAsync(ChangelogRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(new ChangelogEntry
        {
            ToTag = request.ToTag,
            Markdown = $"## [{request.ToTag}] - 2026-05-02\n\n### Added\n- Ship release item ([#42])\n",
            CategoryToPrNumbers = new Dictionary<string, IReadOnlyList<int>>
            {
                ["Added"] = [42],
            },
        });
    }
}

internal sealed class CapturingTaskQueue : ITaskQueue
{
    public List<WorkItemId> Enqueued { get; } = [];
    public int Count => Enqueued.Count;

    public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
    {
        Enqueued.Add(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        => ValueTask.FromResult<WorkItemId?>(Enqueued.Count == 0 ? null : Enqueued[0]);

    public ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Enqueued.Count > 0);
}

internal sealed class RecordingSyncRemote : IUpstreamRemote
{
    private readonly Queue<Func<Task<bool>>> _mergePlan = new();

    public string Name => "uat-sync";
    public List<(string TargetBranch, string SourceBranch)> MergeAttempts { get; } = [];

    public void EnqueueMerge(bool result) => _mergePlan.Enqueue(() => Task.FromResult(result));
    public void EnqueueMergeException(Exception ex) => _mergePlan.Enqueue(() => Task.FromException<bool>(ex));

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true, MergedSha = request.MergeSha });

    public async Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        MergeAttempts.Add((targetBranch, sourceBranch));
        return _mergePlan.Count == 0 || await _mergePlan.Dequeue()();
    }
}

internal sealed class FixedUpstreamFactory(IUpstreamRemote remote) : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project) => remote;
}
