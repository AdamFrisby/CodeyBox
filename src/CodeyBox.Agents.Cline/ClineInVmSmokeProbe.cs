using CodeyBox.Core;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// In-VM smoke check for the cline CLI:
/// <list type="number">
/// <item><c>cline --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>cline --help</c> must advertise <c>--json</c> — the runner's only
/// transport. A build that dropped the NDJSON event stream would otherwise
/// dispatch into an unparseable run and fail late.</item>
/// </list>
///
/// <para>No auth step: cline is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota.
/// Credential viability is covered host-side by
/// <see cref="ClineSmokeProbe"/>; the first real dispatch surfaces
/// provider auth errors through <see cref="ClineTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class ClineInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Cline;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [ClineAgentRunner.DefaultBinary, "--version"],
                FailureHint: "cline binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the flag
            // through grep's exit code: the runner's only transport is the
            // NDJSON event stream, and a build that dropped it must bench
            // here rather than dispatch into an unparseable run.
            new(
                ["bash", "-c", $"{ClineAgentRunner.DefaultBinary} --help | grep -q -- --json"],
                FailureHint: "cline --help does not advertise --json; the runner's only transport could not be verified"),
        ];
    }
}
