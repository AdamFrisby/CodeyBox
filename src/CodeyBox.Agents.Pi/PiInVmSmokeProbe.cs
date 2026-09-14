using CodeyBox.Core;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// In-VM smoke check for the pi CLI:
/// <list type="number">
/// <item><c>pi --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>pi --help</c> must advertise <c>--mode</c>/<c>json</c> — the
/// runner's only transport. A pi build that dropped the JSON event stream
/// would otherwise dispatch into an unparseable run and fail late.</item>
/// </list>
///
/// <para>No auth step: pi supports 30+ providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="PiSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="PiTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class PiInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Pi;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [PiAgentRunner.DefaultBinary, "--version"],
                FailureHint: "pi binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert --mode/json
            // support through grep's exit code: the runner's only transport is
            // the JSON event stream, and a pi build that dropped it must bench
            // here rather than dispatch into an unparseable run.
            new(
                ["bash", "-c", $"{PiAgentRunner.DefaultBinary} --help | grep -q -- --mode"],
                FailureHint: "pi --help does not advertise --mode; --mode json support could not be verified"),
        ];
    }
}
