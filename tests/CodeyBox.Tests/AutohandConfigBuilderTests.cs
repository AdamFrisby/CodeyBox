using System.Text.Json;
using CodeyBox.Agents.Autohand;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandConfigBuilder"/>. The guest config document
/// is load-bearing — the CLI reads its provider credential exclusively from
/// the file (environment variables never backfill it, verified live) — so
/// its shape is pinned here: provider block, key, model, telemetry off.
/// Fixture keys are inert placeholders, never real credentials.
/// </summary>
public sealed class AutohandConfigBuilderTests
{
    [Fact]
    public void GuestConfigRelativePath_IsDotAutohandConfigJson()
    {
        Assert.Equal(".autohand/config.json", AutohandConfigBuilder.GuestConfigRelativePath);
    }

    [Fact]
    public void BuildConfigJson_OpenRouter_WritesProviderBlockKeyModelAndTelemetryOff()
    {
        var json = AutohandConfigBuilder.BuildConfigJson(
            "openrouter",
            "nvidia/nemotron-3.5-lightning:free",
            "test-autohand-key-material");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("openrouter", root.GetProperty("provider").GetString());
        var provider = root.GetProperty("openrouter");
        Assert.Equal("test-autohand-key-material", provider.GetProperty("apiKey").GetString());
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", provider.GetProperty("model").GetString());
        Assert.Equal(AutohandConfigBuilder.OpenRouterBaseUrl, provider.GetProperty("baseUrl").GetString());
        Assert.False(root.GetProperty("telemetry").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void BuildConfigJson_NullModel_OmitsModelField()
    {
        var json = AutohandConfigBuilder.BuildConfigJson("openrouter", null, "test-autohand-key-material");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("openrouter").TryGetProperty("model", out _));
    }

    [Fact]
    public void BuildConfigJson_NonOpenRouterProvider_OmitsBaseUrl()
    {
        var json = AutohandConfigBuilder.BuildConfigJson("ollama", "llama3", "test-autohand-key-material");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ollama", root.GetProperty("provider").GetString());
        Assert.False(root.GetProperty("ollama").TryGetProperty("baseUrl", out _));
        Assert.Equal("llama3", root.GetProperty("ollama").GetProperty("model").GetString());
    }

    [Fact]
    public void BuildConfigJson_TrimsProviderAndModel()
    {
        var json = AutohandConfigBuilder.BuildConfigJson("  openrouter  ", "  model-id  ", "test-autohand-key-material");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("openrouter", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("model-id", doc.RootElement.GetProperty("openrouter").GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigJson_BlankApiKey_Throws(string? apiKey)
    {
        // An empty file key fails closed in the guest ("Setup cancelled"),
        // so the builder fails here with a named cause instead.
        Assert.Throws<ArgumentException>(() =>
            AutohandConfigBuilder.BuildConfigJson("openrouter", "model-id", apiKey!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigJson_BlankProvider_Throws(string? provider)
    {
        Assert.Throws<ArgumentException>(() =>
            AutohandConfigBuilder.BuildConfigJson(provider!, "model-id", "test-autohand-key-material"));
    }
}
