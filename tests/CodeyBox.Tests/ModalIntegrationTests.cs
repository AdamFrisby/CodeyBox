using CodeyBox.Core;
using CodeyBox.ModalPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Live integration tests against the real Modal service.
///
/// <para>Why these cannot run in CI (documented reason, fixtures in their
/// place): they need a funded <c>MODAL_TOKEN_ID</c> / <c>MODAL_TOKEN_SECRET</c>
/// pair, each run provisions billable cloud sandboxes (create/terminate cost
/// real money), the runner needs egress to api.modal.com, and Modal's control
/// plane is SDK-driven with no stable public REST sandbox contract — the
/// first live run also re-validates the plugin's recorded endpoint shapes.
/// <see cref="ModalRecordedShapeTests"/> pins those shapes so the contract is
/// still verified on every run.</para>
///
/// <para>To run live: <c>CODEYBOX_RUN_MODAL_INTEGRATION=1 MODAL_TOKEN_ID=…
/// MODAL_TOKEN_SECRET=… dotnet test --filter
/// "FullyQualifiedName~ModalIntegrationTests"</c>. The tests terminate every
/// sandbox they create, but a crashed run can leave a
/// <c>codeybox-live-*</c> sandbox behind — sweep them in the Modal dashboard.
/// Tagged <c>requires_modal</c> so CI profiles skip them like the multipass
/// VM tests.</para>
/// </summary>
public sealed class ModalIntegrationTests
{
    internal static class ModalIntegrationGate
    {
        public const string EnableVariable = "CODEYBOX_RUN_MODAL_INTEGRATION";

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODAL_TOKEN_ID"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODAL_TOKEN_SECRET"));
    }

    private static bool LiveAvailable => ModalIntegrationGate.IsEnabled;

    private static ModalSandboxProvider NewLiveProvider() => new(
        () => new ModalSandboxOptions
        {
            AppName = "codeybox-live",
            NamePrefix = "codeybox-live-",
            WaitForRunningTimeout = TimeSpan.FromMinutes(5),
        },
        new HttpClient(),
        TimeProvider.System,
        NullLogger.Instance);

    [SkippableFact]
    [Trait("requires_modal", "true")]
    public async Task Live_CreateExecFileSnapshot_Preserve_Terminate()
    {
        Skip.If(!LiveAvailable, "Set CODEYBOX_RUN_MODAL_INTEGRATION=1, MODAL_TOKEN_ID and MODAL_TOKEN_SECRET to run the live Modal test.");

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

        var modal = Assert.IsType<ModalSandbox>(sandbox);
        await modal.WriteFileAsync("/work/live-note.txt", "live-contents", CancellationToken.None);
        Assert.Equal("live-contents", await modal.ReadFileAsync("/work/live-note.txt", CancellationToken.None));

        await modal.StopAndPreserveAsync(CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(modal.LastSnapshotId));
    }
}
