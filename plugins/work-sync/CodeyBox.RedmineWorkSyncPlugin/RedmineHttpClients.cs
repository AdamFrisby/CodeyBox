using System.Net;

namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// Builds the HTTP client the Redmine work-sync plugin sends
/// credential-bearing traffic through. The client never follows redirects:
/// a 3xx from the instance is less-trusted dependency output, and following
/// it would re-send the <c>X-Redmine-API-Key</c> header to the
/// server-chosen Location host. Redirects therefore surface to the caller
/// as ordinary 3xx responses, which <see cref="RedmineRestClient"/>
/// refuses explicitly. The plugin owns this client rather than sharing the
/// host factory so the refusal cannot silently depend on a named-client
/// registration the host may or may not provide.
/// </summary>
internal static class RedmineHttpClients
{
    /// <summary>Connection-pool lifetime, so DNS rotation still propagates on the long-lived client.</summary>
    private const int PooledConnectionLifetimeMinutes = 5;

    /// <summary>
    /// A client for Redmine REST traffic. Never follows redirects.
    /// Per-request timeouts come from the live <c>TimeoutSeconds</c> option
    /// via the caller's cancellation token, so the client's own timeout is
    /// disabled rather than competing with configured values.
    /// </summary>
    internal static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// True for every 3xx status, which this plugin never follows. Any 3xx
    /// reaching the client is refused as a misconfiguration hint
    /// (configure the canonical <c>ApiBaseUrl</c>) rather than followed
    /// with credentials.
    /// </summary>
    internal static bool IsRedirect(HttpStatusCode status) =>
        (int)status >= 300 && (int)status < 400;
}
