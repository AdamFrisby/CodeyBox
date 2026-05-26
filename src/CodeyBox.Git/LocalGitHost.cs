using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Git;

/// <summary>
/// Manages bare repositories on the host filesystem. Exposes them to sandboxes
/// via a stable local path (which providers either bind-mount or front with a
/// git-daemon). Pushes to upstream remotes use the orchestrator's credentials,
/// not anything visible to a sandbox.
/// </summary>
public sealed class LocalGitHost : IGitHost
{
    private static readonly ConcurrentDictionary<string, RepositoryLockState> RepositoryLocks = new(StringComparer.Ordinal);
    private static readonly Regex UrlUserInfoPattern = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^/\s@]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly LocalGitHostOptions _opts;
    private readonly ILogger<LocalGitHost> _log;
    private readonly string _disabledHooksPath;

    public LocalGitHost(LocalGitHostOptions opts, ILogger<LocalGitHost> log)
    {
        _opts = opts;
        _log = log;
        Directory.CreateDirectory(_opts.RootDirectory);
        _disabledHooksPath = Path.Combine(_opts.RootDirectory, ".codeybox-disabled-hooks");
        Directory.CreateDirectory(_disabledHooksPath);
    }

    public async Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => await EnsureRepositoryAsync(id, seedFromUrl, baseBranch: null, ct);

    public async Task<string> EnsureRepositoryAsync(
        WorkItemId id,
        string? seedFromUrl,
        string? baseBranch,
        CancellationToken ct = default)
    {
        var repoId = id.ToString();
        var path = GetRepoPath(repoId);
        var gate = await AcquireRepositoryLockAsync(path, ct);
        try
        {
            if (Directory.Exists(path))
            {
                if (seedFromUrl is not null)
                    await FetchUpstreamAsync(path, seedFromUrl, baseBranch, ct);
                return repoId;
            }

            if (seedFromUrl is not null)
                Validation.ValidateRepositoryUrl(seedFromUrl, nameof(seedFromUrl));

            Directory.CreateDirectory(path);
            if (seedFromUrl is not null)
            {
                // git clone --bare -- <url> <path>
                //
                // The `--` separator stops git treating a URL like
                // "--upload-pack=evil-cmd" as an option. ArgumentList.Add already
                // prevents shell injection; the `--` defends against git's own
                // option parser.
                var rc = await RunGitAsync(workdir: _opts.RootDirectory, ct, "clone", "--bare", "--", seedFromUrl, path);
                if (rc.ExitCode != 0)
                {
                    Directory.Delete(path, recursive: true);
                    throw new InvalidOperationException($"Failed to seed bare repo from {seedFromUrl}: {rc.Stderr}");
                }
            }
            else
            {
                Repository.Init(path, isBare: true);
            }

            // Allow non-fast-forward pushes from the work sandbox to its branch.
            // The receive hook is conservative: protect the default/target branch.
            ApplyReceivePolicy(path);
            return repoId;
        }
        finally
        {
            gate.Dispose();
        }
    }

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
    {
        // Bind-mount ONLY this work item's bare repo into the sandbox. A
        // compromised agent in work item A must not be able to read or write
        // work item B's bare repo. The mount target is the per-id bare repo
        // path; nothing else from the bare-repos root is exposed.
        //
        // VM-backed providers can substitute a git-daemon exposing the same
        // single repo over a unix socket — the per-item scoping is preserved.
        var mount = new SandboxMount
        {
            SandboxPath = SandboxRepoMountPath,
            HostPath = GetRepoPath(repositoryId),
            ReadOnly = false,
        };
        return new SandboxRepositoryAccess(SandboxRepoMountPath, [mount], SandboxNetworkPolicy.Denied);
    }

    /// <summary>Sandbox-side path the bare repo is mounted at. Per-item scoped.</summary>
    public const string SandboxRepoMountPath = "/repo";

    public async Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        using var repo = new Repository(path);
        var head = repo.Refs["HEAD"] as SymbolicReference;
        if (head?.Target?.CanonicalName is { } target && target.StartsWith("refs/heads/", StringComparison.Ordinal))
            return target["refs/heads/".Length..];
        // If the repo was just init'd and has no commits yet, fall back.
        await Task.CompletedTask;
        return _opts.FallbackDefaultBranch;
    }

    public async Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
    {
        Validation.ValidateRepositoryUrl(upstreamUrl, nameof(upstreamUrl));
        Validation.ValidateBranchName(branch, nameof(branch));
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        // git push [<repository> [<refspec>...]] — push doesn't support `--`
        // before <repository>, so we rely on URL validation above to ensure
        // the URL is well-formed and not option-like.
        var rc = await RunGitAsync(
            workdir: path,
            ct,
            extraEnv: upstreamEnv,
            "push", upstreamUrl, $"{branch}:{branch}");
        if (rc.ExitCode == 0)
            return;

        if (!IsNonFastForwardRejection(rc.Stdout, rc.Stderr))
            throw new InvalidOperationException($"git push to upstream failed: {rc.Stderr}");

        await ReconcileRejectedUpstreamPushAsync(path, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);

        rc = await RunGitAsync(
            workdir: path,
            ct,
            extraEnv: upstreamEnv,
            "push", upstreamUrl, $"{branch}:{branch}");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git push to upstream failed after reconcile: {rc.Stderr}");
    }

    public async Task<string?> FetchUpstreamBranchAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
    {
        Validation.ValidateRepositoryUrl(upstreamUrl, nameof(upstreamUrl));
        Validation.ValidateBranchName(branch, nameof(branch));
        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"bare repo for '{repositoryId}' does not exist at {path}");
        SanitizeBareRepositoryConfig(path);

        // Force-update local refs/heads/<branch> to upstream's tip — this is
        // the race-recovery path; we WANT to overwrite the local view because
        // we just discovered upstream has moved.
        var fetch = await RunGitAsync(
            workdir: path,
            ct,
            extraEnv: upstreamEnv,
            "fetch", "--no-tags", upstreamUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
        if (fetch.ExitCode != 0)
            throw new InvalidOperationException(
                $"git fetch upstream branch '{branch}' failed: {fetch.Stderr}");

        var revParse = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
        if (revParse.ExitCode != 0)
            return null;
        return revParse.Stdout.Trim();
    }

    public async Task SetBranchToCommitAsync(
        string repositoryId,
        string branch,
        string sha,
        CancellationToken ct = default)
    {
        Validation.ValidateBranchName(branch, nameof(branch));
        Validation.ValidateCommitSha(sha, nameof(sha));

        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"bare repo for '{repositoryId}' does not exist at {path}");
        SanitizeBareRepositoryConfig(path);

        // Verify the target sha resolves before pointing the ref at it; an
        // invalid sha would silently break the branch ref.
        var verify = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", $"{sha}^{{commit}}");
        if (verify.ExitCode != 0)
            throw new InvalidOperationException(
                $"cannot set branch '{branch}' to '{sha}': sha did not resolve to a commit: {verify.Stderr}");

        var update = await RunGitAsync(
            workdir: path, ct,
            "update-ref", $"refs/heads/{branch}", sha);
        if (update.ExitCode != 0)
            throw new InvalidOperationException(
                $"git update-ref to set '{branch}' to {sha} failed: {update.Stderr}");
    }

    public async Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        var gate = await AcquireRepositoryLockAsync(path, ct);
        try
        {
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to delete bare repo at {Path}", path); }
            }

            MarkRepositoryLockForEviction(path, gate.State);
        }
        finally
        {
            gate.Dispose();
        }
    }

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
    {
        var path = GetRepoPath(id.ToString());
        return Task.FromResult(Directory.Exists(path));
    }

    public async Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
    {
        Validation.ValidateBranchName(branch, nameof(branch));
        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            return false;
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
        return rc.ExitCode == 0;
    }

    public async Task<bool> BranchHasCommitsAheadAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
    {
        Validation.ValidateBranchName(baseBranch, nameof(baseBranch));
        Validation.ValidateBranchName(workBranch, nameof(workBranch));

        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"bare repo for '{repositoryId}' does not exist at {path}");

        SanitizeBareRepositoryConfig(path);
        var baseRef = $"refs/heads/{baseBranch}";
        var workRef = $"refs/heads/{workBranch}";
        var baseResolved = await RunGitAsync(path, ct, "rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}");
        if (baseResolved.ExitCode != 0)
            throw new InvalidOperationException(
                $"cannot compare branch ahead state: base branch '{baseBranch}' did not resolve to a commit: {baseResolved.Stderr}");

        var workResolved = await RunGitAsync(path, ct, "rev-parse", "--verify", "--quiet", $"{workRef}^{{commit}}");
        if (workResolved.ExitCode != 0)
            throw new InvalidOperationException(
                $"cannot compare branch ahead state: work branch '{workBranch}' did not resolve to a commit: {workResolved.Stderr}");

        // rev-list --count base..work prints "0" when work has no commits the
        // base branch doesn't already have. Use `--` to defend against the
        // unlikely case a branch name parses as a path-like rev. Branch names
        // are validated above so they cannot start with "-".
        var rc = await RunGitAsync(path, ct, "rev-list", "--count", $"{baseRef}..{workRef}", "--");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git rev-list failed while comparing '{baseBranch}'..'{workBranch}': {rc.Stderr}");

        if (!int.TryParse(rc.Stdout.Trim(), out var count))
            throw new InvalidOperationException(
                $"git rev-list returned a non-numeric ahead count while comparing '{baseBranch}'..'{workBranch}': {rc.Stdout}");

        return count > 0;
    }

    public async Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch,
        CancellationToken ct = default)
    {
        try
        {
            Validation.ValidateBranchName(baseBranch, nameof(baseBranch));
            Validation.ValidateBranchName(workBranch, nameof(workBranch));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }

        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            return (string.Empty, string.Empty);

        // Use three-dot range so we diff the work branch tip against the
        // merge-base with base, not the current base tip — the same semantics
        // a GitHub PR diff uses.
        var range = $"{baseBranch}...{workBranch}";
        var stat = await RunGitAsync(path, ct, "diff", "--stat", range);
        var diff = await RunGitAsync(path, ct, "diff", range);
        return (
            stat.ExitCode == 0 ? stat.Stdout : string.Empty,
            diff.ExitCode == 0 ? diff.Stdout : string.Empty
        );
    }

    public string GetRepoPath(string repositoryId) => Path.Combine(_opts.RootDirectory, repositoryId + ".git");

    public async Task<GitMergeTreeResult> ComputeMergeTreeAsync(
        string repositoryId,
        string mainCommit,
        string workCommit,
        CancellationToken ct = default)
    {
        Validation.ValidateCommitSha(mainCommit, nameof(mainCommit));
        Validation.ValidateCommitSha(workCommit, nameof(workCommit));
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);

        var rc = await RunGitAsync(
            workdir: path,
            ct,
            "merge-tree", "--write-tree", "--no-messages", mainCommit, workCommit);
        var lines = rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !LooksLikeSha(lines[0]))
            throw new InvalidOperationException($"git merge-tree did not return a tree: {rc.Stderr}{rc.Stdout}");

        var conflicted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var tab = line.LastIndexOf('\t');
            if (tab >= 0 && tab + 1 < line.Length)
                conflicted.Add(line[(tab + 1)..]);
        }

        return new GitMergeTreeResult(
            HasConflicts: rc.ExitCode != 0 || conflicted.Count > 0,
            TreeSha: lines[0].Trim(),
            ConflictedFiles: [.. conflicted],
            RawOutput: rc.Stdout);
    }

    public async Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "rev-parse", "--verify", $"{commitish}^{{commit}}");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse commit '{commitish}' failed: {rc.Stderr}");
        return rc.Stdout.Trim();
    }

    public async Task ResetWorkBranchToBaseAsync(
        string repositoryId,
        string workBranch,
        string baseBranch,
        CancellationToken ct = default)
    {
        Validation.ValidateBranchName(workBranch, nameof(workBranch));
        Validation.ValidateBranchName(baseBranch, nameof(baseBranch));
        if (string.Equals(workBranch, baseBranch, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"refusing to reset workBranch '{workBranch}' onto itself; baseBranch must differ");

        var path = GetRepoPath(repositoryId);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"bare repo for '{repositoryId}' does not exist at {path}");
        SanitizeBareRepositoryConfig(path);

        var baseResolved = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", $"refs/heads/{baseBranch}^{{commit}}");
        if (baseResolved.ExitCode != 0)
            throw new InvalidOperationException(
                $"cannot reset work branch '{workBranch}': base branch '{baseBranch}' did not resolve to a commit: {baseResolved.Stderr}");
        var baseSha = baseResolved.Stdout.Trim();

        var existing = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", $"refs/heads/{workBranch}^{{commit}}");
        if (existing.ExitCode == 0)
        {
            var oldSha = existing.Stdout.Trim();
            if (!string.Equals(oldSha, baseSha, StringComparison.Ordinal))
            {
                var update = await RunGitAsync(
                    workdir: path, ct,
                    "update-ref", $"refs/heads/{workBranch}", baseSha, oldSha);
                if (update.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"git update-ref to reset '{workBranch}' from {oldSha} to base '{baseBranch}' ({baseSha}) failed: {update.Stderr}");
                }
                _log.LogInformation(
                    "Reset work branch '{WorkBranch}' in bare repo {RepoId} from {OldTip} to base '{BaseBranch}' tip {BaseTip} for fresh work-phase entry",
                    workBranch, repositoryId, oldSha, baseBranch, baseSha);
            }
        }

        var verify = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", $"refs/heads/{workBranch}^{{commit}}");
        if (verify.ExitCode == 0)
        {
            var verifiedSha = verify.Stdout.Trim();
            if (!string.Equals(verifiedSha, baseSha, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"work branch '{workBranch}' tip after reset is {verifiedSha}, expected base '{baseBranch}' tip {baseSha}");
            }
        }
    }

    public async Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "rev-parse", "--verify", $"{treeish}^{{tree}}");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse tree '{treeish}' failed: {rc.Stderr}");
        return rc.Stdout.Trim();
    }

    public async Task<string> ReadTextFileAsync(string repositoryId, string treeish, string filePath, CancellationToken ct = default)
    {
        ValidateRepositoryRelativePath(filePath);
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "show", $"{treeish}:{filePath}");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git show '{treeish}:{filePath}' failed: {rc.Stderr}");
        return rc.Stdout;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string pathPrefix, CancellationToken ct = default)
    {
        ValidateRepositoryRelativePath(pathPrefix);
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "ls-tree", "-r", "--name-only", treeish, "--", pathPrefix);
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git ls-tree '{treeish}:{pathPrefix}' failed: {rc.Stderr}");
        return rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(path, ct, "diff", "--name-status", "-M", fromTreeish, toTreeish);
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git diff --name-status failed: {rc.Stderr}");

        var changes = new List<GitChangedPath>();
        foreach (var line in rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.None);
            if (parts.Length == 2)
                changes.Add(new GitChangedPath(parts[0], parts[1]));
            else if (parts.Length == 3)
                changes.Add(new GitChangedPath(parts[0], parts[2], parts[1]));
        }
        return changes;
    }

    public async Task<string> GetUnifiedDiffAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        string filePath,
        CancellationToken ct = default)
    {
        ValidateRepositoryRelativePath(filePath);
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = await RunGitAsync(
            path, ct, "diff", "--no-ext-diff", "--no-color", "--unified=0", fromTreeish, toTreeish, "--", filePath);
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git diff for '{filePath}' failed: {rc.Stderr}");
        return rc.Stdout;
    }

    private static async Task<RepositoryLockLease> AcquireRepositoryLockAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            var state = RepositoryLocks.GetOrAdd(path, static _ => new RepositoryLockState());
            Interlocked.Increment(ref state.References);
            if (!RepositoryLocks.TryGetValue(path, out var current) ||
                !ReferenceEquals(current, state) ||
                Volatile.Read(ref state.EvictWhenIdle) != 0)
            {
                ReleaseRepositoryLockReference(path, state);
                continue;
            }

            try
            {
                await state.Semaphore.WaitAsync(ct);
            }
            catch
            {
                ReleaseRepositoryLockReference(path, state);
                throw;
            }

            if (Volatile.Read(ref state.EvictWhenIdle) == 0)
                return new RepositoryLockLease(path, state);

            state.Semaphore.Release();
            ReleaseRepositoryLockReference(path, state);
        }
    }

    private static void MarkRepositoryLockForEviction(string path, RepositoryLockState state)
    {
        Volatile.Write(ref state.EvictWhenIdle, 1);
        RemoveRepositoryLockIfIdle(path, state);
    }

    private static void ReleaseRepositoryLockReference(string path, RepositoryLockState state)
    {
        if (Interlocked.Decrement(ref state.References) == 0)
            RemoveRepositoryLockIfIdle(path, state);
    }

    private static void RemoveRepositoryLockIfIdle(string path, RepositoryLockState state)
    {
        if (Volatile.Read(ref state.EvictWhenIdle) == 0 ||
            Volatile.Read(ref state.References) != 0)
        {
            return;
        }

        ((ICollection<KeyValuePair<string, RepositoryLockState>>)RepositoryLocks)
            .Remove(new KeyValuePair<string, RepositoryLockState>(path, state));
    }

    private static bool IsNonFastForwardRejection(string stdout, string stderr)
    {
        var output = stdout + "\n" + stderr;
        return output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || output.Contains("! [rejected]", StringComparison.OrdinalIgnoreCase)
            || output.Contains("fetch first", StringComparison.OrdinalIgnoreCase);
    }

    private async Task FetchUpstreamAsync(
        string bareRepoPath,
        string seedFromUrl,
        string? baseBranch,
        CancellationToken ct)
    {
        Validation.ValidateRepositoryUrl(seedFromUrl, nameof(seedFromUrl));
        var safeUpstream = ScrubCredentialMaterial(seedFromUrl);

        SanitizeBareRepositoryConfig(bareRepoPath);
        var branch = await ResolveRefreshBranchAsync(bareRepoPath, seedFromUrl, baseBranch, ct);
        if (branch is null)
        {
            _log.LogWarning(
                "Skipped bare repo refresh for {Path}: no configured base branch and upstream {Upstream} did not advertise a default branch",
                bareRepoPath, safeUpstream);
            return;
        }

        Validation.ValidateBranchName(branch, nameof(baseBranch));
        var rc = await RunGitAsync(
            workdir: bareRepoPath,
            ct,
            "fetch", "--no-tags", "--prune", seedFromUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
        if (rc.ExitCode != 0)
        {
            _log.LogWarning(
                "Failed to refresh bare repo {Path} branch {Branch} from upstream {Upstream}: {Stderr}",
                bareRepoPath, branch, safeUpstream, ScrubCredentialMaterial(rc.Stderr));
        }
    }

    private async Task<string?> ResolveRefreshBranchAsync(
        string bareRepoPath,
        string seedFromUrl,
        string? baseBranch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(baseBranch))
            return baseBranch;

        // HEAD lives inside the sandbox-writable bare repo, so do not use it
        // to choose what host-side refresh should fetch. Ask the upstream for
        // its advertised default branch under the host-controlled git config.
        var rc = await RunGitAsync(bareRepoPath, ct, "ls-remote", "--symref", seedFromUrl, "HEAD");
        if (rc.ExitCode != 0)
        {
            _log.LogWarning(
                "Failed to resolve upstream default branch for bare repo {Path} from {Upstream}: {Stderr}",
                bareRepoPath,
                ScrubCredentialMaterial(seedFromUrl),
                ScrubCredentialMaterial(rc.Stderr));
            return null;
        }

        foreach (var line in rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "ref: refs/heads/";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var branch = line[prefix.Length..].Split('\t', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(branch))
                return branch;
        }

        _log.LogDebug(
            "Upstream {Upstream} did not advertise a symbolic HEAD while refreshing bare repo {Path}",
            ScrubCredentialMaterial(seedFromUrl),
            bareRepoPath);
        return null;
    }

    private static void SanitizeBareRepositoryConfig(string bareRepoPath)
    {
        var configPath = Path.Combine(bareRepoPath, "config");
        var tempPath = Path.Combine(bareRepoPath, "config.codeybox-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(
            tempPath,
            """
            [core]
                repositoryformatversion = 0
                filemode = true
                bare = true

            """);
        File.Move(tempPath, configPath, overwrite: true);
    }

    private static bool LooksLikeSha(string value)
        => value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);

    private static void ValidateRepositoryRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(p => p is "" or "." or ".."))
        {
            throw new ArgumentException("Path must be repository-relative and must not contain traversal segments.", nameof(path));
        }
    }

    private static string ScrubCredentialMaterial(string value)
        => UrlUserInfoPattern.Replace(RawOutputRedactor.Redact(value), "${scheme}***@");

    private async Task ReconcileRejectedUpstreamPushAsync(
        string bareRepoPath,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy,
        CancellationToken ct)
    {
        var upstreamRef = $"refs/remotes/codeybox-upstream/{branch}";
        var fetch = await RunGitAsync(
            workdir: bareRepoPath,
            ct,
            extraEnv: upstreamEnv,
            "fetch", "--no-tags", upstreamUrl, $"+refs/heads/{branch}:{upstreamRef}");
        if (fetch.ExitCode != 0)
            throw new InvalidOperationException($"git fetch upstream branch '{branch}' failed: {fetch.Stderr}");

        var worktreePath = Path.Combine(Path.GetTempPath(), "codeybox-upstream-reconcile-" + Guid.NewGuid().ToString("N"));
        var worktreeAdded = false;
        try
        {
            var add = await RunGitAsync(bareRepoPath, ct, "worktree", "add", worktreePath, branch);
            if (add.ExitCode != 0)
                throw new InvalidOperationException($"git worktree add for upstream reconcile failed: {add.Stderr}");
            worktreeAdded = true;

            if (reconcileStrategy == UpstreamPushReconcileStrategy.Merge)
            {
                var pull = await RunGitAsync(
                    workdir: worktreePath,
                    ct,
                    extraEnv: upstreamEnv,
                    "-c", "user.name=CodeyBox",
                    "-c", "user.email=codeybox@localhost",
                    "pull", "--no-rebase", "--no-edit", upstreamUrl, branch);
                if (pull.ExitCode != 0)
                {
                    await RunGitAsync(worktreePath, CancellationToken.None, "merge", "--abort");
                    throw new UpstreamPushReconcileConflictException(branch, "merge");
                }
                return;
            }

            var rebase = await RunGitAsync(
                worktreePath,
                ct,
                "-c", "user.name=CodeyBox",
                "-c", "user.email=codeybox@localhost",
                "rebase", upstreamRef);
            if (rebase.ExitCode != 0)
            {
                await RunGitAsync(worktreePath, CancellationToken.None, "rebase", "--abort");
                throw new UpstreamPushReconcileConflictException(branch, "rebase");
            }
        }
        finally
        {
            if (worktreeAdded)
                await RunGitAsync(bareRepoPath, CancellationToken.None, "worktree", "remove", "--force", worktreePath);
            if (Directory.Exists(worktreePath))
            {
                try
                {
                    Directory.Delete(worktreePath, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "Failed to remove upstream reconcile worktree at {Path}", worktreePath);
                }
            }
        }
    }

    private static void ApplyReceivePolicy(string barePath)
    {
        // Allow receiving into any branch; orchestration logic, not git hooks,
        // determines what gets merged where. Hooks can be added later for
        // defence in depth (e.g. block direct pushes to main from work phase).
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir,
        CancellationToken ct,
        params string[] args)
        => await RunGitAsync(workdir, ct, extraEnv: null, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv,
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"core.hooksPath={_disabledHooksPath}");
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv) psi.EnvironmentVariables[k] = v;

        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, stdout, stderr);
    }

    private sealed class RepositoryLockState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References;
        public int EvictWhenIdle;
    }

    private sealed class RepositoryLockLease : IDisposable
    {
        private readonly string _path;
        private RepositoryLockState? _state;

        public RepositoryLockLease(string path, RepositoryLockState state)
        {
            _path = path;
            _state = state;
        }

        public RepositoryLockState State =>
            _state ?? throw new ObjectDisposedException(nameof(RepositoryLockLease));

        public void Dispose()
        {
            var state = _state;
            if (state is null)
                return;

            _state = null;
            state.Semaphore.Release();
            ReleaseRepositoryLockReference(_path, state);
        }
    }
}

public sealed record LocalGitHostOptions
{
    public required string RootDirectory { get; init; }
    public string FallbackDefaultBranch { get; init; } = "main";
}
