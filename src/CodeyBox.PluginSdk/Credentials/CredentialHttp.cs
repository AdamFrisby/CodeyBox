using System.Net;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared HTTP plumbing for credential-provider plugins. Credential-bearing
/// traffic never follows redirects: a 3xx from a secret backend is
/// untrusted runtime output, and following it would re-send bearer tokens
/// or grant material to the redirect target. Redirects surface to each send
/// site as ordinary 3xx responses, which refuse them explicitly.
/// One implementation so every backend shares the same redirect and
/// same-origin policy.
/// </summary>
public static class CredentialHttp
{
    /// <summary>Connection-pool lifetime, so DNS rotation still propagates without the shared HTTP factory.</summary>
    private const int PooledConnectionLifetimeMinutes = 5;

    /// <summary>Creates a client that never follows redirects.</summary>
    public static HttpClient CreateNoRedirectClient(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>True for 3xx statuses, which credential plugins never follow.</summary>
    public static bool IsRedirect(HttpStatusCode status) =>
        (int)status >= 300 && (int)status < 400;

    /// <summary>
    /// True when both URIs share scheme, host, and port — i.e. no hop to
    /// another origin happened between them.
    /// </summary>
    public static bool IsSameOrigin(Uri first, Uri second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase)
            && first.Port == second.Port;
    }
}
