using CodeyBox.Agents.Cline;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineQuotaFailureDetector"/>. Quota/auth fixtures
/// are recorded real output (cline 3.0.62 with a <c>$0</c>-spend-limit
/// OpenRouter key); secret-derived workspace key ids are redacted. The key
/// scoping assertion: cline-specific phrases in a completed run's assistant
/// prose must NOT detect — only failure signal (error frames/stderr) counts.
/// </summary>
public sealed class ClineQuotaFailureDetectorTests
{
    private static ClineQuotaFailureDetector Detector() => new();

    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, Detector().Kind);
    }

    [Fact]
    public void Detect_RecordedBadKeyAuthFailure_ReturnsUnauthorized()
    {
        // Recorded real output (cline 3.0.62, bad OpenRouter key in clean
        // state, exit 1): User not found.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:41.599Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"User not found.\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:01:41.673Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":407,\"text\":\"User not found.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";
        const string stderr =
            "{\"ts\":\"2026-09-16T18:01:41.673Z\",\"type\":\"error\",\"message\":\"User not found.\"}";

        var detection = Detector().Detect(stderr, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_RecordedSpendLimitFailure_ReturnsLimitReached()
    {
        // Recorded real output (cline 3.0.62, $0 key against a paid model,
        // exit 1). Redacted: the workspace key id in the recorded message is
        // secret-derived.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:51.679Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:01:51.706Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":152,\"text\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\",\"model\":{\"id\":\"anthropic/claude-sonnet-4\",\"provider\":\"openrouter\"}}";

        var detection = Detector().Detect(stderr: string.Empty, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_RecordedDefaultProviderMiss_ReturnsUnauthorized()
    {
        // Recorded real output (cline 3.0.62, no -P flag: the default cline
        // vendor provider ignores OPENROUTER_API_KEY and fails vendor auth,
        // exit 1) — the confusing first failure when the runner forgets the
        // provider flag.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:26.667Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"Unauthorized: Please make sure you're using the latest version of Cline and re-authenticate your Cline account.\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:02:26.681Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":297,\"text\":\"Unauthorized: Please make sure you're using the latest version of Cline and re-authenticate your Cline account.\",\"model\":{\"id\":\"~deepseek/deepseek-flash-latest\",\"provider\":\"cline\"}}";

        var detection = Detector().Detect(stderr: string.Empty, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_CompletedRunProseAboutAuthPhrases_DoesNotDetect()
    {
        // A completed run whose assistant text discusses "user not found"
        // handling must not park the agent on a false auth signal: the
        // phrase appears only in completed-run prose, never in failure
        // signal.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:09.659Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"done\",\"reason\":\"completed\",\"text\":\"I added handling for the User not found case in the login form.\",\"iterations\":1,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"totalCost\":0}}}\n" +
            "{\"ts\":\"2026-09-16T18:02:09.691Z\",\"type\":\"run_result\",\"finishReason\":\"completed\",\"iterations\":1,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":1000,\"text\":\"I added handling for the User not found case in the login form.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";

        Assert.Null(Detector().Detect(stderr: string.Empty, stdout));
    }

    [Fact]
    public void Detect_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:09.691Z\",\"type\":\"run_result\",\"finishReason\":\"completed\",\"iterations\":1,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":1000,\"text\":\"The task is complete.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";

        Assert.Null(Detector().Detect(stderr: string.Empty, stdout));
        Assert.Null(Detector().Detect(null, null));
        Assert.Null(Detector().Detect(string.Empty, string.Empty));
    }

    [Fact]
    public void Detect_OperatorPatterns_AppendedAfterDefaults()
    {
        var detector = new ClineQuotaFailureDetector(
            [new QuotaFailurePattern("custom spend ceiling hit", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect(
            stderr: "custom spend ceiling hit for this key",
            stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_NeverThrows_OnHostileInput()
    {
        Assert.Null(Detector().Detect("{not json", "{also not json"));
        // An empty operator pattern is filtered from the combined list: the
        // detector behaves exactly like the defaults-only construction.
        var withEmpty = new ClineQuotaFailureDetector([new QuotaFailurePattern("", QuotaFailureKind.Unauthorized)]);
        Assert.Equal(
            Detector().Detect("User not found.", null),
            withEmpty.Detect("User not found.", null));
        Assert.Null(withEmpty.Detect("all clear", null));
    }
}
