using System.Net;
using System.Net.Sockets;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.GiteaUpstreamPlugin;

/// <summary>
/// Where the Gitea repo lives and which environment variable names the token.
/// Bound per call (never cached) so operator config edits apply without a
/// restart: project-agnostic <see cref="CodeyBox.Core.IUpstreamRemote"/>
/// members read the plugin-scoped section
/// (<c>CodeyBox:Plugins:codeybox.gitea-upstream</c>); <c>CompleteAsync</c>
/// lets per-project <c>Upstream.PluginConfig</c> entries override the scoped
/// defaults. Credentials never appear here — only the <em>name</em> of the
/// env var holding the token, whose value is read from the process
/// environment at call time.
/// </summary>
public sealed record GiteaUpstreamOptions
{
    /// <summary>Gitea API base, e.g. <c>https://git.example.com/api/v1</c>. HTTPS required.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Repository owner (user or organisation).</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>Repository name.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable holding the Gitea token. Only the
    /// name is configured; the value is read via
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> at call time
    /// so rotation never needs a restart.
    /// </summary>
    public string TokenEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Reads the plugin-scoped defaults from
    /// <c>CodeyBox:Plugins:codeybox.gitea-upstream</c>. Missing keys stay
    /// empty; <see cref="Validate"/> reports what a call needs.
    /// </summary>
    public static GiteaUpstreamOptions FromScopedConfig(IConfigurationSection scoped)
        => new()
        {
            BaseUrl = scoped["BaseUrl"]?.Trim() ?? string.Empty,
            Owner = scoped["Owner"]?.Trim() ?? string.Empty,
            Repository = scoped["Repository"]?.Trim() ?? string.Empty,
            TokenEnvVar = scoped["TokenEnvVar"]?.Trim() ?? string.Empty,
        };

    /// <summary>
    /// Overlays per-project <c>Upstream.PluginConfig</c> entries
    /// (<c>BaseUrl</c>, <c>Owner</c>, <c>Repository</c>) on top of the scoped
    /// defaults. Unknown keys are ignored. Token names still come from the
    /// completion request or the scoped section — never from plugin config
    /// values, which must not carry secrets.
    /// </summary>
    public GiteaUpstreamOptions WithProjectOverrides(IReadOnlyDictionary<string, string> pluginConfig)
    {
        if (pluginConfig.Count == 0)
            return this;
        return this with
        {
            BaseUrl = OverrideOrSelf(pluginConfig, "BaseUrl", BaseUrl),
            Owner = OverrideOrSelf(pluginConfig, "Owner", Owner),
            Repository = OverrideOrSelf(pluginConfig, "Repository", Repository),
        };
    }

    private static string OverrideOrSelf(
        IReadOnlyDictionary<string, string> config, string key, string current)
        => config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : current;

    /// <summary>
    /// Validates coordinates and returns the canonical API base (no trailing
    /// slash). Throws <see cref="InvalidOperationException"/> naming the
    /// missing key or the offending value — never the token.
    /// </summary>
    public string Validate(string context)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException(
                $"{context}: Gitea BaseUrl is not configured (plugin scope or Upstream.PluginConfig)");
        if (string.IsNullOrWhiteSpace(Owner))
            throw new InvalidOperationException(
                $"{context}: Gitea Owner is not configured (plugin scope or Upstream.PluginConfig)");
        if (string.IsNullOrWhiteSpace(Repository))
            throw new InvalidOperationException(
                $"{context}: Gitea Repository is not configured (plugin scope or Upstream.PluginConfig)");
        if (!IsValidPathSegment(Owner))
            throw new InvalidOperationException(
                $"{context}: Gitea Owner contains characters outside the forge's allowed set");
        if (!IsValidPathSegment(Repository))
            throw new InvalidOperationException(
                $"{context}: Gitea Repository contains characters outside the forge's allowed set");
        ValidateBaseUrl(BaseUrl, context);
        return BaseUrl.TrimEnd('/');
    }

    private static bool IsValidPathSegment(string value)
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
                $"{context}: Gitea BaseUrl is not a valid absolute URI");
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{context}: Gitea BaseUrl must use https:// (got '{uri.Scheme}://')");
        if (SsrfBlockedHosts.Contains(uri.Host))
            throw new InvalidOperationException(
                $"{context}: Gitea BaseUrl must not point to a loopback or internal address");
        if (IPAddress.TryParse(uri.Host, out var ip) && IsNonRoutableAddress(ip))
            throw new InvalidOperationException(
                $"{context}: Gitea BaseUrl must not point to a private or loopback address");
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
