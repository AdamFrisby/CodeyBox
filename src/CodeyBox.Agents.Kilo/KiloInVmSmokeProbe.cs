using CodeyBox.Core;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// In-VM smoke check for the kilo CLI:
/// <list type="number">
/// <item><c>kilo --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>kilo run --help</c> must advertise <c>--format</c> /
/// <c>json</c> — the runner's only transport. A build that dropped the JSON
/// event stream would otherwise dispatch into an unparseable run and fail
/// late.</item>
/// <item><c>kilo run --help</c> must advertise <c>--auto</c> — the flag is
/// mandatory (without it a non-interactive run auto-rejects every permission
/// request and exits 1, which reads as a refusal rather than a configuration
/// error). A build that dropped it must bench here rather than mis-fail
/// first dispatch.</item>
/// </list>
///
/// <para>No auth step: kilo is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="KiloSmokeProbe"/>; the first real dispatch surfaces provider
/// auth errors through <see cref="KiloTerminalDiagnoser"/>. There is
/// deliberately no quota-meter probe either: <c>kilo stats</c> reports local
/// historical usage, not a quota balance, so kilo members fall through to
/// the <c>NullQuotaProbe</c> unknown path like pi/goose/autohand and the
/// router gates on observed failures.</para>
/// </summary>
public sealed class KiloInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Kilo;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [KiloAgentRunner.DefaultBinary, "--version"],
                FailureHint: "kilo binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // transport flag/value and the mandatory autonomy flag through
            // grep's exit code: the runner's only transport is the JSON
            // event stream, and --auto is what keeps a headless run from
            // refusing its own tool calls.
            new(
                ["bash", "-c", $"{KiloAgentRunner.DefaultBinary} run --help | grep -q -- --format && {KiloAgentRunner.DefaultBinary} run --help | grep -q json && {KiloAgentRunner.DefaultBinary} run --help | grep -q -- --auto"],
                FailureHint: "kilo run --help does not advertise --format json and --auto; the runner's transport and autonomy flags could not be verified"),
        ];
    }
}
