using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

internal static class SandboxTargetResolver
{
    public static SandboxTarget ResolveProjectPhase(
        Project project,
        string? configuredNetworkProfile)
    {
        if (!project.GraphicalSandbox)
            return new SandboxTarget(configuredNetworkProfile, SandboxProfileFlavor.Headless);

        return new SandboxTarget(SandboxConventions.GraphicalNetworkProfile, SandboxProfileFlavor.Graphical);
    }

    public static SandboxTarget ResolveAudit(
        string? configuredNetworkProfile,
        AuditCapabilities required)
    {
        if (!required.HasFlag(AuditCapabilities.Graphical))
            return new SandboxTarget(configuredNetworkProfile, SandboxProfileFlavor.Headless);

        return new SandboxTarget(SandboxConventions.GraphicalNetworkProfile, SandboxProfileFlavor.Graphical);
    }
}

internal readonly record struct SandboxTarget(string? NetworkProfile, SandboxProfileFlavor Flavor);
