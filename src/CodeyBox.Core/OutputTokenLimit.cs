using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Shared contract for per-turn model output-token ceilings: the structured
/// stop-reason value providers report when a turn ends because the model hit
/// its maximum output tokens, the bounded same-session continuation policy,
/// and the distinct failure kind used when the continuation bound is
/// exhausted.
///
/// <para>Runners that surface a structured completion reason (Devin's ACP
/// <c>turn_complete.stopReason</c>, Claude's stream-json
/// <c>result.stop_reason</c>, Codex task/turn events, cmd's
/// <c>run_end.result.stopReason</c>) consult this contract instead of
/// keying on per-agent literals: <see cref="IsOutputLimitStopReason"/>
/// recognises the truncation signal and
/// <see cref="ProviderTransientRetryPolicy.BuildTruncationContinuePrompt"/>
/// supplies the continuation text, so the vocabulary stays in one place.
/// The structured JSON shapes below are additionally registered in
/// <see cref="ProviderTransientTruncationSignatures"/>, which makes the
/// orchestrator's truncation-continue path fire for every agent whose
/// captured stream carries them — including agents with no in-runner
/// session loop.</para>
/// </summary>
public static class OutputTokenLimit
{
    /// <summary>
    /// Structured stop-reason value a provider reports when the model hit
    /// its per-turn output-token ceiling mid-turn (Devin ACP
    /// <c>turn_complete.stopReason</c>, Claude stream-json
    /// <c>stop_reason</c>). Compared with
    /// <see cref="IsOutputLimitStopReason"/>, never by substring.
    /// </summary>
    public const string StopReasonValue = "max_tokens";

    /// <summary>
    /// Work-item failure kind recorded when a runner exhausts its bounded
    /// same-session continuations without completing the turn. Deliberately
    /// distinct from the generic no-changes outcome: the agent was cut off
    /// by a provider ceiling, not declining to work.
    /// </summary>
    public const string ExhaustedFailureKind = WorkItemFailureKinds.OutputLimitExhausted;

    /// <summary>
    /// Default bound for bounded same-session continuations after an
    /// output-limit stop. Operators override it per agent (e.g. Devin's
    /// <c>MaxTokenContinuations</c>); this is the value zero-config
    /// dispatches run with.
    /// </summary>
    public const int DefaultMaxContinuations = 8;

    /// <summary>
    /// Hard upper bound for a configured continuation bound. Each
    /// continuation can emit up to a full output ceiling of tokens, so the
    /// cap bounds spend and wall-clock time on a task that structurally
    /// cannot fit; operator values above it degrade to this instead of
    /// throwing on the dispatch path.
    /// </summary>
    public const int AbsoluteMaxContinuations = 32;

    /// <summary>
    /// True only for an exact (case-insensitive) <c>max_tokens</c> stop
    /// reason. Never a substring test: model prose mentioning token limits
    /// must not read as a provider truncation signal.
    /// </summary>
    public static bool IsOutputLimitStopReason(string? stopReason)
        => string.Equals(stopReason, StopReasonValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clamps an operator-configured continuation bound into
    /// [0, <see cref="AbsoluteMaxContinuations"/>]. Zero disables
    /// continuation (the first output-limit stop ends the turn); negative
    /// values degrade to zero instead of throwing on the dispatch path.
    /// </summary>
    public static int ClampContinuations(int configured)
        => Math.Clamp(configured, 0, AbsoluteMaxContinuations);

    /// <summary>
    /// Structured <c>"stop_reason": "max_tokens"</c> shape (Claude
    /// stream-json result lines, Codex/agy result events). Exact JSON
    /// key/value shape — model prose quoting the bare term never matches.
    /// Shared with <see cref="ProviderTransientTruncationSignatures"/> as
    /// its single source of truth.
    /// </summary>
    internal static readonly Regex StructuredSnakeCaseStopReason = new(
        "\"stop_reason\"\\s*:\\s*\"max_tokens\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        ProviderTransientMatcher.MatchTimeout);

    /// <summary>
    /// Structured <c>"stopReason": "max_tokens"</c> shape (Devin ACP
    /// <c>turn_complete</c> envelopes, cmd <c>run_end.result</c>). Exact
    /// JSON key/value shape — model prose quoting the bare term never
    /// matches. Shared with
    /// <see cref="ProviderTransientTruncationSignatures"/> as its single
    /// source of truth.
    /// </summary>
    internal static readonly Regex StructuredCamelCaseStopReason = new(
        "\"stopReason\"\\s*:\\s*\"max_tokens\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        ProviderTransientMatcher.MatchTimeout);

    /// <summary>
    /// True when <paramref name="text"/> carries a structured output-limit
    /// stop shape (see <see cref="StructuredSnakeCaseStopReason"/> and
    /// <see cref="StructuredCamelCaseStopReason"/>). The orchestrator's
    /// success-path gate for truncation continuations: a turn that
    /// completed with one of these shapes did not finish — it was cut off.
    /// Bounded like every transient scan (tail window + match timeout), and
    /// never throws.
    /// </summary>
    public static bool ContainsOutputLimitStopReason(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            return ProviderTransientMatcher.IsMatch(StructuredSnakeCaseStopReason, text)
                || ProviderTransientMatcher.IsMatch(StructuredCamelCaseStopReason, text);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
