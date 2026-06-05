using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OpencodeCostExtractor"/>. opencode fronts multiple
/// upstream model providers (DeepSeek, Anthropic, OpenAI, …) and we have not
/// yet observed which canonical final-summary shape it emits, so the
/// extractor tries a deliberately generous set of shapes. Each shape is
/// pinned here so a future change cannot silently drop one without breaking
/// a test.
/// </summary>
public sealed class OpencodeCostExtractorTests
{
    private static readonly OpencodeCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // opencode fronts providers with very different per-token economics;
        // there is no sensible single fallback rate. Operators must configure
        // per-model pricing under CodeyBox:AgentPricing.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
        Assert.Null(Extractor.TryExtract("   ", null));
        Assert.Null(Extractor.TryExtract(null, "   "));
    }

    [Fact]
    public void Json_OpenAiShape_ParsesPromptAndCompletionTokens()
    {
        var stdout = """{"usage":{"prompt_tokens":1500,"completion_tokens":250}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1500, result!.InputTokens);
        Assert.Equal(250, result.OutputTokens);
        Assert.Equal(0, result.CachedInputTokens);
    }

    [Fact]
    public void Json_AnthropicShape_ParsesInputAndOutputTokens()
    {
        var stdout = """{"usage":{"input_tokens":2000,"output_tokens":300}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(2000, result!.InputTokens);
        Assert.Equal(300, result.OutputTokens);
    }

    [Fact]
    public void Json_AnthropicShape_ParsesCacheReadInputTokens()
    {
        var stdout = """{"usage":{"input_tokens":82750,"cache_read_input_tokens":82000,"output_tokens":290}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(82750, result!.InputTokens);
        Assert.Equal(82000, result.CachedInputTokens);
        Assert.Equal(290, result.OutputTokens);
    }

    [Fact]
    public void Json_AnthropicShape_CachedOnlyUsageStillRecordsSnapshot()
    {
        var stdout = """{"usage":{"input_tokens":0,"cache_read_input_tokens":900,"output_tokens":0}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(0, result!.InputTokens);
        Assert.Equal(900, result.CachedInputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    [Fact]
    public void Json_OpenAiShape_ParsesPromptTokensDetailsCachedTokens()
    {
        var stdout = """{"usage":{"prompt_tokens":82750,"completion_tokens":290,"prompt_tokens_details":{"cached_tokens":82000}}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(750, result!.InputTokens);
        Assert.Equal(82000, result.CachedInputTokens);
        Assert.Equal(290, result.OutputTokens);
    }

    [Fact]
    public void Json_MixedShape_PrefersPromptOverInputAndCompletionOverOutput()
    {
        // If both shapes appear together the OpenAI keys should win because
        // they are checked first; this pins the ordering deterministically.
        var stdout = """{"usage":{"prompt_tokens":1,"input_tokens":99,"completion_tokens":2,"output_tokens":98}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1, result!.InputTokens);
        Assert.Equal(2, result.OutputTokens);
    }

    [Fact]
    public void Json_WithModelField_RecordsModelId()
    {
        var stdout = """{"model":"deepseek/deepseek-coder","usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal("deepseek/deepseek-coder", result!.ModelId);
    }

    [Fact]
    public void Json_ModelFieldLongerThan128Chars_TruncatedToFirst128()
    {
        // Cap incoming model ids defensively so a runaway opencode emission
        // can't blow up downstream cost-snapshot storage (which has a
        // bounded VARCHAR for the model id).
        var longId = "deepseek/" + new string('x', 200);
        var stdout = $"{{\"model\":\"{longId}\",\"usage\":{{\"prompt_tokens\":1,\"completion_tokens\":1}}}}";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(128, result!.ModelId!.Length);
        Assert.Equal(longId[..128], result.ModelId);
    }

    [Fact]
    public void Json_ModelFieldNonString_DoesNotThrowAndLeavesModelIdNull()
    {
        // The catch in TryParseJson is JsonException-only; m.GetString() on a
        // non-string element throws InvalidOperationException, which used to
        // escape the extractor and crash cost reporting. Guard with a
        // ValueKind check so a number/object/null in the model field is
        // tolerated rather than fatal.
        var stdout = """{"model":42,"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1, result!.InputTokens);
        Assert.Equal(1, result.OutputTokens);
        Assert.Null(result.ModelId);
    }

    [Fact]
    public void Json_BothTokenCountsZero_ReturnsNull()
    {
        // A usage block that says "no tokens consumed" carries no useful
        // accounting signal; treat as not-found so we don't write a 0/0 row.
        var stdout = """{"usage":{"prompt_tokens":0,"completion_tokens":0}}""";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void Json_MultiLineNdjson_PicksUsageLineFromStream()
    {
        // opencode may emit several streaming events before the usage line;
        // the parser must scan each '{'-prefixed line, not just the first.
        var stdout = """
            {"type":"start"}
            {"type":"chunk","text":"hello"}
            {"usage":{"prompt_tokens":42,"completion_tokens":7}}
            """;

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(42, result!.InputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void Json_MalformedJson_ReturnsNull()
    {
        // Garbage with the word "usage" in it must not throw.
        var stdout = "Note: usage went over today. {{ broken json {{ ";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void Json_WithoutUsageKeyword_ReturnsNullFast()
    {
        // The TryParseJson fast-path short-circuits when "usage" is absent
        // from the text. Pin that behaviour so future refactors don't quietly
        // start parsing every line of every stdout blob.
        var stdout = """{"prompt_tokens":5,"completion_tokens":3}""";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void HumanReadable_PromptCompletionForm_ParsesBothTokenCounts()
    {
        var stdout = "Prompt tokens: 1,234  Completion tokens: 567";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1234, result!.InputTokens);
        Assert.Equal(567, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_InputOutputForm_ParsesBothTokenCounts()
    {
        var stdout = "Used 12,345 input tokens, 678 output tokens";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12345, result!.InputTokens);
        Assert.Equal(678, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_InputOutputForm_IsCaseInsensitive()
    {
        // InputPattern/OutputPattern carry RegexOptions.IgnoreCase so
        // emissions like "12 INPUT TOKENS" should still match.
        var stdout = "12 INPUT TOKENS, 7 OUTPUT TOKENS";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(12, result!.InputTokens);
        Assert.Equal(7, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_PromptCompletionForm_IsCaseInsensitive()
    {
        // UsagePromptPattern / UsageCompletionPattern also carry IgnoreCase
        // so uppercase final-summary emissions (e.g. "PROMPT TOKENS:" from
        // shouty CLIs) still parse. Pins the symmetry with the
        // Input/Output form above.
        var stdout = "PROMPT TOKENS: 11  COMPLETION TOKENS: 4";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(11, result!.InputTokens);
        Assert.Equal(4, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_OnlyPromptNoCompletion_ReturnsNull()
    {
        // Both halves of the human-readable form must match — otherwise we
        // would be silently recording a 0 output token count.
        var stdout = "Prompt tokens: 100";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void HumanReadable_OnlyInputNoOutput_ReturnsNull()
    {
        var stdout = "Used 100 input tokens";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void HumanReadable_CommasInLargeNumbers_StrippedDuringParse()
    {
        // 1,234,567 → 1234567 — commas are presentation, not part of the value.
        var stdout = "Used 1,234,567 input tokens, 89,012 output tokens";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1234567, result!.InputTokens);
        Assert.Equal(89012, result.OutputTokens);
    }

    [Fact]
    public void HumanReadable_BothZeroFromInputOutputForm_ReturnsNull()
    {
        // The InputPattern/OutputPattern path explicitly returns null when
        // both halves are 0 so we don't store an empty snapshot.
        var stdout = "0 input tokens, 0 output tokens";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void StderrFallback_UsedWhenStdoutHasNoTokens()
    {
        // CLIs often write the final-cost summary to stderr; the extractor
        // must fall through to stderr after stdout fails to match.
        var stderr = """{"usage":{"prompt_tokens":100,"completion_tokens":50}}""";

        var result = Extractor.TryExtract("some chatter", stderr);

        Assert.NotNull(result);
        Assert.Equal(100, result!.InputTokens);
        Assert.Equal(50, result.OutputTokens);
    }

    [Fact]
    public void StdoutPreferredOverStderr_WhenBothContainUsage()
    {
        var stdout = """{"usage":{"prompt_tokens":1,"completion_tokens":2}}""";
        var stderr = """{"usage":{"prompt_tokens":999,"completion_tokens":888}}""";

        var result = Extractor.TryExtract(stdout, stderr);

        Assert.NotNull(result);
        Assert.Equal(1, result!.InputTokens);
        Assert.Equal(2, result.OutputTokens);
    }

    [Fact]
    public void UnrelatedOutput_ReturnsNull()
    {
        // Smoke test: ordinary CLI chatter without any token-count fingerprint
        // must not produce a phantom snapshot.
        Assert.Null(Extractor.TryExtract("hello world\nDone.", null));
    }
}
