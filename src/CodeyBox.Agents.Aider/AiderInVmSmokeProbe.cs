using CodeyBox.Core;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// In-VM smoke check for the aider CLI:
/// <list type="number">
/// <item><c>aider --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>aider --help</c> must advertise <c>--message</c> — the runner's only
/// transport is the one-shot message form. A build that dropped it would
/// otherwise dispatch into an interactive wait that never receives input and
/// fail late.</item>
/// </list>
///
/// <para>No auth step: aider fronts many providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="AiderSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="AiderTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class AiderInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Aider;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [AiderAgentRunner.DefaultBinary, "--version"],
                FailureHint: "aider binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert --message
            // support through grep's exit code: the runner's only transport is
            // the one-shot message form, and a build that dropped it must bench
            // here rather than dispatch into an interactive wait.
            new(
                ["bash", "-c", $"{AiderAgentRunner.DefaultBinary} --help | grep -q -- --message"],
                FailureHint: "aider --help does not advertise --message; one-shot message support could not be verified"),
        ];
    }
}
