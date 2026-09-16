using CodeyBox.Agents.Aider;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AiderCostExtractor"/> over recorded real aider 0.86.2
/// output: a successful one-shot run (header + <c>Tokens:</c> accounting +
/// <c>Applied edit</c>), a failure run, and an empty response. Aider prints its
/// own accounting only as the tokens line (plus an optional dollar
/// <c>Cost:</c> trailer when the model carries cost metadata); the extractor
/// parses tokens and the dispatch model and never fabricates a zero that looks
/// like data.
/// </summary>
public sealed class AiderCostExtractorTests
{
    private static readonly AiderCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsAider()
    {
        Assert.Equal(AgentKind.Aider, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Aider fronts many providers with unrelated per-token economics; no
        // single fallback rate is honest. Operators configure per-model rates
        // under CodeyBox:AgentPricing (or rely on the bundled aider bucket).
        // Null means the calculator reports $0 with a startup warning — an
        // explicit unknown, never a default that looks like measured spend.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        // Unknown, not zero: a missing accounting line must not read as a
        // zero-cost run.
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
        Assert.Null(Extractor.TryExtract("   ", null));
        Assert.Null(Extractor.TryExtract(null, "   "));
    }

    [Fact]
    public void RecordedSuccessRun_ParsesTokensAndDispatchModel()
    {
        // Trimmed live stdout from the aider 0.86.2 verification run
        // (openrouter/nvidia/nemotron-3.5-lightning:free, file edit applied).
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Git repo: .git with 1 files\n" +
            "Repo-map: using 4096 tokens, auto refresh\n" +
            "pong2.txt\n" +
            "```\n" +
            "PONG2\n" +
            "```\n" +
            "Tokens: 766 sent, 905 received.\n" +
            "\n" +
            "pong2.txt\n" +
            "Applied edit to pong2.txt\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(766, result!.InputTokens);
        Assert.Equal(0, result.CachedInputTokens);
        Assert.Equal(905, result.OutputTokens);
        Assert.Equal("openrouter/nvidia/nemotron-3.5-lightning:free", result.ModelId);
    }

    [Fact]
    public void RecordedFailureRun_ReturnsNull()
    {
        // Live stdout from the aider 0.86.2 auth probe (bogus OpenRouter key):
        // no Tokens line, so cost is unknown — never a fabricated zero.
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Git repo: none\n" +
            "Repo-map: disabled\n" +
            "\n" +
            "litellm.AuthenticationError: AuthenticationError: OpenrouterException - \n" +
            "{\"error\":{\"message\":\"Missing Authentication header\",\"code\":401}}\n" +
            "The API provider is not able to authenticate you. Check your API key.\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void EmptyResponse_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract("Aider v0.86.2\nModel: openrouter/x/y with whole edit format\n", null));
    }

    [Fact]
    public void CacheHitSegment_SplitOutOfSent()
    {
        // "sent" already includes cache hits; the calculator charges the
        // cached bucket separately, so hits are split out, not double counted.
        const string stdout =
            "Model: openrouter/anthropic/claude-haiku-4.5 with diff edit format\n" +
            "Tokens: 12k sent, 8.0k cache hit, 500 received.\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(4000, result!.InputTokens);
        Assert.Equal(8000, result.CachedInputTokens);
        Assert.Equal(500, result.OutputTokens);
    }

    [Fact]
    public void KiloSuffix_ParsesOneDecimalAndRounded()
    {
        const string stdout = "Tokens: 1.2k sent, 300 received.\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1200, result!.InputTokens);
        Assert.Equal(300, result.OutputTokens);
    }

    [Fact]
    public void CostTrailer_Ignored_ModelStillRecorded()
    {
        // The dollar Cost trailer is provider-billed spend, not a token count:
        // rates come from the pricing bucket, so it must not leak into tokens.
        const string stdout =
            "Model: openrouter/anthropic/claude-haiku-4.5 with diff edit format\n" +
            "Tokens: 1.2k sent, 300 cache hit, 500 received. Cost: $0.012 message, $0.012 session.\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(900, result!.InputTokens);
        Assert.Equal(300, result.CachedInputTokens);
        Assert.Equal(500, result.OutputTokens);
        Assert.Equal("openrouter/anthropic/claude-haiku-4.5", result.ModelId);
    }

    [Fact]
    public void ZeroTokensLine_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract("Tokens: 0 sent, 0 received.\n", null));
    }

    [Fact]
    public void TokensLineOnStderr_Parsed()
    {
        const string stderr = "Tokens: 591 sent, 564 received.\n";

        var result = Extractor.TryExtract("", stderr);

        Assert.NotNull(result);
        Assert.Equal(591, result!.InputTokens);
        Assert.Equal(564, result.OutputTokens);
    }

    [Fact]
    public void ModelOutputMentioningTokens_NotParsed()
    {
        // Repository content under review discussing token counts must not
        // fabricate a cost row: only aider's own accounting line qualifies.
        const string stdout =
            "The tokenizer produced 500 sent tokens and 300 received tokens.\n" +
            "Applied edit to notes.txt\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }
}
