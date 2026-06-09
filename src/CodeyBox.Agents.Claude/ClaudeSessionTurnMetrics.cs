namespace CodeyBox.Agents.Claude;

/// <summary>
/// Per-turn observability snapshot for a Claude resumable session. Emitted by
/// <see cref="ClaudeSessionWorker"/> after each <c>SendTurnAsync</c> so operators
/// can measure the cache_read vs fresh-input ratio that is the whole point of
/// running a session across the work/audit/rework phases instead of one-shot.
///
/// <para>The fields are derived from the CLI's stream-json <c>result</c> usage
/// record (see <see cref="ClaudeCostExtractor"/>): <c>InputTokens</c> is the
/// total prompt-input bucket (fresh + cache_creation + cache_read) so the
/// operator can chart cache_read share of total turn-over-turn;
/// <c>CachedInputTokens</c> is the cache_read portion (the cheap one);
/// <c>FreshInputTokens</c> is the non-cached billable bucket
/// (fresh + cache_creation), which is what the operator wants to see drop
/// turn-over-turn as the session warms. The extractor's
/// <see cref="AgentCostSnapshot.InputTokens"/> already carries this billable
/// bucket, and <c>InputTokens</c> here is the sum of that with
/// <see cref="AgentCostSnapshot.CachedInputTokens"/>.</para>
///
/// <para><see cref="Transport"/> distinguishes the command-delivery + billing
/// channel that produced this turn (<c>"print"</c> vs <c>"acp"</c>), so
/// dashboards can confirm Claude work is actually being routed off the
/// <c>--print</c> metered pool when the operator has flipped
/// <see cref="ClaudeSessionWorkerOptions.Transport"/> to
/// <see cref="ClaudeSessionTransport.Acp"/>. Empty string means the
/// transport tag was not recorded (legacy callers).</para>
/// </summary>
/// <param name="CliSessionId">Runner-assigned session id used by the transport's continuation flag (Claude CLI id for print; ACP session id for acp).</param>
/// <param name="TurnIndex">Zero-based turn index within this session.</param>
/// <param name="InputTokens">Total prompt-input tokens reported by the CLI (fresh + cache_creation + cache_read).</param>
/// <param name="CachedInputTokens">Prompt-input tokens served from the provider cache (cache_read).</param>
/// <param name="FreshInputTokens">Non-cached billable prompt-input tokens (fresh + cache_creation).</param>
/// <param name="OutputTokens">Output tokens reported by the CLI.</param>
/// <param name="ModelId">Model id reported by the CLI assistant event, if any.</param>
/// <param name="UsedResume">True when this turn passed a continuation id (CLI <c>--resume</c> for print, ACP <c>session/load</c> for acp).</param>
/// <param name="Transport">Transport tag — <c>"print"</c>, <c>"acp"</c>, or empty for legacy callers.</param>
public sealed record ClaudeSessionTurnMetrics(
    string CliSessionId,
    int TurnIndex,
    int InputTokens,
    int CachedInputTokens,
    int FreshInputTokens,
    int OutputTokens,
    string? ModelId,
    bool UsedResume,
    string Transport = "");

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
