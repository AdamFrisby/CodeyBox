using System.Net;
using System.Text;
using CodeyBox.BlaxelPlugin;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Hardening regression tests for the Blaxel provider: service-supplied
/// sandbox URLs are validated before use as request targets, and
/// attacker-influenced data-plane responses are streamed through a byte
/// ceiling tied to the exec output caps (a runaway guest must kill its
/// process and report output-limit, never OOM the host by buffering).
/// </summary>
public sealed class BlaxelSandboxHardeningTests
{
    private static BlaxelSandboxOptions TestOptions() => new()
    {
        ApiKey = "test-key",
        Workspace = "test-workspace",
        ApiBaseUrl = "https://api.blaxel.ai/v0",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        ExecPollInterval = TimeSpan.FromMilliseconds(10),
        SuspendWaitTimeout = TimeSpan.FromSeconds(5),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static BlaxelSandboxProvider NewProvider(FakeBlaxelHandler handler, BlaxelSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new BlaxelSandboxProvider(
            () => options,
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static SandboxSpec BasicSpec() => new()
    {
        ImageReference = "ignored",
        WorkingDirectory = "/work",
    };

    [Fact]
    public void ValidateSandboxUrl_AcceptsHttps_Verbatim()
    {
        const string url = "https://sbx-demo-testws.us-pdx-1.bl.run";
        Assert.Equal(url, BlaxelSandboxProvider.ValidateSandboxUrl(url, allowUnsafeHttp: false));
    }

    [Fact]
    public void ValidateSandboxUrl_RejectsNonAbsoluteAndWrongScheme()
    {
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("/process/abc/logs", allowUnsafeHttp: true));
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("ftp://files.example/out", allowUnsafeHttp: true));
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("   ", allowUnsafeHttp: true));
    }

    [Fact]
    public void ValidateSandboxUrl_RejectsRemoteHttp_EvenWithFlag()
    {
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("http://sandbox.example/", allowUnsafeHttp: true));
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("http://sandbox.example/", allowUnsafeHttp: false));
    }

    [Fact]
    public void ValidateSandboxUrl_LoopbackHttp_RequiresFlag()
    {
        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ValidateSandboxUrl("http://127.0.0.1:8080/", allowUnsafeHttp: false));
        Assert.Equal(
            "http://127.0.0.1:8080/",
            BlaxelSandboxProvider.ValidateSandboxUrl("http://127.0.0.1:8080/", allowUnsafeHttp: true));
    }

    [Fact]
    public void ResolveSandboxUrl_RejectsLyingEndpoint()
    {
        var view = new BlaxelSandboxView
        {
            Metadata = new BlaxelSandboxViewMetadata { Name = "sbx", Url = "http://evil.example/stolen" },
            Spec = new BlaxelSandboxViewSpec { Region = "us-pdx-1" },
        };

        Assert.Throws<BlaxelApiException>(() =>
            BlaxelSandboxProvider.ResolveSandboxUrl(view, "ws", allowUnsafeHttp: true));
    }

    [Fact]
    public void ValidateBaseUrl_RemoteHttpRejected_EvenWithFlag()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BlaxelSandboxProvider.ValidateBaseUrl("http://api.example/v0", allowUnsafeHttp: true));
        BlaxelSandboxProvider.ValidateBaseUrl("http://localhost:8080/", allowUnsafeHttp: true);
        BlaxelSandboxProvider.ValidateBaseUrl("https://api.blaxel.ai/v0", allowUnsafeHttp: false);
    }

    [Fact]
    public void SandboxCtor_RejectsUnvalidatedEndpoint()
    {
        var options = TestOptions();
        Assert.Throws<BlaxelApiException>(() => new BlaxelSandbox(
            "sbx",
            "http://evil.example/stolen",
            new BlaxelControlPlaneClient(new HttpClient(new FakeBlaxelHandler())),
            new BlaxelSandboxApiClient(new HttpClient(new FakeBlaxelHandler())),
            () => options,
            () => new BlaxelCredentials("k", "w"),
            BasicSpec(),
            TimeProvider.System,
            NullLogger.Instance,
            _ => { }));
    }

    [Fact]
    public async Task DataPlaneClient_OversizedBody_ReportsOutputLimit()
    {
        var big = new string('A', 8 * 1024 * 1024);
        var handler = new FixedBodyHandler(
            "{\"logs\":\"" + big + "\",\"stdout\":\"" + big + "\",\"stderr\":\"\"}");
        var client = new BlaxelSandboxApiClient(new HttpClient(handler));
        var credentials = new BlaxelCredentials("k", "w");

        var ex = await Assert.ThrowsAsync<BlaxelApiException>(() => client.GetProcessLogsAsync(
            "https://sandbox.example/", credentials, "proc_1", TimeSpan.FromSeconds(5),
            CancellationToken.None, maxResponseBytes: 64 * 1024));
        Assert.Equal("output-limit", ex.ErrorClass);
    }

    [Fact]
    public async Task DataPlaneClient_LyingContentLength_FailsFast()
    {
        var handler = new LyingLengthHandler();
        var client = new BlaxelSandboxApiClient(new HttpClient(handler));
        var credentials = new BlaxelCredentials("k", "w");

        var ex = await Assert.ThrowsAsync<BlaxelApiException>(() => client.GetProcessAsync(
            "https://sandbox.example/", credentials, "proc_1", TimeSpan.FromSeconds(5),
            CancellationToken.None, maxResponseBytes: 64 * 1024));
        Assert.Equal("output-limit", ex.ErrorClass);
    }

    [Fact]
    public async Task DataPlaneClient_SmallBody_Parses()
    {
        var handler = new FixedBodyHandler("{\"logs\":\"hi\",\"stdout\":\"hi\",\"stderr\":\"\"}");
        var client = new BlaxelSandboxApiClient(new HttpClient(handler));
        var credentials = new BlaxelCredentials("k", "w");

        var logs = await client.GetProcessLogsAsync(
            "https://sandbox.example/", credentials, "proc_1", TimeSpan.FromSeconds(5),
            CancellationToken.None, maxResponseBytes: 64 * 1024);
        Assert.Equal("hi", logs.Stdout);
    }

    [Fact]
    public async Task ExecAsync_RunawayGuestOutput_KillsProcess_AndReportsLimit()
    {
        var handler = new FakeBlaxelHandler();
        handler.ExecutionResponder = (_, _) => (0, new string('A', 3 * 1024 * 1024), string.Empty);
        var provider = NewProvider(handler, TestOptions() with { MaxExecOutputBytes = 64 * 1024 });

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["yes"] }, CancellationToken.None);

        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.Success);
        Assert.True(result.Stdout.Length < 3 * 1024 * 1024);
        Assert.Contains(handler.Requests, r => r.Path.EndsWith("/kill", StringComparison.Ordinal));
    }

    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly string _body;

        public FixedBodyHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class LyingLengthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new LyingLengthContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private sealed class LyingLengthContent : HttpContent
        {
            private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"status\":\"running\"}");

            public LyingLengthContent() => Headers.ContentLength = 1L << 40;

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                await stream.WriteAsync(Body, CancellationToken.None).ConfigureAwait(false);

            protected override bool TryComputeLength(out long length)
            {
                length = Body.Length;
                return false;
            }
        }
    }
}
