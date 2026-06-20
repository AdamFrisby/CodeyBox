using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class MechanicalFixerTests : IDisposable
{
    private readonly string _workspace;

    public MechanicalFixerTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-mechanical-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ProjectRepository_DerivesDotnetFormat_ForCSharpLanguage()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Languages = ["csharp"],
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName], project!.Audit.MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ExplicitEmptyMechanicalFixers_DisablesLanguageDefault()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Languages = ["csharp"],
                        MechanicalFixers = [],
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Empty(project!.Audit.MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_BuiltInUatProfile_DerivesDotnetFormat()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profile = AuditProfilePresets.Uat,
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName],
            project!.Audit.ResolveProfile(AuditProfilePresets.Uat).MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_CustomCSharpProfile_DerivesDotnetFormat()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ci"] = new()
                            {
                                Languages = ["csharp"],
                            },
                        },
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName],
            project!.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ProfileExplicitEmptyMechanicalFixers_DisablesLanguageDefault()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ci"] = new()
                            {
                                Languages = ["csharp"],
                                MechanicalFixers = [],
                            },
                        },
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Empty(project!.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_BoundDefaultsExplicitEmptyMechanicalFixers_DisablesInheritedCSharpProfile()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "CodeyBox": {
                "Defaults": {
                  "Audit": {
                    "Languages": [ "csharp" ],
                    "MechanicalFixers": []
                  }
                },
                "Projects": [
                  {
                    "Id": "p",
                    "RepositoryUrl": "https://example.invalid/repo.git",
                    "Audit": {
                      "Profile": "ci",
                      "Profiles": {
                        "ci": { "Languages": [ "csharp" ] }
                      }
                    }
                  }
                ]
              }
            }
            """));
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        var repo = new ProjectRepository(Options.Create(ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"))));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Empty(project!.Audit.MechanicalFixers);
        Assert.Empty(project.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_BoundDefaultsMechanicalFixersAreInheritedByProjectAndProfile()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "CodeyBox": {
                "Defaults": {
                  "Audit": {
                    "Languages": [ "csharp" ],
                    "MechanicalFixers": [ " custom-normalizer ", "dotnet-format", " " ]
                  }
                },
                "Projects": [
                  {
                    "Id": "p",
                    "RepositoryUrl": "https://example.invalid/repo.git",
                    "Audit": {
                      "Profile": "ci",
                      "Profiles": {
                        "ci": { "Languages": [ "csharp" ] }
                      }
                    }
                  }
                ]
              }
            }
            """));
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        var repo = new ProjectRepository(Options.Create(ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"))));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal(["custom-normalizer", "dotnet-format"], project!.Audit.MechanicalFixers);
        Assert.Equal(["custom-normalizer", "dotnet-format"], project.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_BoundProfileMechanicalFixersPreservesConfiguredAndExplicitEmptyLists()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "CodeyBox": {
                "Projects": [
                  {
                    "Id": "p",
                    "RepositoryUrl": "https://example.invalid/repo.git",
                    "Audit": {
                      "Profiles": {
                        "ci": {
                          "Languages": [ "csharp" ],
                          "MechanicalFixers": [ " dotnet-format ", " custom-normalizer ", " " ]
                        },
                        "manual": {
                          "Languages": [ "csharp" ],
                          "MechanicalFixers": []
                        }
                      }
                    }
                  }
                ]
              }
            }
            """));
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        var repo = new ProjectRepository(Options.Create(ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"))));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal(["dotnet-format", "custom-normalizer"], project!.Audit.ResolveProfile("ci").MechanicalFixers);
        Assert.Empty(project.Audit.ResolveProfile("manual").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ProfileLanguagesDeriveMechanicalFixersWithoutLeakingFallbackCSharpDefault()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Languages = ["csharp"],
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ci"] = new()
                            {
                                Languages = ["python"],
                            },
                        },
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName], project!.Audit.MechanicalFixers);
        Assert.Empty(project.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ExplicitMechanicalFixersAreTrimmedPreservedAndHotReloaded()
    {
        var monitor = new TestProjectsOptionsMonitor(BindProjectsOptions(
            """
            {
              "CodeyBox": {
                "Projects": [
                  {
                    "Id": "p",
                    "RepositoryUrl": "https://example.invalid/repo.git",
                    "Audit": {
                      "Languages": [ "csharp" ],
                      "MechanicalFixers": [ " dotnet-format ", " custom-normalizer ", " " ]
                    }
                  }
                ]
              }
            }
            """));
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);

        var before = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal(["dotnet-format", "custom-normalizer"], before!.Audit.MechanicalFixers);

        monitor.Push(BindProjectsOptions(
            """
            {
              "CodeyBox": {
                "Projects": [
                  {
                    "Id": "p",
                    "RepositoryUrl": "https://example.invalid/repo.git",
                    "Audit": {
                      "Languages": [ "csharp" ],
                      "MechanicalFixers": [ "other-normalizer" ]
                    }
                  }
                ]
              }
            }
            """));

        var after = await repo.GetAsync(new ProjectId("p"));
        Assert.Equal(["other-normalizer"], after!.Audit.MechanicalFixers);
    }

    [Fact]
    public void ProjectMechanicalFixerComposer_TrimsDedupesAndUsesSelectedProfile()
    {
        var fixer = new NoOpMechanicalFixer();
        var composer = ProjectMechanicalFixerComposer.FromFixers([fixer]);
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "p",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                MechanicalFixers = [" other "],
                Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ci"] = new()
                    {
                        MechanicalFixers = [" ", $" {NoOpMechanicalFixer.FixerName} ", NoOpMechanicalFixer.FixerName],
                    },
                },
            },
        };

        var fixers = composer.Compose(project, "ci");

        Assert.Same(fixer, Assert.Single(fixers));
    }

    [Fact]
    public void ProjectMechanicalFixerComposer_UnknownFixerThrows()
    {
        var composer = ProjectMechanicalFixerComposer.FromFixers([]);
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "p",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                MechanicalFixers = ["missing-normalizer"],
            },
        };

        var ex = Assert.Throws<ProjectMechanicalFixerConfigurationException>(() => composer.Compose(project));

        Assert.Contains("missing-normalizer", ex.Message);
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void DotnetFormatFixer_ReusesFormatCheckCommandWithoutReadOnlyFlags()
    {
        var argv = DotnetFormatMechanicalFixer.ToFixerArgv(
            ["dotnet", "format", "--verify-no-changes", "--report", "/tmp/report", "--report=/tmp/other-report", "--no-restore"]);

        Assert.Equal(["dotnet", "format", "--no-restore"], argv);
    }

    [Fact]
    public void DotnetFormatFixer_ReusesAbsoluteDotnetPathWithoutReadOnlyFlags()
    {
        var argv = DotnetFormatMechanicalFixer.ToFixerArgv(
            ["/usr/share/dotnet/dotnet", "format", "--verify-no-changes", "--report", "/tmp/report", "--no-restore"]);

        Assert.Equal(["/usr/share/dotnet/dotnet", "format", "--no-restore"], argv);
    }

    [Fact]
    public void DotnetFormatFixer_ReusesShellScriptFormatCheckCommand()
    {
        var argv = DotnetFormatMechanicalFixer.ToFixerArgv(
        [
            "sh",
            "-c",
            "export DOTNET_ROOT=/opt/dotnet; DOTNET_CLI_HOME=/tmp/dotnet dotnet format --verify-no-changes --report /tmp/report --verbosity diagnostic",
        ]);

        Assert.Equal("sh", argv[0]);
        Assert.Equal("-c", argv[1]);
        Assert.Contains("export DOTNET_ROOT=/opt/dotnet", argv[2]);
        Assert.Contains("DOTNET_CLI_HOME=/tmp/dotnet dotnet format --verify-no-changes --report /tmp/report --verbosity diagnostic", argv[2]);
        Assert.Contains("xargs -0 dotnet", argv[2]);
    }

    [Fact]
    public void DotnetFormatFixer_DoesNotTreatEchoedShellTextAsFormatCommand()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DotnetFormatMechanicalFixer.ToFixerArgv(
            [
                "sh",
                "-c",
                "echo dotnet format --verify-no-changes; ./dangerous-format-wrapper --verify-no-changes",
            ]));

        Assert.Contains("dotnet format", ex.Message);
    }

    [Fact]
    public void DotnetFormatFixer_ToFixerArgvThrowsForShellWrapperScript()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DotnetFormatMechanicalFixer.ToFixerArgv(
            [
                "bash",
                "-lc",
                "export DOTNET_ROOT=/opt/dotnet; dotnet format --verify-no-changes --report /tmp/report --verbosity diagnostic",
            ]));

        Assert.Contains("must invoke 'dotnet format' directly", ex.Message);
    }

    [Fact]
    public void DotnetFormatFixer_ToFixerArgvThrowsForNonDotnetFormatCommand()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DotnetFormatMechanicalFixer.ToFixerArgv(["dotnet", "build", "--no-restore"]));

        Assert.Contains("csharp:format-check", ex.Message);
        Assert.Contains("dotnet format", ex.Message);
        Assert.Contains("dotnet-format", ex.Message);
    }

    [Fact]
    public void DotnetFormatFixer_ToFixerArgvThrowsForShellWrapperThatDoesNotInvokeDotnetFormat()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DotnetFormatMechanicalFixer.ToFixerArgv([
                "bash",
                "-lc",
                "echo dotnet format --verify-no-changes; ./dangerous-format-wrapper --verify-no-changes",
            ]));

        Assert.Contains("dotnet format", ex.Message);
    }

    [Fact]
    public async Task DotnetFormatFixer_ApplyRunsScriptBackedFormatAuditor()
    {
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\n",
            statusOutputs: ["", " M src/App/Program.cs\0"],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                [new DotnetFormatMechanicalFixerInput(
                    [
                        "sh",
                        "-c",
                        "export DOTNET_ROOT=/opt/dotnet; dotnet format --verify-no-changes --report /tmp/report --verbosity diagnostic",
                    ],
                    ".")]));

        Assert.True(result.Changed);
        var formatExec = Assert.Single(sandbox.Execs, e =>
            e.Argv.Count == 3 &&
            e.Argv[0] == "sh" &&
            e.Argv[1] == "-c" &&
            e.Argv[2].Contains("dotnet format --verify-no-changes", StringComparison.Ordinal));
        Assert.Contains("export DOTNET_ROOT=/opt/dotnet", formatExec.Argv[2]);
        Assert.Equal("/work/repo/src/App", formatExec.WorkingDirectory);
    }

    [Fact]
    public async Task DotnetFormatFixer_ApplySkipsUnsupportedFormatCheckCommand()
    {
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                [new DotnetFormatMechanicalFixerInput(
                    ["./format-wrapper", "--verify-no-changes", "--report", "/tmp/report", "--sdk", "10.0.109"],
                    ".")]));

        Assert.False(result.Changed);
        Assert.Contains("does not expose a writable dotnet-format command", result.Summary);
        Assert.Empty(sandbox.Execs);
    }

    [Fact]
    public void DotnetFormatInputProvider_RejectsLookalikeAuditors()
    {
        var provider = new DotnetFormatMechanicalFixerInputProvider();

        Assert.Empty(provider.BuildInputs(
            [CreateLanguagePresetAuditor("csharp", new NamedShellAuditor("csharp:build", ["dotnet", "build"]))]));
        Assert.Empty(provider.BuildInputs(
            [new NamedAuditor("csharp:format-check")]));
        Assert.Empty(provider.BuildInputs(
            [CreateLanguagePresetAuditor("python", new NamedShellAuditor("csharp:format-check", ["dotnet", "format", "--verify-no-changes"]))]));
        Assert.Empty(provider.BuildInputs(
            [CreateLanguagePresetAuditor("csharp", new NamedAuditor("csharp:format-check"))]));
    }

    [Fact]
    public void DotnetFormatInputProvider_AcceptsCustomCsharpFormatCheckShellAuditorWithDefaultMarker()
    {
        var provider = new DotnetFormatMechanicalFixerInputProvider();

        var inputs = provider.BuildInputs(
            [new NamedShellAuditor("csharp:format-check", ["dotnet", "format", "--verify-no-changes"])]);
        var input = Assert.IsType<DotnetFormatMechanicalFixerInput>(Assert.Single(inputs));
        Assert.Equal(["dotnet", "format", "--verify-no-changes"], input.FormatCheckArgv);
        Assert.Equal(DotnetFormatMechanicalFixerInputProvider.DefaultCsharpMarkerScript, input.ProjectMarkerScript);
    }

    [Fact]
    public async Task DotnetFormatFixer_UsesActiveCSharpFormatAuditorAndDetectsTrackedChanges()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\n",
            statusOutputs: ["", " M src/App/Program.cs\0"],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.True(result.Changed);
        var formatExec = Assert.Single(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
        Assert.Equal(["dotnet", "format", "--verbosity", "diagnostic"], formatExec.Argv);
        Assert.Equal("/work/repo/src/App", formatExec.WorkingDirectory);
        Assert.All(
            sandbox.Execs.Where(e => e.Argv.Count >= 2 && e.Argv[0] == "git" && e.Argv.Contains("status")),
            e => Assert.Contains("--untracked-files=no", e.Argv));
    }

    [Fact]
    public async Task DotnetFormatFixer_FormatsAllSelectedCSharpProjectDirectories()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\nsrc/Lib\n",
            statusOutputs: ["", " M src/App/Program.cs\0 M src/Lib/Program.cs\0"],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.True(result.Changed);
        var formatWorkingDirectories = sandbox.Execs
            .Where(e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format")
            .Select(e => e.WorkingDirectory ?? string.Empty)
            .ToArray();
        Assert.Equal(["/work/repo/src/App", "/work/repo/src/Lib"], formatWorkingDirectories);
    }

    [Fact]
    public async Task DotnetFormatFixer_SuccessWithoutTrackedChangesReportsNoOp()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: ["", ""],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Equal("dotnet format made no changes", result.Summary);
    }

    [Fact]
    public async Task DotnetFormatFixer_DetectsFormatterChangesWhenGitStatusIsStable()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string stableStatus = " M src/App/Program.cs\0";
        const string beforePatch =
            """
            diff --git a/src/App/Program.cs b/src/App/Program.cs
            index 1111111111111111111111111111111111111111..2222222222222222222222222222222222222222 100644
            --- a/src/App/Program.cs
            +++ b/src/App/Program.cs
            @@ -1 +1 @@
            -Console.WriteLine(1);
            +Console.WriteLine( 1 );
            """;
        const string afterPatch =
            """
            diff --git a/src/App/Program.cs b/src/App/Program.cs
            index 1111111111111111111111111111111111111111..3333333333333333333333333333333333333333 100644
            --- a/src/App/Program.cs
            +++ b/src/App/Program.cs
            @@ -1 +1 @@
            -Console.WriteLine(1);
            +Console.WriteLine(1);
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\n",
            statusOutputs: [stableStatus, stableStatus],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            diffOutputs: [beforePatch, beforePatch, afterPatch]);

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.True(result.Changed);
        Assert.Contains("normalized 1 C# project directory", result.Summary);
        Assert.Equal(3, sandbox.Execs.Count(e =>
            e.Argv.SequenceEqual(["git", "-C", "/work/repo", "diff", "--binary", "--full-index", "HEAD", "--"])));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureRollsBackAndLetsAuditReport()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(2, "", "format failed"));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains("skipped normalization", result.Summary);
        Assert.Contains("format failed", result.RawOutput);
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandExecutionUnavailableThrowsInfrastructureFailure()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(
                0,
                "",
                "sandbox provider unavailable",
                ExecutionUnavailable: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("could not execute formatter command", ex.Message);
        Assert.Contains("sandbox provider unavailable", ex.Message);
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
        Assert.Single(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureRestoresPreExistingTrackedDiff()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string previousFixerPatch =
            """
            diff --git a/normalizer.txt b/normalizer.txt
            index e69de29bb2d1d6434b8b29ae775ad8c2e48c5391..56a6051ca2b02b04ef92d5150c9ef600403cb1de 100644
            --- a/normalizer.txt
            +++ b/normalizer.txt
            @@ -0,0 +1 @@
            +1
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [" M normalizer.txt\0"],
            formatResult: new SandboxExecResult(2, "", "format failed"),
            diffOutputs: [previousFixerPatch, previousFixerPatch]);

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
        Assert.Equal(previousFixerPatch, Assert.Single(sandbox.WrittenPatches));
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual([
            "git",
            "-C",
            "/work/repo",
            "apply",
            "--whitespace=nowarn",
            "/tmp/codeybox-dotnet-format-before.patch",
        ]));
    }

    [Fact]
    public async Task DotnetFormatFixer_PartialMultiProjectFailureRestoresPreExistingTrackedDiff()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string previousFixerPatch =
            """
            diff --git a/normalizer.txt b/normalizer.txt
            index e69de29bb2d1d6434b8b29ae775ad8c2e48c5391..56a6051ca2b02b04ef92d5150c9ef600403cb1de 100644
            --- a/normalizer.txt
            +++ b/normalizer.txt
            @@ -0,0 +1 @@
            +1
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\nsrc/Lib\n",
            statusOutputs: [" M normalizer.txt\0"],
            formatResult: new SandboxExecResult(0, "formatted app", ""),
            diffOutputs: [previousFixerPatch, previousFixerPatch],
            formatResults:
            [
                new SandboxExecResult(0, "formatted app", ""),
                new SandboxExecResult(2, "", "format lib failed"),
            ]);

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains("src/Lib", result.Summary);
        Assert.Contains("format lib failed", result.RawOutput);
        Assert.Equal(
            ["/work/repo/src/App", "/work/repo/src/Lib"],
            sandbox.Execs
                .Where(e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format")
                .Select(e => e.WorkingDirectory ?? string.Empty)
                .ToArray());
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
        Assert.Equal(previousFixerPatch, Assert.Single(sandbox.WrittenPatches));
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual([
            "git",
            "-C",
            "/work/repo",
            "apply",
            "--whitespace=nowarn",
            "/tmp/codeybox-dotnet-format-before.patch",
        ]));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureRestoresPreExistingTrackedDiffInRealRepo()
    {
        var repo = await TestSupport.CreateSeedRepoAsync(_workspace, "formatter-rollback");
        await AddTrackedFileAsync(repo, "normalizer.txt", "base\n");
        await AddTrackedFileAsync(repo, "formatted.txt", "clean\n");

        await File.WriteAllTextAsync(Path.Combine(repo, "normalizer.txt"), "base\nprevious fixer edit\n");

        var bin = Path.Combine(_workspace, "fake-dotnet-bin");
        Directory.CreateDirectory(bin);
        var fakeDotnet = Path.Combine(bin, "dotnet");
        await File.WriteAllTextAsync(fakeDotnet,
            """
            #!/usr/bin/env sh
            printf 'partial formatter output\n' > formatted.txt
            exit 2
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sandbox = new LocalProcessSandbox(new Dictionary<string, string>
        {
            ["PATH"] = bin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
        });

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            repo,
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                [new DotnetFormatMechanicalFixerInput(["dotnet", "format", "--verify-no-changes"], "printf '.\n'")]));

        Assert.False(result.Changed);
        Assert.Contains("skipped normalization", result.Summary);
        Assert.Equal("base\nprevious fixer edit\n", await File.ReadAllTextAsync(Path.Combine(repo, "normalizer.txt")));
        Assert.Equal("clean\n", await File.ReadAllTextAsync(Path.Combine(repo, "formatted.txt")));

        var (_, diff, _) = await TestSupport.RunGit(repo, "diff", "--", "formatted.txt");
        Assert.Equal(string.Empty, diff);
    }

    [Fact]
    public async Task DotnetFormatFixer_CapsFormatterOutputAndTreatsTruncatedZeroExitAsSuccess()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: ["", ""],
            formatResult: new SandboxExecResult(
                0,
                "formatted",
                "",
                StdoutLimitExceeded: true));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Equal("dotnet format made no changes", result.Summary);
        Assert.Contains("stdout truncated", result.RawOutput);
        var formatExec = Assert.Single(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
        Assert.Equal(DotnetFormatMechanicalFixer.OutputCaptureMaxBytes, formatExec.MaxStdoutBytes);
        Assert.Equal(DotnetFormatMechanicalFixer.OutputCaptureMaxBytes, formatExec.MaxStderrBytes);
        Assert.False(formatExec.KillOnOutputLimit);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureRawOutputIsBounded()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(2, new string('x', 100_000), ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.NotNull(result.RawOutput);
        Assert.True(result.RawOutput!.Length < 20_000, $"raw output was {result.RawOutput.Length} chars");
        Assert.Contains("output truncated", result.RawOutput);
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureThrowsWhenRollbackFails()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(2, "", "format failed"),
            resetResult: new SandboxExecResult(128, "", "reset failed\nwith details"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("could not discard partial changes", ex.Message);
        Assert.Contains("reset failed with details", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureThrowsWhenRollbackPatchWriteFails()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string previousFixerPatch =
            """
            diff --git a/normalizer.txt b/normalizer.txt
            index e69de29bb2d1d6434b8b29ae775ad8c2e48c5391..56a6051ca2b02b04ef92d5150c9ef600403cb1de 100644
            --- a/normalizer.txt
            +++ b/normalizer.txt
            @@ -0,0 +1 @@
            +1
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [" M normalizer.txt\0"],
            formatResult: new SandboxExecResult(2, "", "format failed"),
            diffOutputs: [previousFixerPatch, previousFixerPatch],
            patchWriteResult: new SandboxExecResult(1, "", "write failed\nwith details"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("could not materialize pre-existing changes", ex.Message);
        Assert.Contains("write failed with details", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Equal(previousFixerPatch, Assert.Single(sandbox.WrittenPatches));
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.SequenceEqual([
            "git",
            "-C",
            "/work/repo",
            "apply",
            "--whitespace=nowarn",
            "/tmp/codeybox-dotnet-format-before.patch",
        ]));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureThrowsWhenRollbackPatchApplyFails()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string previousFixerPatch =
            """
            diff --git a/normalizer.txt b/normalizer.txt
            index e69de29bb2d1d6434b8b29ae775ad8c2e48c5391..56a6051ca2b02b04ef92d5150c9ef600403cb1de 100644
            --- a/normalizer.txt
            +++ b/normalizer.txt
            @@ -0,0 +1 @@
            +1
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [" M normalizer.txt\0"],
            formatResult: new SandboxExecResult(2, "", "format failed"),
            diffOutputs: [previousFixerPatch, previousFixerPatch],
            applyResult: new SandboxExecResult(1, "", "apply failed\nwith details"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("could not restore pre-existing changes", ex.Message);
        Assert.Contains("apply failed with details", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Equal(previousFixerPatch, Assert.Single(sandbox.WrittenPatches));
    }

    [Fact]
    public async Task DotnetFormatFixer_InactiveFormatAuditorSkips()
    {
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project"));

        Assert.False(result.Changed);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_MarkerlessRepositorySkips()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Equal("no C# project marker found; dotnet format skipped", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_ProjectDirectoryCapSkipsForAuditorFinding()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var markers = string.Join('\n', Enumerable.Range(0, LanguageProjectDiscovery.MaxProjectDirectoriesToRun + 1)
            .Select(i => $"src/P{i}")) + "\n";
        var sandbox = new DotnetFormatSandbox(
            markerStdout: markers,
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains("directory cap", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_DiscoveryFailureSkipsForAuditorFinding()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            markerResult: new SandboxExecResult(1, "", "find failed"));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains("marker discovery exited 1", result.Summary);
        Assert.Contains("find failed", result.RawOutput);
    }

    [Fact]
    public async Task DotnetFormatFixer_MarkerDiscoveryTrackedSideEffectIsRolledBackAndFails()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        const string markerSideEffectPatch =
            """
            diff --git a/marker.txt b/marker.txt
            index e69de29bb2d1d6434b8b29ae775ad8c2e48c5391..d00491fd7e5bb6fa28c517a0bb32b8b506539d4d 100644
            --- a/marker.txt
            +++ b/marker.txt
            @@ -0,0 +1 @@
            +marker side effect
            """;
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: ["", " M marker.txt\0"],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            diffOutputs: ["", markerSideEffectPatch]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("marker discovery modified tracked files", ex.Message);
        Assert.Contains(sandbox.Execs, e => e.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]));
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_DiscoveryOutputLimitSkipsForAuditorFinding()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            markerResult: new SandboxExecResult(
                0,
                "partial marker output",
                "partial marker stderr",
                StdoutLimitExceeded: true));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                InputsFor(auditor)));

        Assert.False(result.Changed);
        Assert.Contains("marker discovery exceeded", result.Summary);
        Assert.Contains("partial marker output", result.RawOutput);
        Assert.Contains("stdout truncated", result.RawOutput);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Theory]
    [InlineData("/tmp/outside")]
    [InlineData("../outside")]
    [InlineData("src/../outside")]
    [InlineData("C:/outside")]
    public async Task DotnetFormatFixer_UnsafeProjectDirectoryStopsBeforeFormat(string markerPath)
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: markerPath + "\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("unsafe project directory", ex.Message);
        Assert.Contains(markerPath, ex.Message);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_GitStatusFailureThrowsSanitizedBoundedMessage()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            statusResults: [new SandboxExecResult(1, "", "line1\n" + new string('x', 2_000))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("failed to read git status", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("output truncated", ex.Message);
    }

    [Fact]
    public async Task DotnetFormatFixer_GitDiffFailureThrowsSanitizedBoundedMessage()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            diffResults: [new SandboxExecResult(128, "", "diff failed\n" + new string('x', 2_000))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("failed to read git diff", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("output truncated", ex.Message);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task DotnetFormatFixer_GitDiffOutputLimitRejectsNormalizationBeforeFormat()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(0, "formatted", ""),
            diffResults:
            [
                new SandboxExecResult(
                    137,
                    "partial diff",
                    "",
                    StdoutLimitExceeded: true),
            ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    InputsFor(auditor))));

        Assert.Contains("tracked diff for mechanical fixer exceeded", ex.Message);
        Assert.Contains("skipped normalization", ex.Message);
        var diffExec = Assert.Single(sandbox.Execs, e =>
            e.Argv.SequenceEqual(["git", "-C", "/work/repo", "diff", "--binary", "--full-index", "HEAD", "--"]));
        Assert.Equal(MechanicalEditLimits.PatchCaptureMaxBytes, diffExec.MaxStdoutBytes);
        Assert.Equal(MechanicalEditLimits.GitDiagnosticCaptureMaxBytes, diffExec.MaxStderrBytes);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
    }

    [Fact]
    public async Task Pipeline_RunsMechanicalFixerBeforeInitialAuditAndAfterRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 2,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        var auditor = new NormalizerAwareOnceFailingAuditor();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));

        var item = NewItem("feature/mechanical");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", "main:normalizer.txt");
        Assert.Equal("1\n2\n", normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.Equal(2, CountOccurrences(mechanicalLog, "chore: normalize mechanical edits"));
        Assert.Equal(2, CountOccurrences(mechanicalLog, $"{CodeyBoxTrailers.MechanicalFixerTrailerKey}: fake-normalizer"));
        Assert.DoesNotContain(CodeyBoxTrailers.AgentTrailerKey, mechanicalLog);
        Assert.Equal(2, CountOccurrences(mechanicalLog, $"{CodeyBoxTrailers.PromptRevisionTrailerKey}: 1"));
    }

    [Fact]
    public async Task Pipeline_RunsMechanicalFixerOnWorkCompleteResumeBeforeAudit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        var auditor = new NormalizerExpectingAuditor("1\n");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);

        var item = NewItem("feature/mechanical-work-complete-resume");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);
        Assert.Equal(1, auditor.Calls);
        Assert.Empty(tp.Agent.WorkPrompts);

        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:normalizer.txt");
        Assert.Equal("1\n", normalized);
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.Contains($"{CodeyBoxTrailers.MechanicalFixerTrailerKey}: fake-normalizer", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_RunsMechanicalFixerFromWorkItemSelectedAuditProfile()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            MechanicalFixers = [],
            Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
            {
                ["ci"] = new()
                {
                    MaxIterations = 1,
                    AuditTypes = ["scripted"],
                    MechanicalFixers = [AppendingMechanicalFixer.FixerName],
                },
            },
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-profile") with { AuditorProfile = "ci" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:normalizer.txt");
        Assert.Equal("1\n", normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.Contains($"{CodeyBoxTrailers.MechanicalFixerTrailerKey}: fake-normalizer", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_TracksOnlyFormatterChangesAndSkipsSideEffectOnlyCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [UntrackedSideEffectMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new UntrackedSideEffectMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-untracked");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, files, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", "--name-only", "main");
        Assert.DoesNotContain("generated/cache.txt", files);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize mechanical edits", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_CommitsTrackedMechanicalEditsEvenWhenFixerReportsUnchanged()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [FalseReportingTrackedEditMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new FalseReportingTrackedEditMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-dirty-unchanged");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:normalizer.txt");
        Assert.Equal("dirty edit\n", normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.Contains("chore: normalize mechanical edits", mechanicalLog);
        Assert.Contains($"{CodeyBoxTrailers.MechanicalFixerTrailerKey}: {FalseReportingTrackedEditMechanicalFixer.FixerName}", mechanicalLog);
        Assert.DoesNotContain(CodeyBoxTrailers.AgentTrailerKey, mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_MechanicalCommitOmitsPromptRevision_WhenPromptChangedAfterDispatch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PromptRevisionTrailerAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);

        const string fileName = "stale.txt";
        const string fileContent = "v1\n";
        WorkItemId? editTarget = null;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (editTarget is not null)
            {
                await tp.Store.TryReplacePromptAsync(
                    editTarget.Value,
                    "edited while work was running",
                    DateTimeOffset.UtcNow,
                    ct);
            }

            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{fileName}"],
                Stdin = fileContent,
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", fileName],
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "commit", "-m", "agent commit without prompt trailer"],
            }, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite(fileName, fileContent));

        var item = NewItem("feature/mechanical-stale-prompt");
        editTarget = item.Id;
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(2, final.PromptRevision);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, subject, _) = await TestSupport.RunGit(barePath, "log", "-1", "--format=%s", item.WorkBranch!);
        Assert.Equal("chore: normalize mechanical edits", subject.Trim());

        var (_, trailer, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "-1",
            $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(string.Empty, trailer.Trim());
    }

    [Fact]
    public async Task Pipeline_DotnetFormatMechanicalFixerUsesSpecificSubjectAndProjectIdentity()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await AddTrackedFileAsync(seed, "src/App/Program.cs",
            """
            namespace App; public class Program{public static void Main(){System.Console.WriteLine("hi");}}
            """);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            Languages = ["csharp"],
            ExcludedAuditors = ["csharp:build-WaE", "csharp:test-pass"],
            MechanicalFixers = [MechanicalFixerNames.DotnetFormat],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectAudit: audit,
            presetCatalogOverride: new PresetCatalog(),
            projectGitAuthor: ("Project Bot", "project-bot@example.invalid"),
            mechanicalFixers: [new DotnetFormatMechanicalFixer()],
            mechanicalFixerInputProviders: [new DotnetFormatMechanicalFixerInputProvider()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-dotnet-format");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--format=%s%n%an <%ae>",
            item.WorkBranch!);
        Assert.Contains("chore: normalize (dotnet format)", mechanicalLog);
        Assert.DoesNotContain("chore: normalize mechanical edits", mechanicalLog);
        Assert.Contains("Project Bot <project-bot@example.invalid>", mechanicalLog);

        var (_, formattedProgram, _) = await TestSupport.RunGit(
            barePath,
            "show",
            $"{item.WorkBranch!}:src/App/Program.cs");
        Assert.Contains("public class Program {", formattedProgram);
    }

    [Fact]
    public async Task Pipeline_MechanicalSandboxUsesAuditToolProfileWithoutCredentialsAndReadOnlyRepo()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var recorder = new SpecRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: recorder,
            networkProfiles: new ProjectNetworkProfiles
            {
                Work = "work-profile",
                AuditTool = "audit-tool-profile",
            },
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-spec") with { BaselineImageRef = "work-baseline-pin" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var specs = recorder.Specs.Where(s => s.TimingPhase == "mechanical-edit").ToArray();
        Assert.Equal(2, specs.Length);
        var auditSpec = Assert.Single(recorder.Specs, s => s.TimingPhase == "audit");
        Assert.All(specs, spec =>
        {
            Assert.Equal("audit-tool-profile", spec.Network.ProfileName);
            Assert.Equal(auditSpec.Network.ProfileName, spec.Network.ProfileName);
            Assert.Equal(auditSpec.BaselineImageRef, spec.BaselineImageRef);
            Assert.NotEqual(item.BaselineImageRef, spec.BaselineImageRef);
            Assert.Empty(spec.Network.AllowedHosts);
            Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionEnvVar, spec.Environment.Keys);
            Assert.DoesNotContain(spec.Mounts, m => m.SandboxPath == SandboxConventions.CredentialsDir);
        });
        Assert.Equal("audit-tool-profile", auditSpec.Network.ProfileName);
        Assert.Null(auditSpec.BaselineImageRef);

        var normalizationRepoMount = Assert.Single(specs[0].Mounts, m => m.SandboxPath == "/repo");
        Assert.True(normalizationRepoMount.ReadOnly);

        var importRepoMount = Assert.Single(specs[1].Mounts, m => m.SandboxPath == "/repo");
        Assert.False(importRepoMount.ReadOnly);
    }

    [Fact]
    public async Task Pipeline_NoChangeInitialWorkFailsBeforeMechanicalFixerCanMaskIt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var fixer = new AppendingMechanicalFixer();
        var auditor = new CountingAuditor(new AuditResult(true, []));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [fixer]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "seed\n"));

        var item = NewItem("feature/mechanical-no-change-initial");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes", final.LastError, StringComparison.Ordinal);
        Assert.Equal(0, auditor.Calls);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:normalizer.txt");
        Assert.Equal(string.Empty, normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%s",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize mechanical edits", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_NoChangeReworkFailsBeforeMechanicalFixerCanMaskIt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var fixer = new SecondIterationMechanicalFixer();
        var auditor = new OnceFailingAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 2,
            AuditTypes = ["scripted"],
            MechanicalFixers = [SecondIterationMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [fixer]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "same-content\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "same-content\n"));

        var item = NewItem("feature/mechanical-no-change-rework");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Rework agent produced no changes", final.LastError, StringComparison.Ordinal);
        Assert.Equal(1, auditor.Calls);
        Assert.Equal([1], fixer.SeenIterations);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:normalizer.txt");
        Assert.Equal(string.Empty, normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%s",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize mechanical edits", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_ExplicitEmptyMechanicalFixersSkipsRegisteredFixerAndSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new SpecRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: recorder,
            mechanicalFixers: [new ThrowingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-disabled");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);
        Assert.DoesNotContain(recorder.Specs, s => s.TimingPhase == "mechanical-edit");
    }

    [Fact]
    public async Task Pipeline_MechanicalFailureParksWaitingForTransientRetry()
    {
        // The mechanical-edit phase wraps deterministic infra (sandbox /
        // git plumbing); an isolated failure must not terminate the work
        // item — csharp:format-check still gates as a safety net and the
        // retry scheduler will replay from WorkComplete.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [ThrowingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new ThrowingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-failure");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Contains("mechanical-edit failed", final.LastError);
    }

    [Fact]
    public async Task Pipeline_MechanicalCancellationAutoRetriesFromWorkComplete()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new CountingAuditor(new AuditResult(true, []));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [CancellingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [new CancellingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-cancel");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WorkComplete, final!.State);
        Assert.Equal(1, final.TransientCancelRetries);
        Assert.Equal(CancellationSources.Unknown, final.CancellationSource);
        Assert.Null(final.FailureKind);
        Assert.Contains("mechanical-edit", final.LastError, StringComparison.Ordinal);
        Assert.Equal(item.Id, await tp.Queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, auditor.Calls);
    }

    [Fact]
    public async Task Pipeline_MechanicalConfiguredTimeoutIsTimeoutFailureNotTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new CountingAuditor(new AuditResult(true, []));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [HangingMechanicalFixer.FixerName],
            PerIterationTimeout = TimeSpan.FromSeconds(1),
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [new HangingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-timeout");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("timeout", final.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("mechanical-edit"), final.CancellationSource);
        Assert.Equal(0, final.TransientCancelRetries);
        Assert.Equal(0, auditor.Calls);
    }

    [Fact]
    public async Task Pipeline_UnknownMechanicalFixerConfigFailsBeforeAgentWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = ["missing-normalizer"],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-config-error");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("configuration", final.FailureKind);
        Assert.Contains("missing-normalizer", final.LastError);
        Assert.Empty(tp.Agent.WorkPrompts);
        Assert.Single(tp.Agent.WorkPlan);
    }

    [Theory]
    [InlineData("normalizer-create", 1)]
    [InlineData("import-create", 2)]
    public async Task Pipeline_MechanicalSandboxCreationFailurePathsParkWaitingForTransientRetry(
        string branchSuffix,
        int mechanicalSandboxOrdinal)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var provider = new MechanicalSandboxCreationFailureProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            mechanicalSandboxOrdinal,
            "mechanical sandbox create failed");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem($"feature/mechanical-owned-failure-{branchSuffix}");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Contains("mechanical sandbox create failed", final.LastError);
        Assert.True(provider.Fired);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize",
            "--format=%s",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize", mechanicalLog);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pipeline_MechanicalDiskDeferredRethrowsAndLeavesItemWorkComplete(int mechanicalSandboxOrdinal)
    {
        var deferred = new SandboxDiskDeferredException(
            mountPath: "/fake/mp",
            freeBytes: 1L * 1024 * 1024,
            thresholdBytes: 10L * 1024 * 1024,
            recheckIn: TimeSpan.FromMinutes(1));

        await AssertMechanicalDeferralRethrownAsync(mechanicalSandboxOrdinal, deferred);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pipeline_MechanicalProvisioningDeferredRethrowsAndLeavesItemWorkComplete(int mechanicalSandboxOrdinal)
    {
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "start",
            errorClass: "start-failed",
            detail: "sandbox provisioning deferred",
            recheckIn: TimeSpan.FromMinutes(1));

        await AssertMechanicalDeferralRethrownAsync(mechanicalSandboxOrdinal, deferred);
    }

    private async Task AssertMechanicalDeferralRethrownAsync<TException>(
        int mechanicalSandboxOrdinal,
        TException deferred)
        where TException : Exception
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var provider = new MechanicalSandboxDeferralProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            mechanicalSandboxOrdinal,
            deferred);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem($"feature/mechanical-deferred-{mechanicalSandboxOrdinal}-{Guid.NewGuid():N}");
        await tp.Store.CreateAsync(item);

        var thrown = await Assert.ThrowsAsync<TException>(
            () => tp.Pipeline.RunAsync(item, CancellationToken.None));

        Assert.Same(deferred, thrown);
        Assert.True(provider.Fired);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WorkComplete, final!.State);
        Assert.Null(final.FailureKind);
    }

    public static IEnumerable<object[]> PipelineMechanicalGitFailureCases()
    {
        yield return
        [
            "normalizer-clone",
            new MechanicalCommandFailureRule(
                1,
                IsGitClone,
                new SandboxExecResult(128, "", "clone failed")),
            "git clone",
        ];
        yield return
        [
            "normalizer-checkout",
            new MechanicalCommandFailureRule(
                1,
                IsGitCheckout,
                new SandboxExecResult(128, "", "checkout failed")),
            "git -C /work checkout",
        ];
        yield return
        [
            "normalizer-user-name",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitConfigKey(exec, "user.name"),
                new SandboxExecResult(128, "", "config name failed")),
            "git -C /work config user.name",
        ];
        yield return
        [
            "normalizer-user-email",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitConfigKey(exec, "user.email"),
                new SandboxExecResult(128, "", "config email failed")),
            "git -C /work config user.email ***",
        ];
        yield return
        [
            "status",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "status", "--porcelain", "--untracked-files=no"),
                new SandboxExecResult(128, "", "status failed")),
            "mechanical-edit could not read git status",
        ];
        yield return
        [
            "stage",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "add", "-u"),
                new SandboxExecResult(128, "", "add failed")),
            "git -C /work add -u",
        ];
        yield return
        [
            "staged-diff",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"),
                new SandboxExecResult(128, "", "diff failed")),
            "mechanical-edit could not inspect staged diff",
        ];
        yield return
        [
            "first-commit",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitSubcommand(exec, "commit"),
                new SandboxExecResult(1, "", "commit failed")),
            "git -C /work commit -m",
        ];
        yield return
        [
            "patch-export",
            new MechanicalCommandFailureRule(
                1,
                exec => IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "diff", "--binary", "HEAD^", "HEAD"),
                new SandboxExecResult(0, "", "")),
            "mechanical-edit could not export formatter commit diff",
        ];
        yield return
        [
            "import-clone",
            new MechanicalCommandFailureRule(
                2,
                IsGitClone,
                new SandboxExecResult(128, "", "clone failed")),
            "git clone",
        ];
        yield return
        [
            "import-checkout",
            new MechanicalCommandFailureRule(
                2,
                IsGitCheckout,
                new SandboxExecResult(128, "", "checkout failed")),
            "git -C /work checkout",
        ];
        yield return
        [
            "import-user-name",
            new MechanicalCommandFailureRule(
                2,
                exec => IsGitConfigKey(exec, "user.name"),
                new SandboxExecResult(128, "", "config name failed")),
            "git -C /work config user.name",
        ];
        yield return
        [
            "import-user-email",
            new MechanicalCommandFailureRule(
                2,
                exec => IsGitConfigKey(exec, "user.email"),
                new SandboxExecResult(128, "", "config email failed")),
            "git -C /work config user.email ***",
        ];
        yield return
        [
            "patch-materialize",
            new MechanicalCommandFailureRule(
                2,
                exec => exec.Argv.SequenceEqual(["sh", "-c", "cat > \"$0\"", "/tmp/codeybox-mechanical.patch"]),
                new SandboxExecResult(1, "", "write failed")),
            "mechanical-edit could not materialize formatter patch",
        ];
        yield return
        [
            "patch-apply",
            new MechanicalCommandFailureRule(
                2,
                exec => IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "apply", "--index", "/tmp/codeybox-mechanical.patch"),
                new SandboxExecResult(1, "", "apply failed")),
            "mechanical-edit could not import formatter commit",
        ];
        yield return
        [
            "import-commit",
            new MechanicalCommandFailureRule(
                2,
                exec => IsGitSubcommand(exec, "commit"),
                new SandboxExecResult(1, "", "commit failed")),
            "mechanical-edit could not import formatter commit",
        ];
        yield return
        [
            "import-push",
            new MechanicalCommandFailureRule(
                2,
                exec => IsGitSubcommand(exec, "push"),
                new SandboxExecResult(1, "", "permission denied")),
            "mechanical-edit could not import formatter commit",
        ];
    }

    [Theory]
    [MemberData(nameof(PipelineMechanicalGitFailureCases))]
    public async Task Pipeline_MechanicalGitFailurePathsParkWaitingForTransientRetry(
        string branchSuffix,
        MechanicalCommandFailureRule failure,
        string expectedError)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var provider = new MechanicalCommandInterceptingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            [failure]);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem($"feature/mechanical-owned-failure-{branchSuffix}");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Contains(expectedError, final.LastError);
        Assert.True(failure.Fired);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize",
            "--format=%s",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_MechanicalGitEmailConfigUsesPhaseCancellation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var provider = new MechanicalCommandInterceptingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            []);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-cancellable-email-config");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var emailConfigInvocations = provider.MechanicalCommandInvocations
            .Where(invocation => IsGitConfigKey(invocation.Argv, "user.email"))
            .ToArray();

        Assert.Equal([1, 2], emailConfigInvocations.Select(invocation => invocation.MechanicalSandboxOrdinal).ToArray());
        Assert.All(emailConfigInvocations, invocation => Assert.True(invocation.CancellationCanBeCanceled));
    }

    [Fact]
    public async Task Pipeline_MechanicalPatchExportOutputLimitRejectsOversizedCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        SandboxExec? patchExportExec = null;
        var patchExportExceeded = new MechanicalCommandFailureRule(
            1,
            exec =>
            {
                if (!IsGitArgs(exec, "-C", SandboxConventions.WorkDir, "diff", "--binary", "HEAD^", "HEAD"))
                    return false;

                patchExportExec = exec;
                return true;
            },
            new SandboxExecResult(
                137,
                "partial patch",
                "",
                StdoutLimitExceeded: true));
        var provider = new MechanicalCommandInterceptingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            [patchExportExceeded]);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-oversized-patch");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // Oversized-patch rejection rides the same MechanicalFixerException
        // surface as other infra failures and therefore parks for transient
        // retry. The retry budget will eventually exhaust if the patch stays
        // oversized, but a single hit must not terminate the work item.
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Contains("patch cap", final.LastError);
        Assert.True(patchExportExceeded.Fired);
        Assert.NotNull(patchExportExec);
        Assert.Equal(MechanicalEditLimits.PatchCaptureMaxBytes, patchExportExec!.MaxStdoutBytes);
        Assert.Equal(MechanicalEditLimits.GitDiagnosticCaptureMaxBytes, patchExportExec.MaxStderrBytes);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize",
            "--format=%s",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_MechanicalImportPushReconcilesNonFastForwardRejection()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var pushRejected = new MechanicalCommandFailureRule(
            2,
            exec => IsGitSubcommand(exec, "push"),
            new SandboxExecResult(1, "", "! [rejected] feature/mechanical-reconcile -> feature/mechanical-reconcile (non-fast-forward)"));
        var provider = new MechanicalCommandInterceptingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            [pushRejected]);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: provider,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-reconcile");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);
        Assert.True(pushRejected.Fired);
        Assert.Contains(provider.MechanicalCommands, argv =>
            argv.SequenceEqual([
                "git",
                "-C",
                SandboxConventions.WorkDir,
                "fetch",
                "--no-tags",
                "origin",
                $"+refs/heads/{item.WorkBranch}:refs/remotes/origin/{item.WorkBranch}",
            ]));

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%s",
            item.WorkBranch!);
        Assert.Contains("chore: normalize mechanical edits", mechanicalLog);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "mechanical normalizer",
        Prompt = "change the repo",
        WorkBranch = branch,
        BaseBranch = "main",
    };

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static bool IsGitArgs(SandboxExec exec, params string[] args)
        => exec.Argv.Count == args.Length + 1 &&
           exec.Argv[0] == "git" &&
           exec.Argv.Skip(1).SequenceEqual(args);

    private static bool IsGitClone(SandboxExec exec)
        => exec.Argv.Count >= 2 &&
           exec.Argv[0] == "git" &&
           exec.Argv[1] == "clone";

    private static bool IsGitCheckout(SandboxExec exec)
        => exec.Argv.Count >= 4 &&
           exec.Argv[0] == "git" &&
           exec.Argv[1] == "-C" &&
           exec.Argv[2] == SandboxConventions.WorkDir &&
           exec.Argv[3] == "checkout";

    private static bool IsGitConfigKey(SandboxExec exec, string key)
        => IsGitConfigKey(exec.Argv, key);

    private static bool IsGitConfigKey(IReadOnlyList<string> argv, string key)
        => argv.Count >= 6 &&
           argv[0] == "git" &&
           argv[1] == "-C" &&
           argv[2] == SandboxConventions.WorkDir &&
           argv[3] == "config" &&
           argv[4] == key;

    private static bool IsGitSubcommand(SandboxExec exec, string subcommand)
        => exec.Argv.Count >= 4 &&
           exec.Argv[0] == "git" &&
           exec.Argv[1] == "-C" &&
           exec.Argv[3] == subcommand;

    private static async Task AddTrackedFileAsync(string repo, string path, string contents)
    {
        var fullPath = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, contents);
        await TestSupport.RunGit(repo, "add", "--", path);
        await TestSupport.RunGit(repo, "commit", "-m", $"add {path}");
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "work-complete-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");

        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", "--", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
    }

    private static ProjectsOptions BindProjectsOptions(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
    }

    private static IReadOnlyList<IMechanicalFixerInput> InputsFor(IAuditor auditor)
    {
        return new DotnetFormatMechanicalFixerInputProvider().BuildInputs([auditor]);
    }

    private static IAuditor CreateLanguagePresetAuditor(string language, IAuditor inner)
    {
        var type = typeof(DotnetFormatMechanicalFixerInputProvider).Assembly.GetType(
            "CodeyBox.Audit.Presets.Presets.LanguagePresetAuditor",
            throwOnError: true)!;
        var ctor = type.GetConstructor(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(IAuditor),
            ],
            modifiers: null)!;
        return (IAuditor)ctor.Invoke([language, "project marker", "printf '.\n'", inner]);
    }

    private sealed class AppendingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "fake-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    "printf '%s\n' \"$1\" >> \"$0\"",
                    $"{workingDirectory}/normalizer.txt",
                    context.AuditIteration.ToString(CultureInfo.InvariantCulture),
                ],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(true, $"appended iteration {context.AuditIteration}");
        }
    }

    private sealed class UntrackedSideEffectMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "side-effect-only";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "mkdir -p \"$0/generated\" && printf cache > \"$0/generated/cache.txt\"", workingDirectory],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(true, "created untracked side effect");
        }
    }

    private sealed class NoOpMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "noop-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
            => Task.FromResult(new MechanicalFixerResult(false, "no changes"));
    }

    private sealed class FalseReportingTrackedEditMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "false-reporting-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf 'dirty edit\n' > \"$0\"", $"{workingDirectory}/normalizer.txt"],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(false, "edited tracked file but reported unchanged");
        }
    }

    private sealed class SecondIterationMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "second-iteration-normalizer";

        public string Name => FixerName;
        public string Kind => "test";
        public List<int> SeenIterations { get; } = [];

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            SeenIterations.Add(context.AuditIteration);
            if (context.AuditIteration < 2)
                return new MechanicalFixerResult(false, "waiting for second iteration");

            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf '%s\n' \"$1\" >> \"$0\"", $"{workingDirectory}/normalizer.txt", context.AuditIteration.ToString(CultureInfo.InvariantCulture)],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(true, $"appended iteration {context.AuditIteration}");
        }
    }

    private sealed class ThrowingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "throwing-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
            => throw new InvalidOperationException("normalizer unavailable");
    }

    private sealed class CancellingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "cancelling-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            throw new OperationCanceledException("mechanical fixer cancelled");
        }
    }

    private sealed class HangingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "hanging-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new MechanicalFixerResult(false, "unreachable");
        }
    }

    private sealed class NamedAuditor : IAuditor
    {
        public NamedAuditor(string name, string kind = "tool")
        {
            Name = name;
            Kind = kind;
        }

        public string Name { get; }
        public string Kind { get; }
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class NamedShellAuditor : IAuditor, IShellAuditorArgvProvider
    {
        public NamedShellAuditor(string name, IReadOnlyList<string> argv)
        {
            Name = name;
            Argv = argv;
        }

        public string Name { get; }
        public string Kind => "shell";
        public AuditCapabilities Required => AuditCapabilities.None;
        public IReadOnlyList<string> Argv { get; }

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class OnceFailingAuditor : IAuditor
    {
        public string Name => "test:once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Calls { get; private set; }

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            Calls++;
            return Calls == 1
                ? Task.FromResult(new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "first audit requires rework", "scripted one-time failure"),
                ]))
                : Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class NormalizerAwareOnceFailingAuditor : IAuditor
    {
        private int _calls;

        public string Name => "test:once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _calls++;
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{workingDirectory}/normalizer.txt"],
            }, ct);
            if (!read.Success)
            {
                return new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "normalizer output missing", read.Stderr),
                ]);
            }

            var expected = _calls == 1 ? "1\n" : "1\n2\n";
            if (!string.Equals(read.Stdout, expected, StringComparison.Ordinal))
            {
                return new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "auditor did not observe mechanical normalization",
                        $"Expected normalizer.txt to be {expected.Length} bytes for call {_calls}, got: {read.Stdout}"),
                ]);
            }

            if (_calls == 1)
            {
                return new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "first audit requires rework",
                        "scripted one-time failure"),
                ]);
            }

            return new AuditResult(true, []);
        }
    }

    private sealed class NormalizerExpectingAuditor(string expected) : IAuditor
    {
        public string Name => "test:normalizer-expecting";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Calls { get; private set; }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = context;
            Calls++;
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{workingDirectory}/normalizer.txt"],
            }, ct);
            if (!read.Success)
            {
                return new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "normalizer output missing", read.Stderr),
                ]);
            }

            return string.Equals(read.Stdout, expected, StringComparison.Ordinal)
                ? new AuditResult(true, [])
                : new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "auditor did not observe mechanical normalization",
                        $"Expected normalizer.txt to be {expected.Length} bytes, got: {read.Stdout}"),
                ]);
        }
    }

    private sealed class CountingAuditor : IAuditor
    {
        private readonly AuditResult _result;

        public CountingAuditor(AuditResult result) => _result = result;

        public string Name => "test:counting";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Calls { get; private set; }

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class PassingAuditor : IAuditor
    {
        public string Name => "test:passing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class LocalProcessSandbox(IReadOnlyDictionary<string, string>? environment = null) : ISandbox
    {
        public string Id => "local-process";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exec.Argv[0],
                WorkingDirectory = exec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = exec.Stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            for (var i = 1; i < exec.Argv.Count; i++)
                psi.ArgumentList.Add(exec.Argv[i]);

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    psi.Environment[key] = value;
            }

            using var process = Process.Start(psi)!;
            if (exec.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(exec.Stdin);
                await process.StandardInput.DisposeAsync();
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, await stdout, await stderr);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DotnetFormatSandbox : ISandbox
    {
        private readonly string _markerStdout;
        private readonly Queue<string> _statusOutputs;
        private readonly Queue<SandboxExecResult>? _statusResults;
        private readonly Queue<string> _diffOutputs;
        private readonly Queue<SandboxExecResult>? _diffResults;
        private readonly SandboxExecResult _formatResult;
        private readonly Queue<SandboxExecResult>? _formatResults;
        private readonly SandboxExecResult? _markerResult;
        private readonly SandboxExecResult? _resetResult;
        private readonly SandboxExecResult? _applyResult;
        private readonly SandboxExecResult? _patchWriteResult;
        private bool _markerDiscoveryReturned;

        public DotnetFormatSandbox(
            string markerStdout,
            IEnumerable<string> statusOutputs,
            SandboxExecResult formatResult,
            SandboxExecResult? markerResult = null,
            IEnumerable<SandboxExecResult>? statusResults = null,
            SandboxExecResult? resetResult = null,
            IEnumerable<string>? diffOutputs = null,
            SandboxExecResult? applyResult = null,
            IEnumerable<SandboxExecResult>? diffResults = null,
            SandboxExecResult? patchWriteResult = null,
            IEnumerable<SandboxExecResult>? formatResults = null)
        {
            _markerStdout = markerStdout;
            _statusOutputs = new Queue<string>(statusOutputs);
            _diffOutputs = new Queue<string>(diffOutputs ?? []);
            _formatResult = formatResult;
            _formatResults = formatResults is null ? null : new Queue<SandboxExecResult>(formatResults);
            _markerResult = markerResult;
            _statusResults = statusResults is null ? null : new Queue<SandboxExecResult>(statusResults);
            _resetResult = resetResult;
            _applyResult = applyResult;
            _diffResults = diffResults is null ? null : new Queue<SandboxExecResult>(diffResults);
            _patchWriteResult = patchWriteResult;
        }

        public string Id => "dotnet-format-fake";
        public List<SandboxExec> Execs { get; } = [];
        public List<string> WrittenPatches { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.SequenceEqual(["sh", "-c", "cat > \"$0\"", "/tmp/codeybox-dotnet-format-before.patch"]))
            {
                WrittenPatches.Add(exec.Stdin ?? "");
                return Task.FromResult(_patchWriteResult ?? new SandboxExecResult(0, "", ""));
            }

            if (exec.Argv is ["sh", "-c", var script] &&
                !script.Contains("command -v", StringComparison.Ordinal) &&
                !_markerDiscoveryReturned)
            {
                _markerDiscoveryReturned = true;
                return Task.FromResult(_markerResult ?? new SandboxExecResult(0, _markerStdout, ""));
            }

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "git" && exec.Argv.Contains("status"))
            {
                if (_statusResults is { Count: > 0 })
                    return Task.FromResult(_statusResults.Dequeue());
                var stdout = _statusOutputs.Count > 0 ? _statusOutputs.Dequeue() : "";
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "git" && exec.Argv.Contains("diff"))
            {
                if (_diffResults is { Count: > 0 })
                    return Task.FromResult(_diffResults.Dequeue());
                var stdout = _diffOutputs.Count > 0 ? _diffOutputs.Dequeue() : "";
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }

            if (exec.Argv.SequenceEqual(["git", "-C", "/work/repo", "reset", "--hard", "HEAD"]))
                return Task.FromResult(_resetResult ?? new SandboxExecResult(0, "", ""));

            if (exec.Argv.SequenceEqual(["git", "-C", "/work/repo", "apply", "--whitespace=nowarn", "/tmp/codeybox-dotnet-format-before.patch"]))
                return Task.FromResult(_applyResult ?? new SandboxExecResult(0, "", ""));

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "dotnet" && exec.Argv[1] == "format")
            {
                if (_formatResults is { Count: > 0 })
                    return Task.FromResult(_formatResults.Dequeue());
                return Task.FromResult(_formatResult);
            }

            if (_formatResults is { Count: > 0 })
                return Task.FromResult(_formatResults.Dequeue());
            return Task.FromResult(_formatResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SpecRecordingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;

        public SpecRecordingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;
        public List<SandboxSpec> Specs { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Specs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class MechanicalSandboxCreationFailureProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly int _failureOrdinal;
        private readonly string _message;
        private int _mechanicalSandboxCount;

        public MechanicalSandboxCreationFailureProvider(
            ISandboxProvider inner,
            int failureOrdinal,
            string message)
        {
            _inner = inner;
            _failureOrdinal = failureOrdinal;
            _message = message;
        }

        public string Name => _inner.Name;
        public bool Fired { get; private set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (string.Equals(spec.TimingPhase, "mechanical-edit", StringComparison.Ordinal))
            {
                var ordinal = Interlocked.Increment(ref _mechanicalSandboxCount);
                if (ordinal == _failureOrdinal)
                {
                    Fired = true;
                    throw new InvalidOperationException(_message);
                }
            }

            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class MechanicalSandboxDeferralProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly int _deferralOrdinal;
        private readonly Exception _exception;
        private int _mechanicalSandboxCount;

        public MechanicalSandboxDeferralProvider(
            ISandboxProvider inner,
            int deferralOrdinal,
            Exception exception)
        {
            _inner = inner;
            _deferralOrdinal = deferralOrdinal;
            _exception = exception;
        }

        public string Name => _inner.Name;
        public bool Fired { get; private set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (string.Equals(spec.TimingPhase, "mechanical-edit", StringComparison.Ordinal))
            {
                var ordinal = Interlocked.Increment(ref _mechanicalSandboxCount);
                if (ordinal == _deferralOrdinal)
                {
                    Fired = true;
                    throw _exception;
                }
            }

            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    public sealed class MechanicalCommandFailureRule
    {
        private readonly Func<SandboxExec, bool> _predicate;
        private readonly SandboxExecResult _result;

        public MechanicalCommandFailureRule(
            int mechanicalSandboxOrdinal,
            Func<SandboxExec, bool> predicate,
            SandboxExecResult result)
        {
            MechanicalSandboxOrdinal = mechanicalSandboxOrdinal;
            _predicate = predicate;
            _result = result;
        }

        public int MechanicalSandboxOrdinal { get; }
        public bool Fired { get; private set; }

        public bool TryMatch(int mechanicalSandboxOrdinal, SandboxExec exec, out SandboxExecResult result)
        {
            if (!Fired &&
                MechanicalSandboxOrdinal == mechanicalSandboxOrdinal &&
                _predicate(exec))
            {
                Fired = true;
                result = _result;
                return true;
            }

            result = default!;
            return false;
        }
    }

    private sealed class MechanicalCommandInterceptingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly IReadOnlyList<MechanicalCommandFailureRule> _failures;
        private int _mechanicalSandboxCount;

        public MechanicalCommandInterceptingSandboxProvider(
            ISandboxProvider inner,
            IReadOnlyList<MechanicalCommandFailureRule> failures)
        {
            _inner = inner;
            _failures = failures;
        }

        public string Name => _inner.Name;
        public List<IReadOnlyList<string>> MechanicalCommands { get; } = [];
        public List<MechanicalCommandInvocation> MechanicalCommandInvocations { get; } = [];

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var sandbox = await _inner.CreateAsync(spec, ct);
            if (!string.Equals(spec.TimingPhase, "mechanical-edit", StringComparison.Ordinal))
                return sandbox;

            var ordinal = Interlocked.Increment(ref _mechanicalSandboxCount);
            return new MechanicalCommandInterceptingSandbox(
                sandbox,
                ordinal,
                _failures,
                MechanicalCommands,
                MechanicalCommandInvocations);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class MechanicalCommandInterceptingSandbox : ISandbox
    {
        private readonly ISandbox _inner;
        private readonly int _mechanicalSandboxOrdinal;
        private readonly IReadOnlyList<MechanicalCommandFailureRule> _failures;
        private readonly List<IReadOnlyList<string>> _commands;
        private readonly List<MechanicalCommandInvocation> _invocations;

        public MechanicalCommandInterceptingSandbox(
            ISandbox inner,
            int mechanicalSandboxOrdinal,
            IReadOnlyList<MechanicalCommandFailureRule> failures,
            List<IReadOnlyList<string>> commands,
            List<MechanicalCommandInvocation> invocations)
        {
            _inner = inner;
            _mechanicalSandboxOrdinal = mechanicalSandboxOrdinal;
            _failures = failures;
            _commands = commands;
            _invocations = invocations;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv.ToArray();
            _commands.Add(argv);
            _invocations.Add(new MechanicalCommandInvocation(
                _mechanicalSandboxOrdinal,
                argv,
                ct.CanBeCanceled));
            foreach (var failure in _failures)
            {
                if (failure.TryMatch(_mechanicalSandboxOrdinal, exec, out var result))
                    return Task.FromResult(result);
            }

            return _inner.ExecAsync(exec, ct);
        }

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => _inner.KillActiveExecsAsync(ct);

        public ValueTask DisposeAsync()
            => _inner.DisposeAsync();
    }

    private sealed record MechanicalCommandInvocation(
        int MechanicalSandboxOrdinal,
        IReadOnlyList<string> Argv,
        bool CancellationCanBeCanceled);
}
