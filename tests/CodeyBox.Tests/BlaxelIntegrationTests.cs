using CodeyBox.BlaxelPlugin;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Live verification against the real Blaxel service: create, exec, file
/// round-trip, suspend into standby, resume with a working guest, file
/// survival across the cycle, delete. Gated — it needs a funded
/// <c>BL_API_KEY</c> + <c>BL_WORKSPACE</c>, provisions billable cloud VMs,
/// and needs egress to <c>api.blaxel.ai</c> — so CI never runs it. The
/// recorded-shape fixtures pin the contract on every run instead.
///
/// <para>Run on demand:</para>
/// <code>
/// CODEYBOX_RUN_BLAXEL_INTEGRATION=1 BL_API_KEY=… BL_WORKSPACE=… \
///   dotnet test --filter "FullyQualifiedName~BlaxelIntegrationTests"
/// </code>
/// </summary>
[Trait("requires_blaxel", "true")]
public sealed class BlaxelIntegrationTests
{
    private static void SkipUnlessEnabled()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("CODEYBOX_RUN_BLAXEL_INTEGRATION"), "1", StringComparison.Ordinal),
            "Set CODEYBOX_RUN_BLAXEL_INTEGRATION=1 with BL_API_KEY and BL_WORKSPACE to run the live Blaxel integration test.");
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BL_API_KEY")),
            "BL_API_KEY is not set.");
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BL_WORKSPACE")),
            "BL_WORKSPACE is not set.");
    }

    [SkippableFact]
    public async Task LiveLifecycle_CreateExecSuspendResumeDelete()
    {
        SkipUnlessEnabled();
        var apiKey = Environment.GetEnvironmentVariable("BL_API_KEY")!;
        var workspace = Environment.GetEnvironmentVariable("BL_WORKSPACE")!;
        var options = new BlaxelSandboxOptions
        {
            ApiKey = apiKey,
            Workspace = workspace,
            Image = Environment.GetEnvironmentVariable("CODEYBOX_BLAXEL_TEST_IMAGE") ?? "blaxel/base-image:latest",
            MemoryMb = int.TryParse(Environment.GetEnvironmentVariable("CODEYBOX_BLAXEL_TEST_MEMORY_MB"), out var memory) ? memory : 4096,
            WaitForRunningTimeout = TimeSpan.FromMinutes(10),
            SuspendWaitTimeout = TimeSpan.FromMinutes(10),
            ApiTimeout = TimeSpan.FromSeconds(60),
        };

        using var http = new HttpClient();
        var provider = new BlaxelSandboxProvider(
            () => options,
            http,
            TimeProvider.System,
            NullLogger.Instance);
        var control = new BlaxelControlPlaneClient(http);
        var credentials = new BlaxelCredentials(apiKey, workspace);

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
        };

        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        try
        {
            var nonce = $"live-probe-{Guid.NewGuid():N}";
            var echo = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["printf", "%s", nonce], WorkingDirectory = "/work" },
                CancellationToken.None);
            Assert.True(echo.Success, $"live exec failed: exit={echo.ExitCode} stderr={echo.Stderr}");
            Assert.Contains(nonce, echo.Stdout, StringComparison.Ordinal);

            var blaxel = Assert.IsType<BlaxelSandbox>(sandbox);
            await blaxel.WriteFileAsync("/work/live-note.txt", "live-contents", CancellationToken.None);
            Assert.Equal("live-contents", await blaxel.ReadFileAsync("/work/live-note.txt", CancellationToken.None));

            await ((ISuspendableSandbox)sandbox).SuspendAsync(CancellationToken.None);
            Assert.True(blaxel.IsSuspended);

            await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

            var after = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["printf", "%s", "resumed-ok"], WorkingDirectory = "/work" },
                CancellationToken.None);
            Assert.True(after.Success, $"post-resume exec failed: exit={after.ExitCode} stderr={after.Stderr}");
            Assert.Contains("resumed-ok", after.Stdout, StringComparison.Ordinal);
            Assert.Equal("live-contents", await blaxel.ReadFileAsync("/work/live-note.txt", CancellationToken.None));
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        var deleted = await Assert.ThrowsAsync<BlaxelApiException>(() =>
            control.GetSandboxAsync(options.ApiBaseUrl, credentials, sandbox.Id, options.ApiTimeout, CancellationToken.None));
        Assert.Equal("not-found", deleted.ErrorClass);
    }
}
