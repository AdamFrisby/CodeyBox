namespace CodeyBox.Agents.Claude;

/// <summary>
/// Selector for the underlying command-delivery + billing channel the
/// <see cref="ClaudeSessionWorker"/> uses for each turn. The SESSION layer
/// (one logical, cache-warm session continued across the work/rework cycle)
/// stays the same across both values; only the TRANSPORT — how the prompt and
/// completion travel between CodeyBox and Anthropic — varies.
///
/// <para>
/// Bound from the configuration key
/// <c>CodeyBox:ClaudeSession:Transport</c> (case-insensitive string,
/// hot-reloadable through <see cref="ClaudeSessionWorkerOptions"/>). The
/// default is <see cref="Print"/> — the existing
/// <c>claude --print --resume</c> path — so an operator upgrade is safe by
/// default; they flip to <see cref="Acp"/> when ready.
/// </para>
/// </summary>
public enum ClaudeSessionTransport
{
    /// <summary>
    /// The legacy / current transport. Each turn runs
    /// <c>claude --print --dangerously-skip-permissions [--resume &lt;id&gt;]</c>
    /// inside the sandbox. Continuity across turns comes from
    /// <c>--resume</c>; the prompt cache is server-side at Anthropic.
    /// Default.
    /// </summary>
    Print = 0,

    /// <summary>
    /// The Agent Client Protocol transport. CodeyBox stands up an IDE-shaped
    /// ACP endpoint (lockfile under <c>~/.claude/ide/&lt;port&gt;.lock</c>,
    /// WebSocket JSON-RPC 2.0), launches the in-sandbox <c>claude --ide</c>
    /// against it, and delivers each work/rework turn as an ACP
    /// <c>session/prompt</c> over a SINGLE continued ACP session per work item.
    /// The session is INTERACTIVE (no <c>-p</c>), so it does not hit the
    /// metered <c>--print</c> pool, and ACP enforces input-token caching.
    /// Permission requests are auto-granted and agent questions default and
    /// continue (the <c>&lt;codeybox-question&gt;</c> async convention).
    /// </summary>
    Acp = 1,
}
