using System.Net;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// Builds the HTTP client the Doppler plugin sends credential-bearing
/// traffic through. The client never follows redirects: a 3xx from the API
/// is untrusted runtime output (Doppler itself notes plain-http calls
/// redirect to https), and following it would re-send the Authorization
/// bearer token to the redirect target. Redirects therefore surface to
/// each send site as ordinary 3xx responses, which refuse them explicitly.
/// </summary>
internal static class DopplerHttpClients
{
    /// <summary>A client for Doppler API traffic. Never follows redirects.</summary>
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
