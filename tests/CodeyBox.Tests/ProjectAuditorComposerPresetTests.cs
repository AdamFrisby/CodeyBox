using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class ProjectAuditorComposerPresetTests
{
    [Fact]
    public void Compose_UsesRequestedNamedProfile()
    {
        var composer = new ProjectAuditorComposer(new NamedProfileCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["default-type"],
                Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uat"] = new() { AuditTypes = ["uat-type"] },
                },
            },
        };

        var auditors = composer.Compose(project, new CapturingAgent(), profile: "uat");

        Assert.Equal(["uat-type:auditor"], auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void IPresetCatalog_DefaultPlanFrameKeepsLegacyCatalogsSourceCompatible()
    {
        IPresetCatalog catalog = new LegacyCatalog();

        Assert.Equal(
            CodeyBox.Audit.Llm.LlmPromptFrameTemplate.DefaultPlanFrameTemplate,
            catalog.LlmPlanPromptFrameTemplate);
    }

    [Fact]
    public void Compose_UatPresetGoldenAuditorList()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Profile = AuditProfilePresets.Uat,
                Profiles = AuditProfilePresets.CreateBuiltIns(),
            },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(
            [
                "csharp:format-check",
                "csharp:build-WaE",
                "csharp:test-pass",
                "security:gitleaks",
                "security:semgrep",
                "security:llm-review",
                "cheating:deterministic-patterns",
            ],
            auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void ComposeForTarget_SelectsAuditorsByTarget_RespectingActiveSet()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        Project Project(params string[] excluded) => new()
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                // architecture opts into {Plan, Code}; security is Code-only.
                AuditTypes = ["architecture", "security"],
                ExcludedAuditors = excluded,
            },
        };

        var all = composer.Compose(Project(), new CapturingAgent()).Select(a => a.Name).ToArray();
        var codeTargets = composer.ComposeForTarget(Project(), new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name).ToArray();
        var planTargets = composer.ComposeForTarget(Project(), new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name).ToArray();

        // Code audit sees every auditor (all built-ins are Code or Plan+Code).
        Assert.Equal(all, codeTargets);
        Assert.Contains("security:llm-review", codeTargets);
        Assert.Contains("architecture:llm-review", codeTargets);

        // Plan review sees ONLY the plan-targeted reviewer.
        Assert.Equal(["architecture:llm-review"], planTargets);

        // The config-driven active set (ExcludedAuditors) is honoured per target.
        var planAfterExclusion = composer.ComposeForTarget(
                Project("architecture:llm-review"), new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name).ToArray();
        Assert.Empty(planAfterExclusion);
    }

    [Fact]
    public void ComposeForTarget_PlanIncludesBuiltInArchitectureCompletenessAndQuality()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["architecture", "completeness", "quality", "security"],
            },
        };

        var planTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.Contains("architecture:llm-review", planTargets);
        Assert.Contains("completeness:llm-review", planTargets);
        Assert.Contains("quality:llm-review", planTargets);
        Assert.DoesNotContain("security:llm-review", planTargets);
    }

    [Fact]
    public async Task BuiltInBothTargetAuditor_UsesPlanFocusedPromptForPlanTarget()
    {
        var runner = new CapturingAgent();
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["completeness"],
            },
        };
        var auditor = Assert.Single(composer.ComposeForTarget(project, runner, AuditTarget.Plan));

        await auditor.RunAsync(new ResultFileSandbox(), "/work", new AuditContext(
            WorkItemId.New(),
            WorkBranch: "main",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "add migration support",
            Target: AuditTarget.Plan,
            PlanArtifact: """{"approach":"plan the migration","files":["migrations/001.sql"],"testStrategy":["unit"],"risks":["rollback"],"satisfiesTask":"yes"}"""));

        Assert.Contains("COMPLETENESS at the PLAN stage", runner.Prompt, StringComparison.Ordinal);
        Assert.Contains("before implementation", runner.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("this diff", runner.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Read the task text and the diff together", runner.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("add migration support", runner.Prompt, StringComparison.Ordinal);
        Assert.Contains("add migration support", runner.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeForTarget_CustomLlmAuditorCanOptIntoPlanTarget()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Name = "custom:plan-review",
            Kind = "llm",
            ReviewFocus = "review the plan shape",
            Targets = ["plan"],
        });

        var codeTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        var planTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("custom:plan-review", codeTargets);
        Assert.Contains("custom:plan-review", planTargets);
    }

    [Fact]
    public void ComposeForTarget_AuditTypeTargetsApplyToShellPatternsAndLlmAuditors()
    {
        var catalog = new PresetCatalog(new PresetCatalogOptions
        {
            AuditTypeOverrides = new Dictionary<string, AuditTypePresetOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan-tools"] = new()
                {
                    Replace = true,
                    ReviewFocus = "review the plan",
                    Targets = ["plan"],
                    Auditors =
                    [
                        new ConfiguredAuditor
                        {
                            Name = "plan-tools:shell",
                            Argv = ["true"],
                        },
                    ],
                    Patterns =
                    [
                        new ConfiguredDiffPattern
                        {
                            Regex = "unsafe",
                            Description = "unsafe plan marker",
                        },
                    ],
                },
            },
        });
        var composer = new ProjectAuditorComposer(catalog);
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit { AuditTypes = ["plan-tools"] },
        };

        var codeTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        var planTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.Empty(codeTargets);
        Assert.Equal(
            ["plan-tools:shell", "plan-tools:deterministic-patterns", "plan-tools:llm-review"],
            planTargets);
    }

    [Fact]
    public void ComposeForTarget_PluginAuditorUsesItsOwnTargetsWhenDescriptorOmitsTargets()
    {
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [new PlanOnlyPluginAuditor()],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Kind = "plugin",
            PluginId = "test.plan-plugin-auditor",
        });

        var codeTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        var planTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("test:plan-plugin-auditor", codeTargets);
        Assert.Contains("test:plan-plugin-auditor", planTargets);
    }

    [Fact]
    public void ComposeForTarget_PluginDescriptorTargetsNarrowPluginTargets()
    {
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [new BothTargetsPluginAuditor()],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Kind = "plugin",
            PluginId = "test.both-targets-plugin-auditor",
            Targets = ["plan"],
        });

        var codeTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        var planTargets = composer.ComposeForTarget(project, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("test:both-targets-plugin-auditor", codeTargets);
        Assert.Contains("test:both-targets-plugin-auditor", planTargets);
    }

    [Fact]
    public async Task ComposeForTarget_ConfigBackedCustomAuditorSupportsFutureTarget()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Projects:0:Id"] = "alpha",
                ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/repo.git",
                ["CodeyBox:Projects:0:Audit:Custom:0:Name"] = "custom:migration-review",
                ["CodeyBox:Projects:0:Audit:Custom:0:Kind"] = "llm",
                ["CodeyBox:Projects:0:Audit:Custom:0:ReviewFocus"] = "review migration sequencing",
                ["CodeyBox:Projects:0:Audit:Custom:0:Targets:0"] = "Migration",
            })
            .Build();
        var options = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        using var repo = new ProjectRepository(Options.Create(options));
        var project = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(project);

        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var migrationTargets = composer.ComposeForTarget(project!, new CapturingAgent(), new AuditTarget("migration"))
            .Select(a => a.Name)
            .ToArray();
        var codeTargets = composer.ComposeForTarget(project!, new CapturingAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        var planTargets = composer.ComposeForTarget(project!, new CapturingAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.Equal(["custom:migration-review"], migrationTargets);
        Assert.DoesNotContain("custom:migration-review", codeTargets);
        Assert.DoesNotContain("custom:migration-review", planTargets);
    }

    [Fact]
    public async Task Compose_AppliesProjectAuditTypeFocusAndFrame()
    {
        var runner = new CapturingAgent();
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["completeness"],
                AuditTypeOverrides = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["completeness"] = new() { ReviewFocus = "project-specific completeness focus" },
                },
                LlmPromptFrameTemplate = "frame-start\n{{reviewFocus}}\n{{resultFile}}",
            },
        };

        var auditor = Assert.Single(composer.Compose(project, runner));
        await auditor.RunAsync(new ResultFileSandbox(), "/work", new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work"));

        Assert.Contains("frame-start", runner.Prompt, StringComparison.Ordinal);
        Assert.Contains("project-specific completeness focus", runner.Prompt, StringComparison.Ordinal);
        Assert.Contains("Tests which cannot be run in this environment are not part of the scoring or auditing criteria.", runner.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO / FIXME / XXX", runner.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_AppliesProjectLanguageOverride()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp"],
                LanguageOverrides = new Dictionary<string, ProjectLanguagePresetOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["csharp"] = new()
                    {
                        Replace = true,
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "csharp:project-test",
                                Argv = ["dotnet", "test"],
                                Role = "build-test-gate",
                                GateEvidence = "test",
                            },
                        ],
                    },
                },
            },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(["csharp:project-test"], auditors.Select(a => a.Name).ToArray());
        Assert.Equal(AuditorRole.BuildTestGate, auditors.Single().Role);
        Assert.Equal(BuildTestGateEvidence.Test, auditors.Single().BuildTestGateEvidence);
    }

    [Fact]
    public async Task Compose_AppliesProjectAuditTypeAuditorMissingToolSeverityAndCapabilities()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["security"],
                AuditTypeOverrides = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["security"] = new()
                    {
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "security:project-semgrep",
                                Argv = ["semgrep", "--version"],
                                MissingToolSeverity = "warning",
                                RequiredCapabilities = ["network"],
                            },
                        ],
                    },
                },
            },
        };

        var auditor = composer.Compose(project, new CapturingAgent())
            .Single(a => a.Name == "security:project-semgrep");

        Assert.Equal(AuditCapabilities.Network, auditor.Required);
        var result = await auditor.RunAsync(new MissingToolSandbox(), "/work", new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("tool not installed in sandbox: semgrep", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ProjectLanguageOverrideRejectsInvalidTrustedRole()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp"],
                LanguageOverrides = new Dictionary<string, ProjectLanguagePresetOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["csharp"] = new()
                    {
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "csharp:project-test",
                                Argv = ["dotnet", "test"],
                                Role = "ci-gate",
                            },
                        ],
                    },
                },
            },
        };

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("not a recognised auditor role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ProjectAuditTypeOverrideRejectsInvalidTrustedRole()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["custom"],
                AuditTypeOverrides = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["custom"] = new()
                    {
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "custom:test-pass",
                                Argv = ["dotnet", "test"],
                                Role = "ci-gate",
                            },
                        ],
                    },
                },
            },
        };

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("not a recognised auditor role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CustomShellAuditorCanOptIntoBuildTestGate()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Name = "custom:test-pass",
                        Kind = "shell",
                        Argv = ["dotnet", "test"],
                        Role = "build-test-gate",
                        GateEvidence = "test",
                    },
                ],
            },
        };

        var auditor = composer.Compose(project, new CapturingAgent())
            .Single(a => a.Name == "custom:test-pass");

        Assert.Equal(AuditorRole.BuildTestGate, auditor.Role);
        Assert.Equal(BuildTestGateEvidence.Test, auditor.BuildTestGateEvidence);
    }

    [Fact]
    public void Compose_CustomShellBuildTestGateWithoutEvidenceContributesNoEvidence()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Name = "custom:ci-pass",
                        Kind = "shell",
                        Argv = ["./ci.sh"],
                        Role = "build-test-gate",
                    },
                ],
            },
        };

        var auditor = composer.Compose(project, new CapturingAgent())
            .Single(a => a.Name == "custom:ci-pass");

        Assert.Equal(AuditorRole.BuildTestGate, auditor.Role);
        Assert.Equal(BuildTestGateEvidence.None, auditor.BuildTestGateEvidence);
    }

    [Fact]
    public void Compose_CustomGateEvidenceWithoutRoleIsRejected()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Name = "custom:test-pass",
            Kind = "shell",
            Argv = ["dotnet", "test"],
            GateEvidence = "test",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("sets GateEvidence but is not a build-test-gate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CustomUnsupportedRoleIsRejected()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Name = "custom:test-pass",
            Kind = "shell",
            Argv = ["dotnet", "test"],
            Role = "ci-gate",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("unsupported Role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CustomInvalidGateEvidenceIsRejected()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Name = "custom:test-pass",
            Kind = "shell",
            Argv = ["dotnet", "test"],
            Role = "build-test-gate",
            GateEvidence = "tests",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("unsupported GateEvidence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CustomGateMetadataOnNonShellAuditorIsRejected()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Name = "custom:pattern",
            Kind = "diff-pattern",
            Role = "build-test-gate",
            Patterns =
            [
                new DiffPatternDescriptor
                {
                    Regex = "TODO",
                    Description = "No TODOs",
                },
            ],
        });

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("supported only for custom shell auditors", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build-test-gate", null)]
    [InlineData(null, "test")]
    public void Compose_CustomPluginAuditorWithGateMetadataIsRejected(
        string? role,
        string? gateEvidence)
    {
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [new TestPluginAuditor()],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = ProjectWithCustom(new CustomAuditorDescriptor
        {
            Kind = "plugin",
            PluginId = "test.plugin-auditor",
            Role = role,
            GateEvidence = gateEvidence,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("Custom plugin auditor 'test.plugin-auditor' cannot set Role or GateEvidence", ex.Message, StringComparison.Ordinal);
        Assert.Contains("supported only for custom shell auditors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_LoadsLanguagePresetFromLocalRepository()
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

        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = new Uri(temp.Path).AbsoluteUri,
            Audit = new ProjectAudit { Languages = ["elixir"] },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(["elixir:test-pass"], auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void Compose_RepositoryLanguagePresetCannotOverrideMarkerForTrustedGateLanguage()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "csharp.yaml"), """
            id: csharp
            marker:
              globs: ["fake/**/*.csproj"]
            """);

        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = new Uri(temp.Path).AbsoluteUri,
            Audit = new ProjectAudit { Languages = ["csharp"] },
        };

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("cannot override /marker", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingAgent : IAgentRunner, ITextOnlyAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string Prompt { get; private set; } = string.Empty;
        public string UserPrompt { get; private set; } = string.Empty;
        public bool SupportsSeparateSystemPrompt => true;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            Prompt = prompt;
            return Task.FromResult(new AgentResult(true, "ok", "review complete", null));
        }

        public Task<TextOnlyAgentResult> RunTextOnlyAsync(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
        {
            _ = credential;
            _ = modelId;
            _ = reasoningMode;
            _ = sandbox;
            _ = workingDirectory;
            ct.ThrowIfCancellationRequested();
            Prompt = prompt;
            return Task.FromResult(new TextOnlyAgentResult(true, "ok", """{"passed":true,"findings":[]}""", null));
        }

        public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
            string systemPrompt,
            string userPrompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
        {
            _ = credential;
            _ = modelId;
            _ = reasoningMode;
            _ = sandbox;
            _ = workingDirectory;
            ct.ThrowIfCancellationRequested();
            Prompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(new TextOnlyAgentResult(true, "ok", """{"passed":true,"findings":[]}""", null));
        }
    }

    private static Project ProjectWithCustom(params CustomAuditorDescriptor[] custom) => new()
    {
        Id = new ProjectId("alpha"),
        DisplayName = "Alpha",
        RepositoryUrl = "https://example.com/repo.git",
        Audit = new ProjectAudit { Custom = custom },
    };

    private sealed class NamedProfileCatalog : IPresetCatalog
    {
        public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx) => [];
        public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx) => [new NamedAuditor($"{name}:auditor")];
        public IReadOnlyList<string> KnownLanguages => [];
        public IReadOnlyList<string> KnownAuditTypes => ["default-type", "uat-type"];
        public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{resultFile}}";
        public string LlmPlanPromptFrameTemplate => CodeyBox.Audit.Llm.LlmPromptFrameTemplate.DefaultPlanFrameTemplate;
    }

    private sealed class NamedAuditor(string name) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    [CodeyBoxPlugin("test.plugin-auditor", "Test Plugin Auditor")]
    private sealed class TestPluginAuditor : IAuditor
    {
        public string Name => "test:plugin-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    [CodeyBoxPlugin("test.plan-plugin-auditor", "Plan Plugin Auditor")]
    private sealed class PlanOnlyPluginAuditor : IAuditor
    {
        public string Name => "test:plan-plugin-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    [CodeyBoxPlugin("test.both-targets-plugin-auditor", "Both Targets Plugin Auditor")]
    private sealed class BothTargetsPluginAuditor : IAuditor
    {
        public string Name => "test:both-targets-plugin-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanAndCode;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class LegacyCatalog : IPresetCatalog
    {
        public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx) => [];
        public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx) => [];
        public IReadOnlyList<string> KnownLanguages => [];
        public IReadOnlyList<string> KnownAuditTypes => [];
        public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{resultFile}}";
    }

    private sealed class ResultFileSandbox : ISandbox
    {
        public string Id => "result-file-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, "{\"passed\":true,\"findings\":[]}", ""));

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MissingToolSandbox : ISandbox
    {
        public string Id => "missing-tool";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(1, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
