namespace CodeyBox.Cli;

internal sealed class ResolvedConfig
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5036";
    public string? ApiKey { get; set; }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}
