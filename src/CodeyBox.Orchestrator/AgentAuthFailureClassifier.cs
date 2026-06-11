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
    /// when stderr/stdout contains a configured login-prompt signature.
    /// </summary>
    AgentFailureClassification? Detect(AgentKind kind, string? stderr, string? stdout);

    /// <summary>
    /// Returns the auth-required classification plus the stream that supplied
    /// the evidence. Runtime breaker policy uses this to avoid mutating global
    /// availability from stdout-only, model-controlled text unless a separate
    /// non-model-controlled check corroborates it.
    /// </summary>
    AgentAuthFailureDetection? DetectDetailed(AgentKind kind, string? stderr, string? stdout);
}

public sealed record AgentAuthFailureDetection(
    AgentFailureClassification Classification,
    bool MatchedStderr,
    bool MatchedStdout,
    bool MatchedTrustedStdoutTranscript)
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

        var patterns = PatternsFor(kind).ToArray();
        var matchedStderr = false;
        var matchedStdout = false;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            if (!string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedStderr = true;
            }

            if (!string.IsNullOrEmpty(stdout)
                && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedStdout = true;
            }
        }

        var matchedTrustedStdoutTranscript = ContainsTrustedStdoutLoginTranscript(stdout);
        matchedStdout |= matchedTrustedStdoutTranscript;

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
                matchedTrustedStdoutTranscript);
        }

        return null;
    }

    private IEnumerable<AuthFailurePattern> PatternsFor(AgentKind kind)
    {
        foreach (var pattern in DefaultPatterns)
            yield return pattern;

        if (_additionalPatternsByAgent.TryGetValue(kind.Value, out var exact))
        {
            foreach (var pattern in exact)
                yield return pattern;
        }
    }

    private static bool ContainsTrustedStdoutLoginTranscript(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        var hasLoginPrompt =
            stdout.Contains("Authentication required. Please visit", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("Please visit the URL to log in", StringComparison.OrdinalIgnoreCase);
        var hasWaitOrTimeout =
            stdout.Contains("Waiting for authentication (timeout", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("authentication timed out", StringComparison.OrdinalIgnoreCase);
        return hasLoginPrompt && hasWaitOrTimeout;
    }
}

/// <summary>
/// Raised when a real agent invocation emitted an interactive login prompt.
/// The pipeline catches this as infrastructure/auth failure after benching the
/// agent, instead of letting exit-0/no-diff output masquerade as a normal
/// no-change result.
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
