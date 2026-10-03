using CodeyBox.Core;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Live integration tests against a real Tart host.
///
/// <para>Why these cannot run in CI (documented reason, fixtures in their
/// place): they need an Apple Silicon macOS host with Tart and `sshpass`
/// installed, pull a ~25 GB VM image on first use, need guest SSH with the
/// image's `admin` credentials in `TART_SSH_PASSWORD`, and bind a VM per
/// run. <see cref="TartRecordedShapeTests"/> pins the CLI shapes with
/// recorded fixtures so the contract is still verified on every run.</para>
///
/// <para>To run live: <c>CODEYBOX_RUN_TART_INTEGRATION=1 TART_SSH_PASSWORD=…
/// dotnet test --filter "FullyQualifiedName~TartIntegrationTests"</c> on a
/// Mac. The tests clean up every VM they create, but a crashed run can
/// leave a <c>codeybox-live-*</c> VM behind — sweep them with
/// <c>tart list</c> / <c>tart delete</c>.
/// Tagged <c>requires_tart</c> so CI profiles skip them like the multipass
/// VM tests.</para>
/// </summary>
public sealed class TartIntegrationTests
{
    internal static class TartIntegrationGate
    {
        public const string EnableVariable = "CODEYBOX_RUN_TART_INTEGRATION";

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)
            && OperatingSystem.IsMacOS()
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TART_SSH_PASSWORD"));
    }

    private static bool LiveAvailable => TartIntegrationGate.IsEnabled;

    private static TartSandboxProvider NewLiveProvider() => new(
        () => new TartSandboxOptions
        {
            Enabled = true,
            DefaultImage = Environment.GetEnvironmentVariable("CODEYBOX_TART_TEST_IMAGE")
                ?? "ghcr.io/cirruslabs/macos-sequoia-base:latest",
            NamePrefix = "codeybox-live-",
            ReadyTimeoutSeconds = 600,
        },
        new SystemTartProcessRunner(),
        () => OperatingSystem.IsMacOS(),
        TimeProvider.System,
        NullLogger.Instance);

    [SkippableFact]
    [Trait("requires_tart", "true")]
    public async Task Live_CreateExecFileSuspendResume_Delete()
    {
        Skip.If(!LiveAvailable, "Set CODEYBOX_RUN_TART_INTEGRATION=1 and TART_SSH_PASSWORD on a macOS host to run the live Tart test.");

        var provider = NewLiveProvider();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            WorkingDirectory = "/work",
            Environment = new Dictionary<string, string> { ["CODEYBOX_LIVE_PROBE"] = "1" },
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        Assert.StartsWith("codeybox-live-", sandbox.Id, StringComparison.Ordinal);

        var exec = await sandbox.ExecAsync(new SandboxExec { Argv = ["sh", "-c", "echo $CODEYBOX_LIVE_PROBE"] }, CancellationToken.None);
        Assert.True(exec.Success, exec.Stderr);
        Assert.Contains("1", exec.Stdout, StringComparison.Ordinal);

        var tart = Assert.IsType<TartSandbox>(sandbox);
        await tart.WriteFileAsync("/work/live-note.txt", "live-contents", CancellationToken.None);
        Assert.Equal("live-contents", await tart.ReadFileAsync("/work/live-note.txt", CancellationToken.None));

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(managed, m => m.Name == sandbox.Id && m.IsTrackedActive);

        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await suspendable.SuspendAsync(CancellationToken.None);
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        var afterResume = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.True(afterResume.Success, afterResume.Stderr);
    }
}
