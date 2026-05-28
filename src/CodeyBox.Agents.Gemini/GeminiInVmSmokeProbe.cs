using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// In-VM smoke check for the Gemini CLI: <c>gemini --version</c> must return 0.
/// Catches the binary being absent from the sandbox PATH (exit 127) before a
/// work item is dispatched to it. See <see cref="IInVmSmokeProbe"/>.
/// </summary>
public sealed class GeminiInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Gemini;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
    [
        new(["gemini", "--version"], FailureHint: "gemini binary not runnable on sandbox PATH"),
    ];
}
