using CodeyBox.Core;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// In-VM smoke check for the cmd CLI:
/// <list type="number">
/// <item><c>cmd --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>cmd -p --help</c> must advertise <c>--output-format</c> /
/// <c>json</c> — the runner's only transport. A build that dropped the JSON
/// event stream would otherwise dispatch into an unparseable run and fail
/// late. The check additionally pins <c>--yolo</c>: without it headless
/// mode blocks writes/edits/shell and the run exits 0 with no changes
/// (verified), so a build that dropped the flag must bench here rather
/// than mis-dispatch into a blank pass.</item>
/// </list>
///
/// <para>No auth step: cmd fronts 150+ providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="CmdSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="CmdTerminalDiagnoser"/>. There is deliberately no quota-meter
/// probe either: <c>cmd status</c> reports Command Code plan state — not
/// the BYOK provider balance the <c>--local-only</c> runner path spends —
/// so cmd members fall through to the <c>NullQuotaProbe</c> unknown path
/// like omp/goose/kilo and the router gates on observed failures.</para>
/// </summary>
public sealed class CmdInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Cmd;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [CmdAgentRunner.DefaultBinary, "--version"],
                FailureHint: "cmd binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // transport flag/value and the mandatory autonomy flag through
            // grep's exit code: the runner's only transport is the JSON
            // event stream under -p, and --yolo is what keeps a headless
            // run from silently changing nothing.
            new(
                ["bash", "-c", $"{CmdAgentRunner.DefaultBinary} -p --help | grep -q -- --output-format && {CmdAgentRunner.DefaultBinary} -p --help | grep -q json && {CmdAgentRunner.DefaultBinary} -p --help | grep -q -- --yolo"],
                FailureHint: "cmd -p --help does not advertise --output-format json and --yolo; the runner's transport and autonomy flags could not be verified"),
        ];
    }
}
