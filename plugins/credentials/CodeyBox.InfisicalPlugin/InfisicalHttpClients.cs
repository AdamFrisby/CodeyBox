using System.Net;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// Builds the HTTP clients the Infisical plugin sends credential-bearing
/// traffic through. Both clients are constructed over a handler that never
/// follows redirects: a 3xx from the backend (or from a brokered upstream)
/// is untrusted runtime output, and following it would re-send the
/// Authorization bearer token, the universal-auth client secret, or the
/// brokered credential to the redirect target. Redirects therefore surface
/// to each send site as ordinary 3xx responses, which refuse them
/// explicitly (API client) or pass them back to the guest (broker).
/// </summary>
internal static class InfisicalHttpClients
{
    /// <summary>
    /// A client for Infisical API traffic (login, static fetch, dynamic
    /// leases). Never follows redirects.
    /// </summary>
    internal static HttpClient Create(TimeSpan timeout)
        => CredentialHttp.CreateNoRedirectClient(timeout);

    /// <summary>True for 3xx statuses, which this plugin never follows.</summary>
    internal static bool IsRedirect(HttpStatusCode status)
        => CredentialHttp.IsRedirect(status);

    /// <summary>
    /// True when both URIs share scheme, host, and port — i.e. no hop to
    /// another origin happened between them.
    /// </summary>
    internal static bool IsSameOrigin(Uri first, Uri second)
        => CredentialHttp.IsSameOrigin(first, second);
}
