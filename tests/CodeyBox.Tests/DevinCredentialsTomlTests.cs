using CodeyBox.Agents;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinCredentialsToml"/> (the flat
/// <c>credentials.toml</c> parser shared by the smoke probe and the
/// orchestrator's credential extractor) and
/// <see cref="CredentialFileTokenExtractor.ExtractDevinCredentials"/>, which
/// hands both fields to the quota probe.
/// </summary>
public sealed class DevinCredentialsTomlTests
{
    private const string RealisticToml = """
        # written by `devin auth login`
        api_key = "sk-devin-test"
        windsurf_api_key = "ws-test"
        api_server_url = "https://devin-api.example"
        devin_webapp_host = "https://app.devin.ai"
        """;

    [Fact]
    public void TryGetString_ReadsQuotedFields()
    {
        Assert.Equal("sk-devin-test", DevinCredentialsToml.TryGetString(RealisticToml, "api_key"));
        Assert.Equal("https://devin-api.example",
            DevinCredentialsToml.TryGetString(RealisticToml, "api_server_url"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetString_EmptyInput_ReturnsNull(string? toml)
    {
        Assert.Null(DevinCredentialsToml.TryGetString(toml, "api_key"));
    }

    [Fact]
    public void TryGetString_MissingKey_ReturnsNull()
    {
        Assert.Null(DevinCredentialsToml.TryGetString(RealisticToml, "no_such_key"));
    }

    [Fact]
    public void TryGetString_NonStringScalar_ReturnsNull()
    {
        // Bare scalars are outside the credentials contract — treat as absent
        // rather than accepting a value the CLI itself would reject.
        Assert.Null(DevinCredentialsToml.TryGetString("api_key = 42", "api_key"));
    }

    [Fact]
    public void TryGetString_SectionHeadersAreNotScoped()
    {
        // The parser is deliberately flat: the [section] line is skipped but
        // keys are not scoped by it — the FIRST matching line wins. The real
        // credentials.toml is a flat table, so section headers never appear;
        // this pins that the parser makes no attempt to honor them.
        const string toml = """
            [section]
            api_key = "first-match"
            """;
        Assert.Equal("first-match", DevinCredentialsToml.TryGetString(toml, "api_key"));
    }

    [Fact]
    public void ExtractDevinCredentials_ReturnsApiKeyAndServerUrl()
    {
        var (apiKey, apiServerUrl) = CredentialFileTokenExtractor.ExtractDevinCredentials(RealisticToml);

        Assert.Equal("sk-devin-test", apiKey);
        Assert.Equal("https://devin-api.example", apiServerUrl);
    }

    [Fact]
    public void ExtractDevinCredentials_NoServerUrl_ReturnsNullUrl()
    {
        var (apiKey, apiServerUrl) = CredentialFileTokenExtractor.ExtractDevinCredentials(
            "api_key = \"sk-only\"");

        Assert.Equal("sk-only", apiKey);
        Assert.Null(apiServerUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not toml at all")]
    [InlineData("api_server_url = \"https://x\"")]
    public void ExtractDevinCredentials_NoApiKey_ReturnsNulls(string? raw)
    {
        var (apiKey, _) = CredentialFileTokenExtractor.ExtractDevinCredentials(raw);
        Assert.Null(apiKey);
    }
}
