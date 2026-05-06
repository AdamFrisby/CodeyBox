using System.Diagnostics;
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
    private readonly LocalGitHostOptions _opts;
    private readonly ILogger<LocalGitHost> _log;
    private readonly string _trustedHooksPath;

    public LocalGitHost(LocalGitHostOptions opts, ILogger<LocalGitHost> log)
    {
        _opts = opts;
        _log = log;
        Directory.CreateDirectory(_opts.RootDirectory);
        _trustedHooksPath = Path.Combine(_opts.RootDirectory, ".trusted-empty-hooks");
        Directory.CreateDirectory(_trustedHooksPath);
    }

    public async Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
    {
        var repoId = id.ToString();
        var path = GetRepoPath(repoId);
        if (Directory.Exists(path))
            return repoId;

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
        CancellationToken ct = default,
        string mergeMethod = "rebase")
    {
        Validation.ValidateRepositoryUrl(upstreamUrl, nameof(upstreamUrl));
        Validation.ValidateBranchName(branch, nameof(branch));
        var path = GetRepoPath(repositoryId);
        var trustedPushPath = Path.Combine(_opts.RootDirectory, ".upstream-push-" + Guid.NewGuid().ToString("N"));

        try
        {
            await CreateTrustedPushCloneAsync(path, branch, trustedPushPath, ct);

            var rc = await PushTrustedCloneToUpstreamAsync(trustedPushPath, upstreamUrl, branch, upstreamEnv, ct);
            if (rc.ExitCode == 0)
                return;

            if (!IsNonFastForwardPushFailure(rc.Stderr))
                throw new InvalidOperationException($"git push to upstream failed: {rc.Stderr}");

            if (UsesMergeStyleRecovery(mergeMethod))
                await MergeTrustedCloneWithLatestUpstreamAsync(trustedPushPath, path, upstreamUrl, branch, upstreamEnv, ct);
            else
                await RebaseTrustedCloneOnLatestUpstreamAsync(trustedPushPath, path, upstreamUrl, branch, upstreamEnv, ct);

            var retry = await PushTrustedCloneToUpstreamAsync(trustedPushPath, upstreamUrl, branch, upstreamEnv, ct);
            if (retry.ExitCode != 0)
                throw new InvalidOperationException($"git push to upstream failed after non-fast-forward recovery: {retry.Stderr}");
        }
        finally
        {
            RemoveRecoveryCheckout(trustedPushPath);
        }
    }

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
    {
        var path = GetRepoPath(repositoryId);
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete bare repo at {Path}", path); }
        }
        return Task.CompletedTask;
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

    private static void ApplyReceivePolicy(string barePath)
    {
        // Allow receiving into any branch; orchestration logic, not git hooks,
        // determines what gets merged where. Hooks can be added later for
        // defence in depth (e.g. block direct pushes to main from work phase).
    }

    private async Task RebaseTrustedCloneOnLatestUpstreamAsync(
        string trustedClonePath,
        string barePath,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct)
    {
        var remoteRef = $"refs/remotes/codeybox-upstream/{branch}";
        var fetch = await RunGitWithTrustedHostConfigAsync(
            workdir: trustedClonePath,
            ct,
            extraEnv: upstreamEnv,
            "fetch", upstreamUrl, $"+refs/heads/{branch}:{remoteRef}");
        if (fetch.ExitCode != 0)
            throw new InvalidOperationException(
                $"git fetch of upstream branch '{branch}' failed after non-fast-forward rejection: {fetch.Stderr}");

        await CheckoutTrustedRecoveryBranchAsync(trustedClonePath, branch, ct);

        var rebase = await RunGitWithTrustedHostConfigAsync(
            workdir: trustedClonePath,
            ct,
            extraEnv: BuildRebaseEnvironment(),
            "rebase", remoteRef);
        if (rebase.ExitCode == 0)
        {
            await UpdateBareBranchFromRecoveryCheckoutAsync(barePath, branch, trustedClonePath, ct);
            return;
        }

        await AbortRebaseAsync(trustedClonePath);
        throw new UpstreamRebaseConflictException(
            $"upstream rebase conflict on {branch}; manual resolution required: {rebase.Stderr}");
    }

    private async Task MergeTrustedCloneWithLatestUpstreamAsync(
        string trustedClonePath,
        string barePath,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct)
    {
        var remoteRef = $"refs/remotes/codeybox-upstream/{branch}";
        var fetch = await RunGitWithTrustedHostConfigAsync(
            workdir: trustedClonePath,
            ct,
            extraEnv: upstreamEnv,
            "fetch", upstreamUrl, $"+refs/heads/{branch}:{remoteRef}");
        if (fetch.ExitCode != 0)
            throw new InvalidOperationException(
                $"git fetch of upstream branch '{branch}' failed after non-fast-forward rejection: {fetch.Stderr}");

        await CheckoutTrustedRecoveryBranchAsync(trustedClonePath, branch, ct);

        var merge = await RunGitWithTrustedHostConfigAsync(
            workdir: trustedClonePath,
            ct,
            extraEnv: BuildMergeCommitEnvironment(),
            "merge", "--no-ff", remoteRef,
            "-m", $"codeybox: merge latest upstream {branch}",
            "-m", CodeyBoxTrailers.CoAuthoredBy);
        if (merge.ExitCode == 0)
        {
            await UpdateBareBranchFromRecoveryCheckoutAsync(barePath, branch, trustedClonePath, ct);
            return;
        }

        await AbortMergeAsync(trustedClonePath);
        throw new UpstreamRebaseConflictException(
            $"upstream merge conflict on {branch}; manual resolution required: {merge.Stderr}");
    }

    private async Task CreateTrustedPushCloneAsync(
        string barePath,
        string branch,
        string worktreePath,
        CancellationToken ct)
    {
        // Do not run credentialed git commands from the agent-writable bare
        // repo. A no-checkout clone creates fresh host-owned local config;
        // push/fetch/rebase/merge then only consult that trusted config.
        var clone = await RunGitWithTrustedHostConfigAsync(
            workdir: _opts.RootDirectory,
            ct,
            extraEnv: null,
            "clone", "--no-checkout", "--local", "--no-hardlinks", "--", barePath, worktreePath);
        if (clone.ExitCode != 0)
            throw new InvalidOperationException(
                $"git recovery clone setup for upstream reconciliation on '{branch}' failed: {clone.Stderr}");

        var branchRef = await RunGitWithTrustedHostConfigAsync(
            worktreePath, ct, null, "show-ref", "--verify", $"refs/heads/{branch}");
        if (branchRef.ExitCode != 0)
            throw new InvalidOperationException(
                $"git recovery branch setup for upstream reconciliation on '{branch}' failed: {branchRef.Stderr}");
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> PushTrustedCloneToUpstreamAsync(
        string trustedClonePath,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct)
        // git push [<repository> [<refspec>...]] — push doesn't support `--`
        // before <repository>, so we rely on URL validation above to ensure
        // the URL is well-formed and not option-like.
        => RunGitWithTrustedHostConfigAsync(
            workdir: trustedClonePath,
            ct,
            extraEnv: upstreamEnv,
            "push", upstreamUrl, $"refs/heads/{branch}:refs/heads/{branch}");

    private async Task CheckoutTrustedRecoveryBranchAsync(
        string worktreePath,
        string branch,
        CancellationToken ct)
    {
        var checkout = await RunGitWithTrustedHostConfigAsync(
            worktreePath, ct, null, "checkout", branch);
        if (checkout.ExitCode != 0)
            throw new InvalidOperationException(
                $"git recovery checkout for upstream reconciliation on '{branch}' failed: {checkout.Stderr}");
    }

    private async Task UpdateBareBranchFromRecoveryCheckoutAsync(
        string barePath,
        string branch,
        string worktreePath,
        CancellationToken ct)
    {
        var update = await RunGitWithHooksDisabledAsync(
            barePath, ct, null, "fetch", "--no-tags", worktreePath, $"+HEAD:refs/heads/{branch}");
        if (update.ExitCode != 0)
            throw new InvalidOperationException(
                $"git recovery failed to update host branch '{branch}': {update.Stderr}");
    }

    private async Task AbortRebaseAsync(string worktreePath)
    {
        try
        {
            var abort = await RunGitWithTrustedHostConfigAsync(worktreePath, CancellationToken.None, null, "rebase", "--abort");
            if (abort.ExitCode != 0)
                _log.LogWarning("Failed to abort upstream rebase in {WorktreePath}: {Error}", worktreePath, abort.Stderr);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to abort upstream rebase in {WorktreePath}", worktreePath);
        }
    }

    private async Task AbortMergeAsync(string worktreePath)
    {
        try
        {
            var abort = await RunGitWithTrustedHostConfigAsync(worktreePath, CancellationToken.None, null, "merge", "--abort");
            if (abort.ExitCode != 0)
                _log.LogWarning("Failed to abort upstream merge in {WorktreePath}: {Error}", worktreePath, abort.Stderr);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to abort upstream merge in {WorktreePath}", worktreePath);
        }
    }

    private void RemoveRecoveryCheckout(string worktreePath)
    {
        try
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to remove upstream recovery checkout {WorktreePath}", worktreePath);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildRebaseEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_COMMITTER_NAME"] = "CodeyBox",
            ["GIT_COMMITTER_EMAIL"] = "noreply@codeybox.invalid",
        };
    }

    private static IReadOnlyDictionary<string, string> BuildMergeCommitEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = "CodeyBox",
            ["GIT_AUTHOR_EMAIL"] = "noreply@codeybox.invalid",
            ["GIT_COMMITTER_NAME"] = "CodeyBox",
            ["GIT_COMMITTER_EMAIL"] = "noreply@codeybox.invalid",
        };
    }

    private static bool IsNonFastForwardPushFailure(string stderr)
        => stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("(non-fast-forward)", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("! [rejected]", StringComparison.OrdinalIgnoreCase);

    private static bool UsesMergeStyleRecovery(string mergeMethod)
        => string.Equals(mergeMethod, "merge", StringComparison.OrdinalIgnoreCase);

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

    private Task<(int ExitCode, string Stdout, string Stderr)> RunGitWithHooksDisabledAsync(
        string workdir,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv,
        params string[] args)
    {
        var trustedArgs = new string[args.Length + 2];
        trustedArgs[0] = "-c";
        trustedArgs[1] = $"core.hooksPath={_trustedHooksPath}";
        Array.Copy(args, 0, trustedArgs, 2, args.Length);
        return RunGitAsync(workdir, ct, extraEnv, trustedArgs);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunGitWithTrustedHostConfigAsync(
        string workdir,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv,
        params string[] args)
        => RunGitWithHooksDisabledAsync(workdir, ct, BuildTrustedHostGitEnvironment(extraEnv), args);

    private static IReadOnlyDictionary<string, string> BuildTrustedHostGitEnvironment(
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv)
                env[key] = value;

        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        env["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        env["GIT_ATTR_NOSYSTEM"] = "1";
        return env;
    }
}

public sealed record LocalGitHostOptions
{
    public required string RootDirectory { get; init; }
    public string FallbackDefaultBranch { get; init; } = "main";
}
