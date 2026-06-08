namespace CodeyBox.Agents.Claude;

/// <summary>
/// Per-turn observability snapshot for a Claude resumable session. Emitted by
/// <see cref="ClaudeSessionWorker"/> after each <c>SendTurnAsync</c> so operators
/// can measure the cache_read vs fresh-input ratio that is the whole point of
/// running a session across the work/audit/rework phases instead of one-shot.
///
/// <para>The fields are derived from the CLI's stream-json <c>result</c> usage
/// record (see <see cref="ClaudeCostExtractor"/>): <c>InputTokens</c> is the
/// total prompt-input bucket (fresh + cache_creation + cache_read);
/// <c>CachedInputTokens</c> is the cache_read portion (the cheap one);
/// <c>FreshInputTokens</c> is the derived non-cached remainder, which is what
/// the operator wants to see drop turn-over-turn as the session warms.</para>
/// </summary>
/// <param name="CliSessionId">Claude CLI session id used by <c>--resume</c>.</param>
/// <param name="TurnIndex">Zero-based turn index within this session.</param>
/// <param name="InputTokens">Total prompt-input tokens reported by the CLI.</param>
/// <param name="CachedInputTokens">Prompt-input tokens served from the provider cache (cheap).</param>
/// <param name="FreshInputTokens">Prompt-input tokens that were not cached (billed at fresh-input rates).</param>
/// <param name="OutputTokens">Output tokens reported by the CLI.</param>
/// <param name="ModelId">Model id reported by the CLI assistant event, if any.</param>
/// <param name="UsedResume">True when this turn invoked <c>claude --resume</c> rather than a fresh session start.</param>
public sealed record ClaudeSessionTurnMetrics(
    string CliSessionId,
    int TurnIndex,
    int InputTokens,
    int CachedInputTokens,
    int FreshInputTokens,
    int OutputTokens,
    string? ModelId,
    bool UsedResume);

/// <summary>
/// Receives <see cref="ClaudeSessionTurnMetrics"/> snapshots so the host can
/// log / persist / chart the cache_read share. <see cref="ClaudeSessionWorker"/>
/// invokes <see cref="Record"/> at most once per turn; implementations must
/// not throw — sink failure must never break a turn.
/// </summary>
public interface IClaudeSessionMetricsSink
{
    void Record(ClaudeSessionTurnMetrics metrics);
}

/// <summary>
/// Null-object sink. Used when no observability hook is wired so the worker
/// can call the sink unconditionally.
/// </summary>
public sealed class NullClaudeSessionMetricsSink : IClaudeSessionMetricsSink
{
    public static readonly NullClaudeSessionMetricsSink Instance = new();
    private NullClaudeSessionMetricsSink() { }
    public void Record(ClaudeSessionTurnMetrics metrics) { }
}
