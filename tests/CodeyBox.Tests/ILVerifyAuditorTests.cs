using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.ILVerifyAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the ILVerify compiled-assembly auditor plugin:
/// - Missing binary (ilverify or dotnet) or wrong-version tool is an
///   infrastructure failure naming the tool (never a pass or finding).
/// - Exit codes 0 (Verified. marker) and 2 ([IL]: Error lines) are verdicts;
///   exit 1 (loader could not start), 134 (tool exception), and anything else
///   are infrastructure. Exit 0 without the marker and exit 2 without error
///   lines fail closed; loader-error lines (FileLoadErrorGeneric and friends)
///   are infrastructure, never code defects.
/// - Verification failures map to Error findings with verifier codes and
///   repository-relative assembly locations (absolute tool paths are
///   relativized against the probed scan root).
/// - Assemblies/ReferenceAssemblies are required, bounded, and probed: unset,
///   malformed, glob-shaped assemblies, missing files, and empty globs are
///   deterministic infrastructure.
/// - Plugin is disabled by default, absent from baseline provisioning until
///   enabled; enabled it declares ilverify + dotnet requirements.
/// - Real binary execution tests under [Trait("requires_ilverify", "true")].
/// </summary>
public sealed class ILVerifyAuditorTests
{
    private static readonly string? InstalledILVerifyVersion = ProbeInstalledILVerifyVersion();

    // Shapes captured from dotnet-ilverify 10.0.12. The tool canonicalizes
    // input paths to absolute sandbox paths in its report, so fixtures use
    // /work-absolute paths and the FakeSandbox answers the pwd scan-root
    // probe with /work — findings must come back repository-relative.
    private const string CleanStdout =
        "All Classes and Methods in /work/artifacts/valid.dll Verified.\n";

    private const string ErrorStdout =
        "[IL]: Error [StackUnexpected]: [/work/artifacts/invalid.dll : Fixture.Class1::GetName()][offset 0x00000001][found Int32][expected ref 'string'] Unexpected type on the stack.\n"
        + "1 Error(s) Verifying /work/artifacts/invalid.dll\n";

    private const string MixedStdout =
        "All Classes and Methods in /work/artifacts/valid.dll Verified.\n"
        + "[IL]: Error [StackUnexpected]: [/work/artifacts/invalid.dll : Fixture.Class1::GetName()][offset 0x00000001][found Int32][expected ref 'string'] Unexpected type on the stack.\n"
        + "1 Error(s) Verifying /work/artifacts/invalid.dll\n";

    private const string LoaderLineStdout =
        "[IL]: Error [FileLoadErrorGeneric]: [/work/artifacts/app.dll : Fixture.User::Run()] Failed to load assembly 'lib'\n"
        + "1 Error(s) Verifying /work/artifacts/app.dll\n";

    private const string LoaderStartupStdout =
        "Error: Assembly or module not found: mscorlib\n"
        + "Internal.IL.VerifierException: Assembly or module not found: mscorlib\n"
        + "   at ILVerify.Verifier.SetSystemModuleName(AssemblyNameInfo name)\n";

    private const string MissingInputStdout =
        "Unhandled exception. System.CommandLine.CommandLineException: No files matching ./artifacts/does-not-exist.dll\n";

    private static readonly IReadOnlySet<string> DefaultRepoFiles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "artifacts/valid.dll",
            "artifacts/invalid.dll",
            "artifacts/app.dll",
            "refs/mscorlib.dll",
            "refs/netstandard.dll",
            "refs/System.Console.dll",
            "refs/System.Private.CoreLib.dll",
            "refs/System.Runtime.dll",
            "refs/lib.dll",
        };

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingIlverify_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(
            DefaultRepoFiles,
            (exec, _) => Task.FromResult<SandboxExecResult?>(
                IsPresenceProbe(exec, "ilverify") ? new SandboxExecResult(1, "", "") : null),
            onScan: _ =>
            {
                scanExecs++;
                return new SandboxExecResult(0, CleanStdout, "");
            });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingDotnet_IsInfrastructureFailure_NamingDotnet()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, (exec, _) =>
        {
            if (IsPresenceProbe(exec, "dotnet"))
                return Task.FromResult<SandboxExecResult?>(new SandboxExecResult(1, "", ""));
            return Task.FromResult<SandboxExecResult?>(null);
        }, onScan: _ => { scanExecs++; return new SandboxExecResult(0, CleanStdout, ""); });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dotnet", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingIlverify()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(
            DefaultRepoFiles,
            (exec, _) => Task.FromResult<SandboxExecResult?>(
                IsVersionProbe(exec) ? new SandboxExecResult(127, "", "") : null),
            onScan: _ =>
            {
                scanExecs++;
                return new SandboxExecResult(0, CleanStdout, "");
            });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(
            DefaultRepoFiles,
            (exec, _) => Task.FromResult<SandboxExecResult?>(
                IsVersionProbe(exec) ? new SandboxExecResult(0, "9.0.0-rtm.24528.9+abc\n", "") : null),
            onScan: _ =>
            {
                scanExecs++;
                return new SandboxExecResult(0, CleanStdout, "");
            });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ILVerifyAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithVerificationError_YieldsErrorFinding_WithCodeAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: exec =>
        {
            scanExec = exec;
            return new SandboxExecResult(2, ErrorStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(InvalidScope(), CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:ilverify", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("artifacts/invalid.dll", finding.Location);
        Assert.Contains("StackUnexpected", finding.Title, StringComparison.Ordinal);
        Assert.Contains("StackUnexpected", finding.Description, StringComparison.Ordinal);
        Assert.Contains("Severity (tool): error", finding.Description, StringComparison.Ordinal);
        Assert.Contains("Fixture.Class1::GetName", finding.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("/work/", finding.Location, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("ilverify", scanExec!.Argv[0]);
        Assert.Equal("./artifacts/invalid.dll", scanExec.Argv[1]);
        var referenceIndex = scanExec.Argv.ToList().IndexOf("-r");
        Assert.True(referenceIndex >= 0 && referenceIndex + 1 < scanExec.Argv.Count);
        Assert.Equal("./refs/*.dll", scanExec.Argv[referenceIndex + 1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("ilverify ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(0, CleanStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task MixedInputs_VerifiedMarkerPlusError_YieldsSingleFinding_AndFails()
    {
        var sandbox = FakeRepoSandbox(
            DefaultRepoFiles,
            null,
            onScan: _ => new SandboxExecResult(2, MixedStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll,artifacts/invalid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("artifacts/invalid.dll", finding.Location);
    }

    [Fact]
    public async Task Exit0_WithoutVerifiedMarker_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(0, "ilverify 10.0.12-servicing.26422.108\n", ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_WithoutErrorLines_IsInfrastructureFailure()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, LoaderStartupStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoaderErrorLine_IsInfrastructureFailure_NamingReferenceClosure()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, LoaderLineStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/app.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(ILVerifyAuditor.ReferenceAssembliesKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit1_LoaderStartupFailure_IsInfrastructureFailure()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(1, LoaderStartupStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(134, MissingInputStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 134", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedOutput_IsInfrastructureFailure()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, "not json, not ilverify, just chatter\n", ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ilverify", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedOutput_IsInfrastructureFailure_NeverAPass()
    {
        // A capture cut mid-report carries neither the Verified. marker nor
        // a complete error line: the parser must fail closed on it.
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, "[IL]: Error [StackUnexpec", ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(InvalidScope(), CancellationToken.None);
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task MissingAssembliesConfiguration_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(ILVerifyAuditor.AssembliesKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingAssemblyFile_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/does-not-exist.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("does-not-exist.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("artifacts/*.dll")]
    [InlineData("artifacts/app.cs")]
    [InlineData("../outside/evil.dll")]
    [InlineData("/etc/passwd.dll")]
    [InlineData("-evil.dll")]
    public async Task InvalidAssemblyEntry_IsDeterministicInfrastructure(string entry)
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = entry,
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingReferenceAssembliesConfiguration_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(ILVerifyAuditor.ReferenceAssembliesKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingLiteralReference_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/no-such-framework.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("no-such-framework.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task EmptyReferenceGlob_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/empty-*.dll",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("empty-*.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("../refs/*.dll")]
    [InlineData("refs/*")]
    [InlineData("-refs/*.dll")]
    public async Task InvalidReferenceEntry_IsDeterministicInfrastructure(string entry)
    {
        var scanExecs = 0;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
        {
            scanExecs++;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = entry,
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherCodes()
    {
        const string twoErrors =
            "[IL]: Error [StackUnexpected]: [/work/artifacts/invalid.dll : Fixture.Class1::GetName()][offset 0x00000001] Unexpected type on the stack.\n"
            + "[IL]: Error [ExpectedNumericType]: [/work/artifacts/invalid.dll : Fixture.Class1::Add()][offset 0x00000002] Expected numeric type.\n"
            + "2 Error(s) Verifying /work/artifacts/invalid.dll\n";
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, twoErrors, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/invalid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                ["Scoped:IncludedRules"] = "ExpectedNumericType",
            }),
            CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("ExpectedNumericType", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_DropsAssemblyFindings()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(2, ErrorStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/invalid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                ["Scoped:ExcludePaths"] = "artifacts/",
            }),
            CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_AreStructuredArgv_NotAShellString()
    {
        SandboxExec? scanExec = null;
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: exec =>
        {
            scanExec = exec;
            return new SandboxExecResult(0, CleanStdout, "");
        });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                ["Scoped:ExtraArguments"] = "--statistics,--tokens",
            }),
            CancellationToken.None);
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("ilverify", scanExec!.Argv[0]);
        Assert.Contains("--statistics", scanExec.Argv);
        Assert.Contains("--tokens", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains("--statistics,--tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = FakeRepoSandbox(
            DefaultRepoFiles,
            null,
            onScan: _ => new SandboxExecResult(0, CleanStdout, ""),
            onScanAsync: async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new SandboxExecResult(0, CleanStdout, "");
            });

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                ["Scoped:TimeoutSeconds"] = "1",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates_WithoutSwallowing()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, null, onScan: _ =>
            new SandboxExecResult(0, CleanStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(DefaultScope(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolAbsentFromBaselineProvisioning()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        Assert.Empty(loader.DiscoverPlugins());
        var status = Assert.Single(
            loader.GetDiscoveryStatuses(),
            s => s.PluginId == ILVerifyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("ilverify", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresIlverifyAndDotnetRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ILVerifyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ILVerifyAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var binaries = tools.Select(static t => t.Binary).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ilverify", binaries);
        Assert.Contains("dotnet", binaries);
        // Verify-only by design: dotnet-ilverify ships via dotnet tool, no
        // distro apt package carries a version pin.
        Assert.All(tools, static t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("ilverify", flattened, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        var sandbox = FakeRepoSandbox(DefaultRepoFiles, onExec: (exec, _) =>
        {
            if (IsVersionProbe(exec))
                return Task.FromResult<SandboxExecResult?>(new SandboxExecResult(0, "ilverify 9.9.9-servicing.1+abc\n", ""));
            return Task.FromResult<SandboxExecResult?>(null);
        }, onScan: _ => new SandboxExecResult(0, CleanStdout, ""));

        var auditor = new ILVerifyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "9.9.9",
                ["Scoped:Assemblies"] = "artifacts/valid.dll",
                ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
            }),
            CancellationToken.None);

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Parser_MapsAbsoluteAssemblyPaths_ToRepoRelativeLocations()
    {
        var parser = new ILVerifyOutputParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "ilverify", ErrorStdout, "", 2, ScanRoot: "/work", WorkingDirectory: "/work"));

        var finding = Assert.Single(findings);
        Assert.Equal("StackUnexpected", finding.RuleId);
        Assert.Equal("error", finding.SeverityLevel);
        Assert.Equal("artifacts/invalid.dll", finding.Path);
        Assert.Null(finding.Line);
        Assert.Contains("Fixture.Class1::GetName", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_LoaderLines_ThrowParseException()
    {
        var parser = new ILVerifyOutputParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("ilverify", LoaderLineStdout, "", 2)));
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("ilverify", LoaderStartupStdout, "", 1)));
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("ilverify", "", "", 2)));
    }

    [Fact]
    [Trait("requires_ilverify", "true")]
    public async Task RealIlverify_CombinedFixture_YieldsActionableFinding()
    {
        var installed = InstalledILVerifyVersion;
        var frameworkDir = FindSharedFrameworkDir();
        if (installed is null || frameworkDir is null)
            return;

        var fixtureDir = await SeedILVerifyFixtureRepoAsync(frameworkDir);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new ILVerifyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Assemblies"] = "artifacts/valid.dll,artifacts/invalid.dll",
                    ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                }),
                CancellationToken.None);

            var result = await auditor.RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("artifacts/invalid.dll", finding.Location);
            Assert.Contains("StackUnexpected", finding.Title, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_ilverify", "true")]
    public async Task RealIlverify_CleanFixture_Passes()
    {
        var installed = InstalledILVerifyVersion;
        var frameworkDir = FindSharedFrameworkDir();
        if (installed is null || frameworkDir is null)
            return;

        var fixtureDir = await SeedILVerifyFixtureRepoAsync(frameworkDir);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new ILVerifyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Assemblies"] = "artifacts/valid.dll",
                    ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
                }),
                CancellationToken.None);

            var result = await auditor.RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static PluginContext DefaultScope()
        => BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Assemblies"] = "artifacts/valid.dll",
            ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
        });

    private static PluginContext InvalidScope()
        => BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Assemblies"] = "artifacts/invalid.dll",
            ["Scoped:ReferenceAssemblies"] = "refs/*.dll",
        });

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ILVerifyAuditorPlugin.dll");
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: ILVerifyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ILVerify Compiled-Assembly Auditor",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "ilverify " + ILVerifyAuditor.DefaultExpectedVersion + "-servicing.26422.108\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec, string binary)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains(binary, StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "ilverify" && exec.Argv[1] == "--version";

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    private static bool IsGlobProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("find .", StringComparison.Ordinal);

    // Simulates the shared shell probes against an in-memory file set so
    // tests exercise the real probing path (presence batching, glob
    // expansion, scan-root resolution) without a live sandbox. An optional
    // override intercepts specific execs (e.g. failing presence); the scan
    // itself always goes to onScan.
    private static FakeSandbox FakeRepoSandbox(
        IReadOnlySet<string> repoFiles,
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult?>>? onExec,
        Func<SandboxExec, SandboxExecResult> onScan,
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>>? onScanAsync = null)
        => new(async (exec, ct) =>
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (onExec is not null)
            {
                var overridden = await onExec(exec, ct);
                if (overridden is not null)
                    return overridden;
            }
            if (IsPresenceProbe(exec, "ilverify") || IsPresenceProbe(exec, "dotnet")
                || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Ok(exec);
            if (IsFileProbe(exec))
            {
                var requested = exec.Argv.Skip(3);
                var echoed = requested.Where(f => repoFiles.Contains(f, StringComparer.Ordinal));
                return new SandboxExecResult(0, string.Join("\n", echoed) + (echoed.Any() ? "\n" : ""), "");
            }
            if (IsGlobProbe(exec))
            {
                var matches = new List<string>();
                foreach (var glob in exec.Argv.Skip(3))
                {
                    var matcher = new Regex(
                        "^./" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal)
                            .Replace("\\?", ".", StringComparison.Ordinal) + "$",
                        RegexOptions.CultureInvariant);
                    matches.AddRange(repoFiles
                        .Where(f => matcher.IsMatch("./" + f))
                        .Select(static f => "./" + f));
                }
                var distinct = matches.Distinct(StringComparer.Ordinal).ToList();
                return new SandboxExecResult(0, string.Join("\n", distinct) + (distinct.Count > 0 ? "\n" : ""), "");
            }
            if (onScanAsync is not null)
                return await onScanAsync(exec, ct);
            return onScan(exec);
        });

    private static string? ProbeInstalledILVerifyVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ilverify",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return process.ExitCode == 0 && match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSharedFrameworkDir()
    {
        foreach (var root in new[]
        {
            "/usr/share/dotnet/shared/Microsoft.NETCore.App",
            "/usr/lib/dotnet/shared/Microsoft.NETCore.App",
            Environment.GetEnvironmentVariable("DOTNET_ROOT") + "/shared/Microsoft.NETCore.App",
        })
        {
            try
            {
                if (root is null || !Directory.Exists(root))
                    continue;
                var best = Directory.GetDirectories(root)
                    .OrderByDescending(static d => d, StringComparer.Ordinal)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "System.Private.CoreLib.dll")));
                if (best is not null)
                    return best;
            }
            catch
            {
                // Filesystem probing is best-effort; absence skips the test.
            }
        }

        return null;
    }

    private static readonly string[] FixtureReferenceNames =
    [
        "mscorlib.dll",
        "netstandard.dll",
        "System.Console.dll",
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
    ];

    private static async Task<string> SeedILVerifyFixtureRepoAsync(string frameworkDir)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-ilverify-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "artifacts"));
        Directory.CreateDirectory(Path.Combine(dir, "refs"));

        await File.WriteAllBytesAsync(
            Path.Combine(dir, "artifacts", "valid.dll"), Convert.FromBase64String(ValidAssemblyBase64));
        await File.WriteAllBytesAsync(
            Path.Combine(dir, "artifacts", "invalid.dll"), Convert.FromBase64String(InvalidAssemblyBase64));
        foreach (var name in FixtureReferenceNames)
            File.Copy(Path.Combine(frameworkDir, name), Path.Combine(dir, "refs", name));

        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture teardown */ }
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        public string Id => "fake";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string ValidAssemblyBase64 = "TVqQAAMAAAAEAAAA//8AALgAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAA4fug4AtAnNIbgBTM0hVGhpcyBwcm9ncmFtIGNhbm5vdCBiZSBydW4gaW4gRE9TIG1vZGUuDQ0KJAAAAAAAAABQRQAATAEDAHoBQPsAAAAAAAAAAOAAIiALATAAAAgAAAAGAAAAAAAAKiYAAAAgAAAAQAAAAAAAEAAgAAAAAgAABAAAAAAAAAAEAAAAAAAAAACAAAAAAgAAAAAAAAMAYIUAABAAABAAAAAAEAAAEAAAAAAAABAAAAAAAAAAAAAAANUlAABPAAAAAEAAAPQCAAAAAAAAAAAAAAAAAAAAAAAAAGAAAAwAAAAQJQAAVAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAACAAAAAAAAAAAAAAACCAAAEgAAAAAAAAAAAAAAC50ZXh0AAAAMAYAAAAgAAAACAAAAAIAAAAAAAAAAAAAAAAAACAAAGAucnNyYwAAAPQCAAAAQAAAAAQAAAAKAAAAAAAAAAAAAAAAAABAAABALnJlbG9jAAAMAAAAAGAAAAACAAAADgAAAAAAAAAAAAAAAAAAQAAAQgAAAAAAAAAAAAAAAAAAAAAJJgAAAAAAAEgAAAACAAUAWCAAALgEAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABICA1gqAAAAQlNKQgEAAQAAAAAADAAAAHY0LjAuMzAzMTkAAAAABQBsAAAAfAEAACN+AADoAQAA8AEAACNTdHJpbmdzAAAAANgDAAAEAAAAI1VTANwDAAAQAAAAI0dVSUQAAADsAwAAzAAAACNCbG9iAAAAAAAAAAIAAAFHFQAACQAAAAD6ATMAFgAAAQAAAA0AAAACAAAAAQAAAAIAAAALAAAACwAAAAEAAAABAAAAAAB+AQEAAAAAAAYA9AC6AQYARgG6AQYANgCnAQ8A2gEAAAYAYQBkAQYALQGPAQYAvQCPAQYAegCPAQYAlwCPAQYAFAGPAQYASgCPAQYA3AC6AQYA6QGIAQAAAAAIAAAAAAABAAEAgQEQAAEALgA1AAEAAQBQIAAAAACWABUAHgABAAAAAQARAAAAAgATAAkAoQEBABEAoQEGABkAoQEKACkAoQEQADEAoQEQADkAoQEQAEEAoQEQAEkAoQEQAFEAoQEQAFkAoQEQAGEAoQEBACcAWwDDAC4ACwAkAC4AEwAtAC4AGwBMAC4AIwBVAC4AKwCTAC4AMwCeAC4AOwCrAC4AQwC4AC4ASwCTAC4AUwCTAASAAAABAAAAAAAAAAAAAAAAABkAAAAJAAAAAAAAAAAAAAAVAB8AAAAAAAAAAAAAQ2xhc3MxADxNb2R1bGU+AGEAYgBBZGQAdmFsaWQAU3lzdGVtLlJ1bnRpbWUARml4dHVyZQBEZWJ1Z2dhYmxlQXR0cmlidXRlAEFzc2VtYmx5VGl0bGVBdHRyaWJ1dGUAVGFyZ2V0RnJhbWV3b3JrQXR0cmlidXRlAEFzc2VtYmx5RmlsZVZlcnNpb25BdHRyaWJ1dGUAQXNzZW1ibHlJbmZvcm1hdGlvbmFsVmVyc2lvbkF0dHJpYnV0ZQBBc3NlbWJseUNvbmZpZ3VyYXRpb25BdHRyaWJ1dGUAUmVmU2FmZXR5UnVsZXNBdHRyaWJ1dGUAQ29tcGlsYXRpb25SZWxheGF0aW9uc0F0dHJpYnV0ZQBBc3NlbWJseVByb2R1Y3RBdHRyaWJ1dGUAQXNzZW1ibHlDb21wYW55QXR0cmlidXRlAFJ1bnRpbWVDb21wYXRpYmlsaXR5QXR0cmlidXRlAFN5c3RlbS5SdW50aW1lLlZlcnNpb25pbmcAdmFsaWQuZGxsAFN5c3RlbQBTeXN0ZW0uUmVmbGVjdGlvbgAuY3RvcgBTeXN0ZW0uRGlhZ25vc3RpY3MAU3lzdGVtLlJ1bnRpbWUuQ29tcGlsZXJTZXJ2aWNlcwBEZWJ1Z2dpbmdNb2RlcwBPYmplY3QAAAAAAArxfYlR3JlLgOVWzzu4dFUABCABAQgDIAABBSABARERBCABAQ4IsD9ffxHVCjoFAAIICAgIAQAIAAAAAAAeAQABAFQCFldyYXBOb25FeGNlcHRpb25UaHJvd3MBCAEAAgAAAAAAPQEAGC5ORVRDb3JlQXBwLFZlcnNpb249djkuMAEAVA4URnJhbWV3b3JrRGlzcGxheU5hbWUILk5FVCA5LjAKAQAFdmFsaWQAAAwBAAdSZWxlYXNlAAAMAQAHMS4wLjAuMAAACgEABTEuMC4wAAAI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AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private const string InvalidAssemblyBase64 = "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";
}
