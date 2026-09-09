using System.Diagnostics;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end contract tests for the C# test-gate invocation against the REAL
/// <c>dotnet</c> runner (no mocks, no fake <c>dotnet</c> on PATH).
///
/// Background: the 2026-09-07 gate failure. The audit ran
/// <c>dotnet test --no-build</c> in an environment whose test assemblies had
/// never been built; <c>dotnet</c> forwarded the resolved-but-absent dll to
/// VSTest, which exited 1 with
/// <c>The argument ... is invalid. Please use the /help option ...</c> — a
/// runner-invocation refusal with zero tests executed. These tests lock both
/// sides of that contract: the rejected vector fails at argument validation,
/// and the corrected (targeted, prebuilt) invocation runs a suite to a pass.
///
/// All probes are offline-safe: argument validation and <c>--no-build</c> runs
/// need no restore, no build, and no network. Tests that need the repo's own
/// built test assembly resolve it from the executing assembly's location and
/// skip (early return, per repo idiom) when the layout is unrecognised or
/// <c>dotnet</c> is absent.
/// </summary>
public sealed class DotnetTestRunnerVectorTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-testvector-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task AppendedAbsentAssembly_IsRejectedWithoutRunningTests()
    {
        // Ground truth for the incident: appending a built-assembly path that
        // does not exist makes `dotnet test --no-build` drop into VSTest
        // passthrough ("--no-build" is ignored) and VSTest rejects the
        // missing source instead of running anything.
        if (!await IsDotnetAvailableAsync())
            return;

        var absent = Path.Combine(_workspace, "Vec.Tests.dll");
        var (exit, combined) = await RunDotnetAsync(_workspace, DefaultDotnetTimeout, "test", "--no-build", absent);

        Assert.NotEqual(0, exit);
        Assert.Contains("is invalid", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Passed!", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed!", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectedProjectTarget_RunsPassingSuite()
    {
        // The corrected invocation targets the (source-controlled, always
        // present) project with prebuilt outputs: it runs the suite and
        // reports a pass. Runs ONE fast pure test of this very assembly
        // through the real runner, nested inside the outer suite.
        if (!await IsDotnetAvailableAsync())
            return;
        if (!TryLocateOwnTestProject(out var csproj, out var testDll))
            return;

        // The outer suite can be built in either Debug or Release. Match its
        // output configuration: --no-build otherwise makes the nested runner
        // look for Debug binaries during CI's Release-only test gate.
        var configuration = Directory.GetParent(Path.GetDirectoryName(testDll)!)?.Name;
        if (string.IsNullOrWhiteSpace(configuration))
            return;

        const string fastTest = "CodeyBox.Tests.AuditTests.DotnetTestAuditor_AllTestsDefaultOptions_EmitsByteIdenticalLegacyCommand";
        var (exit, combined) = await RunDotnetAsync(
            Path.GetDirectoryName(csproj)!,
            TimeSpan.FromMinutes(4),
            "test", csproj, "--configuration", configuration, "--no-build",
            "--filter", $"FullyQualifiedName={fastTest}");

        Assert.Equal(0, exit);
        Assert.Contains("Passed!", combined, StringComparison.Ordinal);
    }

    private static async Task<bool> IsDotnetAvailableAsync()
    {
        try
        {
            var (exit, _) = await RunDotnetAsync(
                Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60), "--version");
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLocateOwnTestProject(out string csproj, out string testDll)
    {
        csproj = "";
        testDll = "";
        var baseDir = AppContext.BaseDirectory;
        var dll = Path.Combine(baseDir, "CodeyBox.Tests.dll");
        if (!File.Exists(dll))
            return false;

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CodeyBox.Tests.csproj");
            if (File.Exists(candidate))
            {
                csproj = candidate;
                testDll = dll;
                return true;
            }
            dir = dir.Parent;
        }

        return false;
    }

    private static readonly TimeSpan DefaultDotnetTimeout = TimeSpan.FromMinutes(2);

    private static async Task<(int ExitCode, string CombinedOutput)> RunDotnetAsync(
        string workingDirectory,
        TimeSpan timeout,
        params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"dotnet {string.Join(' ', args)} timed out after {timeout}");
        }

        return (process.ExitCode, await stdout + "\n" + await stderr);
    }
}
