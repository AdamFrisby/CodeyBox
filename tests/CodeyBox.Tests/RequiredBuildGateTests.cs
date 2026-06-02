using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class RequiredBuildGateTests : IDisposable
{
    private readonly string _workspace;

    public RequiredBuildGateTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-required-build-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task RetryFromWork_NoChangesOnBrokenCSharpBranch_FailsWithBuildErrorNotNoChanges()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnetPath = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnetPath));

        var item = NewItem("feature/broken-csharp");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken prior attempt");

        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("work left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.DoesNotContain("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnetPath = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnetPath));

        var item = NewItem("feature/audit-broken") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "build.fail", "broken\n", "broken branch");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("required build failed", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildCannotRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider("/usr/bin:/bin"));

        var item = NewItem("feature/audit-no-dotnet") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("could not verify required build", final.LastError);
        Assert.Contains("dotnet is not available", final.LastError);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    private async Task<string> CreateFakeDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            if [ -f build.fail ]; then
              echo "src/Broken.cs(1,1): error CS1061: 'Broken' does not contain a definition" >&2
              exit 1
            fi
            echo "Build succeeded."
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return bin + Path.PathSeparator + "/usr/bin:/bin";
    }

    private static async Task AddDotnetSolutionMarkerAsync(string repoPath)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, "CodeyBox.slnx"), "# solution marker for required build tests\n");
        await TestSupport.RunGit(repoPath, "add", "CodeyBox.slnx");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add solution marker");
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
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

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "required build gate test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private sealed class PassingAuditor : IAuditor
    {
        public string Name => "passing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class PathInjectingSandboxProvider(string path) : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner =
            new(NullLogger<ProcessSandboxProvider>.Instance);

        public string Name => _inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new PathInjectingSandbox(await _inner.CreateAsync(spec, ct), path);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class PathInjectingSandbox(ISandbox inner, string path) : ISandbox
    {
        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var env = exec.ExtraEnvironment is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(exec.ExtraEnvironment);
            env["PATH"] = path;
            return inner.ExecAsync(exec with { ExtraEnvironment = env }, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
