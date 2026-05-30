using System.Net.Http.Headers;

namespace CodeyBox.Cli.Services;

internal static class CodeyBoxHttpFactory
{
    internal static HttpClient CreateClient(ResolvedConfig config, TimeSpan timeout)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(config.ApiBaseUrl),
            Timeout = timeout,
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return http;
    }
}
