using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Cost extractor for Continue: always reports unknown (null).
///
/// <para><c>cn --print</c> emits only the model's final text — no token
/// counts ride the transport (verified against @continuedev/cli 1.5.47:
/// plain-text replies, an empty reply on a file-edit run, and the exit-0
/// <c>{"status":"error",…}</c> envelope all carry nothing machine-readable
/// about usage). Token usage exists only in the guest's
/// <c>~/.continue/sessions/*.json</c> history files, which this extractor
/// cannot reach (it sees stdout/stderr strings only) and must not chase by
/// recency — concurrent runs in one sandbox would cross-attribute. Returning
/// null records no cost row (unknown) rather than a zero snapshot that
/// looks like measured data — same posture as the autohand/vibe extractors.
/// No <see cref="DefaultPricing"/> is shipped either: Continue fronts
/// hundreds of models with unrelated per-token economics, so no single
/// fallback rate is honest. The shipped free-tier member bills $0 via the
/// explicit zero-rate bucket in <c>agent-pricing-defaults.json</c>;
/// operators fronting paid models add that model's OpenRouter list prices
/// there (or under <c>CodeyBox:AgentPricing</c>).</para>
/// </summary>
public sealed class ContinueCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Continue;

    public ModelRateConfig? DefaultPricing { get; } = null;

    /// <summary>
    /// Always returns null: Continue's headless transport carries no
    /// machine-readable usage frame (see the class doc). Never throws.
    /// </summary>
    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        _ = agentStdout;
        _ = agentStderr;
        return null;
    }
}
