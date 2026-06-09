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
/// <remarks>
/// <para>ACP cache-warmth verification (see docs/agents.md "Verifying ACP cache
/// warmth"). The print transport keeps one long-lived <c>claude --print --resume</c>
/// per turn; the ACP transport currently tears down and respawns
/// <c>claude --ide</c> each turn, with continuity supplied by <c>session/load</c>.
/// Whether that <c>session/load</c> actually reattaches to a warm provider-side
/// cache is the question the daemon-bridge follow-up depends on. The split here
/// — <see cref="CachedInputTokens"/> (cache_read) vs
/// <see cref="CacheCreationInputTokens"/> (newly-paid prompt-write tokens) —
/// makes the answer directly readable from per-turn metrics across consecutive
/// turns of one session:</para>
/// <list type="bullet">
///   <item>cache_read dominates after turn 1 and cache_creation collapses to ~0
///   → warmth IS preserved → daemon bridge is purely a latency optimisation
///   (modest priority).</item>
///   <item>cache_creation stays high every turn → cache is being rebuilt each
///   turn → cost/quota regression — escalate.</item>
/// </list>
public sealed record ClaudeSessionTurnMetrics(
    string CliSessionId,
    int TurnIndex,
    int InputTokens,
    int CachedInputTokens,
    int FreshInputTokens,
    int OutputTokens,
    string? ModelId,
    bool UsedResume,
    string Transport = "")
{
    /// <summary>
    /// Prompt-input tokens charged at the (more expensive) cache-write rate this
    /// turn (Anthropic <c>cache_creation_input_tokens</c>). Lives separately
    /// from <see cref="FreshInputTokens"/> so an operator can distinguish "real"
    /// fresh user-typed input from cache-rebuild charges. Defaults to 0 for
    /// callers / fixtures that didn't supply it.
    /// </summary>
    public int CacheCreationInputTokens { get; init; }
}

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
