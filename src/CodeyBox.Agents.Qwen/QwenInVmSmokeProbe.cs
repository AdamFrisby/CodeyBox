using CodeyBox.Core;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// In-VM smoke check for the Qwen Code CLI:
/// <list type="number">
/// <item><c>qwen --version</c> — binary present on PATH (exit 127 otherwise).
/// Catches a missing baseline install before a work item is dispatched
/// to it.</item>
/// <item><c>qwen --help</c> must advertise <c>--output-format</c> /
/// <c>stream-json</c> — the runner's only transport. A build that dropped
/// the structured event stream would otherwise dispatch into an
/// unparseable run and fail late (verified against qwen 0.24.0, whose help
/// advertises both).</item>
/// </list>
///
/// <para>No auth step: qwen fronts many providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="QwenSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="QwenTerminalDiagnoser"/>. There is deliberately no quota-meter
/// probe either: no CLI surface reports the env-key path's remaining budget,
/// so qwen members fall through to the <c>NullQuotaProbe</c> unknown path
/// like pi/prime/goose/omp and the router gates on observed failures.</para>
/// </summary>
public sealed class QwenInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Qwen;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [QwenAgentRunner.DefaultBinary, "--version"],
                FailureHint: "qwen binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert the
            // transport flag/value through grep's exit code: the runner's
            // only transport is the structured event stream, and a build
            // that dropped it must bench here rather than dispatch into an
            // unparseable plaintext run.
            new(
                ["bash", "-c", $"{QwenAgentRunner.DefaultBinary} --help | grep -q -- --output-format && {QwenAgentRunner.DefaultBinary} --help | grep -q stream-json"],
                FailureHint: "qwen --help does not advertise --output-format stream-json; the runner's transport could not be verified"),
        ];
    }
}
