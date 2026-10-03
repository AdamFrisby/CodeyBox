using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.OpenStackSandboxPlugin;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the <c>openstack-smoke</c> orchestration
/// (<see cref="OpenStackSandboxSmoke"/>): acquire → <c>uname -a</c> → file
/// stage round-trip → dispose, against the real provider over a fake
/// in-process OpenStack cloud plus a simulated SSH transport. Every test
/// asserts the cloud is empty afterwards: the smoke runner must dispose the
/// sandbox on success, on mid-run failure, and on cancellation.
/// </summary>
public sealed class OpenStackSmokeTests
{
    [Fact]
    public async Task Smoke_Success_Acquire_Uname_StageRoundTrip_Dispose()
    {
        using var harness = new SmokeHarness();
        var liveBefore = SandboxLiveCounter.Active;

        var output = new StringWriter();
        var result = await OpenStackSandboxSmoke.RunAsync(
            harness.Provider, output, TimeProvider.System, roundTripToken: "smoke-token-1");

        Assert.True(result.Succeeded, "smoke failed: " + result.Failure);
        Assert.Null(result.Failure);
        Assert.StartsWith("Linux", result.UnameOutput, StringComparison.Ordinal);
        Assert.Equal(
            ["acquire", "uname", "stage-read", "stage-write", "dispose"],
            result.Timings.Select(t => t.Name).ToArray());
        Assert.All(result.Timings, t => Assert.True(t.Elapsed >= TimeSpan.Zero));

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);

        var text = output.ToString();
        Assert.Contains("acquired codeybox-", text, StringComparison.Ordinal);
        Assert.Contains("stage round-trip ok", text, StringComparison.Ordinal);
        Assert.Contains("disposed in", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Smoke_MidRunFailure_StillDisposes()
    {
        using var harness = new SmokeHarness();
        harness.Transport.FailStageRead = true;
        var liveBefore = SandboxLiveCounter.Active;

        var result = await OpenStackSandboxSmoke.RunAsync(
            harness.Provider, TextWriter.Null, TimeProvider.System, roundTripToken: "smoke-token-2");

        Assert.False(result.Succeeded);
        Assert.Contains("stage read", result.Failure, StringComparison.Ordinal);
        Assert.Contains("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task Smoke_Cancellation_StillDisposes()
    {
        using var harness = new SmokeHarness();
        using var cts = new CancellationTokenSource();
        harness.Transport.CancelOnUname = cts;
        var liveBefore = SandboxLiveCounter.Active;

        var result = await OpenStackSandboxSmoke.RunAsync(
            harness.Provider, TextWriter.Null, TimeProvider.System, roundTripToken: "smoke-token-3", ct: cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("cancelled", result.Failure);
        Assert.Contains("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task Smoke_DisposeFailure_ReportsFailure()
    {
        using var harness = new SmokeHarness();
        var provider = new DisposeThrowingProvider(harness.Provider);
        var liveBefore = SandboxLiveCounter.Active;

        var output = new StringWriter();
        var result = await OpenStackSandboxSmoke.RunAsync(
            provider, output, TimeProvider.System, roundTripToken: "smoke-token-4");

        Assert.False(result.Succeeded);
        Assert.Contains("dispose failed", result.Failure, StringComparison.Ordinal);
        Assert.Contains("may leak", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("dispose", result.Timings.Select(t => t.Name), StringComparer.Ordinal);
        Assert.Contains("dispose failed", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
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
            throw new InvalidOperationException("injected dispose failure");
        }
    }

    // ------------------------------------------------------------------
    // Harness: real provider, fake cloud, simulated transport
    // ------------------------------------------------------------------

    private sealed class SmokeHarness : IDisposable
    {
        public SmokeFakeCloud Cloud { get; } = new();
        public SmokeFakeTransport Transport { get; } = new();
        public OpenStackSandboxProvider Provider { get; }
        private readonly HttpClient _http;

        public SmokeHarness()
        {
            _http = new HttpClient(Cloud) { Timeout = Timeout.InfiniteTimeSpan };
            var options = new OpenStackSandboxOptions
            {
                Enabled = true,
                AuthUrl = "http://localhost/",
                Region = "test-region",
                Interface = "public",
                AllowUnsafeHttp = true,
                OwnerId = "smoke-host",
                ImageName = "test-image",
                FlavorName = "test-flavor",
                NetworkId = "net-1",
                OrchestratorSshCidrs = ["203.0.113.0/24"],
                DnsServerIps = [],
                NtpServerIps = [],
                PollIntervalMilliseconds = 5,
                MaxPollIntervalMilliseconds = 10,
                ReadyTimeoutSeconds = 30,
                SshReadyTimeoutSeconds = 30,
                SshUser = "tester",
            };
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OS_APPLICATION_CREDENTIAL_ID"] = "cred-id",
                ["OS_APPLICATION_CREDENTIAL_SECRET"] = "cred-secret",
            };
            Provider = new OpenStackSandboxProvider(
                () => options,
                _http,
                new SmokeFakeKeys(),
                new SmokeFakeDns(),
                new SmokeFakeTransportFactory(Transport),
                name => env.TryGetValue(name, out var value) ? value : null,
                TimeProvider.System,
                NullLogger.Instance);
        }

        public void Dispose()
        {
            Provider.Dispose();
            _http.Dispose();
        }
    }

    private sealed class SmokeFakeKeys : IOpenStackKeyGenerator
    {
        public Task<OpenStackClientKeyMaterial> GenerateClientKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = comment; _ = ct;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "id-ed25519-test");
            File.WriteAllText(path, "fake-private");
            return Task.FromResult(new OpenStackClientKeyMaterial(path, "ssh-ed25519 " + new string('A', 64)));
        }

        public Task<OpenStackHostKeyMaterial> GenerateHostKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = directory; _ = comment; _ = ct;
            return Task.FromResult(new OpenStackHostKeyMaterial(
                "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n",
                "ssh-ed25519 " + new string('B', 64)));
        }
    }

    private sealed class SmokeFakeDns : IOpenStackDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            _ = host; _ = ct;
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }
    }

    private sealed class SmokeFakeTransportFactory(SmokeFakeTransport transport) : IOpenStackTransportFactory
    {
        public IRemoteHostTransport Create(OpenStackSshTransportSpec spec)
        {
            _ = spec;
            return transport;
        }
    }

    /// <summary>
    /// Simulated guest: an in-memory file store plus a tiny shell shim that
    /// answers the smoke flow's commands (<c>true</c>, <c>uname -a</c>,
    /// <c>cat</c>, <c>tee</c>). <c>StageIn</c> copies host files into the
    /// store; <c>StageOut</c> writes the store back to the host — the same
    /// directions the real SSH transport moves bytes.
    /// </summary>
    private sealed class SmokeFakeTransport : IRemoteHostTransport
    {
        public string DiagnosticId => "smoke-fake";
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public bool FailStageRead { get; set; }
        public CancellationTokenSource? CancelOnUname { get; set; }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            bool killOnOutputLimit = true)
        {
            _ = stdoutChunkCallback; _ = stderrChunkCallback;
            _ = maxStdoutBytes; _ = maxStderrBytes; _ = killOnOutputLimit;
            ct.ThrowIfCancellationRequested();
            var script = argv.Count == 3 && argv[0] == "bash" ? argv[2] : string.Join(" ", argv);
            var segment = script.Contains("&&", StringComparison.Ordinal)
                ? script[(script.LastIndexOf("&&", StringComparison.Ordinal) + 2)..].Trim()
                : script.Trim();
            var words = SplitShell(segment);
            if (words is ["true"] or ["echo", "ready"])
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            if (words is ["uname", "-a", ..])
            {
                CancelOnUname?.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessRunResult(
                    0, "Linux smoke-guest 6.8.0-generic #1 SMP x86_64 GNU/Linux", string.Empty));
            }
            if (words is ["cat", var path])
            {
                if (FailStageRead)
                    return Task.FromResult(new ProcessRunResult(1, string.Empty, "cat: simulated failure"));
                return Files.TryGetValue(path, out var content)
                    ? Task.FromResult(new ProcessRunResult(0, content, string.Empty))
                    : Task.FromResult(new ProcessRunResult(1, string.Empty, "cat: No such file"));
            }
            if (words is ["tee", var target, ..])
            {
                Files[target] = stdin ?? string.Empty;
                return Task.FromResult(new ProcessRunResult(0, stdin ?? string.Empty, string.Empty));
            }
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
        {
            _ = ct;
            if (Directory.Exists(hostPath))
            {
                foreach (var file in Directory.GetFiles(hostPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(hostPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    Files[remotePath.TrimEnd('/') + "/" + relative] = File.ReadAllText(file);
                }
                return Task.CompletedTask;
            }
            Files[remotePath] = File.ReadAllText(hostPath);
            return Task.CompletedTask;
        }

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            _ = ct;
            if (Files.TryGetValue(remotePath, out var single) && !Directory.Exists(hostPath))
            {
                File.WriteAllText(hostPath, single);
                return Task.CompletedTask;
            }
            Directory.CreateDirectory(hostPath);
            var prefix = remotePath.TrimEnd('/') + "/";
            foreach (var (path, content) in Files)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                    File.WriteAllText(Path.Combine(hostPath, path[prefix.Length..]), content);
            }
            return Task.CompletedTask;
        }

        private static string[] SplitShell(string segment)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            var hasWord = false;
            foreach (var ch in segment)
            {
                if (inQuotes)
                {
                    if (ch == '\'')
                        inQuotes = false;
                    else
                        current.Append(ch);
                }
                else if (ch == '\'')
                {
                    inQuotes = true;
                    hasWord = true;
                }
                else if (char.IsWhiteSpace(ch))
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
                    hasWord = true;
                }
            }
            if (hasWord)
                words.Add(current.ToString());
            return [.. words];
        }
    }

    private sealed class SmokeFakeCloud : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string> Servers { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> Keypairs { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> SecurityGroups { get; } = new(StringComparer.Ordinal);
        private int _seq;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == "/v3/auth/tokens")
            {
                var response = Json(
                    "{ \"token\": { \"expires_at\": \"" + DateTimeOffset.UtcNow.AddHours(1).ToString("O")
                    + "\", \"catalog\": ["
                    + "{\"type\":\"compute\",\"name\":\"compute\",\"endpoints\":"
                    + "[{\"id\":\"e1\",\"interface\":\"public\",\"region\":\"test-region\",\"url\":\"http://localhost/compute/\"}]},"
                    + "{\"type\":\"network\",\"name\":\"network\",\"endpoints\":"
                    + "[{\"id\":\"e2\",\"interface\":\"public\",\"region\":\"test-region\",\"url\":\"http://localhost/network/\"}]},"
                    + "{\"type\":\"image\",\"name\":\"image\",\"endpoints\":"
                    + "[{\"id\":\"e3\",\"interface\":\"public\",\"region\":\"test-region\",\"url\":\"http://localhost/image/\"}]}"
                    + "] } }");
                response.Headers.Add("X-Subject-Token", "token-1");
                return response;
            }
            if (path.StartsWith("/compute/", StringComparison.Ordinal))
                return HandleCompute(request.Method, path["/compute/".Length..], body);
            if (path.StartsWith("/network/", StringComparison.Ordinal))
                return HandleNetwork(request.Method, path["/network/".Length..], request.RequestUri!.Query, body);
            if (path.StartsWith("/image/", StringComparison.Ordinal))
                return HandleImage(request.Method, path["/image/".Length..]);
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private HttpResponseMessage HandleCompute(HttpMethod method, string path, string body)
        {
            if (method == HttpMethod.Get && path.StartsWith("flavors/detail", StringComparison.Ordinal))
                return Json("""{"flavors":[{"id":"flav-1","name":"test-flavor","vcpus":2,"ram":4096,"disk":20}]}""");
            if (method == HttpMethod.Get && path == "limits")
                return Json("""{"limits":{"absolute":{"maxTotalInstances":10,"totalInstancesUsed":1,"maxTotalCores":20,"totalCoresUsed":4,"maxTotalRAMSize":51200,"totalRAMUsed":8192}}}""");
            if (method == HttpMethod.Post && path == "servers")
            {
                var id = "srv-" + Interlocked.Increment(ref _seq);
                using var doc = JsonDocument.Parse(body);
                Servers[id] = doc.RootElement.GetProperty("server").GetProperty("name").GetString() ?? id;
                return Json($"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{Servers[id]}\",\"status\":\"BUILD\"}}}}", HttpStatusCode.Accepted);
            }
            if (method == HttpMethod.Get && path.StartsWith("servers/", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..];
                return Servers.ContainsKey(id)
                    ? Json($"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{Servers[id]}\",\"status\":\"ACTIVE\",\"tags\":[],\"metadata\":{{}}}}}}")
                    : Status(HttpStatusCode.NotFound, "{}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("servers/", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..];
                return Servers.TryRemove(id, out _) ? NoContent() : Status(HttpStatusCode.NotFound, "{}");
            }
            if (method == HttpMethod.Post && path == "os-keypairs")
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("keypair").GetProperty("name").GetString()!;
                Keypairs[name] = string.Empty;
                return Json($"{{\"keypair\":{{\"name\":\"{name}\"}}}}", HttpStatusCode.Created);
            }
            if (method == HttpMethod.Delete && path.StartsWith("os-keypairs/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["os-keypairs/".Length..]);
                return Keypairs.TryRemove(name, out _) ? NoContent() : Status(HttpStatusCode.NotFound, "{}");
            }
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private HttpResponseMessage HandleNetwork(HttpMethod method, string path, string query, string body)
        {
            if (method == HttpMethod.Post && path == "v2.0/security-groups")
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("security_group").GetProperty("name").GetString() ?? "sg";
                var id = "sg-" + Interlocked.Increment(ref _seq);
                SecurityGroups[id] = name;
                return Json($"{{\"security_group\":{{\"id\":\"{id}\",\"name\":\"{name}\"}}}}", HttpStatusCode.Created);
            }
            if (method == HttpMethod.Delete && path.StartsWith("v2.0/security-groups/", StringComparison.Ordinal))
            {
                var id = path["v2.0/security-groups/".Length..].Split('?')[0];
                return SecurityGroups.TryRemove(id, out _) ? NoContent() : Status(HttpStatusCode.NotFound, "{}");
            }
            if (method == HttpMethod.Post && path == "v2.0/security-group-rules")
                return Json("""{"security_group_rule":{"id":"rule-1"}}""", HttpStatusCode.Created);
            if (method == HttpMethod.Get && path.StartsWith("v2.0/ports", StringComparison.Ordinal))
            {
                var deviceId = GetQueryValue(query, "device_id") ?? "unknown";
                return Json("{\"ports\":[{\"id\":\"port-" + deviceId + "\",\"device_id\":\"" + deviceId
                    + "\",\"fixed_ips\":[{\"ip_address\":\"10.0.0.5\"}]}]}");
            }
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage HandleImage(HttpMethod method, string path)
        {
            if (method == HttpMethod.Get && path.StartsWith("v2/images", StringComparison.Ordinal))
                return Json("""{"images":[{"id":"img-1","name":"test-image","status":"active"}]}""");
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private static string? GetQueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query))
                return null;
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.IndexOf('=');
                if (equals <= 0)
                    continue;
                if (Uri.UnescapeDataString(part[..equals]) == name)
                    return Uri.UnescapeDataString(part[(equals + 1)..]);
            }
            return null;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode status, string body) => Json(body, status);

        private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);
    }
}
