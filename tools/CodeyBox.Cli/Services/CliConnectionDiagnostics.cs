using System.Net.Sockets;

namespace CodeyBox.Cli.Services;

internal sealed class CodeyBoxCliException(string message) : Exception(message);

internal sealed class CodeyBoxConnectionException(string message, Exception innerException)
    : HttpRequestException(message, innerException);

internal static class CliConnectionDiagnostics
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private const string Remedy =
        "Run codeybox configure to set the API base URL and key, or pass --api-url.";

    internal static string FormatMalformedApiBaseUrl(ResolvedConfig config, string cause) =>
        string.Join(Environment.NewLine, new[]
        {
            $"Error: malformed API base URL '{FormatApiBaseUrlForDiagnostics(config.ApiBaseUrl)}'.",
            $"Source: {SanitizeDiagnosticText(config.ApiBaseUrlSource)}.",
            $"Cause: {SanitizeDiagnosticText(cause)}",
            $"Precedence: {SanitizeDiagnosticText(ConfigResolver.ApiBaseUrlPrecedence)}.",
            $"Remedy: {Remedy}",
        });

    internal static string FormatConnectionFailure(ResolvedConfig config, Exception exception) =>
        string.Join(Environment.NewLine, new[]
        {
            "Could not connect to the CodeyBox API.",
            $"Resolved API base URL: {FormatApiBaseUrlForDiagnostics(config.ApiBaseUrl)}",
            $"Source: {SanitizeDiagnosticText(config.ApiBaseUrlSource)}.",
            $"Cause: {ClassifyCause(exception)}",
            $"Underlying error: {SanitizeDiagnosticText(exception.GetBaseException().Message)}",
            $"Precedence: {SanitizeDiagnosticText(ConfigResolver.ApiBaseUrlPrecedence)}.",
            $"Remedy: {Remedy}",
        });

    internal static string FormatApiBaseUrlForDiagnostics(string value)
    {
        var sanitized = SanitizeDiagnosticText(value);
        if (Uri.TryCreate(sanitized, UriKind.Absolute, out var uri))
            return FormatParsedUriForDiagnostics(uri);

        return RedactUrlLikeText(sanitized);
    }

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

    private static string SanitizeDiagnosticText(string value) =>
        new(value.Where(c => !char.IsControl(c)).ToArray());

    private static string ClassifyCause(Exception exception)
    {
        var socket = FindSocketException(exception);
        if (socket is not null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "connection refused",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                    "invalid host or DNS lookup failed",
                SocketError.TimedOut => "timeout",
                _ => $"connection failed ({socket.SocketErrorCode})",
            };
        }

        if (exception is TaskCanceledException || FindException<TimeoutException>(exception) is not null)
            return "timeout";

        return "connection failed";
    }

    private static SocketException? FindSocketException(Exception exception) =>
        FindException<SocketException>(exception);

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is TException typed)
                return typed;
        }

        return null;
    }
}
