using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Cost extractor for Cursor CLI output.
///
/// <para>The Cursor CLI does not currently document a final-usage line shape
/// in its stdout. Returning <c>null</c> from <see cref="TryExtract"/> means
/// the cost calculator falls back to <c>usageTotal.elapsedMs</c> for
/// time-spent visibility (operator's stated preference). If/when Cursor adds
/// a usage line, parse it here.</para>
///
/// <para><see cref="DefaultPricing"/> is null because cost is unknown — the
/// model is paid via the operator's flat-rate Cursor subscription, so a
/// per-million-token rate would be misleading. Callers treat that as $0 and
/// the elapsed-time fallback gives the operator the only meaningful signal.</para>
/// </summary>
public sealed class CursorCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Cursor;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        => null;
}
