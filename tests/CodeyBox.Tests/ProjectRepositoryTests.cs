using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

public sealed class ProjectRepositoryTests
{
    [Fact]
    public async Task LoadsProjectsFromConfig()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    DisplayName = "Alpha",
                    RepositoryUrl = "https://github.com/me/alpha.git",
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.Equal("Alpha", p!.DisplayName);
        Assert.Equal("https://github.com/me/alpha.git", p.RepositoryUrl);
        Assert.Equal(AgentKind.Claude, p.DefaultAgent);
    }

    [Fact]
    public async Task ProjectInheritsAuditFromDefaults_WhenAuditOmitted()
    {
        var opts = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig
                {
                    MaxIterations = 5,
                    AuditTypes = ["security", "architecture"],
                },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Equal(5, p!.Audit.MaxIterations);
        Assert.Equal(new[] { "security", "architecture" }, p.Audit.AuditTypes);
    }

    [Fact]
    public async Task AuditLanguagesDefaultToEmpty_WhenOmitted()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Empty(p!.Audit.Languages);
        Assert.False(p.Audit.LanguagesConfigured);
    }

    [Fact]
    public async Task AuditLanguagesCanBeExplicitlyEmpty()
    {
        var opts = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig { Languages = ["csharp"] },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Audit = new ProjectAuditConfig { Languages = [] },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Empty(p!.Audit.Languages);
        Assert.True(p.Audit.LanguagesConfigured);
    }

    [Fact]
    public async Task ProjectAuditFieldsOverrideDefaults()
    {
        var opts = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig { MaxIterations = 3, AuditTypes = ["security"] },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Audit = new ProjectAuditConfig { MaxIterations = 1 },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Equal(1, p!.Audit.MaxIterations);
        // AuditTypes wasn't overridden in the project, so we keep the default list.
        Assert.Equal(new[] { "security" }, p.Audit.AuditTypes);
    }

    [Fact]
    public async Task ProjectAuditTypesObject_BindsSelectionAndPromptOverrides()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/x.git",
                ["CodeyBox:Projects:0:Audit:AuditTypes:security:ReviewFocus"] = "project security focus",
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:DisplayName"] = "Custom review",
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:ReviewFocus"] = "custom focus",
                ["CodeyBox:Projects:0:Audit:LlmPromptFrameTemplate"] = "{{reviewFocus}}\n{{resultFile}}",
            })
            .Build();

        var opts = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["custom", "security"], p!.Audit.AuditTypes.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("project security focus", p.Audit.AuditTypeOverrides["security"].ReviewFocus);
        Assert.Equal("Custom review", p.Audit.AuditTypeOverrides["custom"].DisplayName);
        Assert.Equal("{{reviewFocus}}\n{{resultFile}}", p.Audit.LlmPromptFrameTemplate);
    }

    [Fact]
    public async Task ProjectLanguageOverrides_BindFromLanguagesOverridesPath()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/x.git",
                ["CodeyBox:Projects:0:Audit:Languages:0"] = "csharp",
                ["CodeyBox:Projects:0:Audit:Languages:Overrides:csharp:Replace"] = "true",
                ["CodeyBox:Projects:0:Audit:Languages:Overrides:csharp:Auditors:0:Name"] = "csharp:custom-test",
                ["CodeyBox:Projects:0:Audit:Languages:Overrides:csharp:Auditors:0:Argv:0"] = "dotnet",
                ["CodeyBox:Projects:0:Audit:Languages:Overrides:csharp:Auditors:0:Argv:1"] = "test",
            })
            .Build();

        var opts = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));

        var languageOverride = Assert.Single(p!.Audit.LanguageOverrides);
        Assert.Equal("csharp", languageOverride.Key);
        Assert.True(languageOverride.Value.Replace);
        var auditor = Assert.Single(languageOverride.Value.Auditors);
        Assert.Equal("csharp:custom-test", auditor.Name);
        Assert.Equal(["dotnet", "test"], auditor.Argv);
    }

    [Fact]
    public void ProjectLanguageOverrides_AreValidatedAtRepositoryConstruction()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Audit = new ProjectAuditConfig
                    {
                        LanguageOverrides = new Dictionary<string, ProjectLanguagePresetOverrideConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["csharp"] = new()
                            {
                                Auditors =
                                [
                                    new ProjectConfiguredAuditorConfig
                                    {
                                        Name = "csharp:bad",
                                        Argv = ["dottest", "build"],
                                    },
                                ],
                            },
                        },
                    },
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));

        Assert.Contains("Project 'alpha' audit preset configuration is invalid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'dotnet'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateProjectIds_Throws()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new() { Id = "x", RepositoryUrl = "https://a.com/x.git" },
                new() { Id = "x", RepositoryUrl = "https://a.com/y.git" },
            ],
        };
        Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
    }

    [Fact]
    public void ProjectIdValidation_RejectsBadCharacters()
    {
        Assert.Throws<ArgumentException>(() => new ProjectId(""));
        Assert.Throws<ArgumentException>(() => new ProjectId("has spaces"));
        Assert.Throws<ArgumentException>(() => new ProjectId("../escape"));
        Assert.Throws<ArgumentException>(() => new ProjectId(new string('x', 65)));
        // Valid:
        _ = new ProjectId("ok-name_123");
    }

    [Fact]
    public void RepositoryUrlValidation_AppliedAtConfigLoad()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new() { Id = "evil", RepositoryUrl = "--upload-pack=evil" },
            ],
        };
        Assert.Throws<ArgumentException>(() => new ProjectRepository(Options.Create(opts)));
    }

    [Fact]
    public async Task DefaultAgentClass_LoadedFromConfig()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://github.com/me/alpha.git",
                    DefaultAgentClass = "frontier-coding",
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.Equal("frontier-coding", p!.DefaultAgentClass);
    }

    [Fact]
    public async Task DefaultAgentClass_NullWhenNotConfigured()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://github.com/me/alpha.git" },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Null(p!.DefaultAgentClass);
    }

    [Fact]
    public void InvalidMergeMethod_ThrowsAtStartup()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://github.com/me/alpha.git",
                    Upstream = new ProjectUpstreamConfig
                    {
                        Kind = "github",
                        GitHubOwner = "me",
                        GitHubRepository = "alpha",
                        TokenEnvVar = "GH_TOKEN",
                        MergeMethod = "invalid-value",
                    },
                },
            ],
        };
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("invalid-value", ex.Message);
    }

    [Fact]
    public async Task UpstreamFields_LoadedFromConfig()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://github.com/me/alpha.git",
                    Upstream = new ProjectUpstreamConfig
                    {
                        Kind = "github",
                        GitHubOwner = "me",
                        GitHubRepository = "alpha",
                        TokenEnvVar = "GH_TOKEN",
                        MergeMethod = "squash",
                        AutoMerge = true,
                        PullRequestTitleTemplate = "[bot] {title}",
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.Equal("github", p!.Upstream.Kind);
        Assert.Equal("squash", p.Upstream.MergeMethod);
        Assert.True(p.Upstream.AutoMerge);
        Assert.Equal("[bot] {title}", p.Upstream.PullRequestTitleTemplate);
    }
}
