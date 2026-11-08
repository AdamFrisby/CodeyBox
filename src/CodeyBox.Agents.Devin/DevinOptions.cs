using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Hot-reloadable operator knobs for the <c>devin</c> runner, bound under
/// <c>CodeyBox:Devin</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="DevinOptionsAccessor"/> delegate (same
/// shape as <c>ClineOptionsAccessor</c>).
/// </summary>
public sealed class DevinOptions
{
    /// <summary>
    /// How many times the ACP shim re-prompts the SAME session with the
    /// truncation-continuation prompt after a <c>turn_complete</c> carrying
    /// stop reason <c>max_tokens</c> (see
    /// <see cref="OutputTokenLimit.StopReasonValue"/>) before giving up.
    /// Each continuation emits a <c>token_continuation</c> envelope into the
    /// agent stream; an exhausted bound ends the run with an
    /// <c>output_limit_exhausted</c> terminal envelope, which the pipeline
    /// classifies as the distinct output-limit-exhausted failure kind rather
    /// than a generic no-changes failure. Zero disables continuation (the
    /// first output-limit stop ends the turn). The runner clamps this
    /// through <see cref="OutputTokenLimit.ClampContinuations"/>, so
    /// out-of-range values degrade to the nearest bound instead of throwing
    /// on the dispatch path.
    /// </summary>
    public int MaxTokenContinuations { get; set; } = OutputTokenLimit.DefaultMaxContinuations;
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="DevinOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>ClineOptionsAccessor</c>).
/// </summary>
public delegate DevinOptions DevinOptionsAccessor();
