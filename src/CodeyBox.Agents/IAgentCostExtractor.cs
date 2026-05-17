using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Best-effort extractor for token counts from an agent CLI's captured stdout/stderr.
/// Implementations must never throw and must return null when the output does not
/// contain recognisable token counts (older CLI version, plain-text mode, etc.).
/// </summary>
public interface IAgentCostExtractor
{
    AgentKind Kind { get; }

    /// <summary>
    /// Attempts to extract token counts from captured CLI output.
    /// Returns null if the output doesn't contain token counts.
    /// Never throws.
    /// </summary>
    AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr);

    /// <summary>
    /// Conservative built-in fallback rate the provider library owns. Used by the
    /// cost calculator when no model-specific or agent-level rate is configured.
    /// Return <c>null</c> to opt out (callers treat the cost as $0 in that case).
    /// </summary>
    ModelRateConfig? DefaultPricing { get; }
}

/// <summary>
/// Token snapshot extracted from a single agent CLI invocation.
/// </summary>
public sealed record AgentCostSnapshot(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    string? ModelId);
