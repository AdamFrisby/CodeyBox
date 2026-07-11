using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Detects agent output that indicates the CLI is blocked on an interactive
/// authentication/login flow. Unlike quota detectors, this is intentionally
/// generic across agents because the failure mode is operationally identical:
/// the sandboxed CLI cannot work until an operator re-authorizes it.
/// </summary>
public interface IAgentAuthFailureClassifier
{
    /// <summary>
    /// Returns an <see cref="AgentFailureKind.AuthRequired"/> classification
    /// when stderr or stdout contains a configured/default login-prompt
    /// signature.
    /// </summary>
    AgentFailureClassification? Detect(AgentKind kind, string? stderr, string? stdout);

    /// <summary>
    /// Returns the auth-required classification plus the stream that supplied
    /// the evidence. Stderr is treated as CLI diagnostics and matched by
    /// substring. Stdout defaults use the shared guarded stdout matcher so
    /// concrete CLI login transcripts count even when they are printed without a
    /// matching stderr line. Operator-supplied patterns are stream-scoped and
    /// default to stderr-only; stdout patterns must be opted into explicitly.
    /// </summary>
    AgentAuthFailureDetection? DetectDetailed(AgentKind kind, string? stderr, string? stdout);

    /// <summary>
    /// Classifies a runner result using the shared classifier plus configured
    /// per-agent auth/login-prompt patterns.
    /// </summary>
    AgentFailureClassification ClassifyFailure(AgentKind kind, AgentResult result);

    /// <summary>
    /// Classifies a runner result using configured auth/login-prompt patterns
    /// first, then falling back to the runner's own classifier.
    /// </summary>
    AgentFailureClassification ClassifyFailure(IAgentRunner runner, AgentResult result);

    /// <summary>
    /// Loose stdout fragment check: returns true when any line of stdout looks
    /// like a CLI login prompt OR any operator-configured stdout pattern for
    /// this agent matches. Used by the LLM-audit-execution-failure fallback,
    /// where the structured classifier's trusted-transcript guard would
    /// otherwise miss an auth prompt buried in audit-tool diagnostics.
    /// </summary>
    bool ContainsAuthRequiredFragmentInStdout(AgentKind kind, string? stdout);
}

public sealed class AgentAuthFailureClassifier : IAgentAuthFailureClassifier
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>> _additionalPatternsByAgent;

    public AgentAuthFailureClassifier()
        : this(additionalPatternsByAgent: null)
    {
    }

    public AgentAuthFailureClassifier(
        IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>>? additionalPatternsByAgent)
    {
        _additionalPatternsByAgent = additionalPatternsByAgent
            ?? new Dictionary<string, IReadOnlyList<AuthFailurePattern>>(StringComparer.OrdinalIgnoreCase);
    }

    public AgentFailureClassification? Detect(AgentKind kind, string? stderr, string? stdout)
        => DetectDetailed(kind, stderr, stdout)?.Classification;

    public AgentAuthFailureDetection? DetectDetailed(AgentKind kind, string? stderr, string? stdout)
        => AgentFailureClassifier.DetectAuthRequired(kind, stderr, stdout, _additionalPatternsByAgent);

    public AgentFailureClassification ClassifyFailure(AgentKind kind, AgentResult result)
        => AgentFailureClassifier.Classify(
            kind,
            result.Stderr,
            result.Stdout,
            result.Summary,
            _additionalPatternsByAgent);

    public AgentFailureClassification ClassifyFailure(IAgentRunner runner, AgentResult result)
    {
        var authAwareClassification = ClassifyFailure(runner.Kind, result);
        if (authAwareClassification.Kind == AgentFailureKind.AuthRequired)
        {
            return authAwareClassification;
        }

        return runner.ClassifyFailure(result);
    }

    public bool ContainsAuthRequiredFragmentInStdout(AgentKind kind, string? stdout)
    {
        if (AgentFailureClassifier.ContainsAuthRequiredFragmentInStdout(stdout))
            return true;

        return ContainsConfiguredPattern(kind, stdout, static pattern => pattern.MatchesStdout);
    }

    private bool ContainsConfiguredPattern(
        AgentKind kind,
        string? text,
        Func<AuthFailurePattern, bool> matchesStream)
    {
        if (string.IsNullOrEmpty(text)
            || string.IsNullOrWhiteSpace(kind.Value)
            || !_additionalPatternsByAgent.TryGetValue(kind.Value, out var patterns))
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (!matchesStream(pattern) || string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;
            if (text.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Raised when a real agent invocation emitted an interactive login prompt.
/// The pipeline catches this as infrastructure/auth failure instead of letting
/// exit-0/no-diff output masquerade as a normal no-change result. Global
/// benching is applied when the evidence is authoritative for the phase.
/// </summary>
public sealed class AgentAuthRequiredException : Exception
{
    public AgentKind Agent { get; }
    public string Phase { get; }
    public WorkItemAuthFailureScope Scope { get; }

    public AgentAuthRequiredException(
        AgentKind agent,
        string phase,
        string message,
        WorkItemAuthFailureScope scope = WorkItemAuthFailureScope.Fleet)
        : base(message)
    {
        Agent = agent;
        Phase = phase;
        Scope = scope;
    }
}
