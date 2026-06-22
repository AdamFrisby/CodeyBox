using CodeyBox.Cli.Services;

namespace CodeyBox.Cli;

internal static class ApiBaseUrlValidator
{
    internal static Uri Parse(ResolvedConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl))
            throw Malformed(config, "value is empty");

        if (!Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out var uri))
            throw Malformed(config, "value is not an absolute URI");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw Malformed(config, "scheme must be http or https");

        return uri;
    }

    private static CodeyBoxCliException Malformed(ResolvedConfig config, string cause) =>
        new(CliConnectionDiagnostics.FormatMalformedApiBaseUrl(config, cause));
}
