using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    // git update-ref treats an all-zero old oid as "create this ref only if it
    // does not already exist"; bare repos here use the normal 40-hex SHA-1 oid.
    private const string GitNullObjectId = "0000000000000000000000000000000000000000";
    private const int GitStartTextFileBusyMaxAttempts = 8;
    private const int GitStartTextFileBusyDelayStepMilliseconds = 25;
    // POSIX ETXTBSY. On Linux, Process.Start surfaces this as Win32Exception(26)
    // when another process briefly has the executable open for writing.
    private const int PosixTextFileBusyErrno = 26;

    private readonly LocalGitHostOptions _opts;
    private readonly ILogger<LocalGitHost> _log;
    private readonly string _disabledHooksPath;
    private readonly Func<ProcessStartInfo, ILocalGitProcess> _processFactory;

    public LocalGitHost(LocalGitHostOptions opts, ILogger<LocalGitHost> log)
        : this(opts, log, static psi => new SystemLocalGitProcess(psi))
    {
    }

    internal LocalGitHost(
        LocalGitHostOptions opts,
        ILogger<LocalGitHost> log,
        Func<ProcessStartInfo, ILocalGitProcess> processFactory)
    {
        _opts = opts;
        _log = log;
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
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
                {
                    await FetchUpstreamAsync(path, seedFromUrl, baseBranch, ct);
                }
                return repoId;
            }

            if (seedFromUrl is not null)
                Validation.ValidateRepositoryUrl(seedFromUrl, nameof(seedFromUrl));

            Directory.CreateDirectory(path);
            if (seedFromUrl is not null)
            {
                string? mirrorPath = null;
                if (_opts.EnableSharedUpstreamMirror)
                {
                    mirrorPath = await GetOrCreateMirrorAsync(seedFromUrl, baseBranch, upstreamEnv: null, ct);
                }

                (int ExitCode, string Stdout, string Stderr) rc;
                if (mirrorPath is not null && Directory.Exists(mirrorPath))
                {
                    _log.LogInformation("Cloning bare repo from mirror at {MirrorPath} for {Url}", mirrorPath, ScrubCredentialMaterial(seedFromUrl));
                    rc = await RunGitAsync(
                        workdir: _opts.RootDirectory,
                        ct,
                        // --shared writes an alternates-only child repo. That
                        // avoids Git's local hardlink clone path while still
                        // borrowing objects from the mirror.
                        "clone", "--bare", "--shared", "--", mirrorPath, path);
                    if (rc.ExitCode == 0)
                    {
                        var metadataPath = path + ".mirror_metadata";
                        await File.WriteAllTextAsync(metadataPath, Path.Combine(mirrorPath, "objects"), ct);
                        await RunGitAsync(workdir: path, ct, "remote", "set-url", "origin", seedFromUrl);
                    }
                    else
                    {
                        _log.LogWarning("Clone from reference mirror failed (exit {Code}): {Error}. Falling back to direct clone from remote.", rc.ExitCode, ScrubCredentialMaterial(rc.Stderr));
                        DropMirrorMetadataAndAlternates(path);
                        ResetCloneDestination(path);
                        rc = await RunGitAsync(workdir: _opts.RootDirectory, ct, "clone", "--bare", "--", seedFromUrl, path);
                    }
                }
                else
                {
                    DropMirrorMetadataAndAlternates(path);
                    rc = await RunGitAsync(workdir: _opts.RootDirectory, ct, "clone", "--bare", "--", seedFromUrl, path);
                }

                if (rc.ExitCode != 0)
                {
                    if (Directory.Exists(path))
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

    private IReadOnlyList<SandboxMount> GetRepositoryMounts(string repoPath, bool readOnly)
    {
        var mounts = new List<SandboxMount>
        {
            new SandboxMount
            {
                SandboxPath = SandboxRepoMountPath,
                HostPath = repoPath,
                ReadOnly = readOnly,
            }
        };

        var metadataPath = repoPath + ".mirror_metadata";
        if (File.Exists(metadataPath))
        {
            try
            {
                var lines = File.ReadAllLines(metadataPath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    var fullPath = Path.GetFullPath(trimmed);
                    var mirrorDir = GetSharedMirrorDirectoryFullPath();

                    var mirrorDirWithSlash = mirrorDir.EndsWith(Path.DirectorySeparatorChar)
                        ? mirrorDir
                        : mirrorDir + Path.DirectorySeparatorChar;

                    if (fullPath.StartsWith(mirrorDirWithSlash, StringComparison.Ordinal))
                    {
                        mounts.Add(new SandboxMount
                        {
                            SandboxPath = fullPath,
                            HostPath = fullPath,
                            ReadOnly = true,
                        });
                    }
                    else
                    {
                        _log.LogWarning("Skipping sandbox mount for untrusted git alternate path: {Path}", trimmed);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to read alternates to mount for repo at {RepoPath}", repoPath);
            }
        }

        return mounts;
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
        var repoPath = GetRepoPath(repositoryId);
        var mounts = GetRepositoryMounts(repoPath, readOnly: false);
        return new SandboxRepositoryAccess(SandboxRepoMountPath, mounts, SandboxNetworkPolicy.Denied);
    }

    /// <summary>Sandbox-side path the bare repo is mounted at. Per-item scoped.</summary>
    public const string SandboxRepoMountPath = "/repo";

    /// <summary>
    /// Wires a sandbox to an isolated (merge / conflict-rework) bare clone
    /// using the same per-item /repo mount layout as <see cref="GetSandboxAccess"/>,
    /// so the agent sees an identical clone URL irrespective of which on-host
    /// path is bind-mounted.
    /// </summary>
    public SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
    {
        var mounts = GetRepositoryMounts(isolatedRepoHostPath, readOnly: false);
        return new SandboxRepositoryAccess(SandboxRepoMountPath, mounts, SandboxNetworkPolicy.Denied);
    }

    public async Task<string> CreateIsolatedMergeCloneAsync(
        string repositoryId,
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        var source = GetRepoPath(repositoryId);
        var stagingRoot = ((IGitHost)this).GetMergeStagingRoot(repositoryId);
        var target = Path.Combine(stagingRoot, $"codeybox-merge-{workItemId}-{Guid.NewGuid():N}.git");

        // Write the SIBLING in-flight sentinel BEFORE the clone runs. The
        // sentinel covers the entire create window — between clone-start
        // and clone-end the target directory exists but has no
        // in-directory marker yet, and a host-side cleanup honoring only
        // the in-directory marker would race that gap. The sibling sentinel
        // closes that race and is the load-bearing artifact a marker-respecting
        // external cleaner should check.
        WriteInFlightSibling(target, workItemId);

        try
        {
            var cloneArgs = BuildLocalBareCloneArgs(source, target);
            var rc = await RunGitAsync(stagingRoot, ct, cloneArgs);
            if (rc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git clone --bare for merge staging failed (exit {rc.ExitCode}): {rc.Stderr}{rc.Stdout}");
            VerifyIsolatedMergeCloneOnDisk(target, "create");
            WriteInDirectoryMarker(target, workItemId);

            var sourceMetadata = source + ".mirror_metadata";
            if (File.Exists(sourceMetadata))
            {
                File.Copy(sourceMetadata, target + ".mirror_metadata", overwrite: true);
            }

            _log.LogInformation(
                "isolated merge clone landed for work item {WorkItem}: {Target}",
                workItemId, target);
            return target;
        }
        catch
        {
            // Best-effort sentinel cleanup if clone or verification failed.
            TryDeleteFile(target + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix);
            throw;
        }
    }

    public async Task RestoreIsolatedMergeCloneAsync(
        string repositoryId,
        string targetPath,
        CancellationToken ct = default)
    {
        var source = GetRepoPath(repositoryId);
        var stagingRoot = ((IGitHost)this).GetMergeStagingRoot(repositoryId);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var canonicalTarget = Path.GetFullPath(targetPath);
        if (!canonicalTarget.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(canonicalTarget, canonicalRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"refusing to restore isolated merge clone outside staging root: target={canonicalTarget} root={canonicalRoot}");
        }
        if (Directory.Exists(targetPath))
        {
            try { Directory.Delete(targetPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Failed to clear partial merge staging residue at {Path}", targetPath);
            }
            TryDeleteFile(targetPath + ".mirror_metadata");
        }
        // Re-write the sibling sentinel before re-cloning so the heal path
        // matches the create path's in-flight protection.
        WriteInFlightSibling(targetPath, workItemId: null);
        try
        {
            var cloneArgs = BuildLocalBareCloneArgs(source, targetPath);
            var rc = await RunGitAsync(stagingRoot, ct, cloneArgs);
            if (rc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git clone --bare for merge restore failed (exit {rc.ExitCode}): {rc.Stderr}{rc.Stdout}");
            VerifyIsolatedMergeCloneOnDisk(targetPath, "restore");
            WriteInDirectoryMarker(targetPath, workItemId: null);

            var sourceMetadata = source + ".mirror_metadata";
            if (File.Exists(sourceMetadata))
            {
                File.Copy(sourceMetadata, targetPath + ".mirror_metadata", overwrite: true);
            }
        }
        catch
        {
            // Mirror CreateIsolatedMergeCloneAsync: best-effort sentinel
            // cleanup if the heal-path clone or verification failed, so a
            // failed restore does not leave a stray `.inflight` next to
            // the (likely absent) staging directory.
            TryDeleteFile(targetPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix);
            throw;
        }
    }

    public async Task DisposeIsolatedMergeCloneAsync(
        string repositoryId,
        string targetPath,
        CancellationToken ct = default)
    {
        _ = repositoryId;
        _ = ct;
        if (string.IsNullOrWhiteSpace(targetPath))
            return;
        if (Directory.Exists(targetPath))
        {
            try { Directory.Delete(targetPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Failed to delete isolated merge repository {Path}", targetPath);
            }
        }
        TryDeleteFile(targetPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix);
        TryDeleteFile(targetPath + ".mirror_metadata");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Asserts a freshly-run <c>git clone --bare</c> actually produced the
    /// expected bare-repo layout on disk: the target directory exists AND
    /// contains a HEAD file. Called immediately after the clone returns so
    /// a silent/partial clone or an external process that removed the
    /// directory between clone-exit and verification surfaces here instead
    /// of as a confusing "Source path does not exist" mount failure later.
    /// </summary>
    internal static void VerifyIsolatedMergeCloneOnDisk(string targetPath, string operationContext)
    {
        var dirExists = Directory.Exists(targetPath);
        var headExists = File.Exists(Path.Combine(targetPath, "HEAD"));
        if (!dirExists || !headExists)
        {
            throw new InvalidOperationException(
                $"isolated merge clone {operationContext} did not land on disk: target={targetPath} " +
                $"exists={dirExists} head={headExists}");
        }
    }

    private static void WriteInDirectoryMarker(string stagingPath, WorkItemId? workItemId)
    {
        var markerPath = Path.Combine(stagingPath, IGitHost.IsolatedMergeCloneInFlightMarkerFileName);
        File.WriteAllText(markerPath, BuildMarkerBody(workItemId));
    }

    private static void WriteInFlightSibling(string stagingPath, WorkItemId? workItemId)
    {
        var siblingPath = stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix;
        File.WriteAllText(siblingPath, BuildMarkerBody(workItemId));
    }

    private static string BuildMarkerBody(WorkItemId? workItemId)
        => workItemId is { } id
            ? $"work_item={id}\nhost=LocalGitHost\n"
            : "host=LocalGitHost\n";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort
            _ = ex;
        }
    }

    private void DropMirrorMetadataAndAlternates(string bareRepoPath)
    {
        TryDeleteFile(bareRepoPath + ".mirror_metadata");
        TryDeleteFile(Path.Combine(bareRepoPath, "objects", "info", "alternates"));
    }

    private static void ResetCloneDestination(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    private string[] BuildLocalBareCloneArgs(string source, string target)
    {
        var sourceMetadata = source + ".mirror_metadata";
        if (TryReadSingleTrustedMirrorObjectsPath(sourceMetadata) is { } mirrorObjectsPath)
        {
            var mirrorRepoPath = Path.GetDirectoryName(mirrorObjectsPath);
            if (!string.IsNullOrWhiteSpace(mirrorRepoPath))
            {
                return ["clone", "--bare", "--no-local", "--reference", mirrorRepoPath, "--", source, target];
            }
        }

        return ["clone", "--bare", "--no-local", "--", source, target];
    }

    public async Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        SanitizeAlternates(path);
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
        (int ExitCode, string Stdout, string Stderr) fetch;
        if (_opts.EnableSharedUpstreamMirror)
        {
            var mirrorPath = await GetOrCreateMirrorAsync(upstreamUrl, branch, upstreamEnv, ct);
            if (mirrorPath is not null && Directory.Exists(mirrorPath))
            {
                var metadataPath = path + ".mirror_metadata";
                await File.WriteAllTextAsync(metadataPath, Path.Combine(mirrorPath, "objects"), ct);

                _log.LogInformation("Fetching upstream branch '{Branch}' from local mirror {MirrorPath} for repo {RepoId}", branch, mirrorPath, repositoryId);
                fetch = await RunGitAsync(
                    workdir: path,
                    ct,
                    "fetch", "--no-tags", mirrorPath, $"+refs/heads/{branch}:refs/heads/{branch}");
                if (fetch.ExitCode != 0)
                {
                    _log.LogWarning("Fetch from mirror failed: {Error}. Falling back to direct remote fetch.", fetch.Stderr);
                    DropMirrorMetadataAndAlternates(path);
                    fetch = await RunGitAsync(
                        workdir: path,
                        ct,
                        extraEnv: upstreamEnv,
                        "fetch", "--no-tags", upstreamUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
                }
            }
            else
            {
                DropMirrorMetadataAndAlternates(path);
                fetch = await RunGitAsync(
                    workdir: path,
                    ct,
                    extraEnv: upstreamEnv,
                    "fetch", "--no-tags", upstreamUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
            }
        }
        else
        {
            DropMirrorMetadataAndAlternates(path);
            fetch = await RunGitAsync(
                workdir: path,
                ct,
                extraEnv: upstreamEnv,
                "fetch", "--no-tags", upstreamUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
        }

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
                TryDeleteFile(path + ".mirror_metadata");
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

    public void PrepareRepositoryForHostGitOperations(string repositoryId)
    {
        var path = GetRepoPath(repositoryId);
        SanitizeAlternates(path);
    }

    public void SanitizeRepositoryAlternates(string repositoryId)
        => PrepareRepositoryForHostGitOperations(repositoryId);

    public void SanitizeRepositoryAlternatesByPath(string path)
    {
        SanitizeAlternates(path);
    }

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

    public async Task<string> CreateMergeCommitAsync(
        string repositoryId,
        string treeSha,
        string firstParentCommit,
        string secondParentCommit,
        string message,
        string authorName,
        string authorEmail,
        CancellationToken ct = default)
    {
        Validation.ValidateCommitSha(firstParentCommit, nameof(firstParentCommit));
        Validation.ValidateCommitSha(secondParentCommit, nameof(secondParentCommit));
        if (!LooksLikeSha(treeSha))
            throw new InvalidOperationException($"invalid merge tree sha '{treeSha}'");
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("merge commit message must not be empty", nameof(message));

        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);

        // Identity is supplied via env rather than repo config so a concurrent
        // merge on the same bare repo can't race on `git config`. git
        // commit-tree honours GIT_AUTHOR_*/GIT_COMMITTER_* for both roles.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = authorName,
            ["GIT_AUTHOR_EMAIL"] = authorEmail,
            ["GIT_COMMITTER_NAME"] = authorName,
            ["GIT_COMMITTER_EMAIL"] = authorEmail,
        };

        // -m preserves embedded newlines (each ArgumentList entry is one argv
        // element, so the trailer block survives intact).
        var rc = await RunGitAsync(
            workdir: path, ct, extraEnv: env,
            "commit-tree", treeSha,
            "-p", firstParentCommit,
            "-p", secondParentCommit,
            "-m", message);
        if (rc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git commit-tree (merge of {secondParentCommit} into {firstParentCommit}) failed: {rc.Stderr}");

        var sha = rc.Stdout.Trim();
        if (!LooksLikeSha(sha))
            throw new InvalidOperationException(
                $"git commit-tree did not return a commit sha: {rc.Stderr}{rc.Stdout}");
        return sha;
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
        else
        {
            var update = await RunGitAsync(
                workdir: path, ct,
                "update-ref", $"refs/heads/{workBranch}", baseSha, GitNullObjectId);
            if (update.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git update-ref to create '{workBranch}' at base '{baseBranch}' ({baseSha}) failed: {update.Stderr}");
            }

            _log.LogInformation(
                "Created work branch '{WorkBranch}' in bare repo {RepoId} at base '{BaseBranch}' tip {BaseTip} for fresh work-phase entry",
                workBranch, repositoryId, baseBranch, baseSha);
        }

        var verify = await RunGitAsync(
            workdir: path, ct,
            "rev-parse", "--verify", $"refs/heads/{workBranch}^{{commit}}");
        if (verify.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"work branch '{workBranch}' did not resolve after reset to base '{baseBranch}': {verify.Stderr}");
        }

        var verifiedSha = verify.Stdout.Trim();
        if (!string.Equals(verifiedSha, baseSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"work branch '{workBranch}' tip after reset is {verifiedSha}, expected base '{baseBranch}' tip {baseSha}");
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

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
    {
        var hasPrefix = !string.IsNullOrEmpty(pathPrefix);
        if (hasPrefix)
            ValidateRepositoryRelativePath(pathPrefix!);
        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        var rc = hasPrefix
            ? await RunGitAsync(path, ct, "ls-tree", "-r", "--name-only", treeish, "--", pathPrefix!)
            : await RunGitAsync(path, ct, "ls-tree", "-r", "--name-only", treeish);
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git ls-tree '{treeish}{(hasPrefix ? ":" + pathPrefix : string.Empty)}' failed: {rc.Stderr}");
        return rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<string>> ListFilesEndingWithAsync(
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

        var suffixes = new string[filenameSuffixes.Count];
        for (var i = 0; i < filenameSuffixes.Count; i++)
        {
            var s = filenameSuffixes[i];
            if (string.IsNullOrWhiteSpace(s))
                throw new ArgumentException("filename suffix entries must be non-empty", nameof(filenameSuffixes));
            suffixes[i] = s;
        }

        var path = GetRepoPath(repositoryId);
        SanitizeBareRepositoryConfig(path);
        SanitizeAlternates(path);

        var psi = new ProcessStartInfo
        {
            FileName = _opts.GitExecutable,
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"core.hooksPath={_disabledHooksPath}");
        psi.ArgumentList.Add("ls-tree");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("--name-only");
        psi.ArgumentList.Add(treeish);

        ILocalGitProcess? p = null;
        for (var attempt = 1; ; attempt++)
        {
            p = _processFactory(psi);
            try
            {
                p.Start();
                break;
            }
            catch (Win32Exception ex) when (attempt < GitStartTextFileBusyMaxAttempts && IsTextFileBusy(ex))
            {
                p.Dispose();
                p = null;
                await Task.Delay(GitStartTextFileBusyDelayStepMilliseconds * attempt, ct);
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }
        using var _p = p;

        var results = new List<string>(Math.Min(64, maxResults));
        var capExceeded = false;
        var scannedCapExceeded = false;
        var scanned = 0;
        var stderr = string.Empty;
        try
        {
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                scanned++;
                if (scanned > _opts.ListFilesEndingScannedPathCeiling)
                {
                    scannedCapExceeded = true;
                    break;
                }
                if (!EndsWithAnySuffix(trimmed, suffixes)) continue;
                if (results.Count >= maxResults)
                {
                    capExceeded = true;
                    break;
                }
                results.Add(trimmed);
            }
        }
        catch
        {
            // Read failure (cancellation, broken pipe, etc.): kill the child
            // before propagating so the git process is not left wedged on
            // its stdout pipe after we stop draining it. Without this, an
            // interrupted ListFilesEndingWithAsync would leak the OS
            // process: the finally block below only ran the kill on the
            // cap-exceeded happy-path. Drain + WaitForExit happen in the
            // finally below so the child is always reaped.
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
        finally
        {
            if (capExceeded || scannedCapExceeded)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
            // Always reap stderr + exit with CancellationToken.None so a
            // cancelled caller does not skip the wait and leak the child.
            try { stderr = await p.StandardError.ReadToEndAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            try { await p.WaitForExitAsync(CancellationToken.None); }
            catch { /* best-effort */ }
        }

        if (scannedCapExceeded)
        {
            throw new InvalidOperationException(
                $"git ls-tree '{treeish}' scanned more than {_opts.ListFilesEndingScannedPathCeiling} paths without filling the match cap (tree too large to inspect safely)");
        }

        if (capExceeded)
        {
            throw new InvalidOperationException(
                $"git ls-tree '{treeish}' produced more than {maxResults} matching paths (output cap exceeded)");
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git ls-tree '{treeish}' failed: {stderr}");

        return results;
    }

    private static bool EndsWithAnySuffix(string path, IReadOnlyList<string> suffixes)
    {
        foreach (var s in suffixes)
        {
            if (path.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

        if (_opts.EnableSharedUpstreamMirror)
        {
            var mirrorPath = await GetOrCreateMirrorAsync(seedFromUrl, branch, upstreamEnv: null, ct);
            if (mirrorPath is not null && Directory.Exists(mirrorPath))
            {
                var metadataPath = bareRepoPath + ".mirror_metadata";
                await File.WriteAllTextAsync(metadataPath, Path.Combine(mirrorPath, "objects"), ct);

                _log.LogInformation("Refreshing bare repo {Path} branch {Branch} from shared mirror {MirrorPath}", bareRepoPath, branch, mirrorPath);
                var rcMirror = await RunGitAsync(
                    workdir: bareRepoPath,
                    ct,
                    "fetch", "--no-tags", "--prune", mirrorPath, $"+refs/heads/{branch}:refs/heads/{branch}");
                if (rcMirror.ExitCode == 0)
                {
                    return;
                }
                _log.LogWarning("Fetch from mirror failed: {Error}. Falling back to direct fetch.", rcMirror.Stderr);
                DropMirrorMetadataAndAlternates(bareRepoPath);
            }
            else
            {
                DropMirrorMetadataAndAlternates(bareRepoPath);
            }
        }
        else
        {
            DropMirrorMetadataAndAlternates(bareRepoPath);
        }

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
        SanitizeAlternates(workdir);
        var psi = new ProcessStartInfo
        {
            FileName = _opts.GitExecutable,
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

        for (var attempt = 1; ; attempt++)
        {
            using var p = _processFactory(psi);
            try
            {
                p.Start();
            }
            catch (Win32Exception ex) when (attempt < GitStartTextFileBusyMaxAttempts && IsTextFileBusy(ex))
            {
                await Task.Delay(GitStartTextFileBusyDelayStepMilliseconds * attempt, ct);
                continue;
            }

            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, stdout, stderr);
        }
    }

    private static bool IsTextFileBusy(Win32Exception ex)
        => ex.NativeErrorCode == PosixTextFileBusyErrno
            || ex.Message.Contains("Text file busy", StringComparison.Ordinal);

    private sealed class SystemLocalGitProcess(ProcessStartInfo startInfo) : ILocalGitProcess
    {
        private readonly System.Diagnostics.Process _process = new() { StartInfo = startInfo };

        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;
        public int ExitCode => _process.ExitCode;
        public void Start() => _process.Start();
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
        public void Dispose() => _process.Dispose();
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

    private async Task<string?> GetOrCreateMirrorAsync(
        string upstreamUrl,
        string? baseBranch,
        IReadOnlyDictionary<string, string>? upstreamEnv,
        CancellationToken ct)
    {
        var mirrorPath = GetMirrorPath(upstreamUrl);
        var lease = await AcquireRepositoryLockAsync(mirrorPath, ct);
        try
        {
            var branch = await ResolveMirrorBranchAsync(mirrorPath, upstreamUrl, baseBranch, upstreamEnv, ct);
            if (branch is null)
            {
                _log.LogWarning("Shared mirror: failed to resolve branch to update for {Url}", ScrubCredentialMaterial(upstreamUrl));
                return null;
            }

            if (!Directory.Exists(mirrorPath))
            {
                Directory.CreateDirectory(mirrorPath);
                _log.LogInformation("Creating shared mirror repo at {Path} for {Url}", mirrorPath, ScrubCredentialMaterial(upstreamUrl));
                var rc = await RunGitAsync(
                    workdir: _opts.RootDirectory,
                    ct,
                    extraEnv: upstreamEnv,
                    "clone", "--bare", "--", upstreamUrl, mirrorPath);
                if (rc.ExitCode != 0)
                {
                    _log.LogWarning("Shared mirror clone failed (exit {Code}): {Error}", rc.ExitCode, ScrubCredentialMaterial(rc.Stderr));
                    try { Directory.Delete(mirrorPath, recursive: true); } catch { }
                    return null;
                }

                await RunGitAsync(mirrorPath, ct, "config", "gc.auto", "0");
                await RunGitAsync(mirrorPath, ct, "config", "gc.pruneExpire", "never");
            }
            else
            {
                var remoteSha = await ResolveRemoteBranchShaAsync(upstreamUrl, branch, upstreamEnv, ct);
                if (remoteSha is not null)
                {
                    var localShaRc = await RunGitAsync(
                        workdir: mirrorPath,
                        ct,
                        "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
                    var localSha = localShaRc.ExitCode == 0 ? localShaRc.Stdout.Trim() : null;

                    if (localSha == remoteSha)
                    {
                        _log.LogDebug("Shared mirror up to date for {Url} branch {Branch} (SHA {Sha})", ScrubCredentialMaterial(upstreamUrl), branch, localSha);
                        return mirrorPath;
                    }
                }

                _log.LogInformation("Refreshing shared mirror repo at {Path} branch {Branch}", mirrorPath, branch);
                var fetchRc = await RunGitAsync(
                    workdir: mirrorPath,
                    ct,
                    extraEnv: upstreamEnv,
                    "fetch", "--no-tags", "--prune", upstreamUrl, $"+refs/heads/{branch}:refs/heads/{branch}");
                if (fetchRc.ExitCode != 0)
                {
                    _log.LogWarning("Shared mirror fetch failed (exit {Code}): {Error}", fetchRc.ExitCode, ScrubCredentialMaterial(fetchRc.Stderr));
                    return null;
                }
            }

            return mirrorPath;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error maintaining shared mirror for {Url}", ScrubCredentialMaterial(upstreamUrl));
            return null;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task<string?> ResolveMirrorBranchAsync(
        string mirrorPath,
        string upstreamUrl,
        string? baseBranch,
        IReadOnlyDictionary<string, string>? upstreamEnv,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(baseBranch))
            return baseBranch;

        var workdir = Directory.Exists(mirrorPath) ? mirrorPath : _opts.RootDirectory;
        var rc = await RunGitAsync(workdir, ct, upstreamEnv, "ls-remote", "--symref", upstreamUrl, "HEAD");
        if (rc.ExitCode != 0)
            return null;

        foreach (var line in rc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "ref: refs/heads/";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var branch = line[prefix.Length..].Split('\t', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(branch))
                return branch;
        }

        return null;
    }

    private async Task<string?> ResolveRemoteBranchShaAsync(
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string>? upstreamEnv,
        CancellationToken ct)
    {
        var rc = await RunGitAsync(_opts.RootDirectory, ct, upstreamEnv, "ls-remote", upstreamUrl, $"refs/heads/{branch}");
        if (rc.ExitCode != 0)
            return null;

        var parts = rc.Stdout.Split('\t', 2);
        if (parts.Length > 0 && LooksLikeSha(parts[0].Trim()))
            return parts[0].Trim();

        return null;
    }

    private string? FindCommonGitDir(string path)
    {
        var gitDir = FindGitDir(path);
        if (gitDir == null) return null;

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            try
            {
                var content = File.ReadAllText(commonDirFile).Trim();
                var resolved = Path.GetFullPath(Path.Combine(gitDir, content));
                if (Directory.Exists(resolved))
                {
                    return resolved;
                }
            }
            catch
            {
                // Ignore and fallback
            }
        }
        return gitDir;
    }

    private string? FindGitDir(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "HEAD")) && Directory.Exists(Path.Combine(current, "objects")))
            {
                return current;
            }

            var gitSubdir = Path.Combine(current, ".git");
            if (Directory.Exists(gitSubdir))
            {
                return gitSubdir;
            }
            if (File.Exists(gitSubdir))
            {
                try
                {
                    var content = File.ReadAllText(gitSubdir).Trim();
                    const string prefix = "gitdir:";
                    if (content.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var gitdirPath = content[prefix.Length..].Trim();
                        if (!Path.IsPathRooted(gitdirPath))
                        {
                            gitdirPath = Path.GetFullPath(Path.Combine(current, gitdirPath));
                        }
                        return gitdirPath;
                    }
                }
                catch
                {
                    // Ignore and walk up
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == current || string.IsNullOrEmpty(parent))
            {
                break;
            }
            current = parent;
        }
        return null;
    }

    private void SanitizeAlternates(string workdir)
    {
        try
        {
            var gitDir = FindCommonGitDir(workdir);
            if (gitDir == null) return;

            // Ensure gitDir is within the managed directories (RootDirectory or SharedUpstreamMirrorDirectory)
            // to avoid mutating parent repositories we do not own.
            var resolvedGitDir = Path.GetFullPath(gitDir);
            var rootDir = Path.GetFullPath(_opts.RootDirectory);
            var mirrorDir = GetSharedMirrorDirectoryFullPath();

            var isManaged = IsSameOrUnder(resolvedGitDir, rootDir)
                            || IsSameOrUnder(resolvedGitDir, mirrorDir);

            if (!isManaged)
            {
                return;
            }

            var alternatesPath = Path.Combine(gitDir, "objects", "info", "alternates");
            if (!File.Exists(alternatesPath)) return;

            var metadataPath = gitDir + ".mirror_metadata";
            var allowedPaths = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(metadataPath))
            {
                var metaLines = File.ReadAllLines(metadataPath);
                foreach (var ml in metaLines)
                {
                    var trimmedMeta = ml.Trim();
                    if (!string.IsNullOrEmpty(trimmedMeta))
                    {
                        var fullMetaPath = Path.GetFullPath(trimmedMeta);
                        if (IsSameOrUnder(fullMetaPath, mirrorDir))
                        {
                            allowedPaths.Add(fullMetaPath);
                        }
                        else
                        {
                            _log.LogWarning("Discarding untrusted git alternate metadata path: {Path}", trimmedMeta);
                        }
                    }
                }
            }

            var lines = File.ReadAllLines(alternatesPath);
            var validLines = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var fullPath = Path.GetFullPath(trimmed);
                if (allowedPaths.Count > 0)
                {
                    if (allowedPaths.Contains(fullPath))
                    {
                        validLines.Add(trimmed);
                    }
                    else
                    {
                        _log.LogWarning("Discarding untrusted git alternate path (not in metadata): {Path}", trimmed);
                    }
                }
                else
                {
                    _log.LogWarning("Discarding git alternate path because mirror metadata is absent or invalid: {Path}", trimmed);
                }
            }

            if (validLines.Count != lines.Length)
            {
                if (validLines.Count > 0)
                {
                    File.WriteAllLines(alternatesPath, validLines);
                }
                else
                {
                    File.Delete(alternatesPath);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to sanitize alternates file for workdir {Workdir}", workdir);
            throw new InvalidOperationException(
                $"Failed to sanitize git alternates for managed repository at '{workdir}'", ex);
        }
    }

    private string? TryReadSingleTrustedMirrorObjectsPath(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var mirrorDir = GetSharedMirrorDirectoryFullPath();
        foreach (var line in File.ReadAllLines(metadataPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(trimmed);
            if (IsSameOrUnder(fullPath, mirrorDir))
            {
                return fullPath;
            }

            _log.LogWarning("Ignoring untrusted git alternate metadata path: {Path}", trimmed);
        }

        return null;
    }

    private string GetSharedMirrorDirectoryFullPath()
    {
        var configured = string.IsNullOrWhiteSpace(_opts.SharedUpstreamMirrorDirectory)
            ? "_upstream-mirror"
            : _opts.SharedUpstreamMirrorDirectory;

        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_opts.RootDirectory, configured));
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private string GetMirrorPath(string upstreamUrl)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(upstreamUrl));
        var sb = new StringBuilder();
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return Path.Combine(GetSharedMirrorDirectoryFullPath(), sb.ToString() + ".git");
    }
}

internal interface ILocalGitProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    void Start();
    Task WaitForExitAsync(CancellationToken ct);
    void Kill(bool entireProcessTree);
}

public sealed record LocalGitHostOptions
{
    public required string RootDirectory { get; init; }
    public string GitExecutable { get; init; } = "git";
    public string FallbackDefaultBranch { get; init; } = "main";

    /// <summary>
    /// Hard ceiling on the TOTAL number of paths the streamed
    /// <c>ListFilesEndingWithAsync</c> reader will inspect per call,
    /// independent of how many actually match the suffix filter. Caps the
    /// resource-exhaustion vector where a branch-controlled tree carries
    /// vastly more non-matching paths than matching ones, so the loop never
    /// hits the per-match cap and processes git output unbounded. Real
    /// monorepos top out in the low hundreds of thousands of files; the
    /// default leaves comfortable headroom while still bounding adversarial
    /// trees. Lowered in tests so the cap can be exercised without
    /// generating half a million paths.
    /// </summary>
    public int ListFilesEndingScannedPathCeiling { get; init; } = 500_000;

    public bool EnableSharedUpstreamMirror { get; init; } = false;
    public string SharedUpstreamMirrorDirectory { get; init; } = "_upstream-mirror";
}
