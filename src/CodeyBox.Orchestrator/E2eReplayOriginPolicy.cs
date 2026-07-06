using System.Net;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class E2eReplayOriginPolicy
{
    public static bool TryValidateReadinessUrl(
        string? url,
        IReadOnlyList<string> allowedOrigins,
        out Uri? uri,
        out string detail)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            detail = "readiness.url must be an absolute http(s) URL";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            detail = "readiness.url must not contain userinfo";
            return false;
        }

        if (IsBlockedMetadataHost(parsed.IdnHost))
        {
            detail = $"readiness.url resolves to disallowed metadata address {parsed.IdnHost}";
            return false;
        }

        var normalized = NormalizeOrigin(parsed);
        foreach (var allowedOrigin in allowedOrigins)
        {
            if (!Uri.TryCreate(allowedOrigin, UriKind.Absolute, out var allowedUri))
                continue;
            if (string.Equals(normalized, NormalizeOrigin(allowedUri), StringComparison.OrdinalIgnoreCase))
            {
                uri = parsed;
                detail = string.Empty;
                return true;
            }
        }

        detail = $"readiness.url origin '{normalized}' is not in CodeyBox:E2eExecution:AllowedReadinessOrigins";
        return false;
    }

    public static bool TryValidateReplayNavigationTargets(
        E2eReplayArtifact artifact,
        IReadOnlyList<string> allowedOrigins,
        out string detail)
    {
        foreach (var (step, index) in artifact.Steps.Select((step, index) => (step, index)))
        {
            if (!string.Equals(step.Action, "navigate", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryValidateAllowedAppUrl(step.Target, allowedOrigins, "steps[" + index + "].target", out detail))
                return false;
        }

        detail = string.Empty;
        return true;
    }

    public static bool TryValidateAllowedAppUrl(
        string? url,
        IReadOnlyList<string> allowedOrigins,
        string fieldName,
        out string detail)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            detail = $"{fieldName} must be an absolute http(s) URL";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            detail = $"{fieldName} must not contain userinfo";
            return false;
        }

        if (IsBlockedMetadataHost(parsed.IdnHost))
        {
            detail = $"{fieldName} resolves to disallowed metadata address {parsed.IdnHost}";
            return false;
        }

        if (!IsAllowedOrigin(parsed, allowedOrigins, out var normalized))
        {
            detail = $"{fieldName} origin '{normalized}' is not in CodeyBox:E2eExecution:AllowedReadinessOrigins";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    public static bool IsAllowedOrigin(Uri parsed, IReadOnlyList<string> allowedOrigins, out string normalized)
    {
        normalized = NormalizeOrigin(parsed);
        foreach (var allowedOrigin in allowedOrigins)
        {
            if (!Uri.TryCreate(allowedOrigin, UriKind.Absolute, out var allowedUri))
                continue;
            if (string.Equals(normalized, NormalizeOrigin(allowedUri), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static int EffectivePort(Uri uri) =>
        uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;

    public static string NormalizeOrigin(Uri uri)
    {
        var port = EffectivePort(uri);
        var defaultPort = uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        var host = NormalizeUriHost(uri.IdnHost);
        return port == defaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{port}";
    }

    public static bool IsBlockedMetadataIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return IsIpv4Metadata(bytes);

        if (bytes.Length == 16)
        {
            if (IsIpv4CompatibleMetadata(bytes))
                return true;

            var text = ip.ToString();
            return string.Equals(text, "fd00:ec2::254", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "fe80::a9fe:a9fe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsBlockedMetadataHost(string host)
    {
        var normalized = NormalizeUriHost(host);
        return IPAddress.TryParse(normalized, out var ip) && IsBlockedMetadataIp(ip);
    }

    private static string NormalizeUriHost(string host)
    {
        var trimmed = host.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool IsIpv4CompatibleMetadata(byte[] bytes)
    {
        if (bytes.Length != 16 || !IsIpv4Metadata(bytes[^4..]))
            return false;

        var firstTenZero = bytes.Take(10).All(static b => b == 0);
        if (!firstTenZero)
            return false;

        return (bytes[10] == 0 && bytes[11] == 0)
            || (bytes[10] == 0xff && bytes[11] == 0xff);
    }

    private static bool IsIpv4Metadata(byte[] bytes) =>
        bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254 && bytes[2] == 169 && bytes[3] == 254;
}
