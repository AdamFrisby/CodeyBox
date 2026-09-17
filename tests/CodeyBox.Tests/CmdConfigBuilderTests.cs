using System.Text.Json;
using CodeyBox.Agents.Cmd;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdConfigBuilder"/>: the exact
/// <c>providers.json</c> shape (pinned to the vendor <c>/connect</c>
/// template and the file command-code 1.54.2 auto-creates on first run),
/// the bare-id <c>models</c> map keys, and the non-credential
/// <c>auth.json</c> placeholder contract.
/// </summary>
public sealed class CmdConfigBuilderTests
{
    [Fact]
    public void GuestPaths_TargetCommandcodeHome()
    {
        Assert.Equal(".commandcode/providers.json", CmdConfigBuilder.GuestProvidersRelativePath);
        Assert.Equal(".commandcode/auth.json", CmdConfigBuilder.GuestAuthRelativePath);
    }

    [Fact]
    public void BuildProvidersJson_MatchesVendorConnectShape()
    {
        var json = CmdConfigBuilder.BuildProvidersJson(["openrouter/nvidia/nemotron-3.5-lightning:free"]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var provider = root.GetProperty("provider").GetProperty("openrouter");
        Assert.Equal("OpenRouter", provider.GetProperty("name").GetString());
        Assert.Equal("https://openrouter.ai/api/v1", provider.GetProperty("baseURL").GetString());
        // The key travels as an environment reference — never a raw secret.
        Assert.Equal("$OPENROUTER_API_KEY", provider.GetProperty("apiKey").GetString());
        var models = provider.GetProperty("models");
        // The CLI resolves `-m openrouter/<id>` against the bare-id map
        // entry under that provider.
        Assert.True(models.TryGetProperty("nvidia/nemotron-3.5-lightning:free", out _));
    }

    [Fact]
    public void BuildProvidersJson_BlankAndDuplicateModels_Dropped()
    {
        var json = CmdConfigBuilder.BuildProvidersJson(
        [
            "openrouter/nvidia/nemotron-3.5-lightning:free",
            "  ",
            null,
            "OPENROUTER/nvidia/nemotron-3.5-lightning:free",
        ]);

        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement.GetProperty("provider").GetProperty("openrouter").GetProperty("models");
        var names = models.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["nvidia/nemotron-3.5-lightning:free"], names);
    }

    [Fact]
    public void BuildProvidersJson_NoModels_EmitsEmptyMap()
    {
        var json = CmdConfigBuilder.BuildProvidersJson([]);

        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement.GetProperty("provider").GetProperty("openrouter").GetProperty("models");
        Assert.Empty(models.EnumerateObject());
    }

    [Fact]
    public void StripProviderPrefix_RemovesOpenrouterQualifier()
    {
        Assert.Equal(
            "nvidia/nemotron-3.5-lightning:free",
            CmdConfigBuilder.StripProviderPrefix("openrouter/nvidia/nemotron-3.5-lightning:free"));
        Assert.Equal(
            "anthropic/claude-haiku-4-5",
            CmdConfigBuilder.StripProviderPrefix("anthropic/claude-haiku-4-5"));
    }

    [Fact]
    public void BuildAuthJson_WritesNonCredentialPlaceholder()
    {
        var json = CmdConfigBuilder.BuildAuthJson(CmdAgentRunner.LocalOnlyAuthPlaceholder);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            CmdAgentRunner.LocalOnlyAuthPlaceholder,
            doc.RootElement.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void LocalOnlyAuthPlaceholder_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(CmdAgentRunner.LocalOnlyAuthPlaceholder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAuthJson_BlankPlaceholder_Throws(string placeholder)
    {
        Assert.Throws<ArgumentException>(() => CmdConfigBuilder.BuildAuthJson(placeholder));
    }
}
