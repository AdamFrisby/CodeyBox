using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the concrete <see cref="LocalGitPreMergeVerifier"/>.
/// Unlike <see cref="PreMergeVerifyGateTests"/> — which stubs the verifier to
/// exercise the orchestrator's gate orchestration — these tests exercise the
/// production implementation: a real bare repo, a real worktree, a real
/// process invocation. They are the lock-in for the in-CodeyBox half of the
/// pre-merge CI gate (the GitHub Actions side is covered separately by the
/// workflow file under .github/workflows).
/// </summary>
public sealed class LocalGitPreMergeVerifierTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-premerge-real-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Happy path against a real bare repo: a green verification command
    /// returns <see cref="PreMergeVerifyResult.Ok"/>. Proves the worktree
    /// add + process invocation path actually executes (not just the
    /// short-circuit when argv is empty).
    /// </summary>
    [Fact]
    public async Task RealVerifier_GreenCommand_ReturnsOk()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            // /usr/bin/true is the universal "no-op success" command; if the
            // verifier proceeds far enough to execute argv against a checked-
            // out tree, this will exit 0.
            Argv = ["/usr/bin/true"],
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.None, result.FailureMode);
    }

    /// <summary>
    /// The whole point of the gate: a build-time check that exits non-zero
    /// MUST surface as <see cref="PreMergeVerifyResult.BuildOrTestFailed"/>
    /// so the orchestrator parks the work item with the operator-visible
    /// "rebased build failed" prefix. <c>/usr/bin/false</c> is the cleanest
    /// way to drive that branch without depending on a real build toolchain.
    /// </summary>
    [Fact]
    public async Task RealVerifier_RedCommand_ReturnsBuildOrTestFailed()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = ["/usr/bin/false"],
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        Assert.False(string.IsNullOrEmpty(result.FailureReason));
    }

    /// <summary>
    /// Captured argv output is included in <see cref="PreMergeVerifyResult.FailureReason"/>
    /// so the operator can read the actual error from the work item's
    /// <c>LastError</c> without digging into orchestrator logs. Operators
    /// rely on this for the "is it the same break as last time" check.
    /// </summary>
    [Fact]
    public async Task RealVerifier_RedCommand_FailureReasonIncludesStderr()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = ["/bin/sh", "-c", "echo CS0117-helper-missing >&2; exit 17"],
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        Assert.Contains("CS0117", result.FailureReason);
        // The exit code is part of the operator-visible context — "exit 17"
        // tells them whether the tool's docs describe a specific meaning.
        Assert.Contains("17", result.FailureReason);
    }

    /// <summary>
    /// Empty argv is the "gate not configured" case. Even if the orchestrator
    /// somehow reaches the verifier without filtering this out, the verifier
    /// must self-defend and return Ok without checking out anything — the
    /// alternative would mean an empty config silently parked merges.
    /// </summary>
    [Fact]
    public async Task RealVerifier_EmptyArgv_ReturnsOkWithoutRunning()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = [],
        }, CancellationToken.None);

        Assert.True(result.Success);
    }

    private async Task<(IGitHost GitHost, string RepoId, string MergeSha)> SetupBareRepoWithCommitAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed);
        var mergeSha = await gitHost.ResolveCommitAsync(repoId, "main", CancellationToken.None);
        return (gitHost, repoId, mergeSha);
    }
}
