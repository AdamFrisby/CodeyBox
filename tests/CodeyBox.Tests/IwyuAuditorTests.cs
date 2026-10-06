using CodeyBox.Core;
using CodeyBox.IwyuAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the include-what-you-use auditor plugin:
/// - CompilationDatabase is required; missing path / directory without
///   compile_commands.json / malformed JSON / malformed entries / overlarge
///   databases / zero in-worktree entries are deterministic infrastructure.
/// - Repository-relative database paths must canonicalize inside the
///   worktree; absolute operator paths may live anywhere.
/// - iwyu_tool drives the verified include-what-you-use engine; missing or
///   wrong-version binaries are infrastructure failures, never passes.
/// - Exit 0 is the only verdict; IWYU 0.17+ exits 1 on unrecoverable
///   per-unit errors, so any non-zero run is infrastructure — partial
///   translation-unit coverage is never a pass.
/// - Add/remove blocks map to advisory iwyu-add/iwyu-remove findings with
///   normalized locations; a per-run iwyu-coverage info finding counts the
///   files that received verdicts; exit-0 output without verdict records
///   fails closed.
/// - IWYU_BINARY is stripped from the tool environment so the pinned engine
///   cannot be swapped under the driver.
/// - Operator -p and post-"--" --error/--error_always flags are rejected
///   deterministically (they would bypass database validation or renumber
///   the exit convention).
/// - Plugin is disabled by default; when enabled it declares the iwyu apt
///   package for both binaries.
/// </summary>
public sealed class IwyuAuditorTests
{
    // Shape mirrors real `iwyu_tool -p db .` aggregated output (IWYU 0.21):
    // per-file verdict blocks separated by "---", correct-marker lines, and
    // merged clang diagnostics when a unit degrades.
    private const string OutputWithFindings = """
        /work/src/app.cpp should add these lines:
        #include <vector>          // for std::vector

        /work/src/app.cpp should remove these lines:
        - #include <stdio.h>  // lines 3-3

        The full include-list for /work/src/app.cpp:
        #include <vector>          // for std::vector
        #include "src/app.h"
        ---

        (/work/src/util.h has correct #includes/fwd-decls)
        """;

    private const string OutputClean = """
        (/work/src/app.cpp has correct #includes/fwd-decls)

        (/work/src/lib.cpp has correct #includes/fwd-decls)
        """;

    // Two translation units each emitting a remove suggestion under their
    // own path — the findings stay per-file distinct.
    private const string OutputDuplicatedAcrossTus = """
        /work/src/a.cpp should add these lines:

        /work/src/a.cpp should remove these lines:
        - #include "shared.h"  // lines 4-4

        The full include-list for /work/src/a.cpp:
        ---

        /work/src/b.cpp should add these lines:

        /work/src/b.cpp should remove these lines:
        - #include "shared.h"  // lines 7-7

        The full include-list for /work/src/b.cpp:
        ---
        """;

    private const string OutputVendored = """
        /work/vendor/lib.cpp should add these lines:
        #include <map>             // for map

        /work/vendor/lib.cpp should remove these lines:

        The full include-list for /work/vendor/lib.cpp:
        #include <map>             // for map
        ---
        """;

    private const string OutputAbsoluteOutsidePath = """
        /usr/include/thing.h should add these lines:
        #include <stdint.h>        // for uint32_t

        /usr/include/thing.h should remove these lines:

        The full include-list for /usr/include/thing.h:
        #include <stdint.h>        // for uint32_t
        ---
        """;

    private const string DefaultDatabase = """
        [
          { "directory": "/work",
            "file": "/work/src/app.cpp",
            "arguments": ["clang++", "-std=c++17", "-c", "src/app.cpp"] },
          { "directory": "/work",
            "file": "/work/src/lib.cpp",
            "arguments": ["clang++", "-c", "src/lib.cpp"] },
          { "directory": "/work/build",
            "file": "/tmp/generated/proto.cc",
            "arguments": ["clang++", "-c", "/tmp/generated/proto.cc"] }
        ]
        """;

    private const string EngineBanner =
        "include-what-you-use 0.21 based on Ubuntu clang version 17.0.6\n";

    [Fact]
    public async Task MissingDatabaseConfig_IsDeterministicInfrastructure_NamesKey()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new IwyuAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("CompilationDatabase", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task DatabasePathMissing_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsDatabasePathProbe(exec)
                ? new SandboxExecResult(0, "/work/build\n/work\nmissing\n", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseDirectoryWithoutCompileCommands_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsDatabasePathProbe(exec)
                ? new SandboxExecResult(0, "/work/build\n/work\ndir-no-db\n", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("compile_commands.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseEscapingWorktree_IsInfrastructureFailure()
    {
        // A repo-controlled symlink redirecting a relative path outside the
        // tree must fail closed.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsDatabasePathProbe(exec)
                ? new SandboxExecResult(0, "/outside/compile_commands.json\n/work\nfile\n", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("outside the audited worktree", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsoluteDatabasePath_IsOperatorOwned_AllowedOutsideWorktree()
    {
        SandboxExec? scan = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsDatabasePathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/opt/db/compile_commands.json\n/work\nfile\n", ""));
            if (exec.Argv[0] == "iwyu_tool")
            {
                scan = exec;
                return Task.FromResult(new SandboxExecResult(0, OutputClean, ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor("/opt/db/compile_commands.json");
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scan);
        Assert.Equal(["iwyu_tool", "-p", "/opt/db/compile_commands.json", "."], scan!.Argv);
    }

    [Fact]
    public async Task DatabaseDirectoryForm_ResolvesCompileCommandsJson()
    {
        SandboxExec? read = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsDatabasePathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/build\n/work\ndir\n", ""));
            if (exec.Argv[0] == "cat")
            {
                read = exec;
                return Task.FromResult(new SandboxExecResult(0, DefaultDatabase, ""));
            }
            if (exec.Argv[0] == "iwyu_tool")
                return Task.FromResult(new SandboxExecResult(0, OutputClean, ""));
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor("build");
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(read);
        Assert.Equal("/work/build/compile_commands.json", read!.Argv[2]);
    }

    [Fact]
    public async Task MalformedDatabaseJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "cat"
                ? new SandboxExecResult(0, "this is not json", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseEntryMissingFile_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "cat"
                ? new SandboxExecResult(0, "[{ \"directory\": \"/work\", \"arguments\": [\"c\"] }]", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseEntryMissingDirectory_IsInfrastructureFailure()
    {
        // iwyu_tool reads entry['directory'] unconditionally — a missing
        // field would crash the driver mid-run; the auditor fails
        // deterministically instead.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "cat"
                ? new SandboxExecResult(
                    0,
                    "[{ \"file\": \"/work/src/app.cpp\", \"arguments\": [\"c\"] }]",
                    "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseWithNoInWorktreeEntries_IsInfrastructureFailure_NeverVacuousPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
                scanExecs++;
            return Task.FromResult(exec.Argv[0] == "cat"
                ? new SandboxExecResult(
                    0,
                    "[{ \"directory\": \"/tmp\", \"file\": \"/tmp/x.cc\", \"arguments\": [\"c\"] }]",
                    "")
                : Ok(exec));
        });

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("no", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DatabaseOverEntryBound_IsInfrastructureFailure()
    {
        var db = "["
            + string.Join(",", Enumerable.Range(0, 5).Select(
                i => $"{{ \"directory\": \"/work\", \"file\": \"/work/f{i}.cc\", \"arguments\": [\"c\"] }}"))
            + "]";
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "cat" ? new SandboxExecResult(0, db, "") : Ok(exec)));

        var auditor = await InitializedAuditor(
            maxEntries: "3");
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("entries", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingIwyuTool_IsInfrastructureFailure_NamingDriver()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("iwyu_tool", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingEngine_IsInfrastructureFailure_NamingBinary()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    exec.Argv.Contains("include-what-you-use", StringComparer.Ordinal) ? 1 : 0, "", ""));
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("include-what-you-use", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task EngineVersionMismatch_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsVersionProbe(exec)
                ? new SandboxExecResult(0, "include-what-you-use 0.23 based on clang version 19.1.0\n", "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("include-what-you-use", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.23", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineVersionUnreadable_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsVersionProbe(exec)
                ? new SandboxExecResult(127, "", "include-what-you-use: command not found")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("include-what-you-use", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredExpectedVersion_TwoPartOrThreePart_MatchesBanner()
    {
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(Ok(exec)));
        var auditor = await InitializedAuditor(expectedVersion: "0.21");

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ConfiguredExpectedVersion_Unparseable_IsDeterministicInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(Ok(exec)));
        var auditor = await InitializedAuditor(expectedVersion: "not-a-version");

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task FindingsFixture_YieldsAddRemoveFindings_AndCoverageRecord()
    {
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed); // advisory gate: warnings never block

        var add = Assert.Single(result.Findings, f => f.Title.Contains("iwyu-add", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, add.Severity);
        Assert.Equal("src/app.cpp", add.Location);
        Assert.Contains("#include <vector>", add.Title, StringComparison.Ordinal);

        var remove = Assert.Single(result.Findings, f => f.Title.Contains("iwyu-remove", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, remove.Severity);
        Assert.Equal("src/app.cpp:3", remove.Location);

        var coverage = Assert.Single(result.Findings, f => f.Title.Contains("iwyu-coverage", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, coverage.Severity);
        Assert.Contains("2 file(s)", coverage.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanOutput_Passes_WithCoverageRecordOnly()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, OutputClean, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var coverage = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, coverage.Severity);
        Assert.Contains("2 file(s)", coverage.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit0_WithNoVerdictRecords_IsInfrastructureFailure_NeverVacuousPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, string.Empty, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Exit1_AnyUnitUnrecoverable_IsInfrastructureFailure_NotPartialPass()
    {
        // iwyu_tool exits the worst child code: one translation unit's
        // unrecoverable error makes the whole run unverifiable — never a
        // partial pass over the units that did analyse.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(
                    1,
                    OutputWithFindings + "\n/work/src/bad.cpp:5:1: error: expected ';'\n",
                    "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("iwyu_tool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_UsageError_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(2, "", "usage: iwyu_tool [-h] ...")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SameSuggestionInDifferentTus_MapsToItsOwnFile()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, OutputDuplicatedAcrossTus, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var removals = result.Findings.Where(f => f.Title.Contains("iwyu-remove", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, removals.Count); // one per TU — the suggestion lives in different files
        Assert.Contains(removals, f => f.Location == "src/a.cpp:4");
        Assert.Contains(removals, f => f.Location == "src/b.cpp:7");
    }

    [Fact]
    public async Task IdenticalDuplicate_CollapsesToOneFinding()
    {
        var duplicated = OutputWithFindings + "\n" + OutputWithFindings;
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, duplicated, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(
            3,
            result.Findings.Count); // add + remove + coverage; the repeated blocks dedup
    }

    [Fact]
    public async Task VendoredFindings_AreDroppedByDefaultExcludePaths()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, OutputVendored, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("iwyu-add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportedPathOutsideScanRoot_IsFileSchemeMarked_NeverRepoRelative()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1
                ? new SandboxExecResult(0, OutputAbsoluteOutsidePath, "")
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings, f => f.Title.Contains("iwyu-add", StringComparison.Ordinal));
        Assert.StartsWith("file://", finding.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanExec_StripsIwyuBinaryOverride()
    {
        SandboxExec? scan = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
            {
                scan = exec;
                return Task.FromResult(new SandboxExecResult(0, OutputClean, ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor();
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scan);
        Assert.Contains("IWYU_BINARY", scan!.EnvironmentVariablesToUnset);
    }

    [Fact]
    public async Task OperatorCompilationDatabaseFlag_IsDeterministicInfrastructure()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor(extraArguments: "-p,/other");
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task OperatorForwardedErrorFlag_IsDeterministicInfrastructure()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor(extraArguments: "--,-Xiwyu,--error=1");
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("--error", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            return Ok(exec);
        });

        var auditor = await InitializedAuditor(timeoutSeconds: "1");
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates_NotWrapped()
    {
        using var cts = new CancellationTokenSource();
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
            {
                cts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            return Ok(exec);
        });

        var auditor = await InitializedAuditor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public async Task DatabaseReadOversized_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(exec.Argv[0] == "cat"
                ? new SandboxExecResult(0, new string('x', 4096), "", StdoutLimitExceeded: true)
                : Ok(exec)));

        var auditor = await InitializedAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("bound", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultArguments_DatabaseFileAndWorktreeSelector()
    {
        SandboxExec? scan = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
            {
                scan = exec;
                return Task.FromResult(new SandboxExecResult(0, OutputClean, ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditor();
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scan);
        Assert.Equal(["iwyu_tool", "-p", "/work/build/compile_commands.json", "."], scan!.Argv);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
            s => s.PluginId == IwyuAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("iwyu", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirements_ForDriverAndEngine()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [IwyuAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == IwyuAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Contains(tools, t => t.Binary == "iwyu_tool" && t.AptPackage == "iwyu");
        Assert.Contains(tools, t => t.Binary == "include-what-you-use" && t.AptPackage == "iwyu");
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.IwyuAuditorPlugin.dll");
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static async Task<IwyuAuditor> InitializedAuditor(
        string database = "build/compile_commands.json",
        string? expectedVersion = null,
        string? extraArguments = null,
        string? timeoutSeconds = null,
        string? maxEntries = null)
    {
        var scoped = new Dictionary<string, string?>
        {
            ["Scoped:CompilationDatabase"] = database,
        };
        if (expectedVersion is not null)
            scoped["Scoped:ExpectedVersion"] = expectedVersion;
        if (extraArguments is not null)
            scoped["Scoped:ExtraArguments"] = extraArguments;
        if (timeoutSeconds is not null)
            scoped["Scoped:TimeoutSeconds"] = timeoutSeconds;
        if (maxEntries is not null)
            scoped["Scoped:MaxCompilationDatabaseEntries"] = maxEntries;

        var config = new ConfigurationBuilder().AddInMemoryCollection(scoped).Build();
        var auditor = new IwyuAuditor();
        await auditor.InitializeAsync(
            new PluginContext(
                HostApiVersion: "1.0",
                PluginId: IwyuAuditor.PluginId,
                PluginDisplayName: "CodeyBox: Include-What-You-Use C/C++ Analyzer",
                Host: new TestPluginHost(config.GetSection("Scoped"))),
            CancellationToken.None);
        return auditor;
    }

    // Default happy-path responses per probe: db path canonicalizes in-tree
    // to build/compile_commands.json, the read returns a three-entry DB (one
    // out-of-tree), presence checks pass, the engine reports the pinned
    // version, the scan root is /work, and the scan yields the findings
    // fixture. Individual tests override whichever probe they exercise.
    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsDatabasePathProbe(exec))
            return new SandboxExecResult(0, "/work/build/compile_commands.json\n/work\nfile\n", "");
        if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
            return new SandboxExecResult(0, DefaultDatabase, "");
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, EngineBanner, "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        if (exec.Argv.Count > 0 && exec.Argv[0] == "iwyu_tool" && exec.Argv.Count > 1)
            return new SandboxExecResult(0, OutputWithFindings, "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsDatabasePathProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("realpath", StringComparison.Ordinal);

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2
            && exec.Argv[0] == "include-what-you-use"
            && exec.Argv[1] == "--version";

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

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
