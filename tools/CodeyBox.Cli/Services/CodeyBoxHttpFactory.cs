using System.Net.Http.Headers;

namespace CodeyBox.Cli.Services;

internal static class CodeyBoxHttpFactory
{
    internal static HttpClient CreateClient(ResolvedConfig config, TimeSpan timeout)
    {
        var baseUri = ParseApiBaseUrl(config);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = CliConnectionDiagnostics.ConnectTimeout,
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = timeout,
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return http;
    }

    private static Uri ParseApiBaseUrl(ResolvedConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl))
            throw new CodeyBoxCliException(CliConnectionDiagnostics.FormatMalformedApiBaseUrl(
                config,
                "value is empty"));

        if (!Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out var uri))
            throw new CodeyBoxCliException(CliConnectionDiagnostics.FormatMalformedApiBaseUrl(
                config,
                "value is not an absolute URI"));

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new CodeyBoxCliException(CliConnectionDiagnostics.FormatMalformedApiBaseUrl(
                config,
                "scheme must be http or https"));

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new CodeyBoxCliException(CliConnectionDiagnostics.FormatMalformedApiBaseUrl(
                config,
                "host is empty"));

        return uri;
    }
}
