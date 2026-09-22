using System.Net;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Builds the HTTP client the Bitwarden plugin sends credential-bearing
/// traffic through. The client never follows redirects: a 3xx from the
/// identity or API origin is untrusted runtime output, and following it
/// would re-send the bearer token (or the machine-account secret in the
/// token form) to the redirect target. Redirects therefore surface to each
/// send site as ordinary 3xx responses, which refuse them explicitly.
/// </summary>
internal static class BitwardenHttpClients
{
    /// <summary>Connection-pool lifetime, so DNS rotation still propagates without the shared HTTP factory.</summary>
    private const int PooledConnectionLifetimeMinutes = 5;

    /// <summary>A client for identity and Secrets Manager API traffic. Never follows redirects.</summary>
    internal static HttpClient Create(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>True for 3xx statuses, which this plugin never follows.</summary>
    internal static bool IsRedirect(HttpStatusCode status) =>
        (int)status >= 300 && (int)status < 400;

    /// <summary>
    /// True when both URIs share scheme, host, and port — i.e. no hop to
    /// another origin happened between them.
    /// </summary>
    internal static bool IsSameOrigin(Uri first, Uri second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase)
            && first.Port == second.Port;
    }
}
