using HarnessProgram = CodeyBox.Harness.Program;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the <c>codeybox-harness</c> CLI's argument parsing surface.
/// The launch path itself is exercised via <see cref="WebAppHarnessTests"/>;
/// this fixture pins exit-code conventions, --source/JOBTRACK_SOURCE fallback,
/// unknown-option rejection, and source-directory validation so a regression
/// in any of those branches surfaces without needing a real Multipass VM.
/// </summary>
public sealed class HarnessProgramTests
{
    private static string? NoEnv(string _) => null;

    private static Func<string, string?> EnvOf(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        return key => dict.TryGetValue(key, out var v) ? v : null;
    }

    // ── Exit-code constants ───────────────────────────────────────────────────

    [Fact]
    public void ExitCode_Usage_Is2()
    {
        // The orchestrator-style "usage" exit needs to be the standard "command
        // line usage error" 2 so wrapping scripts can distinguish "operator
        // passed bad flags" (retry not useful) from "launch failed" (might be
        // transient).
        Assert.Equal(2, HarnessProgram.ExitUsage);
    }

    [Fact]
    public void ExitCode_LaunchFailed_Is1()
    {
        Assert.Equal(1, HarnessProgram.ExitLaunchFailed);
    }

    // ── Main dispatcher ───────────────────────────────────────────────────────

    public static TheoryData<string[]> HelpOrEmptyMainArgs
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(Array.Empty<string>());
            data.Add(["-h"]);
            data.Add(["--help"]);
            data.Add(["help"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HelpOrEmptyMainArgs))]
    public async Task Main_HelpOrEmptyArgs_ReturnsUsageExit(string[] args)
    {
        var rc = await HarnessProgram.Main(args);
        Assert.Equal(HarnessProgram.ExitUsage, rc);
    }

    [Fact]
    public async Task Main_UnknownTopLevelCommand_ReturnsUsageExit()
    {
        var rc = await HarnessProgram.Main(["bogus-command"]);
        Assert.Equal(HarnessProgram.ExitUsage, rc);
    }

    // ── jobtrack arg parsing ──────────────────────────────────────────────────

    public static TheoryData<string[]> HelpOrEmptyJobTrackArgs
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(Array.Empty<string>());
            data.Add(["-h"]);
            data.Add(["--help"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HelpOrEmptyJobTrackArgs))]
    public void ParseJobTrackArgs_HelpOrEmpty_ReturnsUsageStatus(string[] args)
    {
        var result = HarnessProgram.ParseJobTrackArgs(args, NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Usage, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseJobTrackArgs_UnknownSubcommand_ReturnsUsageWithError()
    {
        var result = HarnessProgram.ParseJobTrackArgs(["start"], NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Usage, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("jobtrack start", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseJobTrackArgs_UnknownOption_ReturnsUsageWithError()
    {
        // Guards the parser's default-branch: an unrecognised flag must surface
        // as Usage (exit 2) rather than being silently swallowed and producing
        // a launch with default values.
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/x", "--frobnicate"], _ => null, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Usage, result.Status);
        Assert.Contains("--frobnicate", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseJobTrackArgs_NoSource_NoEnv_ReturnsUsageWithError()
    {
        var result = HarnessProgram.ParseJobTrackArgs(["launch"], NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Usage, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("--source", result.Error, StringComparison.Ordinal);
        Assert.Contains("JOBTRACK_SOURCE", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseJobTrackArgs_SourceFromFlag_PrefersFlagOverEnv()
    {
        var env = EnvOf(("JOBTRACK_SOURCE", "/from-env"));
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/from-flag"], env, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.Equal("/from-flag", result.Source);
    }

    [Fact]
    public void ParseJobTrackArgs_SourceFromEnvFallback_WhenFlagMissing()
    {
        var env = EnvOf(("JOBTRACK_SOURCE", "/from-env"));
        var result = HarnessProgram.ParseJobTrackArgs(["launch"], env, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.Equal("/from-env", result.Source);
    }

    [Fact]
    public void ParseJobTrackArgs_BlankSourceFlag_FallsBackToEnv()
    {
        // Operator passes `--source ""` (e.g. unset shell var) — must NOT count
        // as "source provided", or the harness will try to launch with an empty
        // host path. The env fallback should kick in.
        var env = EnvOf(("JOBTRACK_SOURCE", "/from-env"));
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "   "], env, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.Equal("/from-env", result.Source);
    }

    [Fact]
    public void ParseJobTrackArgs_SourceDoesNotExist_ReturnsSourceMissing()
    {
        // SourceMissing (exit 1) is distinct from Usage (exit 2): the operator
        // passed valid args but pointed at a path the system can't see, which
        // looks like a launch problem, not a CLI problem.
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/no/such/path"], NoEnv, _ => false);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.SourceMissing, result.Status);
        Assert.Contains("/no/such/path", result.Error!, StringComparison.Ordinal);
        Assert.Equal("/no/such/path", result.Source);
    }

    [Fact]
    public void ParseJobTrackArgs_ScreenshotOutOverride_Captured()
    {
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/x", "--screenshot-out", "/tmp/out.png"],
            NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.Equal("/tmp/out.png", result.ScreenshotOut);
    }

    [Fact]
    public void ParseJobTrackArgs_ScreenshotOutDefault_IsHarnessReadyPng()
    {
        // The default screenshot path is part of the CLI's documented contract;
        // a regression that swaps it to e.g. an absolute /tmp path would surprise
        // operators who rely on "PNG appears next to where I ran the CLI".
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/x"], NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.Equal("harness-ready.png", result.ScreenshotOut);
    }

    [Fact]
    public void ParseJobTrackArgs_InteractiveFlag_Captured()
    {
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/x", "--interactive"], NoEnv, _ => true);
        Assert.Equal(HarnessProgram.JobTrackParseStatus.Ok, result.Status);
        Assert.True(result.Interactive);
    }

    [Fact]
    public void ParseJobTrackArgs_InteractiveDefault_False()
    {
        var result = HarnessProgram.ParseJobTrackArgs(
            ["launch", "--source", "/x"], NoEnv, _ => true);
        Assert.False(result.Interactive);
    }
}
