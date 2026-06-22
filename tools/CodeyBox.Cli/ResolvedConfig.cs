namespace CodeyBox.Cli;

internal sealed class ResolvedConfig
{
    internal const string DefaultApiBaseUrl = "http://localhost:5036";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string ApiBaseUrlSource { get; set; } = $"built-in default {DefaultApiBaseUrl}";
    public string ApiBaseUrlPrecedence { get; set; } = BuildApiBaseUrlPrecedence("config file");
    public string? ApiKey { get; set; }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    internal static string BuildApiBaseUrlPrecedence(string configFileDescription) =>
        $"--api-url flag > CODEYBOX_CLI_API_URL environment variable > {configFileDescription} > built-in default {DefaultApiBaseUrl}";
}
