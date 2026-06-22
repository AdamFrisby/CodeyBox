namespace CodeyBox.Cli;

internal static class CliDiagnosticFormatting
{
    internal static string FormatApiBaseUrlForDiagnostics(string value)
    {
        var sanitized = SanitizeDiagnosticText(value);
        if (Uri.TryCreate(sanitized, UriKind.Absolute, out var uri))
            return FormatParsedUriForDiagnostics(uri);

        return RedactUrlLikeText(sanitized);
    }

    internal static string SanitizeDiagnosticText(string value) =>
        new(value.Where(c => !char.IsControl(c)).ToArray());

    private static string FormatParsedUriForDiagnostics(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.Host}]"
            : uri.IdnHost;
        var authority = $"{host}:{uri.Port}";
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var query = string.IsNullOrEmpty(uri.Query) ? "" : "?redacted";

        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? "" : "redacted@";
        return string.IsNullOrEmpty(path)
            ? $"{uri.Scheme}://{userInfo}{authority}{query}"
            : $"{uri.Scheme}://{userInfo}{authority}/{path}{query}";
    }

    private static string RedactUrlLikeText(string value)
    {
        var fragmentStart = value.IndexOf('#');
        if (fragmentStart >= 0)
            value = value[..fragmentStart];

        var queryStart = value.IndexOf('?');
        if (queryStart >= 0)
            value = value[..queryStart] + "?redacted";

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var authorityStart = schemeEnd + 3;
            var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
            if (authorityEnd < 0)
                authorityEnd = value.Length;

            if (authorityEnd > authorityStart)
            {
                var userInfoEnd = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
                if (userInfoEnd >= authorityStart)
                    value = value[..authorityStart] + "redacted@" + value[(userInfoEnd + 1)..];
            }
        }

        return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
    }
}
