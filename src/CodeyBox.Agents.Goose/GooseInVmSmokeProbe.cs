using CodeyBox.Core;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// In-VM smoke check for the goose CLI:
/// <list type="number">
/// <item><c>goose --version</c> — binary present on PATH (exit 127 otherwise).</item>
/// <item><c>goose run --help</c> must advertise <c>--output-format</c> /
/// <c>stream-json</c> — the runner's only transport. A goose build that
/// dropped the JSONL event stream would otherwise dispatch into an
/// unparseable run and fail late.</item>
/// </list>
///
/// <para>No auth step: goose supports 30+ providers with no single lightweight
/// "whoami", and any provider call would spend real quota. Credential
/// viability is covered host-side by <see cref="GooseSmokeProbe"/>; the first
/// real dispatch surfaces provider auth errors through
/// <see cref="GooseTerminalDiagnoser"/>.</para>
/// </summary>
public sealed class GooseInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Goose;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [GooseAgentRunner.DefaultBinary, "--version"],
                FailureHint: "goose binary not runnable on sandbox PATH"),
            // Exit-code-only by InVmSmokeStep contract, so assert
            // --output-format/stream-json support through grep's exit code:
            // the runner's only transport is the JSONL event stream, and a
            // goose build that dropped it must bench here rather than dispatch
            // into an unparseable run.
            new(
                ["bash", "-c", $"{GooseAgentRunner.DefaultBinary} run --help | grep -q -- --output-format"],
                FailureHint: "goose run --help does not advertise --output-format; stream-json support could not be verified"),
        ];
    }
}
