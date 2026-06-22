using System.Net.Http.Headers;

namespace CodeyBox.Cli.Services;

internal static class CodeyBoxHttpFactory
{
    internal static HttpClient CreateClient(ResolvedConfig config, TimeSpan timeout)
    {
        var baseUri = ApiBaseUrlValidator.Parse(config);
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
}
