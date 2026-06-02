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

    public static InVmSmokeSandboxTarget ToInVmSmokeTarget(
        SandboxTarget target,
        string? baselineRef = null) =>
        new(target.NetworkProfile, target.Flavor, baselineRef);

    public static InVmSmokeSandboxTarget ToInVmSmokeTarget(
        Project project,
        SandboxTarget target,
        string? workBaselineRef) =>
        ToInVmSmokeTarget(target, BaselineRefForTarget(project, target, workBaselineRef));

    public static string? BaselineRefForTarget(
        Project project,
        SandboxTarget target,
        string? workBaselineRef)
    {
        if (string.IsNullOrWhiteSpace(workBaselineRef))
            return null;
        if (target.Flavor != SandboxProfileFlavor.Headless)
            return null;
        if (string.IsNullOrWhiteSpace(project.NetworkProfiles.Work))
            return null;
        if (!string.Equals(target.NetworkProfile, project.NetworkProfiles.Work, StringComparison.Ordinal))
            return null;

        return workBaselineRef;
    }
}

internal readonly record struct SandboxTarget(string? NetworkProfile, SandboxProfileFlavor Flavor);
