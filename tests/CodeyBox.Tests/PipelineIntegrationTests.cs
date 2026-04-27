using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end pipeline test using the Process sandbox provider. Exercises:
///   - Host bare repo creation seeded from a local origin
///   - Work phase: agent runs, commits, pushes a feature branch
///   - Merge phase: clean merge sandbox, no agent creds, pushes target branch
///   - Final state: WorkItem reaches Done with merged history in the bare repo
///
/// Requires git on PATH (verified at fixture init).
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;

    public PipelineIntegrationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    /// <summary>
    /// Fake agent: writes a single file in the working directory. Behaves
    /// like a deterministic, well-mannered coding agent for the purpose of
    /// the pipeline test.
    /// </summary>
    private sealed class FakeAgentRunner : IAgentRunner
    {
        public AgentKind Kind { get; }
        private readonly string _fileName;
        private readonly string _contents;

        public FakeAgentRunner(AgentKind kind, string fileName, string contents)
        {
            Kind = kind;
            _fileName = fileName;
            _contents = contents;
        }

        public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential, CancellationToken ct = default)
        {
            // Write a file via the sandbox. Use stdin so contents don't go on argv.
            var path = $"{workingDirectory}/{_fileName}";
            var r = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = _contents,
            }, ct);
            return r.Success
                ? new AgentResult(true, "ok", r.Stdout, r.Stderr)
                : new AgentResult(false, "failed", r.Stdout, r.Stderr);
        }
    }

    private sealed class StaticCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
    }

    [Fact]
    public async Task EndToEnd_RunsWorkAndMergePhases()
    {
        // ---- Set up a seed git repo on disk that the orchestrator will clone from. ----
        var seed = Path.Combine(_workspace, "seed");
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "test@local");
        await RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await RunGit(seed, "add", "README.md");
        await RunGit(seed, "commit", "-m", "initial");

        // ---- Compose the orchestrator. ----
        var gitRoot = Path.Combine(_workspace, "repos");
        var stateDb = Path.Combine(_workspace, "state.db");

        using var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var upstream = new NoopUpstreamRemote();
        var registry = new AgentRegistry([new FakeAgentRunner(AgentKind.Claude, "hello.txt", "hello world\n")]);
        var creds = new StaticCredentialProvider();

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, creds, prs, upstream, store,
            new AuditorRegistry([]),
            new AuditOptions(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        // ---- Enqueue an item and run the pipeline directly. ----
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            Title = "add hello",
            Prompt = "create hello.txt",
            RepositoryUrl = seed,
            Agent = AgentKind.Claude,
            PushUpstream = false,
            BaseBranch = "main",
            WorkBranch = "feature/hello",
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        // ---- Verify final state. ----
        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Confirm the bare repo has a hello.txt blob on main now.
        var barePath = Path.Combine(gitRoot, item.Id + ".git");
        var blob = await RunGit(barePath, "show", "main:hello.txt");
        Assert.Equal("hello world\n", blob.stdout);

        // Confirm the feature branch was pushed too.
        var branches = await RunGit(barePath, "branch", "--list");
        Assert.Contains("feature/hello", branches.stdout);
    }

    [Fact]
    public async Task AgentNoChange_FailsWorkItem()
    {
        var seed = Path.Combine(_workspace, "seed2");
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "test@local");
        await RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "x"), "");
        await RunGit(seed, "add", "x");
        await RunGit(seed, "commit", "-m", "initial");

        var gitRoot = Path.Combine(_workspace, "repos2");
        var stateDb = Path.Combine(_workspace, "state2.db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var upstream = new NoopUpstreamRemote();
        // Agent that does nothing.
        var noOpAgent = new FakeAgentNoChange();
        var registry = new AgentRegistry([noOpAgent]);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs, upstream, store,
            new AuditorRegistry([]),
            new AuditOptions(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            Title = "no-op",
            Prompt = "do nothing",
            RepositoryUrl = seed,
            Agent = AgentKind.Claude,
            PushUpstream = false,
            BaseBranch = "main",
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeAgentNoChange : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, CancellationToken _____ = default)
            => Task.FromResult(new AgentResult(true, "no-op", null, null));
    }

    [Fact]
    public async Task WorkBranchEqualsBaseBranch_FailsBeforeSandbox()
    {
        // Ensures the merge-phase containment cannot be bypassed by setting
        // workBranch == baseBranch (audit Finding B).
        var seed = Path.Combine(_workspace, "seed3");
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "test@local");
        await RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "x"), "");
        await RunGit(seed, "add", "x");
        await RunGit(seed, "commit", "-m", "initial");

        var gitRoot = Path.Combine(_workspace, "repos3");
        var stateDb = Path.Combine(_workspace, "state3.db");
        using var store = new SqliteWorkItemStore(stateDb);
        var pipeline = new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance),
            new AgentRegistry([new FakeAgentRunner(AgentKind.Claude, "f.txt", "x\n")]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            new NoopUpstreamRemote(),
            store,
            new AuditorRegistry([]),
            new AuditOptions(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            Title = "should reject",
            Prompt = "x",
            RepositoryUrl = seed,
            Agent = AgentKind.Claude,
            BaseBranch = "main",
            WorkBranch = "main",
            PushUpstream = false,
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("must differ from baseBranch", final.LastError);
    }

    [Fact]
    public async Task TwoWorkItems_DoNotShareBareRepoVisibility()
    {
        // Audit Finding A: each work item's sandbox must mount only its own
        // bare repo, not the bare-repos root. We verify by listing /repo
        // inside the sandbox: it should be a single bare repo, not a
        // directory of many.
        var seed = Path.Combine(_workspace, "seed-iso");
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "t@l");
        await RunGit(seed, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(seed, "f"), "");
        await RunGit(seed, "add", "f");
        await RunGit(seed, "commit", "-m", "i");

        var gitRoot = Path.Combine(_workspace, "repos-iso");
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);

        // Provision two repos so the bare-repos root has multiple entries.
        var idA = WorkItemId.New();
        var idB = WorkItemId.New();
        var repoA = await gitHost.EnsureRepositoryAsync(idA, seed);
        var repoB = await gitHost.EnsureRepositoryAsync(idB, seed);
        Assert.NotEqual(repoA, repoB);

        var access = gitHost.GetSandboxAccess(repoA);
        Assert.Single(access.Mounts);
        // The mount source MUST be the per-item bare repo path, not the
        // bare-repos root. The other repo (repoB) must not be visible.
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, access.Mounts[0].SandboxPath);
        Assert.Contains(repoA, access.Mounts[0].HostPath!);
        Assert.DoesNotContain(repoB, access.Mounts[0].HostPath!);
    }

    private static async Task<(int code, string stdout, string stderr)> RunGit(string cwd, params string[] args)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }
}
