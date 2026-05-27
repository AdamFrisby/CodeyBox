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
    /// Happy path against a real bare repo. The argv intentionally observes
    /// its own working directory (README.md is the seed's only file, present
    /// only in the checked-out worktree — NOT in the bare repo root). A
    /// regression that ran argv in <c>bareRepoPath</c> instead of the
    /// per-invocation worktree, or checked out something other than
    /// <see cref="PreMergeVerifyRequest.MergeSha"/>, would fail this check
    /// because the bare repo has no working tree.
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
            // README.md is in the seed commit and ONLY exists in a checked-
            // out worktree (bare repos have no working tree). A `.git` FILE
            // (not directory) is what `git worktree add --detach` produces;
            // a bare-repo root would have `objects/` `refs/` etc., not a
            // `.git` file. Both checks together pin the contract that argv
            // runs in the worktree of MergeSha.
            Argv = ["/bin/sh", "-c", "test -f README.md && test -f .git"],
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

    /// <summary>
    /// When captured output exceeds <c>MaxCapturedStreamBytes</c> (4 KiB) the
    /// verifier head+tail trims with a literal "..." separator. The point of
    /// keeping BOTH ends is that build output typically has the diagnostic
    /// header at the top (compiler version, target framework) and the
    /// actionable error line at the bottom — dropping either half makes the
    /// FailureReason useless to the operator.
    /// </summary>
    [Fact]
    public async Task RealVerifier_LargeStderr_HeadAndTailBothSurface()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        // Drives ~6 KiB of stderr so the head/tail split (halfBudget = 2048)
        // necessarily drops the middle. Both markers must survive: HEAD-MARK
        // in the leading half, TAIL-MARK in the trailing half. Neither
        // string matches RawOutputRedactor's secret patterns, so they pass
        // through redaction unchanged.
        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = ["/bin/sh", "-c",
                "{ printf 'HEAD-MARK-7271 '; yes filler-line-content | head -c 6000; printf ' TAIL-MARK-9438'; } >&2; exit 1"],
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HEAD-MARK-7271", result.FailureReason);
        Assert.Contains("TAIL-MARK-9438", result.FailureReason);
        // The "..." separator is what tells the operator content was elided —
        // without it the truncated output looks like a complete buffer.
        Assert.Contains("...", result.FailureReason);
        // Sanity bound: the truncated string is far below the original size.
        // Allow some slack for the prefix ("/bin/sh exited 1: ") + the two
        // halves + the separator.
        Assert.True(result.FailureReason!.Length < 6000,
            $"FailureReason was {result.FailureReason!.Length} chars — truncation likely did not run");
    }

    /// <summary>
    /// Token-shaped strings in the argv's stdout/stderr MUST be redacted
    /// before they reach <see cref="PreMergeVerifyResult.FailureReason"/>.
    /// That string flows into the work item's <c>LastError</c> and the
    /// <c>work_item.merge_conflict_resolution_failed</c> webhook payload —
    /// both operator-visible surfaces. A regression that dropped the
    /// <see cref="RawOutputRedactor"/> call (or fed it the wrong field)
    /// would silently leak credentials.
    /// </summary>
    [Fact]
    public async Task RealVerifier_SecretInStderr_IsRedactedFromFailureReason()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        // GitHub Personal Access Token shape (matches RawOutputRedactor's
        // ghp_ pattern). The non-secret context strings flank it so we can
        // tell redaction happened in-place (it should NOT replace the whole
        // line, just the token).
        const string leakedToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            Argv = ["/bin/sh", "-c", $"printf 'leak-before {leakedToken} leak-after\\n' >&2; exit 1"],
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(leakedToken, result.FailureReason);
        // Both flanking non-secret markers must survive — verifies the
        // redactor only replaces the matched token, not the surrounding
        // diagnostic context.
        Assert.Contains("leak-before", result.FailureReason);
        Assert.Contains("leak-after", result.FailureReason);
        // The redactor replaces matches with "***" — without this check, a
        // regression that simply dropped the leaked-token characters would
        // also pass DoesNotContain. The "***" is the positive signal that
        // redaction (not loss) is what happened.
        Assert.Contains("***", result.FailureReason);
    }

    /// <summary>
    /// When the configured argv exceeds the verifier's command timeout the
    /// process must be killed (process-tree, not just the lead process) and
    /// the result surfaces as <see cref="PreMergeVerifyResult.BuildOrTestFailed"/>
    /// with exit code 124 in the message. Without this, a build target stuck
    /// in an infinite loop would block the orchestrator's auto-merge loop
    /// indefinitely.
    /// </summary>
    [Fact]
    public async Task RealVerifier_ArgvExceedsTimeout_ReportsTimeoutFailure()
    {
        var (gitHost, repoId, mergeSha) = await SetupBareRepoWithCommitAsync();
        // Sub-second timeout keeps the test fast; the 30-min production
        // default is set by the parameterless ctor.
        var verifier = new LocalGitPreMergeVerifier(
            gitHost,
            NullLogger<LocalGitPreMergeVerifier>.Instance,
            commandTimeout: TimeSpan.FromMilliseconds(500));

        var start = DateTime.UtcNow;
        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = mergeSha,
            // 30s sleep — the verifier must kill this within ~500ms.
            Argv = ["/bin/sh", "-c", "sleep 30; echo should-never-be-printed"],
        }, CancellationToken.None);
        var elapsed = DateTime.UtcNow - start;

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        // Exit code 124 mirrors GNU `timeout(1)` so operators see a familiar
        // sentinel; the message states "exceeded ... timeout" explicitly.
        Assert.Contains("124", result.FailureReason);
        Assert.Contains("timeout", result.FailureReason);
        // The kill must actually have happened — if WaitForExitAsync never
        // returned we'd be sitting at the full 30s.
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"Verifier took {elapsed.TotalSeconds:0}s to kill the sleep — process-tree kill did not run");
    }

    /// <summary>
    /// Default <see cref="IGitHost"/> implementations throw
    /// <see cref="NotSupportedException"/> from <c>GetRepoPath</c> when the
    /// host does not expose a bare-repo filesystem path (in-memory test
    /// hosts, future remote-only hosts). The verifier must treat that as
    /// "verifier is not applicable here" and return <see cref="PreMergeVerifyResult.Ok"/>
    /// — anything else would silently park every work item on opt-out hosts.
    /// </summary>
    [Fact]
    public async Task RealVerifier_NotSupportedHost_ReturnsOkAsSkip()
    {
        var verifier = new LocalGitPreMergeVerifier(
            new RepoPathThrowingHost(new NotSupportedException("no host path")),
            NullLogger<LocalGitPreMergeVerifier>.Instance);

        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = "any-id",
            BaseBranch = "main",
            WorkBranch = "feature/x",
            // SHA shape doesn't matter — the NotSupportedException branch
            // short-circuits before the verifier validates the SHA.
            MergeSha = new string('a', 40),
            Argv = ["/usr/bin/true"],
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.None, result.FailureMode);
    }

    /// <summary>
    /// When the host reports a bare-repo path that does not exist on disk
    /// the verifier throws <see cref="InvalidOperationException"/>. The
    /// orchestrator's gate catches general <c>Exception</c> and surfaces
    /// this as a parked <see cref="PreMergeVerifyFailureMode.BuildOrTestFailed"/>
    /// rather than silently merging — covered end-to-end by
    /// <see cref="PreMergeVerifyGateTests.VerifierThrows_ParksWithBuildOrTestFailedPrefix"/>;
    /// here we lock in the throw-on-missing-path contract.
    /// </summary>
    [Fact]
    public async Task RealVerifier_MissingBareRepoPath_ThrowsInvalidOperationException()
    {
        var host = new RepoPathStubHost(_ => Path.Combine(_workspace, "definitely-does-not-exist"));
        var verifier = new LocalGitPreMergeVerifier(
            host, NullLogger<LocalGitPreMergeVerifier>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(new PreMergeVerifyRequest
            {
                WorkItemId = WorkItemId.New(),
                ProjectId = new ProjectId("test"),
                RepositoryId = "missing",
                BaseBranch = "main",
                WorkBranch = "feature/x",
                MergeSha = new string('b', 40),
                Argv = ["/usr/bin/true"],
            }, CancellationToken.None));
        Assert.Contains("does not exist", ex.Message);
    }

    /// <summary>
    /// A malformed MergeSha (not 40 hex chars) must be rejected up-front by
    /// <c>Validation.ValidateCommitSha</c> — before any process invocation.
    /// The forge cannot produce a malformed sha through normal paths, but
    /// this guards against bugs in the caller that compute MergeSha from a
    /// wrong source (e.g. a branch name slipping through as the sha).
    /// </summary>
    [Fact]
    public async Task RealVerifier_MalformedMergeSha_IsRejected()
    {
        var (gitHost, repoId, _) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost, NullLogger<LocalGitPreMergeVerifier>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            verifier.VerifyAsync(new PreMergeVerifyRequest
            {
                WorkItemId = WorkItemId.New(),
                ProjectId = new ProjectId("test"),
                RepositoryId = repoId,
                BaseBranch = "main",
                WorkBranch = "feature/x",
                // Branch-name-shaped value (not 40-hex). A real merge sha
                // would be 40 hex chars.
                MergeSha = "main",
                Argv = ["/usr/bin/true"],
            }, CancellationToken.None));
    }

    /// <summary>
    /// A well-formed but non-existent sha makes <c>git worktree add</c> fail
    /// (it cannot resolve the sha to a commit). The verifier surfaces this
    /// as <see cref="PreMergeVerifyResult.BuildOrTestFailed"/> with the
    /// "could not check out merge sha" prefix so the operator can tell the
    /// failure from an argv-side build error. A regression that returned
    /// <see cref="PreMergeVerifyResult.Ok"/> when the worktree was never
    /// added would auto-merge a tree we never validated.
    /// </summary>
    [Fact]
    public async Task RealVerifier_NonExistentSha_ReportsBuildOrTestFailed()
    {
        var (gitHost, repoId, _) = await SetupBareRepoWithCommitAsync();
        var verifier = new LocalGitPreMergeVerifier(
            gitHost, NullLogger<LocalGitPreMergeVerifier>.Instance);

        // 40-hex zeros — well-formed shape, but won't resolve in the bare
        // repo (the empty-tree sha is different and not present either).
        const string unknownSha = "0000000000000000000000000000000000000001";
        var result = await verifier.VerifyAsync(new PreMergeVerifyRequest
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = "feature/x",
            MergeSha = unknownSha,
            Argv = ["/usr/bin/true"],
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PreMergeVerifyFailureMode.BuildOrTestFailed, result.FailureMode);
        Assert.Contains("could not check out merge sha", result.FailureReason);
        Assert.Contains(unknownSha, result.FailureReason);
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

/// <summary>
/// Minimal IGitHost that throws a configured exception from
/// <c>GetRepoPath</c>; everything else is unimplemented. Used to exercise
/// the verifier's <c>NotSupportedException</c> short-circuit without
/// instantiating a real bare repo.
/// </summary>
internal sealed class RepoPathThrowingHost : IGitHost
{
    private readonly Exception _toThrow;
    public RepoPathThrowingHost(Exception toThrow) { _toThrow = toThrow; }

    public string GetRepoPath(string repositoryId) => throw _toThrow;

    // The rest of IGitHost is unreachable for this test — every member is
    // a NotImplementedException so a bug that calls into them is loud.
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) => throw new NotImplementedException();
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => throw new NotImplementedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch, IReadOnlyDictionary<string, string> upstreamEnv, UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) => throw new NotImplementedException();
}

/// <summary>
/// Minimal IGitHost that returns a caller-supplied path from
/// <c>GetRepoPath</c> regardless of repository id. Used to drive the
/// verifier's <c>!Directory.Exists(bareRepoPath)</c> guard without
/// involving a real bare repo.
/// </summary>
internal sealed class RepoPathStubHost : IGitHost
{
    private readonly Func<string, string> _resolve;
    public RepoPathStubHost(Func<string, string> resolve) { _resolve = resolve; }

    public string GetRepoPath(string repositoryId) => _resolve(repositoryId);

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) => throw new NotImplementedException();
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => throw new NotImplementedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch, IReadOnlyDictionary<string, string> upstreamEnv, UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) => throw new NotImplementedException();
}
