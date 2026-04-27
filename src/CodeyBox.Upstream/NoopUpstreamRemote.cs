using CodeyBox.Core;

namespace CodeyBox.Upstream;

/// <summary>
/// No-op upstream. Used when the deployment has no external git remote and
/// the host bare repo is the source of truth.
/// </summary>
public sealed class NoopUpstreamRemote : IUpstreamRemote
{
    public string Name => "noop";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));
}
