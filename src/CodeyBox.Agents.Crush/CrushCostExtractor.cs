using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Cost extractor for Crush: always reports unknown (null).
///
/// <para><c>crush run</c> emits only the model's final text — no token
/// counts ride the transport (verified against @charmland/crush 0.95.0:
/// plain-text replies, an empty reply, and the exit-1 styled <c>ERROR</c>
/// failure blocks all carry nothing machine-readable about usage).
/// <c>crush stats</c> renders an HTML usage report (<c>.crush/stats/</c>)
/// with no machine-readable balance, and per-session token usage lives only
/// in the guest's <c>~/.local/share/crush/crush.json</c> state file, which
/// this extractor cannot reach (it sees stdout/stderr strings only) and
/// must not chase by recency — concurrent runs in one sandbox would
/// cross-attribute. Returning null records no cost row (unknown) rather
/// than a zero snapshot that looks like measured data — same posture as the
/// continue/autohand/vibe extractors. No <see cref="DefaultPricing"/> is
/// shipped either: Crush fronts dozens of providers with unrelated
/// per-token economics, so no single fallback rate is honest. The shipped
/// free-tier member bills $0 via the explicit zero-rate bucket in
/// <c>agent-pricing-defaults.json</c>; operators fronting paid models add
/// that model's OpenRouter list prices there (or under
/// <c>CodeyBox:AgentPricing</c>).</para>
/// </summary>
public sealed class CrushCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Crush;

    public ModelRateConfig? DefaultPricing { get; } = null;

    /// <summary>
    /// Always returns null: Crush's headless transport carries no
    /// machine-readable usage frame (see the class doc). Never throws.
    /// </summary>
    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        _ = agentStdout;
        _ = agentStderr;
        return null;
    }
}
