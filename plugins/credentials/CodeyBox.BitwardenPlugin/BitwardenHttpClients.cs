using System.Net;
using CodeyBox.PluginSdk.Credentials;

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
    /// <summary>A client for identity and Secrets Manager API traffic. Never follows redirects.</summary>
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
