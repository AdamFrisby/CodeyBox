using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Api;

internal static class ProjectSharedFolderEndpoints
{
    public static void Map(WebApplication app)
    {
        var projects = app.MapGroup("/projects");
        projects.MapGet("/{id}/shared-folders", GetSharedFoldersAsync);
    }

    /// <summary>
    /// Returns the operator-configured host-folder shares for a project with
    /// their live resolved status: the canonical host path, the guest mount
    /// point, the configured mode (auditors always receive read-only mounts
    /// regardless of mode), and whether the host path currently exists and is
    /// allowed. A <c>ReadWrite</c> mode lets agent code modify host files
    /// through the mount.
    /// </summary>
    private static async Task<IResult> GetSharedFoldersAsync(
        string id,
        IProjectRepository projects,
        ProjectSharedFolderPolicy sharedFolderPolicy,
        CancellationToken ct)
    {
        ProjectId pid;
        try
        {
            pid = new ProjectId(id);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid project id" });
        }

        var project = await projects.GetAsync(pid, ct);
        if (project is null)
        {
            return Results.NotFound();
        }

        var context = sharedFolderPolicy.ToValidationContext();
        var shares = project.SharedFolders.Select(folder => SharedFolderValidator.ResolveStatus(
            project.Id.Value, folder, context)).ToList();

        return Results.Ok(new
        {
            projectId = project.Id.Value,
            shares = shares.Select(status => new
            {
                name = status.Name,
                hostPath = status.ConfiguredHostPath,
                resolvedHostPath = status.ResolvedHostPath,
                sandboxPath = status.SandboxPath,
                mode = status.Mode.ToString(),
                auditEffectiveMode = SharedFolderMode.ReadOnly.ToString(),
                phases = status.Phases.ToString(),
                required = status.Required,
                writerPolicy = status.WriterPolicy.ToString(),
                exclusiveFallback = status.ExclusiveFallback.ToString(),
                exists = status.Exists,
                allowed = status.Allowed,
                error = status.Error,
            }),
            requiredCapabilitiesWork = SharedFolderPolicy.RequiredCapabilitiesForPhase(
                project.SharedFolders, "work", isAudit: false),
            requiredCapabilitiesAudit = SharedFolderPolicy.RequiredCapabilitiesForPhase(
                project.SharedFolders, "audit", isAudit: true),
        });
    }
}
