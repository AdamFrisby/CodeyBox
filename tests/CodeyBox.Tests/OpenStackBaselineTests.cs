using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.OpenStackSandboxPlugin;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Baseline parity tests for the <c>openstack</c> provider: shared toolchain
/// hash identity with Incus, provider-scoped pin round-trips (including
/// legacy pins), single-flight bakes, failed-build cleanup, foreign-pin
/// equivalence, and retention that never deletes a pinned image.
/// </summary>
public sealed class OpenStackBaselineTests
{
    private const string OwnerId = "baseline-test-host";

    // ------------------------------------------------------------------
    // Shared toolchain hash
    // ------------------------------------------------------------------

    [Fact]
    public void ToolchainHash_IdenticalToIncus_ForSameInputs()
    {
        var hostDir = Directory.CreateTempSubdirectory("os-baseline-hash-").FullName;
        try
        {
            var first = Path.Combine(hostDir, "agent-a");
            var second = Path.Combine(hostDir, "agent-b");
            File.WriteAllText(first, "first-binary-bytes");
            File.WriteAllText(second, "second-binary-bytes");
            var firstSha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("first-binary-bytes")));
            var secondSha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("second-binary-bytes")));

            var runcmd = new[] { "apt-get update", "apt-get install -y dotnet-sdk-10.0" };
            var verifications = new[]
            {
                new BaselineVerificationCommand("dotnet", ["dotnet", "--version"], "dotnet missing"),
                new BaselineVerificationCommand("agent", ["codex", "--version"], "agent missing"),
            };

            var incusOptions = new IncusSandboxOptions
            {
                ExtraRuncmd = runcmd,
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = "/irrelevant/a",
                        VmDestPath = "/usr/local/bin/agent-a",
                        VmSymlinks = ["agent"],
                        Label = "a",
                    },
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = "/irrelevant/b",
                        VmDestPath = "/usr/local/bin/agent-b",
                        VmSymlinks = [],
                        Label = null,
                    },
                ],
                BaselineVerificationCommands = verifications,
            };
            var incusHash = IncusBaselineNaming.ComputeSharedToolchainHash(
                incusOptions, [firstSha, secondSha]);

            var openstackOptions = BaselineOptions(configure: o => o with
            {
                ExtraRuncmd = runcmd,
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = first,
                        VmDestPath = "/usr/local/bin/agent-a",
                        VmSymlinks = ["agent"],
                        Label = "a",
                    },
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = second,
                        VmDestPath = "/usr/local/bin/agent-b",
                        VmSymlinks = [],
                        Label = null,
                    },
                ],
                BaselineVerificationCommands = verifications,
            });
            var builder = NewBuilder(openstackOptions);
            var plan = builder.Plan();

            Assert.Equal(64, incusHash.Length);
            Assert.Equal(incusHash, plan.FullHash);
            Assert.Equal(plan.ShortHash, plan.FullHash[..12]);

            var drifted = BaselineContentHash.ComputeToolchainHash(plan.Inputs with
            {
                Runcmd = ["apt-get update", "apt-get install -y dotnet-sdk-10.0 jq"],
            });
            Assert.NotEqual(plan.FullHash, drifted);
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public void IncusResolveBaselineRef_EmitsScopedPin_WithLiveBareRef()
    {
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions
            {
                StoragePoolName = "codeybox-zfs",
                NetworkProfiles = new Dictionary<string, string> { ["internet-only"] = "cb-net" },
                ExtraRuncmd = ["install git"],
            },
            NullLogger<IncusSandboxProvider>.Instance);

        var pin = provider.ResolveBaselineRef("internet-only", SandboxProfileFlavor.Headless);
        Assert.NotNull(pin);
        Assert.True(BaselinePin.TryParseScopedPin(pin, out var scope, out var hash, out var reference));
        Assert.Equal("incus", scope);
        Assert.Equal(12, hash.Length);
        Assert.StartsWith("cb-incus-baseline-", reference, StringComparison.Ordinal);
        Assert.True(IncusSandboxProvider.IsRoutableBaselineRef(pin));
        Assert.False(IncusSandboxProvider.IsRoutableBaselineRef(
            $"openstack/tc-{hash}/{reference}"));
    }

    // ------------------------------------------------------------------
    // Pin round-trips, including legacy pins
    // ------------------------------------------------------------------

    [Fact]
    public void Pin_RoundTrip_And_LegacyLoad()
    {
        var fullHash = new string('c', 64);
        var pin = BaselinePin.FormatScopedPin("openstack", fullHash, "codeybox-baseline-tc-cccccccccccc");
        Assert.True(BaselinePin.TryParseScopedPin(pin, out var scope, out var hash, out var reference));
        Assert.Equal("openstack", scope);
        Assert.Equal("cccccccccccc", hash);
        Assert.Equal("codeybox-baseline-tc-cccccccccccc", reference);
        Assert.Equal("cccccccccccc", BaselinePin.TryExtractToolchainHash(pin));
        Assert.True(BaselinePin.IsScopedPin(pin));
        Assert.True(BaselinePin.IsScopedTo(pin, "openstack"));
        Assert.False(BaselinePin.IsScopedTo(pin, "incus"));

        var shortForm = BaselinePin.FormatScopedPin("incus", "abcdef123456", "cb-incus-baseline-x-headless-abcdef123456");
        Assert.Equal("abcdef123456", BaselinePin.TryExtractToolchainHash(shortForm));

        Assert.False(BaselinePin.IsScopedPin("cb-incus-baseline-work-headless-a1b2c3d4e5f6"));
        Assert.Null(BaselinePin.TryExtractToolchainHash("cb-incus-baseline-work-headless-a1b2c3d4e5f6"));
        Assert.False(BaselinePin.IsScopedPin(null));
        Assert.False(BaselinePin.IsScopedPin(string.Empty));
        Assert.False(BaselinePin.IsScopedPin("openstack/tc-xyz/not-hex-at-all-here"));
        Assert.False(BaselinePin.IsScopedPin("openstack/tc-abcdef123456"));
        Assert.False(BaselinePin.IsScopedPin("openstack//ref"));
        Assert.False(BaselinePin.IsScopedPin("/tc-abcdef123456/ref"));
        Assert.Throws<ArgumentException>(() => BaselinePin.FormatScopedPin("OpenStack!", fullHash, "ref"));
        Assert.Throws<ArgumentException>(() => BaselinePin.FormatScopedPin("openstack", "xyz", "ref"));
    }

    // ------------------------------------------------------------------
    // Single-flight build
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentEnsures_SingleFlight_OneBuild()
    {
        using var harness = NewHarness();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            harness.Provider.EnsureBaselineImageAsync("work", SandboxProfileFlavor.Headless, null, CancellationToken.None)));

        Assert.All(results, pin => Assert.NotNull(pin));
        Assert.Single(results.Distinct(StringComparer.Ordinal));
        Assert.StartsWith("openstack/tc-", results[0]!, StringComparison.Ordinal);

        Assert.Equal(1, harness.Cloud.CreateImageCalls);
        Assert.Single(harness.Cloud.Images);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);

        var image = harness.Cloud.Images.Values.Single();
        Assert.Equal("active", image.Status);
        Assert.Contains(OpenStackBaselineNaming.ToolchainTagPrefix, string.Join(",", image.Tags));
    }

    // ------------------------------------------------------------------
    // Failed build cleanup
    // ------------------------------------------------------------------

    [Fact]
    public async Task FailedVerification_LeavesNoHalfImage_And_DeletesBuilder()
    {
        using var harness = NewHarness(configure: o => o with
        {
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand("failing", ["failing-probe"], "probe missing"),
            ],
        });
        harness.Transports.OnRun = argv =>
            argv is ["failing-probe"]
                ? new ProcessRunResult(1, string.Empty, "no such binary")
                : new ProcessRunResult(0, "ok", string.Empty);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.EnsureBaselineImageAsync("work", SandboxProfileFlavor.Headless, null, CancellationToken.None));
        Assert.Contains("failing", ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.Cloud.Images);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.Keypairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
        Assert.Equal(0, harness.Cloud.CreateImageCalls);
    }

    // ------------------------------------------------------------------
    // Foreign pins and legacy refs
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForeignScopedPin_WithEquivalentHash_ResolvesWithoutBuild()
    {
        using var harness = NewHarness();
        var livePin = harness.Provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless)!;
        Assert.True(BaselinePin.TryParseScopedPin(livePin, out _, out var liveHash, out var liveImage));
        harness.Cloud.SeedImage(liveImage, [OpenStackBaselineNaming.ToolchainTag(liveHash)]);

        var foreignPin = BaselinePin.FormatScopedPin(
            "incus", liveHash, "cb-incus-baseline-work-headless-000000000000");
        var resolved = await harness.Provider.EnsureBaselineImageAsync(
            "work", SandboxProfileFlavor.Headless, foreignPin, CancellationToken.None);

        Assert.Equal(livePin, resolved);
        Assert.Equal(0, harness.Cloud.ServerCreates);
        Assert.Equal(0, harness.Cloud.CreateImageCalls);
    }

    [Fact]
    public async Task StaleScopedPin_IsRefused()
    {
        using var harness = NewHarness();
        var stale = BaselinePin.FormatScopedPin("openstack", "ffffffffffff", "codeybox-baseline-tc-ffffffffffff");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.EnsureBaselineImageAsync("work", SandboxProfileFlavor.Headless, stale, CancellationToken.None));
        Assert.Equal(0, harness.Cloud.ServerCreates);
    }

    [Fact]
    public async Task LegacyImageRef_Loads_And_AdoptsScopedPin()
    {
        using var harness = NewHarness();
        var livePin = harness.Provider.ResolveBaselineRef("work", SandboxProfileFlavor.Headless)!;
        BaselinePin.TryParseScopedPin(livePin, out _, out var liveHash, out _);
        harness.Cloud.SeedImage("custom-legacy", [OpenStackBaselineNaming.ToolchainTag(liveHash)]);

        var resolved = await harness.Provider.EnsureBaselineImageAsync(
            "work", SandboxProfileFlavor.Headless, "custom-legacy", CancellationToken.None);
        Assert.Equal(BaselinePin.FormatScopedPin("openstack", liveHash, "custom-legacy"), resolved);
        Assert.Equal(0, harness.Cloud.ServerCreates);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.EnsureBaselineImageAsync(
                "work", SandboxProfileFlavor.Headless, "no-such-image", CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Retention
    // ------------------------------------------------------------------

    [Fact]
    public void Retention_KeepsNewestNPerProject_AndNeverDeletesPinned()
    {
        var images = new List<OpenStackRetainedImage>
        {
            new("img-a1", "111111111111", "proj-a", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new("img-a2", "222222222222", "proj-a", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            new("img-a3", "333333333333", "proj-a", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            new("img-a4", "444444444444", "proj-a", new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero)),
            new("img-a5", "555555555555", "proj-a", new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)),
            new("img-b1", "666666666666", "proj-b", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new("img-b2", "777777777777", "proj-b", null),
        };
        var livePins = new HashSet<string>(StringComparer.Ordinal)
        {
            // Cross-provider scoped pin protects img-a1 by hash even though it
            // is the oldest image in its group.
            BaselinePin.FormatScopedPin("incus", "111111111111", "cb-incus-baseline-x-headless-111111111111"),
            // Exact-name pin protects img-b2 despite its missing timestamp.
            "img-b2",
        };

        var doomed = OpenStackBaselineRetention.SelectForDeletion(images, livePins, keepNewestPerGroup: 2);

        Assert.Equal(["img-a2", "img-a3"], doomed.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Retention_PinnedImageBeyondDepth_Survives()
    {
        var images = new List<OpenStackRetainedImage>
        {
            new("old-pinned", "aaaaaaaaaaaa", null, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            new("new-1", "bbbbbbbbbbbb", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new("new-2", "cccccccccccc", null, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
        };
        var livePins = new HashSet<string>(StringComparer.Ordinal)
        {
            BaselinePin.FormatScopedPin("openstack", "aaaaaaaaaaaa", "old-pinned"),
        };

        Assert.Empty(OpenStackBaselineRetention.SelectForDeletion(images, livePins, keepNewestPerGroup: 2));
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static OpenStackSandboxOptions BaselineOptions(
        Func<OpenStackSandboxOptions, OpenStackSandboxOptions>? configure = null)
    {
        var options = new OpenStackSandboxOptions
        {
            Enabled = true,
            AuthUrl = "http://localhost/",
            Region = "test-region",
            Interface = "public",
            AllowUnsafeHttp = true,
            OwnerId = OwnerId,
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
        return configure is null ? options : configure(options);
    }

    private static OpenStackBaselineBuilder NewBuilder(OpenStackSandboxOptions options)
    {
        var credentials = new OpenStackCredentials(
            new Uri("http://localhost/"), "cred-id", "cred-secret", "test-region", "public", true);
        return new OpenStackBaselineBuilder(
            options, credentials,
            new OpenStackApiClient(new HttpClient(), clock: null, options.ToClientLimits()),
            new FakeKeys(), new FakeTransports(),
            _ => null, TimeProvider.System, NullLogger.Instance);
    }

    private static BaselineHarness NewHarness(
        Func<OpenStackSandboxOptions, OpenStackSandboxOptions>? configure = null) =>
        new(configure);

    private sealed class BaselineHarness : IDisposable
    {
        public FakeBaselineCloud Cloud { get; } = new();
        public FakeTransports Transports { get; } = new();
        public OpenStackSandboxProvider Provider { get; }
        private readonly HttpClient _http;

        public BaselineHarness(Func<OpenStackSandboxOptions, OpenStackSandboxOptions>? configure)
        {
            _http = new HttpClient(Cloud) { Timeout = Timeout.InfiniteTimeSpan };
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OS_APPLICATION_CREDENTIAL_ID"] = "cred-id",
                ["OS_APPLICATION_CREDENTIAL_SECRET"] = "cred-secret",
            };
            Provider = new OpenStackSandboxProvider(
                () => BaselineOptions(configure),
                _http,
                new FakeKeys(),
                new FakeDns(),
                Transports,
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

    private sealed class FakeKeys : IOpenStackKeyGenerator
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

    private sealed class FakeDns : IOpenStackDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            _ = host; _ = ct;
            return Task.FromResult<IPAddress[]>([System.Net.IPAddress.Loopback]);
        }
    }

    private sealed class FakeTransports : IOpenStackTransportFactory
    {
        public Func<IReadOnlyList<string>, ProcessRunResult> OnRun { get; set; } =
            _ => new ProcessRunResult(0, "ok", string.Empty);

        public List<(string HostPath, string RemotePath)> StageInCalls { get; } = [];

        public IRemoteHostTransport Create(OpenStackSshTransportSpec spec)
        {
            _ = spec;
            return new FakeTransport(this);
        }

        private sealed class FakeTransport(FakeTransports owner) : IRemoteHostTransport
        {
            public string DiagnosticId => "fake";

            public Task<ProcessRunResult> RunAsync(
                IReadOnlyList<string> argv, string? stdin, CancellationToken ct,
                Action<string>? stdoutChunkCallback = null, Action<string>? stderrChunkCallback = null,
                int? maxStdoutBytes = null, int? maxStderrBytes = null, bool killOnOutputLimit = true)
            {
                _ = stdin; _ = ct;
                _ = stdoutChunkCallback; _ = stderrChunkCallback;
                _ = maxStdoutBytes; _ = maxStderrBytes; _ = killOnOutputLimit;
                return Task.FromResult(owner.OnRun(argv));
            }

            public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
            {
                _ = ct;
                owner.StageInCalls.Add((hostPath, remotePath));
                return Task.CompletedTask;
            }

            public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
            {
                _ = ct;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class FakeBaselineCloud : HttpMessageHandler
    {
        public ConcurrentDictionary<string, FakeServer> Servers { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeImage> Images { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> Keypairs { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> SecurityGroups { get; } = new(StringComparer.Ordinal);
        public int ServerCreates;
        public int CreateImageCalls;
        private int _seq;

        public void SeedImage(string name, IReadOnlyList<string> tags)
        {
            var id = "img-seed-" + Interlocked.Increment(ref _seq);
            Images[id] = new FakeImage(id, name, [.. tags]);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;

            if (request.Method == HttpMethod.Post && path == "/v3/auth/tokens")
            {
                var response = Json(
                    "{ \"token\": { \"expires_at\": \"" + DateTimeOffset.UtcNow.AddHours(1).ToString("O")
                    + "\", \"catalog\": ["
                    + Entry("compute", "http://localhost/compute/") + ","
                    + Entry("network", "http://localhost/network/") + ","
                    + Entry("image", "http://localhost/image/")
                    + "] } }");
                response.Headers.Add("X-Subject-Token", "token-1");
                return response;
            }
            if (path.StartsWith("/compute/", StringComparison.Ordinal))
                return HandleCompute(request.Method, path["/compute/".Length..], body);
            if (path.StartsWith("/network/", StringComparison.Ordinal))
                return HandleNetwork(request.Method, path["/network/".Length..], query, body);
            if (path.StartsWith("/image/", StringComparison.Ordinal))
                return HandleImage(request.Method, path["/image/".Length..], query);
            return Status(HttpStatusCode.NotFound, """{"error":"unknown"}""");
        }

        private static string Entry(string type, string url) =>
            "{ \"type\": \"" + type + "\", \"name\": \"" + type + "\", \"endpoints\": [" +
            "{ \"id\": \"e1\", \"interface\": \"public\", \"region\": \"test-region\", \"url\": \"" + url + "\" }" +
            "] }";

        private HttpResponseMessage HandleCompute(HttpMethod method, string path, string body)
        {
            if (method == HttpMethod.Get && path.StartsWith("flavors/detail", StringComparison.Ordinal))
                return Json("""{"flavors":[{"id":"flav-1","name":"test-flavor","vcpus":2,"ram":4096,"disk":20}]}""");
            if (method == HttpMethod.Get && path == "limits")
                return Json("""{"limits":{"absolute":{"maxTotalInstances":10,"totalInstancesUsed":1,"maxTotalCores":20,"totalCoresUsed":4,"maxTotalRAMSize":51200,"totalRAMUsed":8192}}}""");
            if (method == HttpMethod.Post && path == "servers")
            {
                var id = "srv-" + Interlocked.Increment(ref _seq);
                Interlocked.Increment(ref ServerCreates);
                using var doc = JsonDocument.Parse(body);
                Servers[id] = new FakeServer(doc.RootElement.GetProperty("server").GetProperty("name").GetString() ?? id);
                return Json($"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{Servers[id].Name}\",\"status\":\"BUILD\"}}}}",
                    HttpStatusCode.Accepted);
            }
            if (method == HttpMethod.Get && path.StartsWith("servers/", StringComparison.Ordinal)
                && !path.Contains("/action", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..];
                if (!Servers.TryGetValue(id, out var fake))
                    return Status(HttpStatusCode.NotFound, """{"itemNotFound":{}}""");
                fake.Gets++;
                var status = fake.Stopped ? "SHUTOFF" : (fake.Gets >= 1 ? "ACTIVE" : "BUILD");
                return Json($"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{fake.Name}\",\"status\":\"{status}\"}}}}");
            }
            if (method == HttpMethod.Post && path.Contains("/action", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..].Split("/action")[0];
                if (!Servers.TryGetValue(id, out var fake))
                    return Status(HttpStatusCode.NotFound, """{"itemNotFound":{}}""");
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("os-stop", out _))
                {
                    fake.Stopped = true;
                    return Status(HttpStatusCode.Accepted, string.Empty);
                }
                if (doc.RootElement.TryGetProperty("createImage", out var action))
                {
                    Interlocked.Increment(ref CreateImageCalls);
                    var imageId = "img-" + Interlocked.Increment(ref _seq);
                    var imageName = action.GetProperty("name").GetString() ?? ("image-" + imageId);
                    Images[imageId] = new FakeImage(imageId, imageName, [$"codeybox-tc-{ExtractHash(imageName)}"]);
                    return Json($"{{\"image_id\":\"{imageId}\"}}", HttpStatusCode.Accepted);
                }
                return Status(HttpStatusCode.BadRequest, "{}");
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
                Keypairs[name] = "key";
                return Json($"{{\"keypair\":{{\"name\":\"{name}\"}}}}", HttpStatusCode.Created);
            }
            if (method == HttpMethod.Delete && path.StartsWith("os-keypairs/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["os-keypairs/".Length..]);
                return Keypairs.TryRemove(name, out _) ? NoContent() : Status(HttpStatusCode.NotFound, "{}");
            }
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private static string ExtractHash(string imageName)
        {
            var marker = "tc-";
            var index = imageName.LastIndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? imageName[(index + marker.Length)..] : "unknown";
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
            if (method == HttpMethod.Get && path.StartsWith("v2.0/security-groups", StringComparison.Ordinal))
            {
                var items = string.Join(",", SecurityGroups.Select(kvp =>
                    $"{{\"id\":\"{kvp.Key}\",\"name\":\"{kvp.Value}\"}}"));
                return Json("{\"security_groups\":[" + items + "]}");
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
                var deviceId = GetQuery(query, "device_id") ?? "unknown";
                return Json("{\"ports\":[{\"id\":\"port-" + deviceId + "\",\"device_id\":\"" + deviceId
                    + "\",\"fixed_ips\":[{\"ip_address\":\"10.0.0.5\"}]}]}");
            }
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private HttpResponseMessage HandleImage(HttpMethod method, string path, string query)
        {
            if (method == HttpMethod.Get && path.StartsWith("v2/images/", StringComparison.Ordinal)
                && !path.Contains('?'))
            {
                var id = path["v2/images/".Length..];
                if (Images.TryGetValue(id, out var found))
                {
                    found.Gets++;
                    return Json(SerializeImage(found));
                }
                if (id == "img-1" || id == "test-image")
                    return Json("""{"id":"img-1","name":"test-image","status":"active"}""");
                var named = Images.Values.FirstOrDefault(i => i.Name == id);
                if (named is not null)
                {
                    named.Gets++;
                    return Json(SerializeImage(named));
                }
                return Status(HttpStatusCode.NotFound, "{}");
            }
            if (method == HttpMethod.Get && path.StartsWith("v2/images", StringComparison.Ordinal))
            {
                var name = GetQuery(query, "name");
                var tag = GetQuery(query, "tag");
                var items = Images.Values
                    .Where(i => (name is null || i.Name == name) && (tag is null || i.Tags.Contains(tag)))
                    .Select(SerializeImage);
                if (name == "test-image")
                    items = items.Append("""{"id":"img-1","name":"test-image","status":"active"}""");
                return Json("{\"images\":[" + string.Join(",", items) + "]}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("v2/images/", StringComparison.Ordinal))
            {
                var id = path["v2/images/".Length..];
                return Images.TryRemove(id, out _) ? NoContent() : Status(HttpStatusCode.NotFound, "{}");
            }
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private static string SerializeImage(FakeImage image)
        {
            if (image.Gets >= 1 && image.Status == "saving")
                image.Status = "active";
            var tags = string.Join(",", image.Tags.Select(t => $"\"{t}\""));
            return $"{{\"id\":\"{image.Id}\",\"name\":\"{image.Name}\",\"status\":\"{image.Status}\",\"tags\":[{tags}]," +
                $"\"created_at\":\"{image.CreatedAt:O}\"}}";
        }

        private static string? GetQuery(string query, string name)
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

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return response;
        }

        private static HttpResponseMessage Status(HttpStatusCode status, string body) =>
            Json(body, status);

        private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

        internal sealed class FakeServer(string name)        {
            public string Name { get; } = name;
            public int Gets;
            public bool Stopped;
        }

        internal sealed class FakeImage(string id, string name, List<string> tags)
        {
            public string Id { get; } = id;
            public string Name { get; } = name;
            public List<string> Tags { get; } = tags;
            public string Status { get; set; } = "active";
            public int Gets;
            public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        }
    }
}
