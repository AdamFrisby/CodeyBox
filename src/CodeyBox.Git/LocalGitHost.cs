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

    public LocalGitHost(LocalGitHostOptions opts, ILogger<LocalGitHost> log)
    {
        _opts = opts;
        _log = log;
        Directory.CreateDirectory(_opts.RootDirectory);
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

        await ReconcileRejectedUpstreamPushAsync(path, upstreamUrl, branch, upstreamEnv, reconcileStrategy, _log, ct);

        rc = await RunGitAsync(
            workdir: path,
            ct,
            extraEnv: upstreamEnv,
            "push", upstreamUrl, $"{branch}:{branch}");
        if (rc.ExitCode != 0)
            throw new InvalidOperationException($"git push to upstream failed after reconcile: {rc.Stderr}");
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
        var branch = ResolveRefreshBranch(bareRepoPath, baseBranch);
        Validation.ValidateBranchName(branch, nameof(baseBranch));
        var safeUpstream = ScrubCredentialMaterial(seedFromUrl);

        SanitizeBareRepositoryConfig(bareRepoPath);
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

    private string ResolveRefreshBranch(string bareRepoPath, string? baseBranch)
    {
        if (!string.IsNullOrWhiteSpace(baseBranch))
            return baseBranch;

        // HEAD lives inside the sandbox-writable bare repo, so do not use it
        // to choose what host-side refresh should fetch. Callers that know the
        // project base branch pass it explicitly; older callers get the host
        // fallback rather than trusting repo metadata controlled by an agent.
        _log.LogDebug(
            "No configured base branch supplied for bare repo {Path}; refreshing fallback branch {Branch}",
            bareRepoPath,
            _opts.FallbackDefaultBranch);
        return _opts.FallbackDefaultBranch;
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

    private static string ScrubCredentialMaterial(string value)
        => UrlUserInfoPattern.Replace(RawOutputRedactor.Redact(value), "${scheme}***@");

    private static async Task ReconcileRejectedUpstreamPushAsync(
        string bareRepoPath,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy,
        ILogger<LocalGitHost> log,
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
                    log.LogWarning(ex, "Failed to remove upstream reconcile worktree at {Path}", worktreePath);
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir,
        CancellationToken ct,
        params string[] args)
        => await RunGitAsync(workdir, ct, extraEnv: null, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
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
