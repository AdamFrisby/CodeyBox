namespace CodeyBox.Core;

/// <summary>
/// Manages the host-side bare repositories that sandboxes clone from and push
/// to. Provides the URL/endpoint sandboxes use, plus host-side operations
/// the orchestrator needs (push to upstream, inspect refs).
/// </summary>
public interface IGitHost
{
    /// <summary>
    /// Ensures a host-side bare repo exists for the given work item, seeded
    /// from <paramref name="seedFromUrl"/> if provided (typically the
    /// configured upstream). Returns a stable repository id.
    /// </summary>
    Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default);

    /// <summary>
    /// Ensures a host-side bare repo exists and, when already present, refreshes
    /// the configured base branch from <paramref name="seedFromUrl"/> without
    /// overwriting work-item branches. If no base branch is supplied, the
    /// upstream's advertised default branch is refreshed.
    /// </summary>
    Task<string> EnsureRepositoryAsync(
        WorkItemId id,
        string? seedFromUrl,
        string? baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Describes how a sandbox should be wired up to reach this repository.
    /// Encapsulates whichever transport the host has chosen (path bind-mount,
    /// git-daemon over network, etc.) so callers stay provider-agnostic.
    /// </summary>
    SandboxRepositoryAccess GetSandboxAccess(string repositoryId);

    /// <summary>Returns the host filesystem path for a managed bare repository.</summary>
    string GetRepoPath(string repositoryId)
        => throw new NotSupportedException("This git host does not expose a host repository path.");

    /// <summary>
    /// Gives hosts that expose agent-writable repositories a chance to clean
    /// or validate host-side git state before the orchestrator runs git
    /// commands directly against <see cref="GetRepoPath"/>. Remote-only hosts
    /// and simple test hosts can keep the default no-op.
    /// </summary>
    void PrepareRepositoryForHostGitOperations(string repositoryId)
    {
    }

    /// <summary>
    /// Returns the host directory where the merge / conflict-rework phase
    /// should stage an isolated bare clone. The contract is provider-agnostic:
    /// the returned directory MUST be usable as a bind-mount source by
    /// whichever <see cref="ISandboxProvider"/> the orchestrator is wired up
    /// with. Operators are responsible for configuring the host's bare-repo
    /// root to satisfy that constraint; the default below colocates merge
    /// staging with the durable bare repo so a single configured root covers
    /// both.
    ///
    /// <para>Default returns the bare-repo path's parent directory so the
    /// staged clone is a sibling of the durable bare repo and inherits
    /// whatever bind-mount property <see cref="GetRepoPath"/> already has.
    /// Hosts whose <see cref="GetRepoPath"/> does not sit in a
    /// sandbox-mountable directory must override this method.</para>
    /// </summary>
    string GetMergeStagingRoot(string repositoryId)
    {
        var repoPath = GetRepoPath(repositoryId);
        var parent = Path.GetDirectoryName(repoPath);
        // Guard null AND empty: on Unix, a bare-repo path with no directory
        // component (e.g. "id.git" relative path, or a hostile GetRepoPath
        // override) yields string.Empty rather than null from
        // Path.GetDirectoryName. Without the empty guard, the staging clone
        // would land in the orchestrator process CWD and bypass the
        // sandbox-mountable-root contract this method exists to enforce.
        return string.IsNullOrEmpty(parent)
            ? throw new InvalidOperationException(
                $"unable to derive merge staging root from bare repo path '{repoPath}'")
            : parent;
    }

    /// <summary>
    /// Describes how a sandbox should be wired up to reach an alternative
    /// host-side bare repo path (e.g. the merge / conflict-rework phase's
    /// isolated bare clone). The sandbox-side semantics match
    /// <see cref="GetSandboxAccess"/> so the agent observes the same clone
    /// URL/mount path it would under the normal flow.
    ///
    /// <para>Default throws — only hosts that bind-mount real bare repos
    /// need to implement this.</para>
    /// </summary>
    SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
        => throw new NotSupportedException(
            "This git host does not support sandbox access for an isolated bare repo path.");

    /// <summary>
    /// Name of the in-flight marker file the host writes inside a freshly
    /// staged merge clone to flag the directory as actively in use by the
    /// merge / conflict-rework pipeline. The marker exists for the lifetime
    /// of the orchestrator's create-then-mount window; any host-side
    /// cleanup (existing or future) MUST skip directories whose top level
    /// contains this file. Documented on the interface so the convention
    /// is visible to operators who write cron-driven cleanup scripts
    /// alongside the orchestrator. Hosts MAY additionally drop a sibling
    /// sentinel file alongside the directory; the sibling sentinel covers
    /// the brief create-window before any in-directory marker is writable.
    /// </summary>
    public const string IsolatedMergeCloneInFlightMarkerFileName = ".codeybox-merge-in-flight";

    /// <summary>
    /// Suffix the host appends to the staging clone's directory name to
    /// form a SIBLING marker file (next to the directory, in the staging
    /// root). The sibling marker is written BEFORE the host runs
    /// <c>git clone --bare</c> so external host-side cleanup honoring the
    /// marker convention also covers the clone window — the in-directory
    /// marker cannot be written before the clone because <c>git clone
    /// --bare</c> refuses non-empty targets. Operators implementing
    /// cleanup scripts must check for EITHER the in-directory marker OR
    /// the sibling sentinel to be safe across the full lifetime.
    /// </summary>
    public const string IsolatedMergeCloneInFlightSiblingSuffix = ".inflight";

    /// <summary>
    /// Stages an isolated bare clone of the work item's host repo so the
    /// merge / conflict-rework phase can mutate refs without touching the
    /// durable host bare repo. The returned host path lives under
    /// <see cref="GetMergeStagingRoot"/> and is suitable as a bind-mount
    /// source for the configured <see cref="ISandboxProvider"/>. The host
    /// is responsible for any on-disk verification the bare-repo layout
    /// requires (HEAD file, objects/, etc.) — the orchestrator only sees
    /// the returned path. The implementation MUST write the in-flight
    /// marker (<see cref="IsolatedMergeCloneInFlightMarkerFileName"/>)
    /// before returning so any concurrent host-side cleanup that honors
    /// the marker convention will skip the directory.
    ///
    /// <para>Default throws — hosts that don't model a real bare repo
    /// don't implement this method (and the merge / conflict-rework
    /// flows do not run against them).</para>
    /// </summary>
    Task<string> CreateIsolatedMergeCloneAsync(
        string repositoryId,
        WorkItemId workItemId,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This git host does not support isolated merge clone staging.");

    /// <summary>
    /// Neutral entry point used by phases that need an isolated bare clone
    /// for read-only or scratch-write inspection (e.g. the required-build
    /// gate). The clone is staged under the same root and with the same
    /// in-flight-marker semantics that
    /// <see cref="CreateIsolatedMergeCloneAsync"/> uses; the difference is
    /// purely intent — these callers do not perform merge resolution and
    /// must not depend on merge-specific protocol around the clone.
    ///
    /// <para>The <paramref name="lifetimeId"/> is the work item that owns
    /// the clone's lifetime so concurrent reapers can tie a stranded clone
    /// back to its work item.</para>
    ///
    /// <para>Default delegates to <see cref="CreateIsolatedMergeCloneAsync"/>
    /// so existing hosts keep working without per-call-site implementations;
    /// hosts that want to surface non-merge clones differently (separate
    /// staging root, alternative marker convention) can override here
    /// without affecting merge callers.</para>
    /// </summary>
    Task<string> CreateIsolatedRepositoryCloneAsync(
        string repositoryId,
        WorkItemId lifetimeId,
        CancellationToken ct = default)
        => CreateIsolatedMergeCloneAsync(repositoryId, lifetimeId, ct);

    /// <summary>
    /// Re-stages the isolated bare clone at <paramref name="targetPath"/>
    /// after the host directory has gone missing between create-time and
    /// mount-time (e.g. tmpwatch reaping, future host-side cleanup, or an
    /// orchestrator retry from a stranded prior attempt). The implementation
    /// MUST refuse paths outside its staging root before any filesystem
    /// mutation — orchestrator code calls this with a path it received from
    /// <see cref="CreateIsolatedMergeCloneAsync"/>, but a defensive
    /// containment check here prevents a future wiring bug from turning the
    /// restore into an arbitrary directory delete. The implementation MUST
    /// re-write the in-flight marker after restoring so the directory's
    /// in-flight state is observable again.
    /// </summary>
    Task RestoreIsolatedMergeCloneAsync(
        string repositoryId,
        string targetPath,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This git host does not support isolated merge clone restore.");

    /// <summary>
    /// Removes the host-side artifacts produced by
    /// <see cref="CreateIsolatedMergeCloneAsync"/>: the staging bare-clone
    /// directory itself AND any in-flight markers (in-directory or sibling)
    /// the host wrote. Best-effort — implementations log failures rather
    /// than throwing so a partial cleanup does not mask the original merge
    /// outcome.
    ///
    /// <para>Default falls back to a plain recursive directory delete for
    /// hosts that don't track auxiliary marker files. Operators wiring a
    /// custom host that writes markers must override this to clean up the
    /// marker alongside the directory.</para>
    /// </summary>
    Task DisposeIsolatedMergeCloneAsync(
        string repositoryId,
        string targetPath,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath))
        {
            try { Directory.Delete(targetPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort
                _ = ex;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Neutral disposal entry point that pairs with
    /// <see cref="CreateIsolatedRepositoryCloneAsync"/>. Removes the staged
    /// clone directory and any host-written marker artifacts. Best-effort:
    /// implementations log failures rather than throwing so a partial
    /// cleanup does not mask the caller's primary outcome.
    ///
    /// <para>Default delegates to <see cref="DisposeIsolatedMergeCloneAsync"/>
    /// so the on-disk cleanup surface is single-sourced; hosts overriding the
    /// neutral create may also override this for a matched disposal.</para>
    /// </summary>
    Task DisposeIsolatedRepositoryCloneAsync(
        string repositoryId,
        string targetPath,
        CancellationToken ct = default)
        => DisposeIsolatedMergeCloneAsync(repositoryId, targetPath, ct);

    /// <summary>Returns the resolved default branch name for the repo.</summary>
    Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>Pushes a branch from the host bare repo to a configured upstream URL.</summary>
    Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches a branch from the configured upstream URL into the host bare
    /// repo, overwriting the local ref of the same name. Returns the new sha
    /// the local ref points at, or <c>null</c> when the upstream does not
    /// advertise the branch. Throws on transport / auth errors.
    ///
    /// Default returns <c>null</c> for hosts that do not model an upstream
    /// (so test fakes that don't implement the call behave as "branch not
    /// found" rather than crashing).
    /// </summary>
    Task<string?> FetchUpstreamBranchAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Force-sets <paramref name="branch"/> in the host bare repo to point at
    /// <paramref name="sha"/>. Used by the auto-merge race recovery to advance
    /// the work branch so its tip contains the freshly-resolved merge commit
    /// (which has both upstream base and original work branch tip as parents),
    /// making a subsequent push-and-merge succeed cleanly.
    ///
    /// Default throws — only hosts that expose a real bare repo need to
    /// implement this.
    /// </summary>
    Task SetBranchToCommitAsync(
        string repositoryId,
        string branch,
        string sha,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side branch updates.");

    /// <summary>Discards the host-side state for a finished work item.</summary>
    Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a host-side bare repo for the given work item id is
    /// present and usable. Used by the retry endpoint to validate that
    /// "resume from a later phase" is actually possible — the resumed
    /// phases need the prior phase's branch/merge state in the bare repo.
    /// </summary>
    Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Returns true if <paramref name="branch"/> resolves to a commit in the
    /// host bare repo. Used by the retry endpoint to decide whether a
    /// post-work-phase resume (audit/merge/upstream) actually has the work
    /// branch the pipeline will try to check out — when the work phase died
    /// before producing a commit, the branch is absent and the next phase
    /// would fail with "pathspec did not match any file(s)".
    ///
    /// Default returns <c>true</c> ("don't know, assume yes") so test fakes
    /// that don't implement the check behave as before.
    /// </summary>
    Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(true);

    /// <summary>
    /// Returns true when <paramref name="workBranch"/> has at least one commit
    /// not reachable from <paramref name="baseBranch"/> in the host bare repo
    /// (equivalent to <c>git rev-list --count base..work &gt; 0</c>). Used by
    /// the retry endpoint to auto-pick a sensible resume phase: a work branch
    /// with prior commits should re-audit before discarding work, an empty
    /// work branch should re-run the work phase.
    ///
    /// Implementations should fail loudly when the comparison cannot be trusted
    /// (for example, when a compared branch cannot resolve or git exits
    /// non-zero). Callers that want "fresh start" behavior for expected missing
    /// state should preflight that state before calling this probe.
    ///
    /// Default returns <c>false</c> ("don't know, assume no commits ahead")
    /// so test fakes that don't implement the check behave as before.
    /// </summary>
    Task<bool> BranchHasCommitsAheadAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Returns <c>git diff --stat</c> and <c>git diff</c> output comparing
    /// <paramref name="baseBranch"/> and <paramref name="workBranch"/> in the
    /// host bare repo. Returns empty strings when the diff cannot be computed
    /// (e.g. repo not found, branch does not exist). Never throws.
    /// </summary>
    Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Computes git's canonical host-side merge tree for two commits in the
    /// host bare repo. A non-zero git exit from content conflicts is returned
    /// as <see cref="GitMergeTreeResult.HasConflicts"/> rather than thrown.
    /// </summary>
    Task<GitMergeTreeResult> ComputeMergeTreeAsync(
        string repositoryId,
        string mainCommit,
        string workCommit,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side merge-tree verification.");

    /// <summary>
    /// Creates a merge commit in the host bare repo directly from an
    /// already-computed merge tree (see <see cref="ComputeMergeTreeAsync"/>)
    /// with the two given parents, and returns the new commit sha. Used to land
    /// a clean (non-conflicting) merge entirely host-side — no sandbox, no
    /// agent — since a clean three-way merge is deterministic git plumbing.
    /// </summary>
    Task<string> CreateMergeCommitAsync(
        string repositoryId,
        string treeSha,
        string firstParentCommit,
        string secondParentCommit,
        string message,
        string authorName,
        string authorEmail,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side merge commits.");

    /// <summary>Resolves a ref or commit expression in the host bare repo.</summary>
    Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side commit resolution.");

    /// <summary>
    /// Resets <paramref name="workBranch"/> in the host bare repo so it points
    /// at <paramref name="baseBranch"/>'s head, discarding any prior-attempt
    /// commits on the work branch. If the work branch does not exist, it is
    /// created at the base tip. The base branch must resolve to a commit;
    /// otherwise the call throws. Implementations must verify the post-reset
    /// tip and fail loudly if it does not equal the base head.
    ///
    /// Called from the retry-from-work flow so the agent's next invocation
    /// observes a pristine base state — the bug this guards against is a
    /// fail-quiet "Agent produced no changes to commit" outcome when the
    /// retried agent picks up its prior failed-attempt's commits and decides
    /// the work is already done.
    /// </summary>
    Task ResetWorkBranchToBaseAsync(
        string repositoryId,
        string workBranch,
        string baseBranch,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side work-branch reset.");

    /// <summary>Returns the tree object for a commit or tree-ish expression.</summary>
    Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side tree resolution.");

    /// <summary>Reads a text file from a commit or tree in the host bare repo.</summary>
    Task<string> ReadTextFileAsync(string repositoryId, string treeish, string path, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side file reads.");

    /// <summary>
    /// Lists repository-relative files under a path prefix from a commit or tree.
    /// Pass an empty or null <paramref name="pathPrefix"/> to list every file in the tree.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side file listing.");

    /// <summary>
    /// Returns repository-relative paths in <paramref name="treeish"/> whose final
    /// filename ends (case-insensitive) with any of <paramref name="filenameSuffixes"/>.
    /// Production implementations should stream the underlying tree listing and
    /// filter line by line so a huge tree cannot consume unbounded host memory.
    /// Stops accumulating once <paramref name="maxResults"/> matches have been
    /// collected and throws if any additional matches exist, so callers can treat
    /// the tree as too large to safely inspect rather than silently returning
    /// partial data.
    ///
    /// <para>The default implementation falls back to <see cref="ListFilesAsync"/>
    /// and filters client-side — convenient for hosts that don't need the streaming
    /// bound (e.g. test fakes) but still gives callers the same cap semantics.
    /// Hosts that talk to real git (where unbounded memory IS a concern) override
    /// this with a streaming implementation.</para>
    /// </summary>
    async Task<IReadOnlyList<string>> ListFilesEndingWithAsync(
        string repositoryId,
        string treeish,
        IReadOnlyList<string> filenameSuffixes,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filenameSuffixes);
        if (filenameSuffixes.Count == 0)
            throw new ArgumentException("at least one filename suffix is required", nameof(filenameSuffixes));
        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults), maxResults, "must be positive");

        var all = await ListFilesAsync(repositoryId, treeish, pathPrefix: null, ct);
        var matches = new List<string>();
        foreach (var p in all)
        {
            var ok = false;
            foreach (var s in filenameSuffixes)
            {
                if (p.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            }
            if (!ok) continue;
            if (matches.Count >= maxResults)
            {
                throw new InvalidOperationException(
                    $"tree listing produced more than {maxResults} matching paths (output cap exceeded)");
            }
            matches.Add(p);
        }
        return matches;
    }

    /// <summary>Returns name-status changes between two commits or trees in the host bare repo.</summary>
    Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side diff inspection.");

    /// <summary>Returns a zero-context diff for one path between two commits or trees.</summary>
    Task<string> GetUnifiedDiffAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        string path,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side diff inspection.");
}

public sealed record GitMergeTreeResult(
    bool HasConflicts,
    string TreeSha,
    IReadOnlyList<string> ConflictedFiles,
    string RawOutput);

public sealed record GitChangedPath(
    string Status,
    string Path,
    string? OldPath = null);

/// <summary>
/// All the bits the orchestrator needs to fold into a <see cref="SandboxSpec"/>
/// so a sandbox can clone/push the repository: any mounts the sandbox should
/// receive, the network allowance for git traffic, and the URL the sandbox
/// should pass to "git clone" once those are in place.
/// </summary>
public sealed record SandboxRepositoryAccess(
    string CloneUrlInsideSandbox,
    IReadOnlyList<SandboxMount> Mounts,
    SandboxNetworkPolicy Network);

/// <summary>How a stale local branch should be reconciled after an upstream non-fast-forward rejection.</summary>
public enum UpstreamPushReconcileStrategy
{
    /// <summary>Replay local commits on top of the latest upstream tip before retrying the push.</summary>
    Rebase,

    /// <summary>Merge the latest upstream tip into the local branch before retrying the push.</summary>
    Merge,
}
