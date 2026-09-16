using CodeyBox.Core;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// In-VM smoke check for the prime-agent CLI:
/// <list type="number">
/// <item><c>prime-agent --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>prime-agent --help</c> must advertise BOTH <c>-p/--print</c> and
/// <c>--mode</c>/<c>json</c> — the runner's only transport is the one-shot
/// <c>-p --mode json</c> combination. A build that dropped either flag would
/// otherwise dispatch into an interactive TUI wait (<c>-p</c> missing) or an
/// unparseable run (<c>--mode json</c> missing) and fail late.</item>
/// </list>
///
/// <para>No auth step: prime fronts many providers with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="PrimeSmokeProbe"/>; the first real dispatch surfaces provider
/// auth errors through <see cref="PrimeTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class PrimeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Prime;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [PrimeAgentRunner.DefaultBinary, "--version"],
                FailureHint: "prime-agent binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert both
            // transport flags through grep's exit code: the runner speaks
            // only `-p --mode json`, and a build that dropped either half
            // must bench here rather than dispatch into an interactive wait
            // or an unparseable run.
            new(
                ["bash", "-c", $"{PrimeAgentRunner.DefaultBinary} --help | grep -q -- --print && {PrimeAgentRunner.DefaultBinary} --help | grep -q -- --mode"],
                FailureHint: "prime-agent --help does not advertise -p/--print and --mode; -p --mode json support could not be verified"),
        ];
    }
}
