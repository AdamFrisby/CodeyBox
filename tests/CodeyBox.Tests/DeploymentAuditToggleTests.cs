using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// The deployment-stage toggle is hot-reloadable project config: project
/// values win over defaults, and per-profile values (selectable per work
/// item through <c>WorkItem.AuditorProfile</c>) survive the config
/// round-trip so per-item enablement works.
/// </summary>
public sealed class DeploymentAuditToggleTests
{
    private static ProjectsOptions BuildOptions(
        ProjectAuditConfig? project = null,
        ProjectAuditConfig? defaults = null,
        Dictionary<string, ProjectAuditConfig>? profiles = null)
    {
        if (project is not null && profiles is not null)
            project.Profiles = profiles;
        return new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Audit = project,
                },
            ],
            Defaults = defaults is null ? new ProjectDefaultsConfig() : new ProjectDefaultsConfig { Audit = defaults },
        };
    }

    [Fact]
    public async Task UnsetEverywhere_DefaultsToDisabled()
    {
        var repo = new ProjectRepository(Options.Create(BuildOptions()));

        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.NotNull(p);
        Assert.False(p!.Audit.DeploymentAuditEnabled);
    }

    [Fact]
    public async Task ProjectValue_WinsOverDefaults()
    {
        var repo = new ProjectRepository(Options.Create(BuildOptions(
            project: new ProjectAuditConfig { DeploymentAuditEnabled = true },
            defaults: new ProjectAuditConfig { DeploymentAuditEnabled = false })));

        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.NotNull(p);
        Assert.True(p!.Audit.DeploymentAuditEnabled);
    }

    [Fact]
    public async Task DefaultsFillGap_WhenProjectUnset()
    {
        var repo = new ProjectRepository(Options.Create(BuildOptions(
            project: new ProjectAuditConfig(),
            defaults: new ProjectAuditConfig { DeploymentAuditEnabled = true })));

        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.NotNull(p);
        Assert.True(p!.Audit.DeploymentAuditEnabled);
    }

    [Fact]
    public async Task ProfileValue_SurvivesRoundTrip_ForPerItemEnablement()
    {
        var repo = new ProjectRepository(Options.Create(BuildOptions(
            project: new ProjectAuditConfig(),
            profiles: new Dictionary<string, ProjectAuditConfig>
            {
                ["deploy-check"] = new ProjectAuditConfig { DeploymentAuditEnabled = true },
            })));

        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.NotNull(p);
        Assert.False(p!.Audit.DeploymentAuditEnabled);
        var profile = p.Audit.ResolveProfile("deploy-check");
        Assert.True(profile.DeploymentAuditEnabled);
    }
}
