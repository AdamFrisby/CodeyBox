using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeCostExtractor"/>. Each executed step
/// closes with a <c>step_finish</c> frame carrying the server token vocabulary
/// <c>part.tokens:{input, output, cache:{read, write}}</c>; the extractor sums
/// the step frames. Recorded live frames cover the failure path
/// (<c>step_start</c> + <c>error</c>); the <c>step_finish</c> shape follows the
/// CLI's run-output layer (copied verbatim onto the step part) with field
/// names confirmed live via <c>stats --json</c> — the success path itself
/// could not be exercised without a funded provider credential.
/// </summary>
public sealed class DotNetOpencodeCostExtractorTests
{
    private static readonly DotNetOpencodeCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Provider-agnostic BYOK front: spend bills to the operator's own
        // provider accounts at that provider's list prices, and the CLI
        // reports no model id to key rates on. Any fallback rate would be
        // fabricated data.
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
    public void RecordedFailureRun_ReportsUnknown_NotZeroData()
    {
        // Recorded live 2026-09-16 (bogus Anthropic key → provider HTTP 401):
        // step frames carry no tokens, so the adapter reports unknown (null)
        // rather than a zero snapshot that looks like measured data.
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"part\":{\"id\":\"prt_0a74555ac001QmCuITT448cuhf\",\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"messageID\":\"msg_0a745489a001WHQtkGpO5IBS2h\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void RecordedNoModelRun_ReportsUnknown()
    {
        // Recorded live 2026-09-16 (no configured model):
        // provider.invalid-request carries no tokens → unknown, not zero.
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1789512652804,\"sessionID\":\"ses_0a7440db5001svgNvxOKaMl9i7\",\"error\":{\"type\":\"provider.invalid-request\",\"message\":\"No available model is present in the configured catalog.\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void StepFinish_SumsStepsAndMapsCacheRead()
    {
        const string stdout =
            "{\"type\":\"step_finish\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":1200,\"output\":340,\"reasoning\":50,\"cache\":{\"read\":5600,\"write\":10}}}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_2\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":300,\"output\":60,\"reasoning\":0,\"cache\":{\"read\":400,\"write\":0}}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1500, result!.InputTokens);
        Assert.Equal(400, result.OutputTokens);
        Assert.Equal(6000, result.CachedInputTokens);
        // No run frame echoes the dispatch model: unknown, not defaulted.
        Assert.Null(result.ModelId);
    }

    [Fact]
    public void StepFinish_WithoutTokensObject_Ignored()
    {
        const string stdout =
            "{\"type\":\"step_finish\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void StepFinish_ZeroTokens_Ignored()
    {
        const string stdout =
            "{\"type\":\"step_finish\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":0,\"output\":0,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void StandaloneHostingLogs_SkippedNonJsonLinesDoNotBreakScan()
    {
        // --standalone interleaves ASP.NET hosting logs with the event lines.
        const string stdout =
            "info: Microsoft.Hosting.Lifetime[14]\n" +
            "      Now listening on: http://127.0.0.1:37033\n" +
            "{\"type\":\"step_finish\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":10,\"output\":5,\"cache\":{\"read\":0,\"write\":0}}}}\n" +
            "not-json { broken\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(10, result!.InputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact]
    public void UsageOnWrongType_NotClaimedAsStepTokens()
    {
        // A text frame mentioning step_finish in prose must not mint tokens.
        const string stdout =
            "{\"type\":\"text\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"text\",\"text\":\"the step_finish handler retries\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }
}
