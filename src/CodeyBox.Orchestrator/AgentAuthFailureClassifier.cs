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
}

public sealed record AgentAuthFailureDetection(
    AgentFailureClassification Classification,
    bool MatchedStderr,
    bool MatchedStdout,
    bool MatchedTrustedStdoutTranscript,
    bool MatchedConfiguredStdoutPattern = false,
    bool MatchedDefaultStdoutPattern = false)
{
    public bool IsStdoutOnly => MatchedStdout && !MatchedStderr;
}

public sealed class AgentAuthFailureClassifier : IAgentAuthFailureClassifier
{
    public static readonly IReadOnlyList<AuthFailurePattern> DefaultPatterns =
        AgentFailureClassifier.AuthRequiredPatterns
            .Select(static p => new AuthFailurePattern(p))
            .ToArray();

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
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        var matchedStderr = AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr);
        var matchedStderrAuthError = AgentFailureClassifier.ContainsAuthErrorPattern(stderr);
        var matchedTrustedStdoutTranscript = AgentFailureClassifier.ContainsTrustedStdoutLoginTranscript(stdout);
        var matchedDefaultStdout = matchedTrustedStdoutTranscript;
        var matchedStdoutFragment = AgentFailureClassifier.ContainsAuthRequiredFragmentInStdout(stdout);
        var matchedConfiguredStdout = false;

        foreach (var pattern in AdditionalPatternsFor(kind))
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            if (pattern.MatchesStderr
                && !string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedStderr = true;
            }

            if (pattern.MatchesStdout
                && !string.IsNullOrEmpty(stdout)
                && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedConfiguredStdout = true;
            }
        }

        var matchedStdout = matchedDefaultStdout || matchedConfiguredStdout
            || matchedStderrAuthError && matchedStdoutFragment;
        if (matchedStderrAuthError && (matchedStdout || matchedStdoutFragment))
            matchedStderr = true;

        if (matchedStderr || matchedStdout)
        {
            var source = matchedStderr && matchedStdout
                ? "stderr/stdout"
                : matchedStderr ? "stderr" : "stdout";
            return new AgentAuthFailureDetection(
                new AgentFailureClassification(
                    AgentFailureKind.AuthRequired,
                    Reason: $"auth/login prompt pattern matched in {source}"),
                matchedStderr,
                matchedStdout,
                matchedTrustedStdoutTranscript,
                matchedConfiguredStdout,
                matchedDefaultStdout);
        }

        return null;
    }

    private IEnumerable<AuthFailurePattern> AdditionalPatternsFor(AgentKind kind)
    {
        if (_additionalPatternsByAgent.TryGetValue(kind.Value, out var exact))
        {
            foreach (var pattern in exact)
                yield return pattern;
        }
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

    public AgentAuthRequiredException(AgentKind agent, string phase, string message)
        : base(message)
    {
        Agent = agent;
        Phase = phase;
    }
}
