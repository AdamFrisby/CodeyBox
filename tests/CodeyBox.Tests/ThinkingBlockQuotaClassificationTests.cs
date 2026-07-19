using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression: Claude API 400 thinking-block errors must not classify as quota.
/// </summary>
public sealed class ThinkingBlockQuotaClassificationTests
{
    private readonly CompositeQuotaFailureClassifier _classifier = new(
    [
        new ClaudeQuotaFailureDetector(),
        new CodexQuotaFailureDetector(),
        new GeminiQuotaFailureDetector(),
    ]);

    private const string ClaudeThinkingBlockStreamJson =
        """
        {"type":"system","subtype":"init","session_id":"s1"}
        {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"messages: content: `thinking` or `redacted_thinking` blocks in the latest assistant message cannot be modified. These blocks must remain as they were in the original response.\"}}"}
        """;

    [Fact]
    public void Detect_ClaudeThinkingBlock400_IsNotQuota()
    {
        var detection = _classifier.Detect(AgentKind.Claude, stderr: null, stdout: ClaudeThinkingBlockStreamJson);

        Assert.Null(detection);
    }

    [Fact]
    public void Detect_ClaudeThinkingBlock400_WithStaleRateLimitTextInStream_IsNotQuota()
    {
        // Long multi-turn runs can carry earlier quota-shaped lines in the same
        // stdout buffer; the final 400 thinking-block result must not inherit
        // quota classification from stale context.
        var stdout = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial"}]}}
            {"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded earlier in session"}
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"messages: `thinking` blocks cannot be modified"}
            """;

        var detection = _classifier.Detect(AgentKind.Claude, stderr: null, stdout: stdout);

        Assert.Null(detection);
    }

    [Fact]
    public async Task RecordIfQuotaFailure_ThinkingBlock400_DoesNotPersistObservedFailure()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-thinking-quota-{Guid.NewGuid():N}.db");
        try
        {
            using var failures = new SqliteQuotaFailureStore(dbPath);
            var now = DateTimeOffset.UtcNow;

            await _classifier.RecordIfQuotaFailureAsync(
                failures,
                AgentKind.Claude,
                "claude-opus-4-8",
                summary: "agent exited 1",
                stderr: null,
                now,
                TimeSpan.FromMinutes(30),
                CancellationToken.None,
                stdout: ClaudeThinkingBlockStreamJson);

            Assert.False(await failures.HasRecentAsync(
                AgentKind.Claude,
                "claude-opus-4-8",
                TimeSpan.FromMinutes(10),
                now));
        }
        finally
        {
            TestTempArtifacts.DeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public void Detect_ClaudeRateLimit429StreamJson_StillQuota()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":429,"result":"Error: rate_limit_exceeded — please retry after 30m"}
            """;

        var detection = _classifier.Detect(AgentKind.Claude, stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }
}
