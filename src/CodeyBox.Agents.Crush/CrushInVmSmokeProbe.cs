using CodeyBox.Core;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// In-VM smoke check for the Crush CLI:
/// <list type="number">
/// <item><c>crush --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>crush run --help</c> must advertise <c>-m/--model</c> and
/// <c>-q/--quiet</c> — the runner's exact dispatch flags. A build that
/// dropped the model override would otherwise dispatch into the CLI's paid
/// default (a quota failure on a <c>:free</c>-only key), and a build that
/// dropped quiet mode would run with spinner output the pipeline never
/// parses. There is deliberately NO <c>--yolo</c> assertion: <c>--yolo</c>
/// is a root-only flag and <c>crush run --yolo</c> hard-fails with
/// <c>Unknown flag: --yolo</c> (verified live) — permissions are already
/// auto-approved inside <c>run</c>, so no approval flag is needed.</item>
/// </list>
///
/// <para>No auth step: Crush is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="CrushSmokeProbe"/>; the first real dispatch surfaces provider
/// auth errors through <see cref="CrushTerminalDiagnoser"/>. There is
/// deliberately no quota-meter probe either: <c>crush stats</c> renders an
/// HTML usage report with no machine-readable balance, so Crush members
/// fall through to the <c>NullQuotaProbe</c> unknown path like
/// continue/cmd and the router gates on observed failures.</para>
/// </summary>
public sealed class CrushInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Crush;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [CrushAgentRunner.DefaultBinary, "--version"],
                FailureHint: "crush binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // dispatch flags through grep's exit code: the runner dispatches
            // `crush run -q -m <model>` with the prompt on stdin, so the run
            // subcommand must advertise both the model override and quiet
            // mode. --yolo is deliberately absent from the assertion: it is
            // a root-only flag that `run` rejects, and run auto-approves
            // permissions without it.
            new(
                ["bash", "-c", $"{CrushAgentRunner.DefaultBinary} run --help | grep -q -- --model && {CrushAgentRunner.DefaultBinary} run --help | grep -q -- --quiet"],
                FailureHint: "crush run --help does not advertise --model and --quiet; the runner's dispatch flags could not be verified"),
        ];
    }
}
