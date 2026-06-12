using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// In-VM smoke check for the Copilot CLI: <c>copilot --version</c> must return 0.
/// Catches the binary being absent from the sandbox PATH (exit 127) before a
/// work item is dispatched to it. See <see cref="IInVmSmokeProbe"/>.
/// </summary>
public sealed class CopilotInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Copilot;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
    [
        new([CopilotAgentRunner.DefaultBinary, "--version"], FailureHint: "copilot binary not runnable on sandbox PATH"),
    ];
}
