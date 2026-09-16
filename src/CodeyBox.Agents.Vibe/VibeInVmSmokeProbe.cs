using CodeyBox.Core;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// In-VM smoke check for the vibe CLI:
/// <list type="number">
/// <item><c>vibe --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>vibe --help</c> must advertise <c>--output</c> /
/// <c>streaming</c> — the runner's only transport. A vibe build that dropped
/// the NDJSON history-event stream would otherwise dispatch into an
/// unparseable run and fail late.</item>
/// </list>
///
/// <para>No auth step: vibe fronts many providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="VibeSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="VibeTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class VibeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Vibe;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [VibeAgentRunner.DefaultBinary, "--version"],
                FailureHint: "vibe binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert
            // --output/streaming support through grep's exit code: the
            // runner's only transport is the NDJSON history-event stream, and
            // a vibe build that dropped it must bench here rather than
            // dispatch into an unparseable run.
            new(
                ["bash", "-c", $"{VibeAgentRunner.DefaultBinary} --help | grep -q -- --output && {VibeAgentRunner.DefaultBinary} --help | grep -q streaming"],
                FailureHint: "vibe --help does not advertise --output streaming; streaming support could not be verified"),
        ];
    }
}
