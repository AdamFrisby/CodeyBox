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

        foreach (var pattern in PatternsFor(kind))
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            var inStderr = !string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout)
                && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase);
            if (inStderr || inStdout)
            {
                return new AgentFailureClassification(
                    AgentFailureKind.AuthRequired,
                    Reason: "auth/login prompt pattern matched");
            }
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
