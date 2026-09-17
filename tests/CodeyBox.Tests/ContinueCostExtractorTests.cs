using CodeyBox.Agents.Continue;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueCostExtractor"/>: the unknown-not-zero
/// contract. Continue's headless transport (<c>cn --print</c>) carries no
/// machine-readable usage frame — plain-text replies, empty replies, and
/// the exit-0 error envelope alike — so the extractor returns null (no cost
/// row) rather than a zero snapshot that looks like measured data.
/// <c>DefaultPricing</c> stays null because no single fallback rate is
/// honest across the hundreds of models Continue fronts; the shipped
/// free-tier member bills $0 via the explicit zero-rate pricing bucket.
/// </summary>
public sealed class ContinueCostExtractorTests
{
    [Fact]
    public void Kind_IsContinue()
    {
        Assert.Equal(AgentKind.Continue, new ContinueCostExtractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        Assert.Null(new ContinueCostExtractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_PlainTextReply_ReturnsNull()
    {
        // Recorded real output (@continuedev/cli 1.5.47): free text with no
        // usage signal must not fabricate a snapshot.
        Assert.Null(new ContinueCostExtractor().TryExtract("HELLO-CN-OK\n", string.Empty));
    }

    [Fact]
    public void TryExtract_EmptyReply_ReturnsNull()
    {
        // Recorded real behaviour: a file-edit run replied with empty stdout
        // (exit 0, change merged). Empty output is unknown cost, not zero.
        Assert.Null(new ContinueCostExtractor().TryExtract(string.Empty, string.Empty));
    }

    [Fact]
    public void TryExtract_ErrorEnvelope_ReturnsNull()
    {
        // Recorded real shape ($0-spend-limit key against a paid model):
        // the envelope names the quota failure but carries no token counts.
        const string envelope =
            "{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058\"}";
        Assert.Null(new ContinueCostExtractor().TryExtract(envelope, string.Empty));
    }

    [Fact]
    public void TryExtract_NullStreams_ReturnsNull()
    {
        Assert.Null(new ContinueCostExtractor().TryExtract(null, null));
    }
}
