using System.Net;

namespace CodeyBox.BitbucketUpstreamPlugin;

/// <summary>
/// Failure talking to the Bitbucket Cloud forge itself: unreachable host,
/// refused auth, rate limiting, or an unexpected forge response. The
/// orchestrator treats any exception escaping
/// <see cref="CodeyBox.Core.IUpstreamRemote"/> as an infrastructure failure
/// (retryable, never a verdict on the work item's diff); this type makes that
/// classification explicit and carries the scrubbed forge detail. Credentials
/// are never included in the message.
/// </summary>
public sealed class BitbucketUpstreamException : InvalidOperationException
{
    /// <summary>Forge HTTP status that produced this failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }

    public BitbucketUpstreamException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public BitbucketUpstreamException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
