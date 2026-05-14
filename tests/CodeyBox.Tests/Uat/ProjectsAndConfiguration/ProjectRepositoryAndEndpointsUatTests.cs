using System.Net;
using System.Net.Http.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests.Uat.ProjectsAndConfiguration;

/// <summary>
/// UAT coverage for "Project repository and defaults - Loads project repo, branch,
/// agent, audit, upstream, network, budget, and release config".
/// Plan anchor: docs/uat/00-plan.md#projects-and-configuration
/// </summary>
public sealed class ProjectRepositoryAndDefaultsUatTests
{
    [Fact]
    public async Task ConfiguredProject_LoadsRepoDefaultsOverridesAndReleaseConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Defaults:Agent"] = "codex",
                ["CodeyBox:Defaults:BaseBranch"] = "main",
                ["CodeyBox:Defaults:Audit:MaxIterations"] = "4",
                ["CodeyBox:Defaults:Audit:AuditTypes:0"] = "security",
                ["CodeyBox:Defaults:Audit:Languages:0"] = "csharp",
                ["CodeyBox:Defaults:NetworkProfiles:Work"] = "default-work",
                ["CodeyBox:Defaults:NetworkProfiles:AuditTool"] = "isolated",
                ["CodeyBox:Projects:0:Id"] = "operator-api",
                ["CodeyBox:Projects:0:DisplayName"] = "Operator API",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://github.com/example/operator-api.git",
                ["CodeyBox:Projects:0:BaseBranch"] = "develop",
                ["CodeyBox:Projects:0:Agent"] = "claude",
                ["CodeyBox:Projects:0:DefaultAgentClass"] = "frontier-coding",
                ["CodeyBox:Projects:0:Upstream:Kind"] = "github",
                ["CodeyBox:Projects:0:Upstream:GitHubOwner"] = "example",
                ["CodeyBox:Projects:0:Upstream:GitHubRepository"] = "operator-api",
                ["CodeyBox:Projects:0:Upstream:TokenEnvVar"] = "OPERATOR_API_GITHUB_TOKEN",
                ["CodeyBox:Projects:0:Upstream:MergeMethod"] = "squash",
                ["CodeyBox:Projects:0:Upstream:AutoMerge"] = "true",
                ["CodeyBox:Projects:0:Audit:MaxIterations"] = "2",
                ["CodeyBox:Projects:0:Audit:Profile"] = "uat",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:MaxIterations"] = "5",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:AuditTypes:0"] = "security",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:ExcludedAuditors:0"] = "cheating:llm-review",
                ["CodeyBox:Projects:0:NetworkProfiles:Merge"] = "merge-egress",
                ["CodeyBox:Projects:0:Budget:MaxItemsPerHour"] = "3",
                ["CodeyBox:Projects:0:Budget:MaxItemsPerDay"] = "12",
                ["CodeyBox:Projects:0:Budget:MaxConcurrentForProject"] = "2",
                ["CodeyBox:Projects:0:Release:Enabled"] = "true",
                ["CodeyBox:Projects:0:Release:BranchNameTemplate"] = "release/{name}",
                ["CodeyBox:Projects:0:Release:AutoSyncMainIntervalMinutes"] = "30",
                ["CodeyBox:Projects:0:Release:DeepAuditors:0"] = "security:llm-review",
                ["CodeyBox:Projects:0:Release:DeepAuditMaxIterations"] = "4",
                ["CodeyBox:Projects:0:Release:CreateGitHubRelease"] = "true",
                ["CodeyBox:Projects:0:Release:GitHubTagTemplate"] = "v{name}",
            })
            .Build();

        var repo = new ProjectRepository(Options.Create(ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"))));

        var project = await repo.GetAsync(new ProjectId("operator-api"));

        Assert.NotNull(project);
        Assert.Equal("Operator API", project!.DisplayName);
        Assert.Equal("https://github.com/example/operator-api.git", project.RepositoryUrl);
        Assert.Equal("develop", project.DefaultBaseBranch);
        Assert.Equal(AgentKind.Claude, project.DefaultAgent);
        Assert.Equal("frontier-coding", project.DefaultAgentClass);
        Assert.Equal("github", project.Upstream.Kind);
        Assert.Equal("example", project.Upstream.GitHubOwner);
        Assert.Equal("operator-api", project.Upstream.GitHubRepository);
        Assert.Equal("OPERATOR_API_GITHUB_TOKEN", project.Upstream.TokenEnvVar);
        Assert.Equal("squash", project.Upstream.MergeMethod);
        Assert.True(project.Upstream.AutoMerge);
        Assert.Equal(2, project.Audit.MaxIterations);
        Assert.Equal(["csharp"], project.Audit.Languages);
        Assert.Equal(["security"], project.Audit.AuditTypes);
        Assert.Equal("default-work", project.NetworkProfiles.Work);
        Assert.Equal("default-work", project.NetworkProfiles.Rework);
        Assert.Equal("isolated", project.NetworkProfiles.AuditTool);
        Assert.Equal("merge-egress", project.NetworkProfiles.Merge);
        Assert.Equal(3, project.Budget.MaxItemsPerHour);
        Assert.Equal(12, project.Budget.MaxItemsPerDay);
        Assert.Equal(2, project.Budget.MaxConcurrentForProject);
        Assert.True(project.ReleaseConfig.Enabled);
        Assert.Equal("release/{name}", project.ReleaseConfig.BranchNameTemplate);
        Assert.Equal(TimeSpan.FromMinutes(30), project.ReleaseConfig.AutoSyncMainInterval);
        Assert.Equal(["security:llm-review"], project.ReleaseConfig.DeepAuditors);
        Assert.Equal(4, project.ReleaseConfig.DeepAuditMaxIterations);
        Assert.True(project.ReleaseConfig.CreateGitHubRelease);
        Assert.Equal("v{name}", project.ReleaseConfig.GitHubTagTemplate);

        var profile = project.Audit.ResolveProfile();
        Assert.Equal("uat", profile.Profile);
        Assert.Equal(5, profile.MaxIterations);
        Assert.Equal(["cheating:llm-review"], profile.ExcludedAuditors);
    }

    [Fact]
    public async Task NullProjectBaseBranch_RemainsNullForGitHostDefaultBranch()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "uses-host-default",
                    RepositoryUrl = "https://github.com/example/default-branch.git",
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("uses-host-default"));

        Assert.NotNull(project);
        Assert.Null(project!.DefaultBaseBranch);
    }

    [Fact]
    public void RepositoryValidationRejectsInvalidIdsDuplicateIdsUrlsAndMergeMethods()
    {
        Assert.Throws<InvalidOperationException>(() => BuildRepo(new()
        {
            Projects = [new ProjectConfig { Id = "", RepositoryUrl = "https://github.com/example/repo.git" }],
        }));
        Assert.Throws<InvalidOperationException>(() => BuildRepo(new()
        {
            Projects =
            [
                new ProjectConfig { Id = "same", RepositoryUrl = "https://github.com/example/one.git" },
                new ProjectConfig { Id = "same", RepositoryUrl = "https://github.com/example/two.git" },
            ],
        }));
        Assert.Throws<ArgumentException>(() => BuildRepo(new()
        {
            Projects = [new ProjectConfig { Id = "bad-url", RepositoryUrl = "--upload-pack=evil" }],
        }));
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRepo(new()
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "bad-merge",
                    RepositoryUrl = "https://github.com/example/repo.git",
                    Upstream = new ProjectUpstreamConfig
                    {
                        Kind = "github",
                        MergeMethod = "force",
                    },
                },
            ],
        }));
        Assert.Contains("MergeMethod", ex.Message);
    }

    private static ProjectRepository BuildRepo(ProjectsOptions options)
        => new(Options.Create(options));
}

[Collection("GlobalSerilog")]
public sealed class ProjectEndpointConfigurationUatTests
{
    [Fact]
    public async Task ProjectListAndGetExposeOperatorReadableResolvedConfig()
    {
        using var factory = new ProjectsAndConfigurationApiFactory(projects: new InMemoryProjectRepository(
            new Project
            {
                Id = new ProjectId("alpha"),
                DisplayName = "Alpha Project",
                RepositoryUrl = "https://github.com/example/alpha.git",
                DefaultBaseBranch = "main",
                DefaultAgent = AgentKind.Codex,
                Upstream = new ProjectUpstream { Kind = "github" },
                Audit = new ProjectAudit
                {
                    Profile = "uat",
                    AuditTypes = ["security"],
                    Languages = ["csharp"],
                    MaxIterations = 4,
                    Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["uat"] = new ProjectAudit
                        {
                            Profile = "uat",
                            AuditTypes = ["security"],
                            Languages = ["csharp"],
                            MaxIterations = 5,
                        },
                    },
                },
            }));
        using var client = factory.CreateClient();

        var listResponse = await client.GetAsync("/projects");
        var getResponse = await client.GetAsync("/projects/alpha");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var list = await listResponse.Content.ReadFromJsonAsync<List<ProjectDto>>();
        var project = Assert.Single(list!);
        Assert.Equal("alpha", project.Id);
        Assert.Equal("Alpha Project", project.DisplayName);
        Assert.Equal("https://github.com/example/alpha.git", project.RepositoryUrl);
        Assert.Equal("main", project.DefaultBaseBranch);
        Assert.Equal("codex", project.DefaultAgent);
        Assert.Equal("github", project.UpstreamKind);
        Assert.Equal(["csharp"], project.AuditLanguages);
        Assert.Equal(["security"], project.AuditTypes);
        Assert.Equal(5, project.AuditMaxIterations);

        var single = await getResponse.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(single);
        Assert.Equal(project.Id, single!.Id);
        Assert.Equal(project.DisplayName, single.DisplayName);
        Assert.Equal(project.RepositoryUrl, single.RepositoryUrl);
        Assert.Equal(project.DefaultBaseBranch, single.DefaultBaseBranch);
        Assert.Equal(project.DefaultAgent, single.DefaultAgent);
        Assert.Equal(project.UpstreamKind, single.UpstreamKind);
        Assert.Equal(project.AuditLanguages, single.AuditLanguages);
        Assert.Equal(project.AuditTypes, single.AuditTypes);
        Assert.Equal(project.AuditMaxIterations, single.AuditMaxIterations);
    }

    [Fact]
    public async Task NoProjectsConfigured_ApiStartsButCreateReportsEmptyProjectList()
    {
        using var factory = new ProjectsAndConfigurationApiFactory(projects: new InMemoryProjectRepository());
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/healthz");
        var create = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "missing",
            title = "try missing project",
            prompt = "do the thing",
        });

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var error = await create.Content.ReadFromJsonAsync<UnknownProjectResponse>();
        Assert.NotNull(error);
        Assert.Contains("unknown project", error!.Error);
        Assert.Empty(error.Available);
    }

    [Fact]
    public async Task OpenReleaseIdIsRejectedWhenProjectReleaseManagementDisabled()
    {
        var releaseId = ReleaseId.New();
        using var factory = new ProjectsAndConfigurationApiFactory(projects: new InMemoryProjectRepository(
            ProjectsAndConfigurationFixtures.Project(
                "release-disabled",
                "Release Disabled",
                "https://github.com/example/release-disabled.git",
                new ProjectReleaseConfig { Enabled = false })));
        using var client = factory.CreateClient();
        await factory.ReleaseStore.CreateAsync(new Release
        {
            Id = releaseId,
            ProjectId = new ProjectId("release-disabled"),
            Name = "1.0",
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var response = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "release-disabled",
            title = "release-scoped work",
            prompt = "do the thing",
            releaseId = releaseId.ToString(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("release management is not enabled", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UnknownProjectResponse(string Error, IReadOnlyList<string> Available);
}
