using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for secret-group authorisation: default deny, project-wide
/// versus work-item grants, operator-only matching (no work-item content
/// field can widen access), phase scoping within a granted group, per
/// injection attribution, and migration of pre-grant flat declarations.
/// </summary>
public sealed class ProjectSandboxSecretGroupsTests
{
    private static string UniqueEnvName(string prefix)
        => $"{prefix}_{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

    private sealed class HostEnvScope : IDisposable
    {
        private readonly string _name;

        private HostEnvScope(string name, string value)
        {
            _name = name;
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, null);

        public static HostEnvScope Set(string name, string value) => new(name, value);
    }

    private static WorkItem NewItem(ProjectId projectId, string title = "grouped secrets") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId,
        Title = title,
        Prompt = "do work",
    };

    private static Project GrantedProject(
        string hostName,
        string sandboxName,
        string group,
        IReadOnlyList<ProjectSandboxSecretGrant> grants,
        IReadOnlyList<string>? scopes = null) => new()
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret
                {
                    HostEnvVar = hostName,
                    SandboxEnvVar = sandboxName,
                    Group = group,
                    Scopes = scopes ?? ["work", "rework"],
                },
            ],
            SandboxSecretGrants = grants,
        };

    private static Project UngrantedProject(string hostName, string sandboxName, string group) =>
        GrantedProject(hostName, sandboxName, group, []);

    // ── Default deny ──────────────────────────────────────────────────────

    [Fact]
    public void GroupWithNoGrant_IsNotInjected()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "must-not-leak");
        var project = UngrantedProject(hostName, "GROUPED_KEY", "llm");

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ProjectWithSecretsAndNoGrants_InjectsNothing()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "must-not-leak");
        var project = UngrantedProject(hostName, "GROUPED_KEY", ProjectSandboxSecretGroups.Default);

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable);

        Assert.Empty(resolved);
        Assert.Empty(ProjectSandboxSecretResolver.DescribeInjections(project, WorkItemId.New(), "work"));
    }

    [Fact]
    public void GrantForAnotherGroup_DoesNotAuthorise()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "must-not-leak");
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "llm",
            [new ProjectSandboxSecretGrant { Group = "other" }]);

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable);

        Assert.Empty(resolved);
    }

    [Fact]
    public void GroupMatch_IsCaseSensitive()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "must-not-leak");
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "llm",
            [new ProjectSandboxSecretGrant { Group = "LLM" }]);

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable);

        Assert.Empty(resolved);
    }

    // ── Project-wide versus work-item grants ──────────────────────────────

    [Fact]
    public void ProjectWideGrant_ReachesEveryItem()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        const string SecretValue = "shared-value";
        using var _ = HostEnvScope.Set(hostName, SecretValue);
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "llm",
            [new ProjectSandboxSecretGrant { Group = "llm" }]);

        foreach (var itemId in new[] { WorkItemId.New(), WorkItemId.New(), WorkItemId.New() })
        {
            var resolved = ProjectSandboxSecretResolver.ResolveForScope(
                project, itemId, "work", Environment.GetEnvironmentVariable);
            Assert.Equal(SecretValue, resolved["GROUPED_KEY"]);
        }
    }

    [Fact]
    public void WorkItemGrant_ReachesOnlyThatItem()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        const string SecretValue = "single-item-value";
        using var _ = HostEnvScope.Set(hostName, SecretValue);
        var authorised = WorkItemId.New();
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "acme",
            [new ProjectSandboxSecretGrant { Group = "acme", WorkItemId = authorised.Value }]);

        var allowed = ProjectSandboxSecretResolver.ResolveForScope(
            project, authorised, "work", Environment.GetEnvironmentVariable);
        Assert.Equal(SecretValue, allowed["GROUPED_KEY"]);

        var denied = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable);
        Assert.Empty(denied);
    }

    // ── Operator-only: work-item content cannot widen access ──────────────

    [Fact]
    public void WorkItemContentCannotCauseGrantToMatch()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "content-must-not-authorise");
        var projectId = new ProjectId("p");
        var authorised = NewItem(projectId);
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "acme",
            [new ProjectSandboxSecretGrant { Group = "acme", WorkItemId = authorised.Id.Value }]);

        // The granted item keeps access when every content field changes:
        // authorisation follows identity, not content.
        var rewritten = authorised with
        {
            Title = "totally different title",
            Prompt = "totally different prompt",
            RequiredCapabilities = ["internet", "gpu"],
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["changeScope"] = "refactor" },
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jobtrack"] = "999",
                ["legacy"] = authorised.Id.ToString(),
            },
        };
        var stillAllowed = ProjectSandboxSecretResolver.ResolveForScope(
            project, rewritten.Id, "work", Environment.GetEnvironmentVariable);
        Assert.NotEmpty(stillAllowed);

        // A different item copying every content field — including the
        // authorised id smuggled into title, prompt, capabilities, knobs and
        // external ids — still gets nothing: only identity matches.
        var impostor = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = authorised.Id.ToString(),
            Prompt = authorised.Id.ToString(),
            RequiredCapabilities = [authorised.Id.ToString()],
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["changeScope"] = authorised.Id.ToString(),
            },
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jobtrack"] = authorised.Id.ToString(),
            },
        };
        var denied = ProjectSandboxSecretResolver.ResolveForScope(
            project, impostor.Id, "work", Environment.GetEnvironmentVariable);
        Assert.Empty(denied);
    }

    [Fact]
    public void FindAuthorizingGrant_MatchesIdentityOnly()
    {
        var authorised = WorkItemId.New();
        var grants = new List<ProjectSandboxSecretGrant>
        {
            new() { Group = "acme", WorkItemId = authorised.Value },
        }.AsReadOnly();

        Assert.NotNull(ProjectSandboxSecretResolver.FindAuthorizingGrant(grants, "acme", authorised));
        Assert.Null(ProjectSandboxSecretResolver.FindAuthorizingGrant(grants, "acme", WorkItemId.New()));
        Assert.Null(ProjectSandboxSecretResolver.FindAuthorizingGrant(grants, "acme", null));
        Assert.Null(ProjectSandboxSecretResolver.FindAuthorizingGrant(grants, "other", authorised));

        var projectWide = new List<ProjectSandboxSecretGrant>
        {
            new() { Group = "acme" },
        }.AsReadOnly();
        Assert.NotNull(ProjectSandboxSecretResolver.FindAuthorizingGrant(projectWide, "acme", authorised));
        Assert.NotNull(ProjectSandboxSecretResolver.FindAuthorizingGrant(projectWide, "acme", null));
    }

    // ── Phase scoping unchanged within a granted group ────────────────────

    [Theory]
    [InlineData("work", true)]
    [InlineData("rework", false)]
    [InlineData("audit-agent", false)]
    [InlineData("audit-tool", false)]
    [InlineData("merge", false)]
    public void GrantedGroup_StillHonoursPhaseScopes(string scope, bool expected)
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "phase-value");
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "llm",
            [new ProjectSandboxSecretGrant { Group = "llm" }],
            ["work"]);

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), scope, Environment.GetEnvironmentVariable);

        Assert.Equal(expected, resolved.ContainsKey("GROUPED_KEY"));
    }

    [Theory]
    [InlineData("audit-agent", true)]
    [InlineData("audit-tool", false)]
    public void GrantedGroup_DistinguishesAuditAgentFromAuditTool(string scope, bool expected)
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_SG_HOST");
        using var _ = HostEnvScope.Set(hostName, "audit-value");
        var project = GrantedProject(
            hostName, "GROUPED_KEY", "llm",
            [new ProjectSandboxSecretGrant { Group = "llm" }],
            ["audit-agent"]);

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), scope, Environment.GetEnvironmentVariable);

        Assert.Equal(expected, resolved.ContainsKey("GROUPED_KEY"));
    }

    // ── Attribution ───────────────────────────────────────────────────────

    [Fact]
    public void EachInjection_IsAttributableToGrantAndGroup()
    {
        var hostA = UniqueEnvName("CODEYBOX_TEST_SG_HOST_A");
        var hostB = UniqueEnvName("CODEYBOX_TEST_SG_HOST_B");
        using var _a = HostEnvScope.Set(hostA, "value-a");
        using var _b = HostEnvScope.Set(hostB, "value-b");
        var itemId = WorkItemId.New();
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret { HostEnvVar = hostA, SandboxEnvVar = "KEY_A", Group = "wide" },
                new ProjectSandboxSecret { HostEnvVar = hostB, SandboxEnvVar = "KEY_B", Group = "narrow" },
            ],
            SandboxSecretGrants =
            [
                new ProjectSandboxSecretGrant { Group = "wide" },
                new ProjectSandboxSecretGrant { Group = "narrow", WorkItemId = itemId.Value },
            ],
        };

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, itemId, "work", Environment.GetEnvironmentVariable);
        Assert.Equal(2, resolved.Count);

        var injections = ProjectSandboxSecretResolver.DescribeInjections(project, itemId, "work");
        Assert.Equal(2, injections.Count);
        var wide = Assert.Single(injections, injection => injection.SandboxEnvVar == "KEY_A");
        Assert.Equal("wide", wide.Group);
        Assert.True(wide.GrantedProjectWide);
        Assert.False(wide.GrantedByImplicitMigration);
        var narrow = Assert.Single(injections, injection => injection.SandboxEnvVar == "KEY_B");
        Assert.Equal("narrow", narrow.Group);
        Assert.False(narrow.GrantedProjectWide);
        Assert.False(narrow.GrantedByImplicitMigration);

        // The other item holds only the project-wide secret, attributed likewise.
        var otherInjections = ProjectSandboxSecretResolver.DescribeInjections(project, WorkItemId.New(), "work");
        var only = Assert.Single(otherInjections);
        Assert.Equal("KEY_A", only.SandboxEnvVar);
        Assert.Equal("wide", only.Group);
    }

    // ── Migration ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyFlatConfig_BehavesIdenticallyAfterMigration()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "CODEYBOX_OPENROUTER_API_KEY",
                            SandboxEnvVar = "OPENROUTER_API_KEY",
                        },
                    ],
                },
            ],
        };

        var repo = new ProjectRepository(Options.Create(opts));
        var project = await repo.GetAsync(new ProjectId("alpha"));

        var secret = Assert.Single(project!.SandboxSecrets);
        Assert.Equal(ProjectSandboxSecretGroups.Default, secret.Group);
        Assert.Equal(["work", "rework"], secret.Scopes);

        // The migration is explicit: an implicit project-wide grant for the
        // default group is present on the resolved model.
        var grant = Assert.Single(project.SandboxSecretGrants);
        Assert.Equal(ProjectSandboxSecretGroups.Default, grant.Group);
        Assert.True(grant.IsProjectWide);
        Assert.True(grant.IsImplicit);

        using var _ = HostEnvScope.Set("CODEYBOX_OPENROUTER_API_KEY", "legacy-value");
        var itemId = WorkItemId.New();
        Assert.Equal(
            "legacy-value",
            ProjectSandboxSecretResolver.ResolveForScope(
                project, itemId, "work", Environment.GetEnvironmentVariable)["OPENROUTER_API_KEY"]);
        Assert.Equal(
            "legacy-value",
            ProjectSandboxSecretResolver.ResolveForScope(
                project, itemId, "rework", Environment.GetEnvironmentVariable)["OPENROUTER_API_KEY"]);
        Assert.Empty(ProjectSandboxSecretResolver.ResolveForScope(
            project, itemId, "audit-agent", Environment.GetEnvironmentVariable));
        Assert.Empty(ProjectSandboxSecretResolver.ResolveForScope(
            project, itemId, "merge", Environment.GetEnvironmentVariable));
    }

    // ── Config load ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigLoad_BindsGroupsAndGrants()
    {
        var itemId = Guid.NewGuid();
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_LLM",
                            SandboxEnvVar = "LLM_KEY",
                            Group = "llm",
                        },
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_ACME",
                            SandboxEnvVar = "ACME_TOKEN",
                            Group = "acme",
                            Phases = ["merge"],
                        },
                    ],
                    SecretGrants =
                    [
                        new ProjectSecretGrantConfig { Group = "llm" },
                        new ProjectSecretGrantConfig { Group = "acme", WorkItemId = itemId.ToString("N") },
                    ],
                },
            ],
        };

        var repo = new ProjectRepository(Options.Create(opts));
        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(2, project!.SandboxSecrets.Count);
        Assert.Equal("llm", project.SandboxSecrets[0].Group);
        Assert.Equal("acme", project.SandboxSecrets[1].Group);
        Assert.Equal(2, project.SandboxSecretGrants.Count);
        var wide = Assert.Single(project.SandboxSecretGrants, grant => grant.Group == "llm");
        Assert.True(wide.IsProjectWide);
        Assert.False(wide.IsImplicit);
        var narrow = Assert.Single(project.SandboxSecretGrants, grant => grant.Group == "acme");
        Assert.False(narrow.IsProjectWide);
        Assert.Equal(itemId, narrow.WorkItemId);

        // No implicit migration grant: this project declared explicit groups.
        Assert.DoesNotContain(project.SandboxSecretGrants, grant => grant.IsImplicit);
    }

    [Fact]
    public void ConfigLoad_RejectsInvalidGroupName()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "not a group!",
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigLoad_RejectsGrantForUndeclaredGroup()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "llm",
                        },
                    ],
                    SecretGrants =
                    [
                        new ProjectSecretGrantConfig { Group = "typo-group" },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("typo-group", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigLoad_RejectsGrantWithoutGroup()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "llm",
                        },
                    ],
                    SecretGrants =
                    [
                        new ProjectSecretGrantConfig { Group = null },
                    ],
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
    }

    [Fact]
    public void ConfigLoad_RejectsNonGuidGrantWorkItemId()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "llm",
                        },
                    ],
                    SecretGrants =
                    [
                        new ProjectSecretGrantConfig { Group = "llm", WorkItemId = "some-prompt-text" },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("WorkItemId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigLoad_RejectsDuplicateGrants()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "llm",
                        },
                    ],
                    SecretGrants =
                    [
                        new ProjectSecretGrantConfig { Group = "llm" },
                        new ProjectSecretGrantConfig { Group = "llm" },
                    ],
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
    }

    [Fact]
    public async Task ConfigLoad_UngrantedGroup_LoadsButInjectsNothing()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Group = "llm",
                        },
                    ],
                },
            ],
        };

        // Default deny is not a load error: the project loads, and the item
        // runs without the secret.
        var repo = new ProjectRepository(Options.Create(opts));
        var project = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Single(project!.SandboxSecrets);
        Assert.Empty(project.SandboxSecretGrants);
        using var _ = HostEnvScope.Set("HOST_KEY", "value");
        Assert.Empty(ProjectSandboxSecretResolver.ResolveForScope(
            project, WorkItemId.New(), "work", Environment.GetEnvironmentVariable));
    }
}
