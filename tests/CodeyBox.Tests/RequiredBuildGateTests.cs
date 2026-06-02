using CodeyBox.Core;
using CodeyBox.Git;
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
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

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
        Assert.Equal("build", final.FailureKind);
        Assert.DoesNotContain("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkCompletion_NewCommitThatBreaksRequiredBuild_FailsWithBuildFailureKind()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/work-build-break");
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("work left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
    }

    [Fact]
    public async Task AuditRework_NewCommitThatBreaksRequiredBuild_FailsWithReworkBuildError()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new OneTimeFailingAuditor()],
            maxAuditIterations: 2,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/rework-build-break");
        tp.Agent.WorkPlan.Enqueue(new FileWrite("initial.txt", "initial\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("rework left the branch non-compiling", final.LastError);
        Assert.Contains("error CS1061", final.LastError);
        Assert.Equal("build", final.FailureKind);
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

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
    public async Task AuditPass_CannotReachAuditPassed_WithNoAuditorsAndZeroAuditIterations_WhenRequiredBuildFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 0,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-broken-no-auditors") with { State = WorkItemState.WorkComplete };
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
    public async Task AuditPass_RequiredBuildBuildsRootSolutionAndTestProject_WhenNoAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateFakeDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

        var item = NewItem("feature/audit-green-no-auditors") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var dotnetInvocations = await File.ReadAllLinesAsync(fakeDotnet.LogPath);
        Assert.Contains("build ./CodeyBox.slnx", dotnetInvocations);
        Assert.Contains("build ./tests/CodeyBox.Tests.csproj", dotnetInvocations);
    }

    [Fact]
    public async Task RequiredBuild_MaliciousBuildCannotMutateAuthoritativeBareRepository()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateMaliciousDotnetAsync();
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            auditReports: null,
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var item = NewItem("feature/malicious-build") with { State = WorkItemState.WorkComplete };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "evil.txt", "evil\n", "malicious branch");
        var (_, seedMain, _) = await TestSupport.RunGit(seed, "rev-parse", "main");
        await TestSupport.RunGit(barePath, "update-ref", "refs/heads/main", seedMain.Trim());
        var mainBefore = seedMain;

        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            RepositoryId = repoId,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, result.Status);
        var (_, mainAfter, _) = await TestSupport.RunGit(barePath, "rev-parse", "main");
        var dotnetLog = File.Exists(fakeDotnet.LogPath)
            ? string.Join(" | ", await File.ReadAllLinesAsync(fakeDotnet.LogPath))
            : "(fake dotnet log missing)";
        Assert.True(
            string.Equals(mainBefore.Trim(), mainAfter.Trim(), StringComparison.Ordinal),
            $"authoritative main changed from {mainBefore.Trim()} to {mainAfter.Trim()}; fake dotnet log: {dotnetLog}");
    }

    [Fact]
    public async Task AuditPass_CannotReachAuditPassed_WhenRequiredBuildCannotRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var fakeDotnet = await CreateUnavailableDotnetAsync();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            maxAuditIterations: 1,
            sandboxProvider: new PathInjectingSandboxProvider(fakeDotnet.Path, fakeDotnet.Environment));

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
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.NotEqual(WorkItemState.AuditPassed, final.State);
    }

    private async Task<FakeDotnet> CreateFakeDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            case "$2" in
              ./CodeyBox.slnx|./tests/CodeyBox.Tests.csproj) ;;
              *)
                echo "unexpected dotnet build target: $2" >&2
                exit 43
                ;;
            esac
            if [ -f build.fail ]; then
              echo "src/Broken.cs(1,1): error CS1061: 'Broken' does not contain a definition" >&2
              exit 1
            fi
            echo "Build succeeded."
            exit 0
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private async Task<FakeDotnet> CreateMaliciousDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-malicious-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-malicious-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            if [ "$1" != "build" ]; then
              echo "unexpected dotnet command: $*" >&2
              exit 42
            fi
            case "$2" in
              ./CodeyBox.slnx|./tests/CodeyBox.Tests.csproj) ;;
              *)
                echo "unexpected dotnet build target: $2" >&2
                exit 43
                ;;
            esac
            tip=$(git rev-parse HEAD)
            repo=$(git remote get-url origin)
            printf 'origin=%s\n' "$repo" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            printf 'origin_target=%s\n' "$(readlink "$repo" 2>/dev/null || true)" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            git -C "$repo" update-ref refs/heads/main "$tip" || exit 44
            echo "Build succeeded."
            exit 0
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private async Task<FakeDotnet> CreateUnavailableDotnetAsync()
    {
        var bin = Path.Combine(_workspace, "fake-dotnet-unavailable-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        var dotnet = Path.Combine(bin, "dotnet");
        var log = Path.Combine(_workspace, "fake-dotnet-unavailable-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        await File.WriteAllTextAsync(dotnet, """
            #!/bin/sh
            printf '%s\n' "$*" >> "$CODEYBOX_FAKE_DOTNET_LOG"
            echo "dotnet is not available in the sandbox PATH" >&2
            exit 127
            """);
        MakeExecutable(dotnet);
        return new FakeDotnet(
            bin + Path.PathSeparator + "/usr/bin:/bin",
            log,
            new Dictionary<string, string> { ["CODEYBOX_FAKE_DOTNET_LOG"] = log });
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task AddDotnetSolutionMarkerAsync(string repoPath)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, "CodeyBox.slnx"), "# solution marker for required build tests\n");
        var testProjectPath = Path.Combine(repoPath, "tests", "CodeyBox.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(testProjectPath)!);
        await File.WriteAllTextAsync(testProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestSupport.RunGit(repoPath, "add", "CodeyBox.slnx", "tests/CodeyBox.Tests.csproj");
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

    private sealed record FakeDotnet(
        string Path,
        string LogPath,
        IReadOnlyDictionary<string, string> Environment);

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

    private sealed class OneTimeFailingAuditor : IAuditor
    {
        private int _calls;
        public string Name => "one-time-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "force rework",
                        "force one rework iteration"),
                ]));
            }

            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class PathInjectingSandboxProvider(
        string path,
        IReadOnlyDictionary<string, string>? environment = null) : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner =
            new(NullLogger<ProcessSandboxProvider>.Instance);

        public string Name => _inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new PathInjectingSandbox(await _inner.CreateAsync(spec, ct), path, environment);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class PathInjectingSandbox(
        ISandbox inner,
        string path,
        IReadOnlyDictionary<string, string>? environment) : ISandbox
    {
        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var env = exec.ExtraEnvironment is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(exec.ExtraEnvironment);
            env["PATH"] = path;
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    env[key] = value;
            }
            return inner.ExecAsync(exec with { ExtraEnvironment = env }, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
