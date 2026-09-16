using System.Text.Json;
using CodeyBox.Agents.Kilo;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloConfigBuilder"/>. Pins the guest
/// <c>kilo.jsonc</c> shape verified against @kilocode/cli 7.7.2: the generic
/// <c>openai-compatible</c> provider block (no first-class
/// <c>openrouter</c> id exists) with <c>options.baseURL</c> +
/// <c>options.apiKey</c>, and the mandatory <c>models</c> allowlist the CLI
/// resolves <c>-m</c> against (a missing entry fails closed with
/// <c>Model not found</c>).
/// </summary>
public sealed class KiloConfigBuilderTests
{
    [Fact]
    public void BuildConfigJson_WritesProviderBlockAndModelsMap()
    {
        var json = KiloConfigBuilder.BuildConfigJson(
            "https://openrouter.ai/api/v1",
            "secret-key",
            ["openai-compatible/nvidia/nemotron-3.5-lightning:free"]);

        using var doc = JsonDocument.Parse(json);
        var provider = doc.RootElement.GetProperty("provider").GetProperty("openai-compatible");
        var options = provider.GetProperty("options");
        Assert.Equal("https://openrouter.ai/api/v1", options.GetProperty("baseURL").GetString());
        Assert.Equal("secret-key", options.GetProperty("apiKey").GetString());

        // The -m id resolves against the map entry WITHOUT the provider
        // qualifier (verified live: this map key served
        // -m openai-compatible/nvidia/nemotron-3.5-lightning:free).
        var models = provider.GetProperty("models");
        Assert.True(models.TryGetProperty("nvidia/nemotron-3.5-lightning:free", out _));
    }

    [Fact]
    public void BuildConfigJson_EmitsOneEntryPerDistinctModel()
    {
        var json = KiloConfigBuilder.BuildConfigJson(
            "https://proxy.internal/v1",
            "k",
            ["openai-compatible/a:free", "openai-compatible/a:free", "  ", null, "bare-model"]);

        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement.GetProperty("provider").GetProperty("openai-compatible").GetProperty("models");
        Assert.True(models.TryGetProperty("a:free", out _));
        Assert.True(models.TryGetProperty("bare-model", out _));
        Assert.Equal(2, models.EnumerateObject().Count());
    }

    [Fact]
    public void BuildConfigJson_RespectsConfiguredBaseUrl()
    {
        var json = KiloConfigBuilder.BuildConfigJson("https://proxy.internal/v1", "k", []);

        using var doc = JsonDocument.Parse(json);
        var options = doc.RootElement.GetProperty("provider").GetProperty("openai-compatible").GetProperty("options");
        Assert.Equal("https://proxy.internal/v1", options.GetProperty("baseURL").GetString());
    }

    [Fact]
    public void BuildConfigJson_BlankKey_ThrowsNamedCause()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            KiloConfigBuilder.BuildConfigJson("https://openrouter.ai/api/v1", "  ", []));

        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigJson_BlankBaseUrl_ThrowsNamedCause()
    {
        Assert.Throws<ArgumentException>(() =>
            KiloConfigBuilder.BuildConfigJson("  ", "k", []));
    }

    [Fact]
    public void StripProviderPrefix_RemovesOpenAiCompatibleQualifier()
    {
        Assert.Equal(
            "nvidia/nemotron-3.5-lightning:free",
            KiloConfigBuilder.StripProviderPrefix("openai-compatible/nvidia/nemotron-3.5-lightning:free"));
    }

    [Fact]
    public void StripProviderPrefix_LeavesBareIdsAlone()
    {
        Assert.Equal("bare-model", KiloConfigBuilder.StripProviderPrefix("bare-model"));
    }
}
