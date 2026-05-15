using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

internal static class SandboxTargetResolver
{
    public static SandboxTarget Resolve(
        Project project,
        string? configuredNetworkProfile,
        bool graphicalEligible)
    {
        if (!project.GraphicalSandbox || !graphicalEligible)
            return new SandboxTarget(configuredNetworkProfile, SandboxProfileFlavor.Headless);

        var networkProfile = string.IsNullOrWhiteSpace(configuredNetworkProfile)
            ? null
            : configuredNetworkProfile;
        return new SandboxTarget(networkProfile, SandboxProfileFlavor.Graphical);
    }
}

internal readonly record struct SandboxTarget(string? NetworkProfile, SandboxProfileFlavor Flavor);
