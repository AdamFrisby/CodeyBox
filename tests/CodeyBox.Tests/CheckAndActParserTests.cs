using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CheckAndActPipeline.TryParseVerdict"/>: the parser
/// must be strict (no guessing past missing sentinels / missing fields) so a
/// malformed agent response produces a clean Failed transition rather than a
/// silently-wrong yes/no.
/// </summary>
public sealed class CheckAndActParserTests
{
    [Fact]
    public void BuildPrompt_IncludesQuestionAndSentinels()
    {
        var spec = new CheckAndActSpec
        {
            Question = "Is any user-facing SQL built via string concatenation?",
            OnYes = new OnYesActionSpec { Title = "Fix it", Prompt = "remediate" },
        };
        var prompt = CheckAndActPipeline.BuildPrompt(spec);
        Assert.Contains(spec.Question, prompt);
        Assert.Contains(CheckAndActPipeline.StartSentinel, prompt);
        Assert.Contains(CheckAndActPipeline.EndSentinel, prompt);
        Assert.Contains("READ-ONLY", prompt);
    }

    [Fact]
    public void BuildCompletionPromptBlocks_UsesStableCacheableSystemAndReviewPrefix()
    {
        var reviewContext = """
            Base branch: main

            ### File: src/Foo.cs

            var sql = $"select * from users where id = {id}";
            """;
        var first = CheckAndActPipeline.BuildCompletionPromptBlocks(new CheckAndActSpec
        {
            Question = "Is user input interpolated into SQL?",
            OnYes = new OnYesActionSpec { Title = "Fix SQL", Prompt = "Use parameters." },
        }, reviewContext);
        var second = CheckAndActPipeline.BuildCompletionPromptBlocks(new CheckAndActSpec
        {
            Question = "Does this diff introduce poorly named variables?",
            OnYes = new OnYesActionSpec { Title = "Rename", Prompt = "Improve names." },
        }, reviewContext);

        Assert.Equal(first.CacheablePrefix, second.CacheablePrefix);
        Assert.DoesNotContain("poorly named variables", first.CacheablePrefix);
        Assert.Contains("[1: fixed generic system prompt]", first.Render());
        Assert.Contains("[2: the code/diff under review]", first.Render());
        Assert.Contains("[3: the specific check question]", first.Render());
        Assert.Contains(reviewContext.Trim(), first.ReviewBlock);
        Assert.Contains("Is user input interpolated into SQL?", first.QuestionBlock);
        Assert.Contains(CheckAndActPipeline.StartSentinel, first.QuestionBlock);
        Assert.Contains(CheckAndActPipeline.EndSentinel, first.QuestionBlock);
        Assert.Contains("no-tools", first.SystemBlock);
    }

    [Fact]
    public void TryParse_HappyPath_ParsesAnswerEvidenceConfidence()
    {
        var stdout = $$"""
            Some preamble the agent emitted while exploring.

            {{CheckAndActPipeline.StartSentinel}}
            {"answer": true, "evidence": "src/Foo.cs L42 builds SQL with string interpolation", "confidence": "high"}
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.True(ok, error);
        Assert.NotNull(verdict);
        Assert.True(verdict!.Answer);
        Assert.Contains("src/Foo.cs", verdict.Evidence);
        Assert.Equal("high", verdict.Confidence);
    }

    [Fact]
    public void TryParse_NoAnswer_VerdictFalseParses()
    {
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            {"answer": false, "evidence": "no SQL string concatenation found across src/**/*.cs"}
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.True(ok, error);
        Assert.False(verdict!.Answer);
        Assert.Null(verdict.Confidence);
    }

    [Fact]
    public void TryParse_TrailingCodeFence_StillParses()
    {
        // Some CLIs wrap the JSON in a ```json … ``` fence inside the sentinels.
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            ```json
            {"answer": true, "evidence": "x"}
            ```
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.True(ok, error);
        Assert.True(verdict!.Answer);
    }

    [Fact]
    public void TryParse_LastVerdictBlockWins()
    {
        // If the agent emitted the sentinels twice (e.g. revised mid-run), the
        // LAST block is authoritative — the one closest to the agent's final
        // decision.
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            {"answer": false, "evidence": "initial guess"}
            {{CheckAndActPipeline.EndSentinel}}
            ... agent revised after additional grep ...
            {{CheckAndActPipeline.StartSentinel}}
            {"answer": true, "evidence": "found vulnerability in src/Bar.cs"}
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.True(ok, error);
        Assert.True(verdict!.Answer);
        Assert.Contains("Bar.cs", verdict.Evidence);
    }

    [Fact]
    public void TryParse_MissingStartSentinel_Fails()
    {
        var stdout = """{"answer": true, "evidence": "no sentinels"}""";
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("start sentinel", error);
    }

    [Fact]
    public void TryParse_MissingEndSentinel_Fails()
    {
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            {"answer": true, "evidence": "..."}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("end sentinel", error);
    }

    [Fact]
    public void TryParse_MissingAnswerField_Fails()
    {
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            {"evidence": "no answer field present"}
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("answer", error);
    }

    [Fact]
    public void TryParse_MissingEvidence_Fails()
    {
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            {"answer": true, "evidence": ""}
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("evidence", error);
    }

    [Fact]
    public void TryParse_UnparseableJson_Fails()
    {
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            this is not JSON at all
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("parse", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_LiteralNullJson_FailsCleanly()
    {
        // The JSON literal `null` between the sentinels deserialises to a
        // null payload object. The parser guards against this and surfaces a
        // descriptive error rather than NullReferenceException-ing on access.
        var stdout = $$"""
            {{CheckAndActPipeline.StartSentinel}}
            null
            {{CheckAndActPipeline.EndSentinel}}
            """;
        var ok = CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var error);
        Assert.False(ok);
        Assert.Null(verdict);
        Assert.Contains("null", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_NullOrEmptyStdout_Fails()
    {
        Assert.False(CheckAndActPipeline.TryParseVerdict(null, out _, out var errNull));
        Assert.Contains("no stdout", errNull);
        Assert.False(CheckAndActPipeline.TryParseVerdict("", out _, out var errEmpty));
        Assert.Contains("no stdout", errEmpty);
    }
}
