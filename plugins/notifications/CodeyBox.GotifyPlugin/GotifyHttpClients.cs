using System.Net;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Builds the HTTP client the Gotify plugin sends credential-bearing
/// traffic through. The client never follows redirects: a 3xx from the
/// server is untrusted peer output, and following it would re-send the
/// <c>X-Gotify-Key</c> application token to the server-chosen Location
/// host. Redirects therefore surface to the caller as ordinary 3xx
/// responses, which <see cref="GotifyApiClient"/> refuses explicitly.
/// </summary>
internal static class GotifyHttpClients
{
    /// <summary>Connection-pool lifetime, so DNS rotation still propagates on the long-lived client.</summary>
    private const int PooledConnectionLifetimeMinutes = 5;

    /// <summary>A client for Gotify REST traffic. Never follows redirects.
    /// Per-call timeouts come from the caller's cancellation token, so the
    /// client's own timeout is disabled rather than competing with
    /// configured values above the default.</summary>
    internal static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>True for 3xx statuses, which this plugin never follows.</summary>
    internal static bool IsRedirect(HttpStatusCode status) =>
        (int)status >= 300 && (int)status < 400;
}
