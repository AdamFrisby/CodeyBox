using System.Threading;
using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// R8-resilience: when a multipass suspend/start cycle tears down in-flight TCP
/// connections, agent CLIs often surface transient network errors on exit.
/// A single re-invocation at the CodeyBox shim layer is reserved for exit-code
/// only suspend fallout. Recognised transient network failures are handled by
/// the orchestrator's durable backoff+jitter scheduler.
/// </summary>
public static class AgentSuspendResilience
{
    private static int _maxRetries = 1;
    /// <summary>Maximum automatic re-invocations after a failed agent exec. Hot-reloadable via <see cref="SetMaxRetries"/>.</summary>
    public static int MaxRetries => Volatile.Read(ref _maxRetries);

    public static void SetMaxRetries(int value)
    {
        if (value < 0) value = 0;
        Volatile.Write(ref _maxRetries, value);
    }

    /// <summary>
    /// Exit codes commonly observed when an HTTP client gives up after a long
    /// frozen TCP connection post-resume (curl 52/56, generic 1, npm-style 92)
    /// or when the sandbox kills an agent process mid-run (SIGKILL/OOM 137).
    /// </summary>
    private static readonly HashSet<int> SuspendRelatedExitCodes = [1, 52, 56, 92, 137];

    /// <summary>
    /// Returns true when <paramref name="exitCode"/> matches the set of exit
    /// shapes the suspend-resilience retry treats as transient. Exposed so
    /// the CLI-native session-resume path can apply the same allowlist (resume
    /// is recovery for the same family of transient blips), keeping the two
    /// recovery policies aligned.
    /// </summary>
    public static bool IsSuspendRelatedExitCode(int exitCode) =>
        SuspendRelatedExitCodes.Contains(exitCode);

    /// <summary>
    /// All built-in agent CLIs routed through <see cref="CliAgentRunnerBase"/>.
    /// </summary>
    private static readonly HashSet<string> SupportedAgents = new(StringComparer.Ordinal)
    {
        "claude", "codex", "gemini", "cursor", "opencode",
    };

    /// <summary>
    /// Returns true when <paramref name="agent"/> should receive one automatic
    /// re-invocation for an unclassified suspend-related exit shape.
    /// </summary>
    public static bool ShouldRetry(AgentKind agent, AgentFailureClassification classification, int exitCode)
    {
        if (!SupportedAgents.Contains(agent.Value))
            return false;

        if (classification.Kind == AgentFailureKind.Unknown && SuspendRelatedExitCodes.Contains(exitCode))
            return true;

        return false;
    }
}
