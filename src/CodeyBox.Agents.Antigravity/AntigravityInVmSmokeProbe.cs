using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// In-VM smoke check for the Antigravity CLI: <c>agy --version</c> must return 0.
/// Catches the binary being absent from the sandbox PATH (exit 127) before a
/// work item is dispatched to it. See <see cref="IInVmSmokeProbe"/>.
/// </summary>
public sealed class AntigravityInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Antigravity;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
    [
        new([AntigravityAgentRunner.DefaultBinary, "--version"], FailureHint: "agy binary not runnable on sandbox PATH"),
    ];
}
