using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class UnknownLanguageStringTests
{
    [Fact]
    public async Task ProjectRepositoryLogsWarningAndSkipsUnknownLanguage()
    {
        var logger = new CapturingLogger<ProjectRepository>();
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
        }), logger);

        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["python"], project!.Audit.Languages);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("unsupported audit language 'zig'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectRepositorySkipsLanguagesWithoutBuiltInCveCoverage()
    {
        var logger = new CapturingLogger<ProjectRepository>();
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
        }), logger);

        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["typescript", "javascript"], project!.Audit.Languages);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("unsupported audit language 'ruby'", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("unsupported audit language 'shell'", StringComparison.Ordinal));
    }
}
