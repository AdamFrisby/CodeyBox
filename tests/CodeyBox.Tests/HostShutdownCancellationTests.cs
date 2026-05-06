using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests that exercise the real <see cref="PipelineRunner"/> shutdown semantics.
///
/// Host shutdown (IHostApplicationLifetime.ApplicationStopping): the pipeline must leave
/// the item in its current mid-flight state so the recovery loop can pick it up on next start.
///
/// Operator-requested cancel (DELETE /workitems/{id}): the pipeline must transition to
/// Cancelled with CancellationReason=OperatorRequested.
/// </summary>
[Collection("Pipeline integration")]
public sealed class HostShutdownCancellationTests : IDisposable
{
    private readonly string _workspace;

    public HostShutdownCancellationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-shutdown-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private ShutdownTestHarness BuildBlockingPipeline(string seedRepoUrl)
        => BuildPipeline(seedRepoUrl, new BlockingAgentRunner());

    private ShutdownTestHarness BuildPipeline(
        string seedRepoUrl,
        IAgentRunner agent,
        PipelineOptions? options = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([agent]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 3, AuditTypes = [] },
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var upstreamFactory = new TestUpstreamFactory();

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer, store,
            new NullWebhookDispatcher(),
            options ?? new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        return new ShutdownTestHarness(pipeline, store, gitHost);
    }

    // ── Host shutdown: leave item in mid-flight state ─────────────────────────

    [Fact]
    public async Task HostShutdown_DoesNotCancelItem_LeavesWorkingState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildBlockingPipeline(seed);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        // Poll until the real PipelineRunner has committed Working state to the DB
        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Signal host shutdown without cancelling the work item token; the
        // pipeline should preempt the running agent rather than treat this as
        // operator cancellation.
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Null(final.CancellationReason);
        Assert.NotNull(final.PreemptedAt);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", final.PreemptCheckpoint);

        var showRef = await TestSupport.RunGit(harness.GitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", final.PreemptCheckpoint!);
        Assert.Equal(0, showRef.code);
    }

    [Fact]
    public async Task HostShutdown_PreemptHookTimeout_StillCreatesCheckpoint()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildPipeline(
            seed,
            new HangingPreemptAgentRunner(),
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                ShutdownGrace = TimeSpan.FromSeconds(8),
            });

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource();
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));
        await hostShutdownCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask.WaitAsync(TimeSpan.FromSeconds(10)));

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, final!.State);
        Assert.Equal($"refs/heads/codeybox/preempt/{item.Id}", final.PreemptCheckpoint);

        var showRef = await TestSupport.RunGit(harness.GitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", final.PreemptCheckpoint!);
        Assert.Equal(0, showRef.code);
    }

    // ── Operator cancel: item must be Cancelled with OperatorRequested reason ──

    [Fact]
    public async Task OperatorCancel_TransitionsItem_ToCancelled_WithReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildBlockingPipeline(seed);

        var item = NewItem();
        await harness.Store.CreateAsync(item);

        using var hostShutdownCts = new CancellationTokenSource(); // never fires in this test
        using var operatorCancelCts = new CancellationTokenSource();

        var pipelineTask = Task.Run(() =>
            harness.Pipeline.RunAsync(item, operatorCancelCts.Token, hostShutdownCts.Token));

        await WaitForStateAsync(harness.Store, item.Id, WorkItemState.Working, TimeSpan.FromSeconds(30));

        // Signal operator cancel only — hostShutdownToken does NOT fire
        await operatorCancelCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipelineTask);

        var final = await harness.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, final!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, final.CancellationReason);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public void ResumePrompt_DoesNotEmbedScratchpadContent()
    {
        var prompt = PipelineRunner.BuildResumePrompt(
            "original prompt",
            "refs/heads/codeybox/preempt/wi");

        Assert.Contains("original prompt", prompt);
        Assert.Contains("refs/heads/codeybox/preempt/wi", prompt);
        Assert.Contains("restored work tree", prompt);
        Assert.DoesNotContain("BEGIN UNTRUSTED PREEMPT SCRATCHPAD", prompt);
        Assert.DoesNotContain("ignore previous instructions", prompt);
    }

    [Fact]
    public async Task PreemptedRework_RunsResumeAgentBeforeAuditing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new ReworkResumeRecordingAgent();
        var registry = new AgentRegistry([agent]);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        });
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost, registry, new StaticCredentialProvider(), new InMemoryPullRequestService(),
            projects, new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        var item = NewItem() with
        {
            State = WorkItemState.Reworking,
            WorkBranch = "codeybox/rework-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, agent.ResumeCalls);
        Assert.Contains("Interrupted Rework Resume", agent.LastResumePrompt);
        Assert.True(agent.SawScratchpad);
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_RestoresScratchpadAndResumes()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new StartupResumeRecordingAgent();
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(), new InMemoryProjectRepository(new Project
            {
                Id = new ProjectId("test-project"),
                DisplayName = "Test Project",
                RepositoryUrl = seed,
                DefaultBaseBranch = "main",
                DefaultAgent = AgentKind.Claude,
                Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
            }),
            new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);
        using var orchestrator = new OrchestratorService(
            queue, store, pipeline, new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/startup-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await orchestrator.StartAsync(CancellationToken.None);
        var final = await WaitForStateAsync(store, item.Id, WorkItemState.Done, TimeSpan.FromSeconds(20));
        await orchestrator.StopAsync(CancellationToken.None);

        Assert.Equal(1, agent.ResumeCalls);
        Assert.True(agent.RestoredScratchpad);
        Assert.Null(final.PreemptedAt);
        Assert.Null(final.PreemptCheckpoint);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_RemovesCheckpointScratchpadFromWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new NoopResumeAgent();
        var pipeline = BuildResumePipeline(seed, gitHost, store, agent);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/no-scratchpad-leak",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var tree = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree", "-r", "--name-only", item.WorkBranch!);
        Assert.Equal(0, tree.code);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad.md", tree.stdout);
        Assert.DoesNotContain(".codeybox/preempt-scratchpad.tgz", tree.stdout);
    }

    [Fact]
    public async Task StartupReplay_PreemptedWorkingItem_WithNoNewDiff_PushesCheckpointWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var agent = new NoopResumeAgent();
        var pipeline = BuildResumePipeline(seed, gitHost, store, agent);

        var item = NewItem() with
        {
            State = WorkItemState.Working,
            BaseBranch = "main",
            WorkBranch = "codeybox/no-new-diff-resume",
            PreemptedAt = DateTimeOffset.UtcNow,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
            PushUpstream = false,
        };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await store.CreateAsync(item);
        await CreatePreemptCheckpointAsync(gitHost, item, seed, includeScratchpad: false);

        await pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, agent.ResumeCalls);

        var branch = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "show-ref", "--verify", $"refs/heads/{item.WorkBranch}");
        Assert.Equal(0, branch.code);

        var tree = await TestSupport.RunGit(gitHost.GetRepoPath(item.Id.ToString()),
            "ls-tree", "-r", "--name-only", item.WorkBranch!);
        Assert.Equal(0, tree.code);
        Assert.Contains("partial-rework.txt", tree.stdout);
    }

    [Fact]
    public async Task ProcessSandbox_StopAndPreserve_SkipsDisposeDeletion()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var pwd = await sandbox.ExecAsync(new SandboxExec { Argv = ["pwd"] });
        Assert.True(pwd.Success);
        var root = Directory.GetParent(pwd.Stdout.Trim())!.FullName;

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();
        await sandbox.DisposeAsync();

        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, ".codeybox-preempt")));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private PipelineRunner BuildResumePipeline(
        string seed,
        LocalGitHost gitHost,
        SqliteWorkItemStore store,
        IAgentRunner agent)
    {
        return new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost, new AgentRegistry([agent]), new StaticCredentialProvider(),
            new InMemoryPullRequestService(), new InMemoryProjectRepository(new Project
            {
                Id = new ProjectId("test-project"),
                DisplayName = "Test Project",
                RepositoryUrl = seed,
                DefaultBaseBranch = "main",
                DefaultAgent = AgentKind.Claude,
                Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
            }),
            new TestUpstreamFactory(), new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store, new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);
    }

    private async Task CreatePreemptCheckpointAsync(LocalGitHost gitHost, WorkItem item, string seed, bool includeScratchpad = true)
    {
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed);
        var clone = Path.Combine(_workspace, "checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        var bare = gitHost.GetRepoPath(repoId);
        Assert.Equal(0, (await TestSupport.RunGit(_workspace, "clone", bare, clone)).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "config", "user.name", "Test")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch!)).code);
        if (includeScratchpad)
        {
            Directory.CreateDirectory(Path.Combine(clone, ".codeybox"));
            await File.WriteAllTextAsync(Path.Combine(clone, ".codeybox", "preempt-scratchpad.md"), "resume scratchpad");
            var scratchRoot = Path.Combine(_workspace, "scratch-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(scratchRoot, "home", ".testagent"));
            await File.WriteAllTextAsync(Path.Combine(scratchRoot, "home", ".testagent", "session.txt"), "resume session");
            await RunProcessAsync(clone, "tar", "-czf", Path.Combine(clone, ".codeybox", "preempt-scratchpad.tgz"), "-C", scratchRoot, ".");
        }
        await File.WriteAllTextAsync(Path.Combine(clone, "partial-rework.txt"), "partial");
        Assert.Equal(0, (await TestSupport.RunGit(clone, "add", "-A")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "commit", "-m", "checkpoint")).code);
        Assert.Equal(0, (await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{item.PreemptCheckpoint}")).code);
    }

    private static async Task<WorkItem> WaitForStateAsync(
        SqliteWorkItemStore store, WorkItemId id, WorkItemState target, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await store.GetAsync(id);
            if (current?.State == target) return current;
            await Task.Delay(25);
        }
        var actual = (await store.GetAsync(id))?.State;
        throw new TimeoutException(
            $"Item {id} did not reach state {target} within {timeout}; final state: {actual}");
    }

    private static async Task RunProcessAsync(string cwd, string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed: {stderr}");
    }
}

/// <summary>
/// Disposable bundle of pipeline resources used by <see cref="HostShutdownCancellationTests"/>.
/// </summary>
internal sealed class ShutdownTestHarness : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public LocalGitHost GitHost { get; }

    public ShutdownTestHarness(PipelineRunner pipeline, SqliteWorkItemStore store, LocalGitHost gitHost)
    {
        Pipeline = pipeline;
        Store = store;
        GitHost = gitHost;
    }

    public void Dispose() => Store.Dispose();
}

/// <summary>
/// Agent runner that blocks indefinitely until the cancellation token fires.
/// Holds the pipeline in the Working phase so shutdown tests can signal cancellation
/// while the item is provably mid-flight.
/// </summary>
internal sealed class BlockingAgentRunner : IAgentRunner
{
    public AgentKind Kind { get; init; } = AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new AgentResult(false, "unreachable", null, null);
    }
}

internal sealed class HangingPreemptAgentRunner : IAgentRunner, IPreemptibleAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AgentResult(false, "unreachable", null, null);
    }

    public Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default)
        => Task.Delay(Timeout.InfiniteTimeSpan);
}

internal sealed class StartupResumeRecordingAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }
    public bool RestoredScratchpad { get; private set; }

    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        throw new InvalidOperationException("preempted startup work should use RunResumedAsync");
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        var restore = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; tmp=$(mktemp -d); tar -xzf \"$1\" -C \"$tmp\"; test -f \"$tmp/home/.testagent/session.txt\"; cp -a \"$tmp/home/.\" \"$HOME/\"; test -f \"$HOME/.testagent/session.txt\"; printf '%s\n' resumed > resumed-startup.txt",
                "startup-resume",
                resume.ScratchpadArchivePath,
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        RestoredScratchpad = restore.Success;
        return new AgentResult(restore.Success, restore.Success ? "ok" : "restore failed", restore.Stdout, restore.Stderr);
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge startup resume test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}

internal sealed class ReworkResumeRecordingAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }
    public string LastResumePrompt { get; private set; } = string.Empty;
    public bool SawScratchpad { get; private set; }

    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        throw new InvalidOperationException("preempted rework should use RunResumedAsync");
    }

    public async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        LastResumePrompt = prompt;
        var scratchpad = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", ".codeybox/preempt-scratchpad.md"],
            WorkingDirectory = workingDirectory,
        }, ct);
        SawScratchpad = scratchpad.Success;

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf '%s\n' resumed > resumed-rework.txt"],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(true, "ok", "resumed", null);
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}

internal sealed class NoopResumeAgent : IAgentRunner, IResumableAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int ResumeCalls { get; private set; }

    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return MergeAsync(sandbox, workingDirectory, prompt, ct);

        return Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    public Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        ResumeCalls++;
        return Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    private static async Task<AgentResult> MergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var workBranch = ExtractBetween(prompt, "merge branch `", "` into branch `");
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; git merge --no-ff \"$1\" -m 'codeybox: merge noop resume test\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>'",
                "merge-test",
                $"origin/{workBranch}",
            ],
            WorkingDirectory = workingDirectory,
        }, ct);
        return new AgentResult(result.Success, result.Success ? "ok" : "merge failed", result.Stdout, result.Stderr);
    }

    private static string ExtractBetween(string text, string left, string right)
    {
        var start = text.IndexOf(left, StringComparison.Ordinal);
        if (start < 0) return "main";
        start += left.Length;
        var end = text.IndexOf(right, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end];
    }
}
