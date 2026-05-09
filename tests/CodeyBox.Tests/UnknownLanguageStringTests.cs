using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class UnknownLanguageStringTests
{
    [Fact]
    public async Task ProjectRepositoryPreservesConfigOnlyLanguagesForPresetCatalog()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { Languages = ["python", "zig"] },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["python", "zig"], project!.Audit.Languages);
    }

    [Fact]
    public async Task ProjectRepositoryNoLongerUsesCompileTimeLanguageAllowlist()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { Languages = ["typescript", "javascript", "ruby", "shell"] },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["typescript", "javascript", "ruby", "shell"], project!.Audit.Languages);
    }
}
