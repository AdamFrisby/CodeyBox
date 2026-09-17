using CodeyBox.Core;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// In-VM smoke check for the Continue CLI:
/// <list type="number">
/// <item><c>cn --version</c> — binary present on PATH (exit 127 otherwise).
/// The binary is <c>cn</c>, not <c>continue</c> (npm
/// <c>@continuedev/cli</c>).</item>
/// <item><c>cn --help</c> must advertise <c>--print</c> — the runner's only
/// transport (the one-shot contract). A build that dropped headless mode
/// would otherwise dispatch into an interactive session and hang the
/// sandbox.</item>
/// <item><c>cn --help</c> must advertise <c>--auto</c> — the autonomy flag
/// that keeps a headless run from gating on approvals no human can give. A
/// build that dropped it must bench here rather than mis-fail first
/// dispatch as "no changes".</item>
/// </list>
///
/// <para>No auth step: Continue is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="ContinueSmokeProbe"/>; the first real dispatch surfaces
/// provider auth errors through
/// <see cref="ContinueTerminalDiagnoser"/>. There is deliberately no
/// quota-meter probe either: <c>cn</c> exposes no quota balance endpoint,
/// so Continue members fall through to the <c>NullQuotaProbe</c> unknown
/// path like pi/kilo/autohand and the router gates on observed
/// failures.</para>
/// </summary>
public sealed class ContinueInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Continue;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [ContinueAgentRunner.DefaultBinary, "--version"],
                FailureHint: "cn binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // transport flag and the autonomy flag through grep's exit code:
            // the runner's only transport is --print, and --auto is what
            // keeps a headless run from gating on approvals.
            new(
                ["bash", "-c", $"{ContinueAgentRunner.DefaultBinary} --help | grep -q -- --print && {ContinueAgentRunner.DefaultBinary} --help | grep -q -- --auto"],
                FailureHint: "cn --help does not advertise --print and --auto; the runner's transport and autonomy flags could not be verified"),
        ];
    }
}
