using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Devin cost extractor's "always null" contract: devin is a
/// subscription CLI whose print-mode output carries no machine-readable
/// per-token usage line, so no extraction is attempted. The plan windows are
/// surfaced by <see cref="DevinQuotaProbe"/> instead. These tests fail if a
/// speculative parser is ever added — that should be a deliberate decision,
/// not a silent one.
/// </summary>
public sealed class DevinCostExtractorTests
{
    private static readonly DevinCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, Extractor.Kind);

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Subscription billing: a per-million-token rate would be misleading.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void TryExtract_NullInputs_ReturnsNull()
        => Assert.Null(Extractor.TryExtract(null, null));

    [Fact]
    public void TryExtract_UsageLikeShapes_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract("""{"usage":{"prompt_tokens":1}}""", null));
        Assert.Null(Extractor.TryExtract(null, "1,234 input tokens"));
    }
}
