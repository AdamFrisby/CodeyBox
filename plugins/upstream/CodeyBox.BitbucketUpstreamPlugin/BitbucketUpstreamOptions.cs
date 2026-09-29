using System.Net;
using System.Net.Sockets;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.BitbucketUpstreamPlugin;

/// <summary>
/// Where the Bitbucket Cloud repository lives and which environment variable
/// names the credential. Bound per call (never cached) so operator config
/// edits apply without a restart: project-agnostic
/// <see cref="CodeyBox.Core.IUpstreamRemote"/> members read the plugin-scoped
/// section (<c>CodeyBox:Plugins:codeybox.bitbucket-upstream</c>);
/// <c>CompleteAsync</c> lets per-project <c>Upstream.PluginConfig</c> entries
/// override the scoped defaults. Credentials never appear here — only the
/// <em>name</em> of the env var holding the credential, whose value is read
/// from the process environment at call time.
/// </summary>
public sealed record BitbucketUpstreamOptions
{
    /// <summary>
    /// Bitbucket Cloud API base. Defaults to
    /// <c>https://api.bitbucket.org/2.0</c>; override only to route through a
    /// proxy. HTTPS required.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.bitbucket.org/2.0";

    /// <summary>Bitbucket workspace id (the <c>{workspace}</c> URL segment).</summary>
    public string Workspace { get; init; } = string.Empty;

    /// <summary>Repository slug (the <c>{repo_slug}</c> URL segment).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable holding the Bitbucket credential. Only
    /// the name is configured; the value is read via
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> at call time
    /// so rotation never needs a restart. The value is either
    /// <c>username:app-password</c> (Basic auth) or a bare OAuth/API token
    /// (Bearer auth); see README.md.
    /// </summary>
    public string TokenEnvVar { get; init; } = string.Empty;

    /// <summary>Items requested per API page, 1–100. Default 50.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Page cap per listing, 1–50. Default 10.</summary>
    public int MaxListPages { get; init; } = 10;

    /// <summary>
    /// Reads the plugin-scoped defaults from
    /// <c>CodeyBox:Plugins:codeybox.bitbucket-upstream</c>. Missing keys stay
    /// at their defaults; <see cref="Validate"/> reports what a call needs.
    /// </summary>
    public static BitbucketUpstreamOptions FromScopedConfig(IConfigurationSection scoped)
        => new()
        {
            BaseUrl = scoped["BaseUrl"]?.Trim() is { Length: > 0 } baseUrl
                ? baseUrl
                : "https://api.bitbucket.org/2.0",
            Workspace = scoped["Workspace"]?.Trim() ?? string.Empty,
            Repository = scoped["Repository"]?.Trim() ?? string.Empty,
            TokenEnvVar = scoped["TokenEnvVar"]?.Trim() ?? string.Empty,
            PageSize = ParseBoundedInt(scoped["PageSize"], 50, 1, 100),
            MaxListPages = ParseBoundedInt(scoped["MaxListPages"], 10, 1, 50),
        };

    /// <summary>
    /// Overlays per-project <c>Upstream.PluginConfig</c> entries
    /// (<c>BaseUrl</c>, <c>Workspace</c>, <c>Repository</c>,
    /// <c>PageSize</c>, <c>MaxListPages</c>) on top of the scoped defaults.
    /// Unknown keys are ignored. Credential names still come from the
    /// completion request or the scoped section — never from plugin config
    /// values, which must not carry secrets.
    /// </summary>
    public BitbucketUpstreamOptions WithProjectOverrides(IReadOnlyDictionary<string, string> pluginConfig)
    {
        if (pluginConfig.Count == 0)
            return this;
        return this with
        {
            BaseUrl = OverrideOrSelf(pluginConfig, "BaseUrl", BaseUrl),
            Workspace = OverrideOrSelf(pluginConfig, "Workspace", Workspace),
            Repository = OverrideOrSelf(pluginConfig, "Repository", Repository),
            PageSize = OverrideBoundedInt(pluginConfig, "PageSize", PageSize, 1, 100),
            MaxListPages = OverrideBoundedInt(pluginConfig, "MaxListPages", MaxListPages, 1, 50),
        };
    }

    private static string OverrideOrSelf(
        IReadOnlyDictionary<string, string> config, string key, string current)
        => config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : current;

    private static int OverrideBoundedInt(
        IReadOnlyDictionary<string, string> config, string key, int current, int min, int max)
        => config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : current;

    private static int ParseBoundedInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    /// <summary>
    /// Validates coordinates and returns the canonical API base (no trailing
    /// slash). Throws <see cref="InvalidOperationException"/> naming the
    /// missing key or the offending value — never the credential.
    /// </summary>
    public string Validate(string context)
    {
        if (string.IsNullOrWhiteSpace(Workspace))
            throw new InvalidOperationException(
                $"{context}: Bitbucket Workspace is not configured (plugin scope or Upstream.PluginConfig)");
        if (string.IsNullOrWhiteSpace(Repository))
            throw new InvalidOperationException(
                $"{context}: Bitbucket Repository is not configured (plugin scope or Upstream.PluginConfig)");
        if (!IsValidPathSegment(Workspace))
            throw new InvalidOperationException(
                $"{context}: Bitbucket Workspace contains characters outside the forge's allowed set");
        if (!IsValidPathSegment(Repository))
            throw new InvalidOperationException(
                $"{context}: Bitbucket Repository contains characters outside the forge's allowed set");
        ValidateBaseUrl(BaseUrl, context);
        return BaseUrl.TrimEnd('/');
    }

    internal static bool IsValidPathSegment(string value)
        => !value.Contains('/')
            && !value.Contains('?')
            && !value.Contains('#')
            && !value.Contains('%')
            && !value.Contains("..", StringComparison.Ordinal)
            && !value.Any(char.IsWhiteSpace);

    private static readonly HashSet<string> SsrfBlockedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "metadata.google.internal" };

    private static void ValidateBaseUrl(string baseUrl, string context)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{context}: Bitbucket BaseUrl is not a valid absolute URI");
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{context}: Bitbucket BaseUrl must use https:// (got '{uri.Scheme}://')");
        if (SsrfBlockedHosts.Contains(uri.Host))
            throw new InvalidOperationException(
                $"{context}: Bitbucket BaseUrl must not point to a loopback or internal address");
        if (IPAddress.TryParse(uri.Host, out var ip) && IsNonRoutableAddress(ip))
            throw new InvalidOperationException(
                $"{context}: Bitbucket BaseUrl must not point to a private or loopback address");
    }

    private static bool IsNonRoutableAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xfe) == 0xfc
                || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
        }

        return false;
    }
}
