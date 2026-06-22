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
            $"Error: malformed API base URL '{CliDiagnosticFormatting.FormatApiBaseUrlForDiagnostics(config.ApiBaseUrl)}'.",
            $"Source: {CliDiagnosticFormatting.SanitizeDiagnosticText(config.ApiBaseUrlSource)}.",
            $"Cause: {CliDiagnosticFormatting.SanitizeDiagnosticText(cause)}",
            $"Precedence: {CliDiagnosticFormatting.SanitizeDiagnosticText(config.ApiBaseUrlPrecedence)}.",
            $"Remedy: {Remedy}",
        });

    internal static string FormatConnectionFailure(ResolvedConfig config, Exception exception) =>
        string.Join(Environment.NewLine, new[]
        {
            "Could not connect to the CodeyBox API.",
            $"Resolved API base URL: {CliDiagnosticFormatting.FormatApiBaseUrlForDiagnostics(config.ApiBaseUrl)}",
            $"Source: {CliDiagnosticFormatting.SanitizeDiagnosticText(config.ApiBaseUrlSource)}.",
            $"Cause: {ClassifyCause(exception)}",
            $"Underlying error: {CliDiagnosticFormatting.SanitizeDiagnosticText(exception.GetBaseException().Message)}",
            $"Precedence: {CliDiagnosticFormatting.SanitizeDiagnosticText(config.ApiBaseUrlPrecedence)}.",
            $"Remedy: {Remedy}",
        });

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
