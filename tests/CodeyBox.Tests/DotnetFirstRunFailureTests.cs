using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Presets.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// .NET CLI first-use failures — the "NuGet-Migrations" named-mutex crash and
/// the bare SDK first-run banner — fire inside
/// <c>DotnetFirstTimeUseConfigurer</c> before the command evaluates the tree,
/// so they are infrastructure faults, never verdicts on the diff. The
/// classifiers must route them to <see cref="AuditUnavailableException"/>
/// (non-deterministic: retried without consuming a rework iteration) while
/// genuine compiler, analyzer, MSBuild, test, and restore failures keep their
/// blocking-finding path.
/// </summary>
public sealed class DotnetFirstRunFailureTests
{
    // The exact exception text observed in audit output (work items
    // b7c7aab5 / 8197cf0f): dotnet first-use creating NuGet's named mutex.
    private const string NuGetMigrationsException =
        "System.IO.IOException: The system cannot open the device or file specified. : 'NuGet-Migrations'.\n" +
        "   at System.Threading.Mutex.CreateMutexCore(...)\n" +
        "   at System.Threading.Mutex..ctor(Boolean initiallyOwned, String name)\n" +
        "   at NuGet.Common.Migrations.MigrationRunner.Run(...)\n" +
        "   at Microsoft.DotNet.Configurer.DotnetFirstTimeUseConfigurer.Configure()\n" +
        "   at Microsoft.DotNet.Cli.Program.ConfigureDotNetForFirstTimeUse(...)";

    private const string FirstRunBanner =
        "Welcome to .NET 10.0!\n" +
        "---------------------\n" +
        "SDK Version: 10.0.403\n" +
        "Telemetry\n" +
        "---------\n" +
        "The .NET tools collect usage data.\n";

    private static readonly IReadOnlyList<string> BuildArgv = ["dotnet", "build", "--no-incremental", "/warnaserror"];
    private static readonly IReadOnlyList<string> TestArgv = ["dotnet", "test", "--no-build"];
    private static readonly IReadOnlyList<string> FormatArgv =
        ["dotnet", "format", "--verify-no-changes", "--verbosity", "diagnostic"];

    private static AuditResultClassificationContext ContextFor(
        string auditorName,
        IReadOnlyList<string> argv,
        string output,
        IReadOnlyList<string>? executedArgv = null) =>
        new(
            auditorName,
            argv,
            new SandboxExecResult(1, output, string.Empty),
            output,
            new AuditFinding(auditorName, AuditSeverity.Error, "command failed", "exit 1"),
            executedArgv ?? argv);

    [Fact]
    public void GenericClassifier_NuGetMigrationsException_ClassifiedAsInfrastructure()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("csharp:build-WaE", BuildArgv, NuGetMigrationsException)));

        Assert.Contains("first-run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NuGet-Migrations", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void GenericClassifier_FirstRunBannerWithoutDiagnostics_ClassifiedAsInfrastructure()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output = FirstRunBanner + "An issue was encountered verifying workloads.\n";

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("csharp:build-WaE", BuildArgv, output)));

        Assert.Contains("first-run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void GenericClassifier_FirstRunBanner_WithCompilerError_StaysGeneric()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output = FirstRunBanner
            + "/work/src/C.cs(1,1): error CS1002: ; expected [/work/src/lib.csproj]\n"
            + "Build FAILED.\n";

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("csharp:build-WaE", BuildArgv, output)));
    }

    [Fact]
    public void GenericClassifier_MutexException_WithCompilerError_StaysGeneric()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output = NuGetMigrationsException
            + "\n/work/src/C.cs(1,1): error CS1002: ; expected\n";

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("csharp:build-WaE", BuildArgv, output)));
    }

    [Fact]
    public void GenericClassifier_NewlineHeavyBannerOutput_StillClassifies()
    {
        // Regression: the banner regex once led with `^\s*` under Multiline,
        // letting the greedy whitespace scan cross line boundaries and
        // backtrack once per line start — O(n^2) on repo-controlled,
        // newline-heavy output. `^[ \t]*` keeps the scan inside one line, and
        // the match timeout bounds whatever backtracking remains.
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output = new string('\n', 200_000) + "Welcome to .NET 10.0!\n";

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("csharp:build-WaE", BuildArgv, output)));

        Assert.Contains("first-run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void GenericClassifier_GenuineBuildFailure_ReturnsNull()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output =
            "/work/src/C.cs(1,1): error CS1002: ; expected [/work/src/lib.csproj]\n"
            + "    0 Warning(s)\n"
            + "    1 Error(s)\n"
            + "\nBuild FAILED.\n";

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("csharp:build-WaE", BuildArgv, output)));
    }

    [Fact]
    public void GenericClassifier_NuGetMigrationsText_WithoutFirstRunFrames_ReturnsNull()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var output =
            "/work/src/C.cs(1,1): error CS1002: ; expected [/work/src/lib.csproj]\n"
            + "note: see NuGet-Migrations migration notes\n"
            + "Build FAILED.\n";

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("csharp:build-WaE", BuildArgv, output)));
    }

    [Fact]
    public void GenericClassifier_NonDotnetArgv_IgnoresFirstRunSignature()
    {
        var classifier = new DotnetFirstRunCommandResultClassifier();
        var argv = new[] { "sh", "-c", "dotnet build" };

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("custom:gate", argv, NuGetMigrationsException)));
    }

    [Fact]
    public void GenericClassifier_AnyDotnetSubcommand_ClassifiedAsInfrastructure()
    {
        // The classifier is wired for every dotnet shell auditor (language
        // presets, audit types, custom auditors), so the signature applies to
        // any dotnet subcommand — not only `dotnet build`.
        var classifier = new DotnetFirstRunCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("custom:dotnet-restore", ["dotnet", "restore"], NuGetMigrationsException)));

        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void TestClassifier_NuGetMigrationsException_ClassifiedAsInfrastructure()
    {
        var classifier = new DotnetTestCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("csharp:test-pass", TestArgv, NuGetMigrationsException)));

        Assert.Contains("first-run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void FormatClassifier_NuGetMigrationsException_ClassifiedAsInfrastructure()
    {
        var classifier = new DotnetFormatCommandResultClassifier();

        var ex = Assert.Throws<AuditUnavailableException>(
            () => classifier.ClassifyFailedCommand(
                ContextFor("csharp:format-check", FormatArgv, NuGetMigrationsException)));

        Assert.Contains("first-run", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public void FormatClassifier_FirstRunBanner_WithFormatViolation_StaysFinding()
    {
        var classifier = new DotnetFormatCommandResultClassifier();
        var output = FirstRunBanner
            + "/work/src/C.cs(1,1): error WHITESPACE: Fix whitespace formatting. [/work/src/lib.csproj]\n";

        var result = classifier.ClassifyFailedCommand(
            ContextFor("csharp:format-check", FormatArgv, output));

        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.Contains("format", result.Findings[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestClassifier_GenuineTestFailure_StillParsesFindings()
    {
        var classifier = new DotnetTestCommandResultClassifier();
        var output = FirstRunBanner
            + "Failed CodeyBox.Tests.SampleTests.DoesTheThing [1 ms]\n"
            + "Error Message:\n   Assert.True() Failure\n"
            + "Failed!  - Failed:     1, Passed:     0, Skipped:     0\n";

        var result = classifier.ClassifyFailedCommand(
            ContextFor("csharp:test-pass", TestArgv, output));

        Assert.NotNull(result);
        Assert.False(result!.Passed);
    }

    [Fact]
    public void TestClassifier_NonDotnetArgv_IgnoresFirstRunSignature()
    {
        var classifier = new DotnetTestCommandResultClassifier();
        var argv = new[] { "sh", "-c", "dotnet build" };

        Assert.Null(classifier.ClassifyFailedCommand(
            ContextFor("custom:gate", argv, NuGetMigrationsException)));
    }

    [Fact]
    public async Task CustomDotnetShellAuditor_FirstRunFailure_RoutesToInfrastructure()
    {
        // End-to-end through the real composition path: a project-configured
        // `dotnet build` shell auditor must surface the first-run mutex crash
        // as AuditUnavailableException — retried infrastructure, never a
        // blocking finding against the diff.
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustomAuditor(["dotnet", "build", "--no-incremental", "/warnaserror"]);
        var auditor = Assert.Single(composer.Compose(project, new ScriptedAgent([])));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(
                new FirstRunFailingSandbox(NuGetMigrationsException),
                "/work",
                new AuditContext(
                    WorkItemId.New(),
                    WorkBranch: "codeybox/test",
                    BaseBranch: "main",
                    Iteration: 1,
                    OriginalPrompt: "add a feature")));

        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public async Task LanguagePresetDotnetAuditor_FirstRunFailure_RoutesToInfrastructure()
    {
        // A dotnet gate under a non-csharp language id or auditor name —
        // projects can add/override language auditors via Audit.Languages
        // — must get the same first-run classification as csharp:build-WaE.
        // The classifier self-guards on argv shape, so no name gate may
        // exclude it.
        var auditor = LanguagePresetHelpers.Shell(
            "fsharp",
            "*.fsproj",
            "echo .",
            "fsharp:build",
            ["dotnet", "build", "--no-incremental", "/warnaserror"]);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(
                new MarkerDiscoverySandbox(NuGetMigrationsException),
                "/work",
                new AuditContext(
                    WorkItemId.New(),
                    WorkBranch: "codeybox/test",
                    BaseBranch: "main",
                    Iteration: 1,
                    OriginalPrompt: "add a feature")));

        Assert.False(ex.IsDeterministic);
    }

    [Fact]
    public async Task CustomNonDotnetShellAuditor_SameOutput_StaysGenericFinding()
    {
        // The first-run signature is dotnet-specific: a non-dotnet custom
        // auditor whose command happens to print the same text keeps the
        // ordinary command-failure finding.
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithCustomAuditor(["grep", "-q", "pattern", "file.txt"]);
        var auditor = Assert.Single(composer.Compose(project, new ScriptedAgent([])));

        var result = await auditor.RunAsync(
            new FirstRunFailingSandbox(NuGetMigrationsException),
            "/work",
            new AuditContext(
                WorkItemId.New(),
                WorkBranch: "codeybox/test",
                BaseBranch: "main",
                Iteration: 1,
                OriginalPrompt: "add a feature"));

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    private static Project ProjectWithCustomAuditor(IReadOnlyList<string> argv) => new()
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
                    Name = "custom:gate",
                    Kind = "shell",
                    Argv = argv,
                },
            ],
        },
    };

    /// <summary>
    /// Sandbox for the language-preset path: the preset discovery exec
    /// (<c>sh -c "echo ."</c>) reports one project directory, the tool probe
    /// succeeds, and every other exec — the wrapped dotnet command — fails
    /// with the supplied output.
    /// </summary>
    private sealed class MarkerDiscoverySandbox(string commandOutput) : ISandbox
    {
        public string Id => "marker-discovery";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count == 3
                && exec.Argv[0] == "sh"
                && exec.Argv[1] == "-c"
                && exec.Argv[2] == "echo .")
            {
                return Task.FromResult(new SandboxExecResult(0, ".\n", string.Empty));
            }

            if (exec.Argv.Count > 2
                && exec.Argv[0] == "sh"
                && exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new SandboxExecResult(1, commandOutput, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sandbox whose missing-tool probe succeeds and whose command exec fails
    /// with the supplied output — the shape a crashed dotnet first-use leaves.
    /// </summary>
    private sealed class FirstRunFailingSandbox(string commandOutput) : ISandbox
    {
        public string Id => "first-run-failing";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            // The tool-presence probe is `sh -c 'command -v "$1" ...'`; let it
            // succeed so the auditor proceeds to the command exec under test.
            if (exec.Argv.Count > 2
                && exec.Argv[0] == "sh"
                && exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new SandboxExecResult(1, commandOutput, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
