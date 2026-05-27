using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class UnknownLanguageStringTests
{
    [Fact]
    public void ProjectRepositoryRejectsUnknownLanguageIdsWithSuggestion()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { Languages = ["python", "cshrap"] },
                },
            ],
        })));

        Assert.Contains("unknown language id 'cshrap'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'csharp'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectRepositoryRejectsUnknownAuditTypeIdsWithSuggestion()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { AuditTypes = ["securty"] },
                },
            ],
        })));

        Assert.Contains("unknown audit type id 'securty'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'security'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectRepositoryLoadsConfigOnlyLanguagesFromLocalRepository()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "elixir.yaml"), """
            id: elixir
            displayName: "Elixir"
            marker:
              globs: ["**/mix.exs"]
            auditors:
              - name: elixir:test-pass
                argv: ["mix", "test"]
            """);

        var repo = new ProjectRepository(
            Options.Create(new ProjectsOptions
            {
                Projects =
                [
                    new ProjectConfig
                    {
                        Id = "alpha",
                        RepositoryUrl = new Uri(temp.Path).AbsoluteUri,
                        // Local-filesystem repository URLs are blocked by the
                        // noop+local-seed validator unless the operator opts
                        // in. This test is exercising preset loading from a
                        // local directory — exactly the sandbox-isolation
                        // use case the flag is designed for.
                        Upstream = new ProjectUpstreamConfig
                        {
                            Kind = "noop",
                            AcknowledgeSandboxIsolation = true,
                        },
                        Audit = new ProjectAuditConfig { Languages = ["elixir"] },
                    },
                ],
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            new PresetCatalogOptions());

        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["elixir"], project!.Audit.Languages);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeybox-presets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
