using System.Text;
using CodeyBox.Core;
using CodeyBox.HetznerSandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the hetzner smoke orchestration
/// (<see cref="HetznerSandboxSmoke"/>): acquire → <c>uname -a</c> → file
/// stage round-trip → dispose, against the real provider over a fake
/// in-process Hetzner cloud plus a simulated SSH transport. Every test
/// asserts the cloud is empty afterwards: the smoke runner must dispose the
/// sandbox on success, on mid-run failure, and on cancellation.
/// </summary>
public sealed class HetznerSmokeTests
{
    [Fact]
    public async Task Smoke_Success_Acquire_Uname_StageRoundTrip_Dispose()
    {
        using var harness = new SmokeHarness("smoke-token-1");
        var liveBefore = SandboxLiveCounter.Active;

        var output = new StringWriter();
        var result = await HetznerSandboxSmoke.RunAsync(
            harness.Harness.Provider, output, TimeProvider.System, roundTripToken: "smoke-token-1");

        Assert.True(result.Succeeded, "smoke failed: " + result.Failure);
        Assert.Null(result.Failure);
        Assert.StartsWith("Linux", result.UnameOutput, StringComparison.Ordinal);
        Assert.Equal(
            ["acquire", "uname", "stage-read", "stage-write", "dispose"],
            result.Timings.Select(t => t.Name).ToArray());
        Assert.All(result.Timings, t => Assert.True(t.Elapsed >= TimeSpan.Zero));

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Harness.Cloud.Servers);
        Assert.Empty(harness.Harness.Cloud.SshKeys);
        Assert.Empty(harness.Harness.Cloud.Firewalls);

        var text = output.ToString();
        Assert.Contains("acquired codeybox-", text, StringComparison.Ordinal);
        Assert.Contains("stage round-trip ok", text, StringComparison.Ordinal);
        Assert.Contains("disposed in", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Smoke_MidRunFailure_StillDisposes()
    {
        using var harness = new SmokeHarness("smoke-token-2", corruptStageRead: true);
        var liveBefore = SandboxLiveCounter.Active;

        var result = await HetznerSandboxSmoke.RunAsync(
            harness.Harness.Provider, TextWriter.Null, TimeProvider.System, roundTripToken: "smoke-token-2");

        Assert.False(result.Succeeded);
        Assert.Contains("mismatch", result.Failure, StringComparison.Ordinal);
        Assert.Contains("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Harness.Cloud.Servers);
        Assert.Empty(harness.Harness.Cloud.SshKeys);
        Assert.Empty(harness.Harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task Smoke_Cancellation_StillDisposes()
    {
        using var harness = new SmokeHarness("smoke-token-3");
        using var cts = new CancellationTokenSource();
        harness.CancelOnUname(cts);
        var liveBefore = SandboxLiveCounter.Active;

        var result = await HetznerSandboxSmoke.RunAsync(
            harness.Harness.Provider, TextWriter.Null, TimeProvider.System,
            roundTripToken: "smoke-token-3", ct: cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("cancelled", result.Failure);
        Assert.Contains("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Harness.Cloud.Servers);
        Assert.Empty(harness.Harness.Cloud.SshKeys);
    }

    [Fact]
    public async Task Smoke_DisposeFailure_ReportsFailure()
    {
        using var harness = new SmokeHarness("smoke-token-4");
        var provider = new DisposeThrowingProvider(harness.Harness.Provider);
        var liveBefore = SandboxLiveCounter.Active;

        var output = new StringWriter();
        var result = await HetznerSandboxSmoke.RunAsync(
            provider, output, TimeProvider.System, roundTripToken: "smoke-token-4");

        Assert.False(result.Succeeded);
        Assert.Contains("dispose failed", result.Failure, StringComparison.Ordinal);
        Assert.Contains("may leak", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);
        Assert.Contains("dispose failed", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Harness.Cloud.Servers);
    }

    /// <summary>
    /// Simulated guest: a <c>Files</c> dict standing in for the guest
    /// filesystem, with stage-in/out moving bytes the way the real SSH
    /// transport does. Command parsing mirrors the provider's
    /// <c>bash -lc "cd ... &amp;&amp; 'argv' ..."</c> wrapper by reading the
    /// last <c>&amp;&amp;</c> segment's quoted words.
    /// </summary>
    private sealed class SmokeHarness : IDisposable
    {
        public HetznerHarness Harness { get; } = new();

        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        private readonly string _token;
        private readonly bool _corruptStageRead;
        private CancellationTokenSource? _cancelOnUname;

        public SmokeHarness(string token, bool corruptStageRead = false)
        {
            _token = token;
            _corruptStageRead = corruptStageRead;
            Harness.TransportFactory.ConfigureTransport = transport =>
            {
                transport.OnRun = (argv, stdin) => Answer(argv, stdin);
                transport.OnStageIn = StageIn;
                transport.OnStageOut = StageOut;
            };
        }

        public void CancelOnUname(CancellationTokenSource cts) => _cancelOnUname = cts;

        private ProcessRunResult Answer(IReadOnlyList<string> argv, string? stdin)
        {
            var script = argv.Count == 3 && argv[0] == "bash" ? argv[2] : string.Join(" ", argv);
            var segment = script.Contains("&&", StringComparison.Ordinal)
                ? script[(script.LastIndexOf("&&", StringComparison.Ordinal) + 2)..].Trim()
                : script.Trim();
            var words = Unquote(segment);
            if (words is ["true"] or ["echo", "ready"])
                return new ProcessRunResult(0, string.Empty, string.Empty);
            if (words is ["uname", "-a", ..])
            {
                if (_cancelOnUname is not null)
                {
                    _cancelOnUname.Cancel();
                    throw new OperationCanceledException(_cancelOnUname.Token);
                }
                return new ProcessRunResult(0, "Linux fake-smoke 6.8.0 x86_64", string.Empty);
            }
            if (words is ["cat", var path])
            {
                if (_corruptStageRead)
                    return new ProcessRunResult(0, "corrupted", string.Empty);
                return Files.TryGetValue(path, out var content)
                    ? new ProcessRunResult(0, content, string.Empty)
                    : new ProcessRunResult(1, string.Empty, "cat: No such file");
            }
            if (words is ["tee", var target, ..])
            {
                if (!string.Equals(stdin, _token, StringComparison.Ordinal))
                    return new ProcessRunResult(1, string.Empty, "token mismatch");
                Files[target] = stdin ?? string.Empty;
                return new ProcessRunResult(0, stdin ?? string.Empty, string.Empty);
            }
            return new ProcessRunResult(0, string.Empty, string.Empty);
        }

        private static List<string> Unquote(string segment)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            var inQuote = false;
            var hasWord = false;
            foreach (var ch in segment)
            {
                if (ch == '\'')
                {
                    inQuote = !inQuote;
                    hasWord = true;
                }
                else if (ch == ' ' && !inQuote)
                {
                    if (hasWord)
                    {
                        words.Add(current.ToString());
                        current.Clear();
                        hasWord = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            if (hasWord)
                words.Add(current.ToString());
            return words;
        }

        private void StageIn(string hostPath, string remotePath)
        {
            if (Directory.Exists(hostPath))
            {
                foreach (var file in Directory.GetFiles(hostPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(hostPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    Files[remotePath.TrimEnd('/') + "/" + relative] = File.ReadAllText(file);
                }
                return;
            }
            Files[remotePath] = File.ReadAllText(hostPath);
        }

        private void StageOut(string remotePath, string hostPath)
        {
            if (Files.TryGetValue(remotePath, out var single) && !Directory.Exists(hostPath))
            {
                File.WriteAllText(hostPath, single);
                return;
            }
            Directory.CreateDirectory(hostPath);
            var prefix = remotePath.TrimEnd('/') + "/";
            foreach (var (path, content) in Files)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                    File.WriteAllText(
                        Path.Combine(hostPath, path[prefix.Length..]), content);
            }
        }

        public void Dispose() => Harness.Dispose();
    }

    private sealed class DisposeThrowingProvider(ISandboxProvider inner) : ISandboxProvider
    {
        public string Name => inner.Name;

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            inner.DisposeLeakedAsync(name, ct);

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            new DisposeThrowingSandbox(await inner.CreateAsync(spec, ct).ConfigureAwait(false));
    }

    private sealed class DisposeThrowingSandbox(ISandbox inner) : ISandbox
    {
        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            inner.ExecAsync(exec, ct);

        public Task SyncStateToHostAsync(CancellationToken ct = default) =>
            inner.SyncStateToHostAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("simulated dispose failure");
        }
    }
}
