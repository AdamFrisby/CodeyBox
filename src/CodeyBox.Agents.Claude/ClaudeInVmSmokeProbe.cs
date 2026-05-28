using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// In-VM smoke check for the Claude CLI: <c>claude --version</c> must return 0.
/// Catches the binary being absent from the sandbox PATH (exit 127) before a
/// work item is dispatched to it. See <see cref="IInVmSmokeProbe"/>.
/// </summary>
public sealed class ClaudeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Claude;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
    [
        new([ClaudeAgentRunner.DefaultBinary, "--version"], FailureHint: "claude binary not runnable on sandbox PATH"),
    ];
}
