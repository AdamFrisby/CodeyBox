using CodeyBox.Core;
using CodeyBox.E2bSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Live integration tests against the real E2B service.
///
/// <para>Why these cannot run in CI (documented reason, fixtures in their
/// place): they need a funded <c>E2B_API_KEY</c>, each run provisions a
/// billable cloud sandbox (create/delete cost real money), and the runner
/// needs egress to api.e2b.dev plus the per-sandbox envd hosts. The
/// <see cref="E2bRecordedShapeTests"/> and <c>FakeE2bHandler</c> pin the API
/// shapes with recorded fixtures so the contract is still verified on every
/// run.</para>
///
/// <para>To run live: <c>CODEYBOX_RUN_E2B_INTEGRATION=1 E2B_API_KEY=…
/// dotnet test --filter "FullyQualifiedName~E2bIntegrationTests"</c>. The
/// tests clean up every sandbox they create, but a crashed run can leave a
/// <c>codeybox-live-*</c> sandbox behind — sweep them in the E2B dashboard.
/// Tagged <c>requires_e2b</c> so CI profiles skip them like the multipass
/// VM tests.</para>
/// </summary>
public sealed class E2bIntegrationTests
{
    internal static class E2bIntegrationGate
    {
        public const string EnableVariable = "CODEYBOX_RUN_E2B_INTEGRATION";

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2B_API_KEY"));
    }

    private static bool LiveAvailable => E2bIntegrationGate.IsEnabled;

    private static E2bSandboxProvider NewLiveProvider() => new(
        () => new E2bSandboxOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("E2B_API_KEY"),
            NamePrefix = "codeybox-live-",
            SandboxTimeoutSeconds = 600,
            WaitForRunningTimeout = TimeSpan.FromMinutes(5),
        },
        new HttpClient(),
        TimeProvider.System,
        NullLogger.Instance);

    [SkippableFact]
    [Trait("requires_e2b", "true")]
    public async Task Live_CreateExecFileSnapshotPauseResume_Delete()
    {
        Skip.If(!LiveAvailable, "Set CODEYBOX_RUN_E2B_INTEGRATION=1 and E2B_API_KEY to run the live E2B test.");

        var provider = NewLiveProvider();
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/home/user",
            Environment = new Dictionary<string, string> { ["CODEYBOX_LIVE_PROBE"] = "1" },
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(sandbox.Id));

        var echo = await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "live-$CODEYBOX_LIVE_PROBE"] }, CancellationToken.None);
        Assert.True(echo.Success, echo.Stderr);
        Assert.Contains("live-1", echo.Stdout, StringComparison.Ordinal);

        var e2b = Assert.IsType<E2bSandbox>(sandbox);
        await e2b.WriteFileAsync("/home/user/live-note.txt", "live-contents", CancellationToken.None);
        Assert.Equal("live-contents", await e2b.ReadFileAsync("/home/user/live-note.txt", CancellationToken.None));

        var snapshotId = await e2b.CreateSnapshotAsync("codeybox-live-test", CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(snapshotId));

        await e2b.SuspendAsync(CancellationToken.None);
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        var afterResume = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", "/home/user/live-note.txt"] }, CancellationToken.None);
        Assert.True(afterResume.Success, afterResume.Stderr);
        Assert.Contains("live-contents", afterResume.Stdout, StringComparison.Ordinal);

        await sandbox.DisposeAsync();
        var listed = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, m => string.Equals(m.Name, sandbox.Id, StringComparison.Ordinal));
    }
}
