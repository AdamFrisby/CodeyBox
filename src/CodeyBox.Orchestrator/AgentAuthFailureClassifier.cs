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
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        var patterns = PatternsFor(kind).ToArray();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            if (!string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return new AgentFailureClassification(
                    AgentFailureKind.AuthRequired,
                    Reason: "auth/login prompt pattern matched");
            }
        }

        if (ContainsTrustedStdoutLoginTranscript(stdout))
        {
            return new AgentFailureClassification(
                AgentFailureKind.AuthRequired,
                Reason: "auth/login prompt pattern matched");
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
