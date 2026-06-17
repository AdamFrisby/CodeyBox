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
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:Auditors:0:Name"] = "custom:test-pass",
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:Auditors:0:Argv:0"] = "dotnet",
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:Auditors:0:Argv:1"] = "test",
                ["CodeyBox:Projects:0:Audit:AuditTypes:custom:Auditors:0:Role"] = "build-test-gate",
                ["CodeyBox:Projects:0:Audit:LlmPromptFrameTemplate"] = "{{reviewFocus}}\n{{resultFile}}",
            })
            .Build();

        var opts = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(["custom", "security"], p!.Audit.AuditTypes.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("project security focus", p.Audit.AuditTypeOverrides["security"].ReviewFocus);
        Assert.Equal("Custom review", p.Audit.AuditTypeOverrides["custom"].DisplayName);
        Assert.Equal("build-test-gate", p.Audit.AuditTypeOverrides["custom"].Auditors.Single().Role);
        Assert.Equal("{{reviewFocus}}\n{{resultFile}}", p.Audit.LlmPromptFrameTemplate);
    }

    [Fact]
    public async Task ProjectAuditProfiles_BindAndResolveSelectedProfile()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/x.git",
                ["CodeyBox:Projects:0:Audit:Profile"] = "uat",
                ["CodeyBox:Projects:0:Audit:MaxIterations"] = "9",
                ["CodeyBox:Projects:0:Audit:BuildScriptRequired"] = "true",
                ["CodeyBox:Projects:0:Audit:AuditTypes:0"] = "security",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:MaxIterations"] = "5",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:Languages:0"] = "csharp",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:AuditTypes:security:ReviewFocus"] = "uat security focus",
                ["CodeyBox:Projects:0:Audit:Profiles:uat:ExcludedAuditors:0"] = "cheating:llm-review",
            })
            .Build();

        var opts = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal("uat", p!.Audit.Profile);
        Assert.Equal(9, p.Audit.MaxIterations);
        Assert.True(p.Audit.BuildScriptRequired);

        var uat = p.Audit.ResolveProfile();
        Assert.Equal(5, uat.MaxIterations);
        Assert.True(uat.BuildScriptRequired);
        Assert.Equal(["csharp"], uat.Languages);
        Assert.Equal(["security"], uat.AuditTypes);
        Assert.Equal("uat security focus", uat.AuditTypeOverrides["security"].ReviewFocus);
        Assert.Equal(["cheating:llm-review"], uat.ExcludedAuditors);
    }

    [Fact]
    public async Task ProjectAuditComplexityIterationBudgets_BindAndOverrideDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Defaults:Audit:BudgetOverrideMaxIterations"] = "50",
                ["CodeyBox:Defaults:Audit:ComplexityIterationBudgets:hard"] = "20",
                ["CodeyBox:Defaults:Audit:ComplexityIterationBudgets:very-hard"] = "40",
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/x.git",
                ["CodeyBox:Projects:0:Audit:BudgetOverrideMaxIterations"] = "60",
                ["CodeyBox:Projects:0:Audit:ComplexityIterationBudgets:hard"] = "30",
            })
            .Build();

        var opts = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));

        Assert.Equal(30, p!.Audit.ComplexityIterationBudgets["hard"]);
        Assert.Equal(40, p.Audit.ComplexityIterationBudgets["very-hard"]);
        Assert.Equal(60, p.Audit.BudgetOverrideMaxIterations);
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
                        // The pre-merge CI gate reads this list — populate it
                        // here so the binding is locked in. A regression that
                        // dropped this field would leave operators with a
                        // gate silently disabled despite their config setting it.
                        PreMergeVerifyArgv = ["dotnet", "build", "--no-restore"],
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
        Assert.Equal(new[] { "dotnet", "build", "--no-restore" }, p.Upstream.PreMergeVerifyArgv);
    }

    [Fact]
    public async Task MaxPriority_LoadedFromConfig()
    {
        // Verifies the ProjectConfig.MaxPriority field binds through to
        // Project.MaxPriority via ProjectRepository.Resolve. Production
        // deployments configure the cap this way (YAML/JSON), not by building
        // Project instances in code.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://github.com/me/alpha.git",
                    MaxPriority = 200,
                },
                new ProjectConfig
                {
                    Id = "beta",
                    RepositoryUrl = "https://github.com/me/beta.git",
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var alpha = await repo.GetAsync(new ProjectId("alpha"));
        var beta = await repo.GetAsync(new ProjectId("beta"));
        Assert.Equal(200, alpha!.MaxPriority);
        Assert.Null(beta!.MaxPriority);
    }

    [Fact]
    public async Task MaxPriority_BindsThroughConfigurationBuilder()
    {
        // End-to-end: the configuration loader (the real path used by
        // appsettings.json deployments) populates ProjectConfig.MaxPriority,
        // and ProjectRepository forwards it to Project.MaxPriority.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://github.com/me/alpha.git",
                ["CodeyBox:Projects:0:MaxPriority"] = "750",
            })
            .Build();
        var bound = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        var repo = new ProjectRepository(Options.Create(bound));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Equal(750, p!.MaxPriority);
    }

    // --- noop + local-seed validator ------------------------------------------
    //
    // The validator refuses Upstream.Kind=noop + local RepositoryUrl unless the
    // operator explicitly acknowledges the isolated-sandbox semantics. These
    // tests cover the canonical failure modes that produced the original bug
    // (absolute local path, file:// URI) plus the two recovery paths
    // (acknowledge the isolation or configure a real upstream).

    [Theory]
    [InlineData("/home/operator/.codeybox/seeds/foo.git")]
    [InlineData("file:///srv/codeybox/seeds/foo.git")]
    public void Rejects_NoopUpstream_With_LocalRepositoryUrl(string localUrl)
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "isolated",
                    RepositoryUrl = localUrl,
                    // Explicit noop kind — the dangerous default combination.
                    Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                },
            ],
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProjectRepository(Options.Create(opts)));
        Assert.Contains("noop", ex.Message);
        Assert.Contains("AcknowledgeSandboxIsolation", ex.Message);
    }

    [Fact]
    public void Rejects_NoopUpstream_With_LocalRepositoryUrl_WhenUpstreamOmitted()
    {
        // ProjectUpstream defaults to Kind=noop when the Upstream section is
        // omitted entirely. The validator still fires — the failure mode is
        // identical whether the operator wrote "noop" explicitly or simply
        // left the section out.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "isolated",
                    RepositoryUrl = "/home/operator/.codeybox/seeds/foo.git",
                },
            ],
        };
        Assert.Throws<InvalidOperationException>(() =>
            new ProjectRepository(Options.Create(opts)));
    }

    [Fact]
    public async Task Allows_NoopUpstream_With_LocalRepositoryUrl_WhenAcknowledged()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "isolated",
                    RepositoryUrl = "/home/operator/.codeybox/seeds/foo.git",
                    Upstream = new ProjectUpstreamConfig
                    {
                        Kind = "noop",
                        AcknowledgeSandboxIsolation = true,
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("isolated"));
        Assert.NotNull(p);
        Assert.True(p!.Upstream.AcknowledgeSandboxIsolation);
    }

    [Fact]
    public async Task Allows_NoopUpstream_With_RemoteRepositoryUrl()
    {
        // A remote RepositoryUrl with Kind=noop is unusual but not the
        // shared-seed failure mode — pushes are skipped, but each work item
        // still clones a real remote. No acknowledgement required.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "remote-noop",
                    RepositoryUrl = "https://example.com/foo.git",
                    Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("remote-noop"));
        Assert.NotNull(p);
        Assert.Equal("noop", p!.Upstream.Kind);
    }

    [Fact]
    public async Task Allows_GitHubUpstream_With_LocalRepositoryUrl()
    {
        // The validator only fires for Kind=noop. A real upstream means
        // merged work flows back to a shared remote even when the seed
        // happens to live on the local filesystem.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "local-with-github",
                    RepositoryUrl = "/home/operator/.codeybox/seeds/foo.git",
                    Upstream = new ProjectUpstreamConfig
                    {
                        Kind = "github",
                        GitHubOwner = "me",
                        GitHubRepository = "foo",
                        TokenEnvVar = "GH_TOKEN",
                    },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("local-with-github"));
        Assert.NotNull(p);
        Assert.Equal("github", p!.Upstream.Kind);
    }

    [Fact]
    public async Task Reload_Rejects_NoopUpstream_With_LocalRepositoryUrl()
    {
        // Hot-reload regression guard: an appsettings.json edit that flips an
        // existing project into the dangerous combination must not silently
        // swap the snapshot. The reload exception is caught inside the
        // repository's OnChange callback, so the prior snapshot stays live;
        // the rejection is logged at ERROR for the operator to spot.
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                },
            ],
        };
        var monitor = new TestProjectsOptionsMonitor(initial);
        var logger = new CapturingLogger<ProjectRepository>();
        using var repo = new ProjectRepository(monitor, logger);

        // Flip the project into noop + local-seed. The reload candidate must
        // be rejected; the original snapshot remains.
        var bad = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "/home/operator/.codeybox/seeds/alpha.git",
                    Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                },
            ],
        };
        monitor.Push(bad);

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error &&
            e.Exception is InvalidOperationException);
        // The prior snapshot stayed live: alpha still resolves with its
        // original https RepositoryUrl, not the rejected local-seed candidate.
        var preserved = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(preserved);
        Assert.Equal("https://example.com/alpha.git", preserved!.RepositoryUrl);
    }

    [Fact]
    public async Task Reload_SpuriousOnChange_WithIdenticalOptions_DoesNotRebuildOrLog()
    {
        // ASP.NET Core's directory-level file watcher fires OnChange for any
        // sibling-file write in the watched config dir. With reloadOnChange:true
        // on codeybox-extra.json this routinely fans into hundreds of spurious
        // notifications per hour even when the JSON itself never changes. The
        // content-hash guard MUST short-circuit those duplicates: no snapshot
        // rebuild, no log noise, no transient-swap window for readers.
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    DisplayName = "Alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                },
            ],
        };
        var monitor = new TestProjectsOptionsMonitor(initial);
        var logger = new CapturingLogger<ProjectRepository>();
        using var repo = new ProjectRepository(monitor, logger);

        var snapshotBefore = await repo.GetAsync(new ProjectId("alpha"));
        var infoCountBefore = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information);

        // Push a freshly-constructed but semantically-identical ProjectsOptions
        // (object identity differs; serialized content is the same). Repeat
        // many times to simulate the production stampede.
        for (var i = 0; i < 50; i++)
        {
            monitor.Push(new ProjectsOptions
            {
                Projects =
                [
                    new ProjectConfig
                    {
                        Id = "alpha",
                        DisplayName = "Alpha",
                        RepositoryUrl = "https://example.com/alpha.git",
                    },
                ],
            });
        }

        var snapshotAfter = await repo.GetAsync(new ProjectId("alpha"));
        // Snapshot reference is unchanged because the no-op guard skipped the
        // Volatile.Write — readers never observe a transient-swap window.
        Assert.Same(snapshotBefore, snapshotAfter);

        // No new info-level reload log lines for the 50 spurious pushes.
        var infoCountAfter = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information);
        Assert.Equal(infoCountBefore, infoCountAfter);
    }

    [Fact]
    public async Task Reload_RealChange_TriggersExactlyOneRebuild()
    {
        // A real edit to the config (e.g. adding a project, changing
        // MaxLlmAuditorParallelism) must still produce exactly one effective
        // reload — hot-reload is relied on for operator stopgaps. Confirms
        // the content-hash guard does NOT swallow genuine changes.
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { MaxLlmAuditorParallelism = 3 },
                },
            ],
        };
        var monitor = new TestProjectsOptionsMonitor(initial);
        var logger = new CapturingLogger<ProjectRepository>();
        using var repo = new ProjectRepository(monitor, logger);

        var infoBefore = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information);

        // Real edit: bump MaxLlmAuditorParallelism. The new candidate hashes
        // differently from the initial, so the guard lets it through.
        var updated = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { MaxLlmAuditorParallelism = 7 },
                },
            ],
        };
        monitor.Push(updated);

        // Exactly one reload log entry produced by the real change.
        var reloadLines = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information &&
                        e.Message.StartsWith("ProjectRepository reloaded", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(reloadLines);
        Assert.Equal(infoBefore + 1, logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information));

        // And the new value took effect.
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.Equal(7, p!.Audit.MaxLlmAuditorParallelism);

        // A duplicate spurious event carrying the same (now-current) options
        // does NOT re-trigger another rebuild.
        monitor.Push(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { MaxLlmAuditorParallelism = 7 },
                },
            ],
        });
        var reloadLinesAfterDup = logger.Entries
            .Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information &&
                        e.Message.StartsWith("ProjectRepository reloaded", StringComparison.Ordinal));
        Assert.Equal(1, reloadLinesAfterDup);
    }

    [Fact]
    public async Task Reload_FailedRebuild_DoesNotRetryOnDuplicateSpuriousEvent()
    {
        // When a real edit produces an invalid candidate (e.g. flips into the
        // noop+local-seed combination) the Build throws and the prior snapshot
        // is preserved. A subsequent duplicate spurious OnChange carrying the
        // SAME bad candidate must NOT keep re-running the failing Build —
        // log noise is the symptom this whole change is fighting.
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                },
            ],
        };
        var monitor = new TestProjectsOptionsMonitor(initial);
        var logger = new CapturingLogger<ProjectRepository>();
        using var repo = new ProjectRepository(monitor, logger);

        var bad = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "/home/operator/.codeybox/seeds/alpha.git",
                    Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                },
            ],
        };
        monitor.Push(bad);

        // First push throws inside Build → exactly one ERROR entry.
        var errorCountAfterFirst = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Equal(1, errorCountAfterFirst);

        // Push the same bad options 20 more times (simulated spurious watcher
        // fan-out from a sibling-file write). No additional ERROR entries.
        for (var i = 0; i < 20; i++)
        {
            monitor.Push(new ProjectsOptions
            {
                Projects =
                [
                    new ProjectConfig
                    {
                        Id = "alpha",
                        RepositoryUrl = "/home/operator/.codeybox/seeds/alpha.git",
                        Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                    },
                ],
            });
        }
        var errorCountAfterDuplicates = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Equal(1, errorCountAfterDuplicates);

        // Prior snapshot is still live.
        var preserved = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(preserved);
        Assert.Equal("https://example.com/alpha.git", preserved!.RepositoryUrl);
    }

    [Theory]
    [InlineData("https://example.com/foo.git")]
    [InlineData("http://example.com/foo.git")]
    [InlineData("git://example.com/foo.git")]
    [InlineData("ssh://git@example.com/foo.git")]
    [InlineData("git@github.com:me/foo.git")]
    public async Task Allows_NoopUpstream_With_NetworkOrScpRepositoryUrl(string url)
    {
        // Every form that ValidateRepositoryUrl recognises as a network /
        // scp-style endpoint must be accepted with Kind=noop — the dangerous
        // combination is specifically noop + local-filesystem seed.
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "remote",
                    RepositoryUrl = url,
                    Upstream = new ProjectUpstreamConfig { Kind = "noop" },
                },
            ],
        };
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("remote"));
        Assert.NotNull(p);
        Assert.Equal("noop", p!.Upstream.Kind);
    }
}

/// <summary>
/// Minimal in-memory <see cref="IOptionsMonitor{T}"/> for ProjectsOptions hot
/// reload tests. Exercises the OnChange callback path inside
/// <see cref="ProjectRepository"/> without spinning up a real configuration
/// provider — the production guarantee being tested is "the reload validator
/// keeps the prior snapshot when the candidate is invalid", which depends on
/// OnChange being invoked, not on the source of the change.
/// </summary>
internal sealed class TestProjectsOptionsMonitor : IOptionsMonitor<ProjectsOptions>
{
    private ProjectsOptions _current;
    private readonly List<Action<ProjectsOptions, string?>> _listeners = new();

    public TestProjectsOptionsMonitor(ProjectsOptions initial)
    {
        _current = initial;
    }

    public ProjectsOptions CurrentValue => _current;
    public ProjectsOptions Get(string? name) => _current;

    public IDisposable OnChange(Action<ProjectsOptions, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    public void Push(ProjectsOptions next)
    {
        _current = next;
        foreach (var listener in _listeners.ToArray())
            listener(next, null);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() => _onDispose();
    }
}
