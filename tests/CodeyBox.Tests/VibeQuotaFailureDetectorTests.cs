using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="VibeQuotaFailureDetector"/> over recorded real vibe
/// 2.25.4 failure shapes: the missing-key startup error and the OpenRouter
/// 403 quota rejection.
/// </summary>
public sealed class VibeQuotaFailureDetectorTests
{
    // Recorded real shape: no provider key in the environment (exit 1).
    private const string MissingKeyStderr =
        "Error: Missing OPENROUTER_API_KEY environment variable for openrouter provider. " +
        "Set the environment variable (e.g. in ~/.vibe/.env or your shell), " +
        "or run `vibe --setup` once interactively.";

    // Recorded real shape: $0-limit OpenRouter key against a paid model
    // (exit 1; stdout carries only the user-echo history entry).
    private const string QuotaStdout =
        """{"id":"0447d122-1175-477c-96c9-6145edfed5e3","sessionId":"fe267585-22f6-653b-95c5-1ba5c441fdf8","turnId":"7e381af2-b392-46ee-a18b-130d7d0d1718","createdAt":1789580940416,"updatedAt":1789580940416,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"Say OK."}],"source":"turn_start","userDisplayContent":null}""";

    private const string QuotaStderr =
        "Error: API error from openrouter (model: anthropic/claude-haiku-4.5): LLM backend error [openrouter]\n" +
        "  status: 403 Forbidden\n" +
        "  reason: Forbidden\n" +
        "  request_id: N/A\n" +
        "  endpoint: https://openrouter.ai/api/v1/chat/completions\n" +
        "  model: anthropic/claude-haiku-4.5\n" +
        "  provider_message: Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058";

    [Fact]
    public void Kind_IsVibe()
    {
        Assert.Equal(AgentKind.Vibe, new VibeQuotaFailureDetector().Kind);
    }

    [Fact]
    public void Detect_MissingKey_StderrOnly_Unauthorized()
    {
        var detection = new VibeQuotaFailureDetector().Detect(MissingKeyStderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_KeyLimitExceeded_BothStreams_LimitReached()
    {
        // The failure text lives on stderr; stdout holds only the user echo.
        // The detector must scan both: neither stream alone tells the story.
        var fromStderr = new VibeQuotaFailureDetector().Detect(QuotaStderr, QuotaStdout);

        Assert.NotNull(fromStderr);
        Assert.Equal(QuotaFailureKind.LimitReached, fromStderr.Kind);
    }

    [Fact]
    public void Detect_StdoutOnly_RejectedText_StillMatches()
    {
        // Defensive: a harness that merges streams onto stdout must still
        // classify.
        var detection = new VibeQuotaFailureDetector().Detect(stderr: null, stdout: QuotaStderr);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Theory]
    [InlineData("authentication_error", QuotaFailureKind.Unauthorized)]
    [InlineData("API Error: 401", QuotaFailureKind.Unauthorized)]
    [InlineData("insufficient credits", QuotaFailureKind.LimitReached)]
    [InlineData("quota exceeded", QuotaFailureKind.LimitReached)]
    public void Detect_ProviderRelayedShapes_Classified(string text, QuotaFailureKind expected)
    {
        var detection = new VibeQuotaFailureDetector().Detect(text, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(expected, detection.Kind);
    }

    [Fact]
    public void Detect_HealthyStream_NoMatch()
    {
        // Recorded real healthy run: history entries only. Model output
        // discussing quota-adjacent words without an exhaustion verb must not
        // gate dispatch.
        const string healthy =
            """{"id":"265ca6b7-305c-40f6-a8ca-46cca31fd819","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580847231,"updatedAt":1789580847320,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"assistant","content":[{"type":"text","text":"8"}],"source":null,"userDisplayContent":null}""";

        Assert.Null(new VibeQuotaFailureDetector().Detect(stderr: null, stdout: healthy));
        Assert.Null(new VibeQuotaFailureDetector().Detect(null, null));
        Assert.Null(new VibeQuotaFailureDetector().Detect(string.Empty, string.Empty));
    }

    [Fact]
    public void Detect_OperatorPatterns_AppendedAfterDefaults()
    {
        var extras = new[] { new QuotaFailurePattern("vibe bespoke outage phrase", QuotaFailureKind.RateLimitExceeded) };

        var detection = new VibeQuotaFailureDetector(extras).Detect("vibe bespoke outage phrase", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
        // Defaults still apply alongside operator patterns.
        Assert.NotNull(new VibeQuotaFailureDetector(extras).Detect(MissingKeyStderr, stdout: null));
    }
}
