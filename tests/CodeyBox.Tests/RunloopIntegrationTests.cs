using CodeyBox.Core;
using CodeyBox.RunloopPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Live integration tests against the real Runloop service.
///
/// <para>Why these cannot run in CI (documented reason, fixtures in their
/// place): they need a funded <c>RUNLOOP_API_KEY</c>, each run provisions a
/// billable cloud VM (create/shutdown cost real money), and the runner needs
/// egress to api.runloop.ai. <see cref="RunloopRecordedShapeTests"/> pins the
/// API shapes with recorded fixtures so the contract is still verified on
/// every run.</para>
///
/// <para>To run live: <c>CODEYBOX_RUN_RUNLOOP_INTEGRATION=1 RUNLOOP_API_KEY=… dotnet test
/// --filter "FullyQualifiedName~RunloopIntegrationTests"</c>. The tests clean
/// up every devbox they create, but a crashed run can leave a
/// <c>codeybox-live-*</c> devbox behind — sweep them in the Runloop dashboard.
/// Tagged <c>requires_runloop</c> so CI profiles skip them like the multipass
/// VM tests.</para>
/// </summary>
public sealed class RunloopIntegrationTests
{
    internal static class RunloopIntegrationGate
    {
        public const string EnableVariable = "CODEYBOX_RUN_RUNLOOP_INTEGRATION";

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUNLOOP_API_KEY"));
    }

    private static bool LiveAvailable => RunloopIntegrationGate.IsEnabled;

    private static RunloopSandboxProvider NewLiveProvider() => new(
        () => new RunloopSandboxOptions
        {
            Token = Environment.GetEnvironmentVariable("RUNLOOP_API_KEY"),
            NamePrefix = "codeybox-live-",
            ResourceSize = "X_SMALL",
            WaitForRunningTimeout = TimeSpan.FromMinutes(5),
        },
        new HttpClient(),
        TimeProvider.System,
        NullLogger.Instance);

    [SkippableFact]
    [Trait("requires_runloop", "true")]
    public async Task Live_CreateExecFileSnapshotSuspendResume_Shutdown()
    {
        Skip.If(!LiveAvailable, "Set CODEYBOX_RUN_RUNLOOP_INTEGRATION=1 and RUNLOOP_API_KEY to run the live Runloop test.");

        var provider = NewLiveProvider();
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            Environment = new Dictionary<string, string> { ["CODEYBOX_LIVE_PROBE"] = "1" },
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(sandbox.Id));

        var echo = await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "live-$CODEYBOX_LIVE_PROBE"] }, CancellationToken.None);
        Assert.True(echo.Success, echo.Stderr);
        Assert.Contains("live-1", echo.Stdout, StringComparison.Ordinal);

        var runloop = Assert.IsType<RunloopSandbox>(sandbox);
        await runloop.WriteFileAsync("/work/live-note.txt", "live-contents", CancellationToken.None);
        Assert.Equal("live-contents", await runloop.ReadFileAsync("/work/live-note.txt", CancellationToken.None));

        var snapshotId = await runloop.CreateSnapshotAsync("codeybox-live-test", CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(snapshotId));

        await runloop.SuspendAsync(CancellationToken.None);
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        var afterResume = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", "/work/live-note.txt"] }, CancellationToken.None);
        Assert.True(afterResume.Success, afterResume.Stderr);
        Assert.Contains("live-contents", afterResume.Stdout, StringComparison.Ordinal);
    }
}
