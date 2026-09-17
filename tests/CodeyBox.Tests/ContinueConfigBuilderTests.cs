using CodeyBox.Agents.Continue;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueConfigBuilder"/>. Pins the guest
/// <c>config.yaml</c> shape verified against @continuedev/cli 1.5.47: the
/// first-class <c>openrouter</c> provider with <c>apiBase</c> +
/// <c>apiKey</c> in a single model entry (first-entry-wins selection), and
/// the blank/newline guards that keep a malformed document from reaching
/// the guest.
/// </summary>
public sealed class ContinueConfigBuilderTests
{
    [Fact]
    public void GuestConfigRelativePath_IsContinueDefaultLookup()
    {
        // The runner writes exactly where the CLI reads by default, so no
        // --config flag is needed.
        Assert.Equal(".continue/config.yaml", ContinueConfigBuilder.GuestConfigRelativePath);
    }

    [Fact]
    public void BuildConfigYaml_WritesSingleOpenRouterModelEntry()
    {
        var yaml = ContinueConfigBuilder.BuildConfigYaml(
            "https://openrouter.ai/api/v1",
            "secret-key",
            "nvidia/nemotron-3.5-lightning:free");

        Assert.Contains("schema: v1", yaml, StringComparison.Ordinal);
        Assert.Contains("provider: openrouter", yaml, StringComparison.Ordinal);
        Assert.Contains("model: 'nvidia/nemotron-3.5-lightning:free'", yaml, StringComparison.Ordinal);
        Assert.Contains("apiBase: 'https://openrouter.ai/api/v1'", yaml, StringComparison.Ordinal);
        Assert.Contains("apiKey: 'secret-key'", yaml, StringComparison.Ordinal);
        Assert.Equal(1, yaml.Split("- name:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildConfigYaml_RespectsConfiguredBaseUrl()
    {
        var yaml = ContinueConfigBuilder.BuildConfigYaml("https://proxy.internal/v1", "k", "m:free");

        Assert.Contains("apiBase: 'https://proxy.internal/v1'", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigYaml_QuotesYamlSpecialValues()
    {
        // A key containing ':' or '#' must survive the YAML parse.
        var yaml = ContinueConfigBuilder.BuildConfigYaml("https://openrouter.ai/api/v1", "sk-or-v1:abc#def'ghi", "m:free");

        Assert.Contains("apiKey: 'sk-or-v1:abc#def''ghi'", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigYaml_TrimsModelAndBaseUrl()
    {
        var yaml = ContinueConfigBuilder.BuildConfigYaml(" https://proxy.internal/v1 ", "k", "  m:free  ");

        Assert.Contains("model: 'm:free'", yaml, StringComparison.Ordinal);
        Assert.Contains("apiBase: 'https://proxy.internal/v1'", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigYaml_BlankBaseUrl_Throws(string? baseUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            ContinueConfigBuilder.BuildConfigYaml(baseUrl, "k", "m:free"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigYaml_BlankApiKey_Throws(string? apiKey)
    {
        Assert.Throws<ArgumentException>(() =>
            ContinueConfigBuilder.BuildConfigYaml("https://openrouter.ai/api/v1", apiKey!, "m:free"));
    }

    [Theory]
    [InlineData("key\ninjected: true")]
    [InlineData("key\rinjected: true")]
    public void BuildConfigYaml_NewlineInApiKey_Throws(string apiKey)
    {
        // A newline would break out of the quoted scalar and rewrite the
        // document — never a valid API key.
        Assert.Throws<ArgumentException>(() =>
            ContinueConfigBuilder.BuildConfigYaml("https://openrouter.ai/api/v1", apiKey, "m:free"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigYaml_BlankModel_Throws(string? modelId)
    {
        Assert.Throws<ArgumentException>(() =>
            ContinueConfigBuilder.BuildConfigYaml("https://openrouter.ai/api/v1", "k", modelId));
    }
}
