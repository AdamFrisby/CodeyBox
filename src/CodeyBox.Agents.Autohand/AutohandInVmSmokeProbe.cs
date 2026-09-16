using CodeyBox.Core;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// In-VM smoke check for the autohand CLI:
/// <list type="number">
/// <item><c>autohand --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>autohand --help</c> must advertise <c>--output-format</c> /
/// <c>stream-json</c> — the runner's only transport (this CLI version rejects
/// whole-doc <c>json</c>, so the check pins the exact value, not just the
/// flag). A build that dropped the JSONL event stream would otherwise
/// dispatch into an unparseable run and fail late.</item>
/// </list>
///
/// <para>No auth step: autohand is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="AutohandSmokeProbe"/>; the first real dispatch surfaces
/// provider auth errors through
/// <see cref="AutohandTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class AutohandInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Autohand;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [AutohandAgentRunner.DefaultBinary, "--version"],
                FailureHint: "autohand binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert both the
            // flag and the exact stream value through grep's exit code: the
            // runner's only transport is the JSONL event stream, and a build
            // that dropped it must bench here rather than dispatch into an
            // unparseable run.
            new(
                ["bash", "-c", $"{AutohandAgentRunner.DefaultBinary} --help | grep -q -- --output-format && {AutohandAgentRunner.DefaultBinary} --help | grep -q stream-json"],
                FailureHint: "autohand --help does not advertise --output-format stream-json; the runner's only transport could not be verified"),
        ];
    }
}
