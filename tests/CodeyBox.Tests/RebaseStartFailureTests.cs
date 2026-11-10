using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit coverage for the pickup-time rebase start-failure handling: the
/// stderr classifier, the porcelain scratch parser, and the
/// rebase-in-progress probe. Staged end-to-end coverage (dirty worktree,
/// untracked overwrite, real conflicts, stderr logging) lives in
/// <see cref="MergeLandingSerializationTests"/>.
/// </summary>
public sealed class RebaseStartFailureTests
{
    [Theory]
    [InlineData("error: cannot rebase: You have unstaged changes.\nerror: Please commit or stash them.", PipelineRunner.RebaseStartFailureKind.DirtyWorktree)]
    [InlineData("error: cannot rebase: Your index contains uncommitted changes.", PipelineRunner.RebaseStartFailureKind.DirtyWorktree)]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:\n\tfoo.cs\nPlease commit your changes or stash them before you merge.", PipelineRunner.RebaseStartFailureKind.DirtyWorktree)]
    [InlineData("error: The following untracked working tree files would be overwritten by merge:\n\tdata.txt\nPlease move or remove them before you merge.", PipelineRunner.RebaseStartFailureKind.UntrackedFilesWouldBeOverwritten)]
    [InlineData("error: The following untracked working tree files would be overwritten by checkout:\n\tdata.txt\nPlease move or remove them before you switch branches.\nAborting", PipelineRunner.RebaseStartFailureKind.UntrackedFilesWouldBeOverwritten)]
    [InlineData("fatal: Unable to create '/work/.git/index.lock': File exists.", PipelineRunner.RebaseStartFailureKind.LockContention)]
    [InlineData("Another git process seems to be running in this repository, e.g.", PipelineRunner.RebaseStartFailureKind.LockContention)]
    [InlineData("It seems that there is already a rebase-merge directory, and I wonder if you are in the middle of another rebase.", PipelineRunner.RebaseStartFailureKind.LockContention)]
    [InlineData("fatal: no such branch/commit 'origin/nope'", PipelineRunner.RebaseStartFailureKind.MissingUpstreamRef)]
    [InlineData("fatal: invalid upstream 'origin/nope'", PipelineRunner.RebaseStartFailureKind.MissingUpstreamRef)]
    [InlineData("Author identity unknown\n\n*** Please tell me who you are.", PipelineRunner.RebaseStartFailureKind.IdentityOrHook)]
    [InlineData("error: gpg failed to sign the data", PipelineRunner.RebaseStartFailureKind.IdentityOrHook)]
    [InlineData("", PipelineRunner.RebaseStartFailureKind.Unknown)]
    [InlineData("some totally novel git failure", PipelineRunner.RebaseStartFailureKind.Unknown)]
    public void ClassifyRebaseStartFailure_MapsKnownGitErrors(string stderr, PipelineRunner.RebaseStartFailureKind expected) =>
        Assert.Equal(expected, PipelineRunner.ClassifyRebaseStartFailure(stderr));

    [Fact]
    public void ClassifyRebaseStartFailure_NullIsUnknown() =>
        Assert.Equal(PipelineRunner.RebaseStartFailureKind.Unknown, PipelineRunner.ClassifyRebaseStartFailure(null));

    [Fact]
    public void AnalyzeWorktreeDirt_CleanTree()
    {
        var report = PipelineRunner.AnalyzeWorktreeDirt("");
        Assert.True(report.IsClean);
    }

    [Fact]
    public void AnalyzeWorktreeDirt_PipelineScratchOnly()
    {
        var report = PipelineRunner.AnalyzeWorktreeDirt(
            " M .codeybox/agent-logs/run-1.log\n?? .codeybox/agent-logs/run-2.log\nA  .codeybox/suggestions.json\n");
        Assert.False(report.IsClean);
        Assert.True(report.AllPipelineOwnedScratch);
        Assert.True(report.HasTrackedScratch);
    }

    [Fact]
    public void AnalyzeWorktreeDirt_UntrackedScratchOnlyHasNoTrackedEntries()
    {
        var report = PipelineRunner.AnalyzeWorktreeDirt("?? .codeybox/agent-logs/run-1.log\n");
        Assert.False(report.IsClean);
        Assert.True(report.AllPipelineOwnedScratch);
        Assert.False(report.HasTrackedScratch);
    }

    [Fact]
    public void AnalyzeWorktreeDirt_MixedContentIsNotPipelineOwned()
    {
        var report = PipelineRunner.AnalyzeWorktreeDirt(" M .codeybox/agent-logs/run-1.log\n M src/Foo.cs\n");
        Assert.False(report.IsClean);
        Assert.False(report.AllPipelineOwnedScratch);
        Assert.True(report.HasTrackedScratch);
    }

    [Fact]
    public void AnalyzeWorktreeDirt_PrefixLookalikeIsNotOwned()
    {
        // ".codeybox-evil/" shares a string prefix but is not the namespace.
        var report = PipelineRunner.AnalyzeWorktreeDirt("?? .codeybox-evil/payload\n");
        Assert.False(report.AllPipelineOwnedScratch);
    }

    [Fact]
    public void AnalyzeWorktreeDirt_RenameChecksBothSides()
    {
        var report = PipelineRunner.AnalyzeWorktreeDirt("R  .codeybox/old.log -> src/new.cs\n");
        Assert.False(report.AllPipelineOwnedScratch);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public async Task IsRebaseInProgress_ProbesRefThenStateDirs(
        bool rebaseHeadResolves, bool mergeDirExists, bool applyDirExists, bool expected)
    {
        var sandbox = new RebaseProbeSandbox(rebaseHeadResolves, mergeDirExists, applyDirExists);
        Assert.Equal(expected, await PipelineRunner.IsRebaseInProgressAsync(sandbox, CancellationToken.None));
        // The sequencer-dir probes only run when REBASE_HEAD is absent, and
        // the apply dir is only probed when the merge dir is absent.
        Assert.Equal(!rebaseHeadResolves, sandbox.ProbedGitDir);
        Assert.Equal(!rebaseHeadResolves && !mergeDirExists, sandbox.ProbedApplyDir);
    }

    [Fact]
    public async Task IsRebaseInProgress_GitDirFailureIsNotInProgress()
    {
        var sandbox = new RebaseProbeSandbox(rebaseHeadResolves: false, mergeDirExists: false, applyDirExists: false)
        {
            FailGitDir = true,
        };
        Assert.False(await PipelineRunner.IsRebaseInProgressAsync(sandbox, CancellationToken.None));
    }

    private sealed class RebaseProbeSandbox(
        bool rebaseHeadResolves, bool mergeDirExists, bool applyDirExists) : ISandbox
    {
        public string Id => "rebase-probe";
        public bool FailGitDir { get; set; }
        public bool ProbedGitDir { get; private set; }
        public bool ProbedApplyDir { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv;
            if (argv.Count >= 5
                && argv[0] == "git" && argv[^2] == "--verify" && argv[^1] == "REBASE_HEAD")
            {
                return Task.FromResult(rebaseHeadResolves
                    ? new SandboxExecResult(0, "abc123\n", "")
                    : new SandboxExecResult(1, "", "fatal: Needed a single revision\n"));
            }

            if (argv.Count >= 4 && argv[0] == "git" && argv[^1] == "--absolute-git-dir")
            {
                ProbedGitDir = true;
                return Task.FromResult(FailGitDir
                    ? new SandboxExecResult(128, "", "fatal: not a git repository\n")
                    : new SandboxExecResult(0, "/work/.git\n", ""));
            }

            if (argv.Count == 3 && argv[0] == "test" && argv[1] == "-d")
            {
                if (argv[2].EndsWith("/rebase-apply", StringComparison.Ordinal))
                    ProbedApplyDir = true;
                var exists = argv[2].EndsWith("/rebase-merge", StringComparison.Ordinal)
                    ? mergeDirExists
                    : applyDirExists;
                return Task.FromResult(exists
                    ? new SandboxExecResult(0, "", "")
                    : new SandboxExecResult(1, "", ""));
            }

            throw new InvalidOperationException($"unexpected probe argv: {string.Join(' ', argv)}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
