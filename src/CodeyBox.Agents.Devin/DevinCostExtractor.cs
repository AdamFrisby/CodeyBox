using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Cost extractor for devin CLI output.
///
/// <para>The devin CLI's print mode writes the assistant's answer as plain
/// text with no token/usage summary line (verified against devin 3000.11.1).
/// Returning <c>null</c> from <see cref="TryExtract"/> means the pipeline
/// records a zero-token, zero-cost row whose timestamps still feed
/// <c>usageTotal.elapsedMs</c> for time-spent visibility. If/when the CLI
/// exposes a usage line, parse it here.</para>
///
/// <para><see cref="DefaultPricing"/> is null because cost is unknown —
/// consumption is billed as ACUs on the operator's Devin subscription, so a
/// per-million-token rate would be misleading. Callers treat that as $0 and
/// the elapsed-time fallback gives the operator the only meaningful
/// signal.</para>
/// </summary>
public sealed class DevinCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Devin;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        => null;
}
