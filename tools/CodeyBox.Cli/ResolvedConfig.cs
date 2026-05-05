namespace CodeyBox.Cli;

internal sealed class ResolvedConfig
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5050";
    public string? ApiKey { get; set; }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}
