using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Upstream;

/// <summary>
/// Generic git upstream: pushes the host bare repo to any URL git understands.
/// Authentication is handled by the host's git config (credential helpers,
/// SSH keys, etc.) — never injected into a sandbox.
/// </summary>
public sealed class GitGenericUpstreamRemote : IUpstreamRemote
{
    private readonly IGitHost _gitHost;
    private readonly GitGenericUpstreamOptions _opts;

    public GitGenericUpstreamRemote(IGitHost gitHost, GitGenericUpstreamOptions opts)
    {
        _gitHost = gitHost;
        _opts = opts;
    }

    public string Name => "git-generic";

    public async Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
    {
        try
        {
            await _gitHost.PushToUpstreamAsync(repositoryId, _opts.UpstreamUrl, branch, _opts.ExtraEnvironment, ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, ex.Message);
        }
    }

    public async Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        // Generic git has no PR concept — push baseBranch and report done.
        try
        {
            await _gitHost.PushToUpstreamAsync(request.RepositoryId, _opts.UpstreamUrl, request.BaseBranch, _opts.ExtraEnvironment, ct);
            AuditLog.UpstreamPush(request.BaseBranch, ScrubUrlCredentials(_opts.UpstreamUrl));
            return new UpstreamCompletionOutcome { BranchPushed = true };
        }
        catch (Exception ex)
        {
            // Strip embedded credentials (e.g. https://user:pass@host/repo.git) from
            // the exception message before it reaches the orchestrator's Warning log.
            var safeUrl = ScrubUrlCredentials(_opts.UpstreamUrl);
            var safeMessage = ex.Message.Replace(_opts.UpstreamUrl, safeUrl, StringComparison.Ordinal);
            throw new InvalidOperationException(
                $"Failed to push '{request.BaseBranch}' to '{safeUrl}': {safeMessage}", ex);
        }
    }

    /// <summary>
    /// Clones <paramref name="targetBranch"/> to a temp directory, fetches
    /// <paramref name="sourceBranch"/> from origin, and attempts <c>git merge</c>.
    /// On conflict returns false; on success pushes back to origin and returns true.
    /// The temp directory is deleted regardless of outcome.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        var safeUrl = ScrubUrlCredentials(_opts.UpstreamUrl);
        var tmpDir = Path.Combine(Path.GetTempPath(), "codeybox-sync-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tmpDir);
            var clone = await RunGitAsync(tmpDir, ct, _opts.ExtraEnvironment,
                "clone", "--branch", targetBranch, "--single-branch", "--", _opts.UpstreamUrl, tmpDir);
            if (clone.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git clone failed: {clone.Stderr.Replace(_opts.UpstreamUrl, safeUrl, StringComparison.Ordinal)}");

            var fetch = await RunGitAsync(tmpDir, ct, _opts.ExtraEnvironment, "fetch", "origin", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git fetch failed: {fetch.Stderr.Replace(_opts.UpstreamUrl, safeUrl, StringComparison.Ordinal)}");

            var merge = await RunGitAsync(tmpDir, ct, _opts.ExtraEnvironment,
                "merge", $"FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await RunGitAsync(tmpDir, ct, _opts.ExtraEnvironment, "merge", "--abort");
                return false;
            }

            var push = await RunGitAsync(tmpDir, ct, _opts.ExtraEnvironment, "push", "origin", targetBranch);
            if (push.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git push failed: {push.Stderr.Replace(_opts.UpstreamUrl, safeUrl, StringComparison.Ordinal)}");

            return true;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir, CancellationToken ct,
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

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, stdout, stderr);
    }

    private static string ScrubUrlCredentials(string url)
    {
        try
        {
            var builder = new UriBuilder(url);
            if (string.IsNullOrEmpty(builder.UserName) && string.IsNullOrEmpty(builder.Password))
                return url;
            builder.UserName = string.Empty;
            builder.Password = string.Empty;
            return builder.Uri.ToString();
        }
        catch
        {
            return "[url-redacted]";
        }
    }
}

public sealed record GitGenericUpstreamOptions
{
    public required string UpstreamUrl { get; init; }
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();
}
