using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// In-VM smoke check for the Codex CLI: <c>codex --version</c> must return 0.
/// Catches the binary being absent from the sandbox PATH (exit 127) before a
/// work item is dispatched to it. See <see cref="IInVmSmokeProbe"/>.
/// </summary>
public sealed class CodexInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Codex;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
    [
        new(["codex", "--version"], FailureHint: "codex binary not runnable on sandbox PATH"),
    ];
}
