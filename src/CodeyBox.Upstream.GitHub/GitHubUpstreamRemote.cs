using CodeyBox.Core;
using CodeyBox.Git;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// GitHub upstream remote. Pushes the host bare repo to a GitHub repository
/// using a Personal Access Token. The PAT lives only in the orchestrator
/// process and never on argv:
///   - URL is the bare https://github.com/owner/repo.git (no embedded token).
///   - GIT_ASKPASS points to a per-call script that reads the token from env.
///   - Env vars are visible only to the git child process (not on argv,
///     not in process listings).
///
/// Compatible with classic PATs and fine-grained tokens. For fine-grained,
/// scope = "Contents: write" on the target repo only.
/// </summary>
public sealed class GitHubUpstreamRemote : IUpstreamRemote
{
    private readonly IGitHost _gitHost;
    private readonly GitHubUpstreamOptions _opts;

    public GitHubUpstreamRemote(IGitHost gitHost, GitHubUpstreamOptions opts)
    {
        _gitHost = gitHost;
        _opts = opts;
        if (string.IsNullOrEmpty(_opts.Token))
            throw new ArgumentException("GitHub PAT must be provided", nameof(opts));
    }

    public string Name => "github";

    public async Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
    {
        var url = $"https://github.com/{_opts.Owner}/{_opts.Repository}.git";
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
        try
        {
            await _gitHost.PushToUpstreamAsync(repositoryId, url, branch, askpass.Environment, ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            // Defence-in-depth: scrub the token from any error surface, even
            // though we never put it on argv. The askpass script could in
            // theory have printed it to stderr if invoked weirdly.
            var scrubbed = ex.Message.Replace(_opts.Token, "***", StringComparison.Ordinal);
            return new UpstreamPushResult(false, scrubbed);
        }
    }
}

public sealed record GitHubUpstreamOptions
{
    public required string Owner { get; init; }
    public required string Repository { get; init; }
    /// <summary>GitHub PAT or fine-grained token. Never logged, never on argv.</summary>
    public required string Token { get; init; }
}
