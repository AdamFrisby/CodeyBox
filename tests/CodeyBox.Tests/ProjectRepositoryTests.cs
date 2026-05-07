using Microsoft.Extensions.Options;
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
