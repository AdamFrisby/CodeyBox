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
        catch (UpstreamRebaseConflictException)
        {
            throw;
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
