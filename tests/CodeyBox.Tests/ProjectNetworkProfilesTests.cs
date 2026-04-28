using Microsoft.Extensions.Options;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Per-phase network-profile selection: project config maps each phase
/// to a profile name; the orchestrator threads that into the sandbox
/// spec at the right phase. These tests verify the config plumbing.
/// </summary>
public sealed class ProjectNetworkProfilesTests
{
    [Fact]
    public async Task Profile_PerPhase_Loaded_FromProjectConfig()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://example.com/p.git",
                    NetworkProfiles = new ProjectNetworkProfilesConfig
                    {
                        Work = "claude",
                        Rework = "claude",
                        AuditAgent = "claude",
                        AuditTool = "isolated",
                        Merge = "claude-with-tests",
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal("claude", p!.NetworkProfiles.Work);
        Assert.Equal("claude", p.NetworkProfiles.Rework);
        Assert.Equal("claude", p.NetworkProfiles.AuditAgent);
        Assert.Equal("isolated", p.NetworkProfiles.AuditTool);
        Assert.Equal("claude-with-tests", p.NetworkProfiles.Merge);
    }

    [Fact]
    public async Task Defaults_FillIn_When_ProjectOmits()
    {
        var opts = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                NetworkProfiles = new ProjectNetworkProfilesConfig
                {
                    Work = "default-claude",
                    Merge = "default-merge",
                    AuditTool = "default-isolated",
                },
            },
            Projects =
            [
                new() { Id = "p", RepositoryUrl = "https://e.com/p.git" },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal("default-claude", p!.NetworkProfiles.Work);
        Assert.Equal("default-merge", p.NetworkProfiles.Merge);
        Assert.Equal("default-isolated", p.NetworkProfiles.AuditTool);
    }

    [Fact]
    public async Task Project_Overrides_Default_PerField()
    {
        var opts = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                NetworkProfiles = new ProjectNetworkProfilesConfig
                {
                    Work = "default-work",
                    Merge = "default-merge",
                },
            },
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://e.com/p.git",
                    NetworkProfiles = new ProjectNetworkProfilesConfig
                    {
                        Merge = "project-specific-merge",
                        // Work omitted — should fall through to default-work.
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal("default-work", p!.NetworkProfiles.Work);
        Assert.Equal("project-specific-merge", p.NetworkProfiles.Merge);
    }

    [Fact]
    public async Task Rework_FallsBackTo_Work_WhenNotSet()
    {
        // The merge agent and rework agent often need the same network as
        // work. Rework specifically falls back to Work as a last-resort
        // default, so operators don't have to repeat themselves.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new()
                {
                    Id = "p",
                    RepositoryUrl = "https://e.com/p.git",
                    NetworkProfiles = new ProjectNetworkProfilesConfig
                    {
                        Work = "claude",
                        // Rework not set explicitly.
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal("claude", p!.NetworkProfiles.Work);
        Assert.Equal("claude", p.NetworkProfiles.Rework);
    }
}
