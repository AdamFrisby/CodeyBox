using System.Net;

namespace CodeyBox.GiteaUpstreamPlugin;

/// <summary>
/// Failure talking to the Gitea forge itself: unreachable host, refused auth,
/// rate limiting, or an unexpected forge response. The orchestrator treats any
/// exception escaping <see cref="CodeyBox.Core.IUpstreamRemote"/> as an
/// infrastructure failure (retryable, never a verdict on the work item's
/// diff); this type makes that classification explicit and carries the
/// scrubbed forge detail. The auth token is never included in the message.
/// </summary>
public sealed class GiteaUpstreamException : InvalidOperationException
{
    /// <summary>Forge HTTP status that produced this failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }

    public GiteaUpstreamException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public GiteaUpstreamException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
