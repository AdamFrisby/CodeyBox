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
    public void DotnetFormatFixer_ToFixerArgvThrowsForNonDotnetFormatCommand()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DotnetFormatMechanicalFixer.ToFixerArgv(["dotnet", "build", "--no-restore"]));

        Assert.Contains("csharp:format-check", ex.Message);
        Assert.Contains("dotnet format", ex.Message);
        Assert.Contains("dotnet-format", ex.Message);
    }

    [Fact]
    public async Task DotnetFormatFixer_IncompatibleFormatAuditorSkipsWithoutThrowing()
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
                [new DotnetFormatMechanicalFixerInput(["./format-wrapper", "--verify-no-changes"], ".")]));

        Assert.False(result.Changed);
        Assert.Contains("dotnet-format skipped", result.Summary);
        Assert.Empty(sandbox.Execs);
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
            diffOutputs: [previousFixerPatch]);

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
            diffOutputs: [previousFixerPatch],
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
            diffOutputs: [previousFixerPatch],
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
        Assert.Equal(2, CountOccurrences(mechanicalLog, "CodeyBox-Agent: mechanical/fake-normalizer"));
        Assert.Equal(2, CountOccurrences(mechanicalLog, $"{CodeyBoxTrailers.PromptRevisionTrailerKey}: 1"));
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
                AuditTool = "audit-tool-profile",
            },
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-spec");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var specs = recorder.Specs.Where(s => s.TimingPhase == "mechanical-edit").ToArray();
        Assert.Equal(2, specs.Length);
        Assert.All(specs, spec =>
        {
            Assert.Equal("audit-tool-profile", spec.Network.ProfileName);
            Assert.Empty(spec.Network.AllowedHosts);
            Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionEnvVar, spec.Environment.Keys);
            Assert.DoesNotContain(spec.Mounts, m => m.SandboxPath == SandboxConventions.CredentialsDir);
        });

        var normalizationRepoMount = Assert.Single(specs[0].Mounts, m => m.SandboxPath == "/repo");
        Assert.True(normalizationRepoMount.ReadOnly);

        var importRepoMount = Assert.Single(specs[1].Mounts, m => m.SandboxPath == "/repo");
        Assert.False(importRepoMount.ReadOnly);
    }

    [Fact]
    public async Task Pipeline_MechanicalFailureIsInfrastructureFailure()
    {
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
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("mechanical-edit failed", final.LastError);
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

    public static IEnumerable<object[]> PipelineMechanicalGitFailureCases()
    {
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
    public async Task Pipeline_MechanicalGitFailurePathsAreInfrastructureFailures(
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
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
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

    private sealed class DotnetFormatSandbox : ISandbox
    {
        private readonly string _markerStdout;
        private readonly Queue<string> _statusOutputs;
        private readonly Queue<SandboxExecResult>? _statusResults;
        private readonly Queue<string> _diffOutputs;
        private readonly Queue<SandboxExecResult>? _diffResults;
        private readonly SandboxExecResult _formatResult;
        private readonly SandboxExecResult? _markerResult;
        private readonly SandboxExecResult? _resetResult;
        private readonly SandboxExecResult? _applyResult;
        private readonly SandboxExecResult? _patchWriteResult;

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
            SandboxExecResult? patchWriteResult = null)
        {
            _markerStdout = markerStdout;
            _statusOutputs = new Queue<string>(statusOutputs);
            _diffOutputs = new Queue<string>(diffOutputs ?? []);
            _formatResult = formatResult;
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

            if (exec.Argv is ["sh", "-c", var script] && !script.Contains("command -v", StringComparison.Ordinal))
                return Task.FromResult(_markerResult ?? new SandboxExecResult(0, _markerStdout, ""));

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
                return Task.FromResult(_formatResult);

            return Task.FromResult(new SandboxExecResult(0, "", ""));
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
                MechanicalCommands);
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

        public MechanicalCommandInterceptingSandbox(
            ISandbox inner,
            int mechanicalSandboxOrdinal,
            IReadOnlyList<MechanicalCommandFailureRule> failures,
            List<IReadOnlyList<string>> commands)
        {
            _inner = inner;
            _mechanicalSandboxOrdinal = mechanicalSandboxOrdinal;
            _failures = failures;
            _commands = commands;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _commands.Add(exec.Argv.ToArray());
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
}
