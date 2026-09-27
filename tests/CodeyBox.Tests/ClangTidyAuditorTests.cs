using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ClangTidyAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Clang-Tidy auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming clang-tidy (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); other exits are infrastructure.
/// - Exit 1 without a YAML report fails closed as an infrastructure failure.
/// - Exit 0 with text diagnostics but no YAML report (hijacked --export-fixes sink) fails closed.
/// - Export-fixes YAML maps to findings with rule ids and relativized file locations (no lines:
///   the tool reports byte offsets, not lines); cross-TU duplicates collapse to one finding.
/// - Raw tool levels go through the declared mapping (Warning advisory, Error failing).
/// - Warnings alone pass: the gate is explicitly non-blocking by default.
/// - Discovery: empty scope, overflow, truncation, untrusted entries, and
///   dash-prefixed entries (CLI flag injection) are infrastructure.
/// - Scoped options (ExpectedVersion, Checks, ConfigFile, CompileFlags).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled; when
///   enabled it declares the clang-tidy apt package.
/// - Real binary execution tests under [Trait("requires_clang_tidy", "true")].
/// </summary>
public sealed class ClangTidyAuditorTests
{
    private static readonly string? InstalledClangTidyVersion = ProbeInstalledClangTidyVersion();

    // Shape mirrors real `clang-tidy --export-fixes=/dev/stdout` (18.1.3):
    // text diagnostics interleaved on stdout, one merged YAML document with
    // per-entry DiagnosticName / Message / FilePath / FileOffset / Level /
    // BuildDirectory. The nullptr diagnostic appears twice (once per
    // including TU) to pin cross-TU duplicate suppression.
    private const string YamlWithFindings = """
        /work/src/app.cpp:1:10: warning: use nullptr [modernize-use-nullptr]
            1 | int *p = 0;
              |          ^
              |          nullptr
        ---
        MainSourceFile:  '/work/src/app.cpp'
        Diagnostics:
          - DiagnosticName:  modernize-use-nullptr
            DiagnosticMessage:
              Message:         'use nullptr'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      9
              Replacements:
                - FilePath:        '/work/src/app.cpp'
                  Offset:          9
                  Length:          1
                  ReplacementText: nullptr
            Level:           Warning
            BuildDirectory:  '/work'
          - DiagnosticName:  modernize-use-nullptr
            DiagnosticMessage:
              Message:         'use nullptr'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      9
              Replacements:
                - FilePath:        '/work/src/app.cpp'
                  Offset:          9
                  Length:          1
                  ReplacementText: nullptr
            Level:           Warning
            BuildDirectory:  '/work'
          - DiagnosticName:  clang-diagnostic-error
            DiagnosticMessage:
              Message:         'expected expression'
              FilePath:        '/work/src/broken.cpp'
              FileOffset:      21
              Replacements:    []
            Level:           Error
            BuildDirectory:  '/work'
        ...
        """;

    private const string YamlWarningsOnly = """
        ---
        MainSourceFile:  '/work/src/app.cpp'
        Diagnostics:
          - DiagnosticName:  modernize-use-nullptr
            DiagnosticMessage:
              Message:         'use nullptr'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      9
              Replacements:
                - FilePath:        '/work/src/app.cpp'
                  Offset:          9
                  Length:          1
                  ReplacementText: nullptr
            Level:           Warning
            BuildDirectory:  '/work'
        ...
        """;

    private const string YamlWithLevels = """
        ---
        MainSourceFile:  '/work/src/app.cpp'
        Diagnostics:
          - DiagnosticName:  rule-error
            DiagnosticMessage:
              Message:         'Error-level diagnostic.'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      1
              Replacements:    []
            Level:           Error
            BuildDirectory:  '/work'
          - DiagnosticName:  rule-warning
            DiagnosticMessage:
              Message:         'Warning-level diagnostic.'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      2
              Replacements:    []
            Level:           Warning
            BuildDirectory:  '/work'
          - DiagnosticName:  rule-note
            DiagnosticMessage:
              Message:         'Note-level diagnostic.'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      3
              Replacements:    []
            Level:           Note
            BuildDirectory:  '/work'
          - DiagnosticName:  rule-unknown
            DiagnosticMessage:
              Message:         'Unrecognised level.'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      4
              Replacements:    []
            Level:           blocker
            BuildDirectory:  '/work'
          - DiagnosticName:  rule-nolevel
            DiagnosticMessage:
              Message:         'No level field.'
              FilePath:        '/work/src/app.cpp'
              FileOffset:      5
              Replacements:    []
            BuildDirectory:  '/work'
        ...
        """;

    // Relative FilePath entries (as produced for files under a compilation
    // database with relative paths) to exercise the finding-level
    // ExcludePaths mechanism through this auditor.
    private const string YamlWithRelativePaths = """
        ---
        MainSourceFile:  'src/app.cpp'
        Diagnostics:
          - DiagnosticName:  modernize-use-nullptr
            DiagnosticMessage:
              Message:         'use nullptr'
              FilePath:        'src/app.cpp'
              FileOffset:      9
              Replacements:    []
            Level:           Warning
            BuildDirectory:  '/work'
          - DiagnosticName:  modernize-use-nullptr
            DiagnosticMessage:
              Message:         'use nullptr'
              FilePath:        'vendor/lib.cpp'
              FileOffset:      9
              Replacements:    []
            Level:           Warning
            BuildDirectory:  '/work'
        ...
        """;

    private const string YamlWithRanges = """
        ---
        MainSourceFile:  '/work/src/app.cpp'
        Diagnostics:
          - DiagnosticName:  clang-diagnostic-unused-variable
            DiagnosticMessage:
              Message:         'unused variable ''unused_var'''
              FilePath:        '/work/src/app.cpp'
              FileOffset:      17
              Replacements:    []
              Ranges:
                - FilePath:        '/work/src/app.cpp'
                  FileOffset:      17
                  Length:          10
            Level:           Warning
            BuildDirectory:  '/work'
        ...
        """;

    // Text diagnostics with no YAML report: what stdout looks like when an
    // operator --export-fixes redirects the report away from stdout.
    private const string TextWithoutYaml = """
        /work/src/app.cpp:1:10: warning: use nullptr [modernize-use-nullptr]
            1 | int *p = 0;
              |          ^
        """;

    private const string DefaultDiscovery = "src/app.cpp\nsrc/broken.cpp\n";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingClangTidy_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "clang-tidy: command not found"));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, DefaultDiscovery, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingClangTidy()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, DefaultDiscovery, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RealVersionString_ParsesToExpectedPin()
    {
        // The exact banner the pinned Ubuntu build prints; the shared
        // first-token extractor must resolve it to DefaultExpectedVersion.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Ubuntu LLVM version 18.1.3\n  Optimized build.\n", ""));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Ubuntu LLVM version 19.1.1\n  Optimized build.\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation_Deduped()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, YamlWithFindings, "1 warning and 1 error generated.\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // The duplicated nullptr diagnostic (same rule/path/offset/message
        // re-emitted through two TUs) collapses to one finding.
        Assert.Equal(2, result.Findings.Count);

        var modernize = Assert.Single(result.Findings, f => f.Title.Contains("modernize-use-nullptr", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, modernize.Severity);
        // The YAML report carries byte offsets, not lines: the location is
        // the relativized file path with no :line suffix.
        Assert.Equal("src/app.cpp", modernize.Location);

        var diagnostic = Assert.Single(result.Findings, f => f.Title.Contains("clang-diagnostic-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, diagnostic.Severity);
        Assert.Equal("src/broken.cpp", diagnostic.Location);
    }

    [Fact]
    public async Task ExitCode0_WithWarningsOnly_Passes_WarningsAreAdvisory()
    {
        // The gate is explicitly non-blocking by default: warnings alone do
        // not fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, YamlWarningsOnly, "1 warning generated.\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("modernize-use-nullptr", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        // A clean scan emits no YAML at all (exit 0, empty stdout): that is
        // the tool's own clean signal, not a missing report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithErrorReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, YamlWithFindings, "Found compiler error(s).\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
    }

    [Fact]
    public async Task ExitCode1_WithoutReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1,
                "USAGE: clang-tidy [options] <source0> [... <sourceN>]\n",
                "Error: no checks enabled.\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_DriverErrorsWithoutReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1,
                "error: no such file or directory: '/work/src/gone.cpp' [clang-diagnostic-error]\n",
                "Error while processing /work/src/gone.cpp.\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode0_TextDiagnosticsWithoutReport_IsInfrastructureFailure()
    {
        // Stdout shows diagnostics but no YAML document: the report sink was
        // overridden (operator --export-fixes elsewhere). That must fail
        // loudly, never read as a clean pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, TextWithoutYaml, "1 warning generated.\n"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, YamlWarningsOnly, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "clang-tidy: command not found"));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, YamlWithLevels, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(5, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-nolevel", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> Warning
    }

    [Fact]
    public async Task ReportRanges_AreIgnored_EntriesStillParsed()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, YamlWithRanges, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("clang-diagnostic-unused-variable", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/app.cpp", finding.Location);
    }

    [Fact]
    public async Task DefaultArguments_ExportFixesToStdout_AndDiscoveredFiles()
    {
        SandboxExec? scanExec = null;
        SandboxExec? discoveryExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
            {
                discoveryExec = exec;
                return Task.FromResult(new SandboxExecResult(0, DefaultDiscovery, ""));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(discoveryExec);
        Assert.Equal("sh", discoveryExec!.Argv[0]);
        Assert.Contains("257", discoveryExec.Argv);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("clang-tidy", argv[0]);
        Assert.Contains("--export-fixes=/dev/stdout", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("--checks", StringComparison.Ordinal));
        Assert.Contains("src/app.cpp", argv);
        Assert.Contains("src/broken.cpp", argv);
    }

    [Fact]
    public async Task EmptyDiscovery_IsInfrastructureFailure_Deterministic()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no C/C++ translation units", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DiscoveryOverflow_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var oversized = string.Concat(Enumerable.Range(0, 300).Select(i => $"src/file{i}.cpp\n"));
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, oversized, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DiscoveryTruncation_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src/app.cpp\nsrc/half", ""));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("clang-tidy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryUntrustedEntry_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src/app.cpp\n../escape.cpp\n", ""));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task DiscoveryDashPrefixedEntry_IsInfrastructureFailure_NeverReachesScanArgv()
    {
        // A repository file named like a CLI flag (find reports it as
        // ./--checks=.c) must fail closed in discovery: LLVM keeps the last
        // occurrence of --checks/--config-file/--export-fixes and the tool
        // accepts options in any position, so passing it through would let
        // repo-controlled content override the auditor's own flags.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src/app.cpp\n./--checks=.c\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
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
            s => s.PluginId == ClangTidyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("clang-tidy", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresClangTidyRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ClangTidyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ClangTidyAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("clang-tidy", tool.Binary);
        // Apt-backed by design: the Debian package is clang-tidy's
        // distribution channel, so baseline provisioning installs it — only
        // when this plugin is enabled.
        Assert.Equal("clang-tidy", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("clang-tidy", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("clang-tidy", install, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, DefaultDiscovery, ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Ubuntu LLVM version 19.1.1\n  Optimized build.\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "19.1.1",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_Checks_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Checks"] = "-*,modernize-*",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--checks=-*,modernize-*", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_OperatorChecks_TakeOverSelection()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Checks"] = "-*,modernize-*",
                ["Scoped:ExtraArguments"] = "--checks=-*",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Single(scanExec!.Argv, a => a.StartsWith("--checks", StringComparison.Ordinal));
        Assert.Contains("--checks=-*", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigFile_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/opt/codeybox/clang-tidy.operator",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--config-file=/opt/codeybox/clang-tidy.operator", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_CompileFlags_BecomeExtraArgs()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:CompileFlags"] = "-std=c++17,-Iinclude",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--extra-arg=-std=c++17", scanExec!.Argv);
        Assert.Contains("--extra-arg=-Iinclude", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, YamlWithRelativePaths, ""));
        });

        IAuditor auditor = new ClangTidyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ finding is dropped by the default ExcludePaths; the
        // src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/app.cpp", finding.Location);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, YamlWithFindings, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "clang-diagnostic-error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("clang-diagnostic-error", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverityError_DropsWarnings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, YamlWarningsOnly, ""));
        });

        var auditor = new ClangTidyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    [Trait("requires_clang_tidy", "true")]
    public async Task RealClangTidy_DirtyFixture_YieldsFindings_WithRuleIdAndFile()
    {
        var installed = InstalledClangTidyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedClangTidyFixtureRepoAsync(clean: false);

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

            var auditor = new ClangTidyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Checks"] = "-*,modernize-use-nullptr",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.NotEmpty(result.Findings);

            var nullptrFinding = Assert.Single(
                result.Findings, f => f.Title.Contains("modernize-use-nullptr", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, nullptrFinding.Severity);
            Assert.EndsWith("bad.cpp", nullptrFinding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_clang_tidy", "true")]
    public async Task RealClangTidy_CleanFixture_Passes()
    {
        var installed = InstalledClangTidyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedClangTidyFixtureRepoAsync(clean: true);

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

            var auditor = new ClangTidyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Checks"] = "-*,modernize-use-nullptr",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ClangTidyAuditorPlugin.dll");
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
            PluginId: ClangTidyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Clang-Tidy C/C++ Analyzer",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "Ubuntu LLVM version " + ClangTidyAuditor.DefaultExpectedVersion + "\n  Optimized build.\n", "");
        if (IsDiscoveryProbe(exec))
            return new SandboxExecResult(0, DefaultDiscovery, "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("clang-tidy", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "clang-tidy" && exec.Argv[1] == "--version";

    private static bool IsDiscoveryProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("find .", StringComparison.Ordinal);

    private static async Task<string> SeedClangTidyFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-clang-tidy-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.cpp"), "int main() { return 0; }\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.cpp"), "int *p = 0;\nint main() { return *p; }\n");
        }

        return dir;
    }

    private static string? ProbeInstalledClangTidyVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "clang-tidy",
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

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
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
}
