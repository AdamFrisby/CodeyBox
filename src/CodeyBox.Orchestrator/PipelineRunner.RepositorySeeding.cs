using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.RepositorySeeding.cs — bare-repo ensure/refresh wrapper that
// classifies git-transport failures as infrastructure, never quota evidence.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Ensures the bare repo for <paramref name="item"/> (seeding from
    /// <see cref="Project.RepositoryUrl"/> when absent, refreshing the base
    /// branch when present) and resolves the effective base branch. Any
    /// transport failure is wrapped in <see cref="GitRepositorySeedingException"/>
    /// — always infrastructure, never quota evidence — with secret material
    /// scrubbed from the message before it can reach <c>LastError</c> or logs.
    /// Host/operator cancellation and sandbox-deferral signals pass through
    /// untouched so their dedicated handling still applies.
    /// </summary>
    private async Task<(string RepoId, string BaseBranch)> EnsurePipelineRepositoryAsync(
        WorkItem item,
        Project project,
        string? configuredBaseBranch,
        CancellationToken ct)
    {
        string repoId;
        try
        {
            repoId = await _gitHost.EnsureRepositoryAsync(item.Id, project.RepositoryUrl, configuredBaseBranch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not SandboxDiskDeferredException
            and not SandboxProvisioningDeferredException)
        {
            throw new GitRepositorySeedingException(
                "ensure-repository",
                RawOutputRedactor.Redact(ex.Message),
                ex);
        }

        try
        {
            return (repoId, configuredBaseBranch ?? await _gitHost.GetDefaultBranchAsync(repoId, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not SandboxDiskDeferredException
            and not SandboxProvisioningDeferredException)
        {
            throw new GitRepositorySeedingException(
                "resolve-default-branch",
                RawOutputRedactor.Redact(ex.Message),
                ex);
        }
    }
}
