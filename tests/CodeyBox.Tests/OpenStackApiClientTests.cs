using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.OpenStackSandboxPlugin;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape coverage for the OpenStack REST client. A live integration
/// test cannot run in this suite — it needs an OpenStack cloud with quota and
/// outbound network — so <see cref="FakeOpenStackServer"/> below mirrors the
/// Keystone/Nova/Neutron/Glance surfaces the client drives, with response
/// shapes taken from the OpenStack API references (Keystone v3 tokens, Nova
/// servers/flavors/os-keypairs/limits + createImage action, Neutron v2.0
/// security groups/rules/floating IPs/ports, Glance v2 images).
/// </summary>
public sealed class OpenStackApiClientTests
{
    private const string CredentialId = "test-credential-id";
    private const string CredentialSecret = "test-credential-secret-not-a-real-one";

    private static OpenStackCredentials TestCredentials() => new(
        new Uri("http://localhost:5000/v3", UriKind.Absolute),
        CredentialId,
        CredentialSecret,
        "test-region",
        "public",
        AllowUnsafeHttp: true);

    private static OpenStackApiClient NewClient(
        FakeOpenStackServer server,
        TimeProvider? clock = null,
        OpenStackClientLimits? limits = null) =>
        new(new HttpClient(server), clock ?? TimeProvider.System,
            limits ?? new OpenStackClientLimits { AllowUnsafeHttp = true });

    private static OpenStackClientLimits FastPollLimits() => new()
    {
        AllowUnsafeHttp = true,
        PollInterval = TimeSpan.FromMilliseconds(5),
        MaxPollInterval = TimeSpan.FromMilliseconds(20),
        HttpTimeout = TimeSpan.FromSeconds(30),
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // ------------------------------------------------------------------
    // Options / credential chain
    // ------------------------------------------------------------------

    [Fact]
    public void ProviderKind_IsOpenstack_AndDisabledByDefault()
    {
        Assert.Equal("openstack", OpenStackSandboxOptions.ProviderKind);
        Assert.Equal("codeybox.openstack-sandbox", OpenStackSandboxOptions.PluginId);
        Assert.False(new OpenStackSandboxOptions().Enabled);
    }

    [Fact]
    public void CredentialChain_ResolvesFromEnvironment_NeverFromConfig()
    {
        var options = new OpenStackSandboxOptions
        {
            AuthUrl = "https://cloud.example.com:5000/v3",
            Region = "region-a",
            Interface = "public",
        };
        string? Env(string name) => name switch
        {
            "OS_APPLICATION_CREDENTIAL_ID" => "id-from-env",
            "OS_APPLICATION_CREDENTIAL_SECRET" => "secret-from-env",
            _ => null,
        };
        var creds = OpenStackCredentials.Resolve(options, Env);
        Assert.Equal("https://cloud.example.com:5000/v3", creds.AuthUrl.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("id-from-env", creds.ApplicationCredentialId);
        Assert.Equal("secret-from-env", creds.ApplicationCredentialSecret);
        Assert.Equal("region-a", creds.Region);
    }

    [Fact]
    public void CredentialChain_MissingSecret_FailsLoudlyNamingTheVariable()
    {
        var options = new OpenStackSandboxOptions { AuthUrl = "https://cloud.example.com:5000/v3" };
        string? Env(string name) => name == "OS_APPLICATION_CREDENTIAL_ID" ? "id" : null;
        var ex = Assert.Throws<InvalidOperationException>(() => OpenStackCredentials.Resolve(options, Env));
        Assert.Contains("OS_APPLICATION_CREDENTIAL_SECRET", ex.Message);
        Assert.DoesNotContain("secret-value", ex.Message);
    }

    [Fact]
    public void CredentialChain_RemoteHttpAuthUrl_IsRefused()
    {
        var options = new OpenStackSandboxOptions { AuthUrl = "http://203.0.113.7:5000/v3" };
        string? Env(string name) => "x";
        Assert.Throws<InvalidOperationException>(() => OpenStackCredentials.Resolve(options, Env));
    }

    // ------------------------------------------------------------------
    // Auth + catalog
    // ------------------------------------------------------------------

    [Fact]
    public async Task Auth_SendsApplicationCredential_AndCachesToken()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();

        var (token, expiresAt) = await client.AuthenticateAsync(creds, CancellationToken.None);
        var (token2, _) = await client.AuthenticateAsync(creds, CancellationToken.None);

        Assert.Equal("token-1", token);
        Assert.Equal(token, token2);
        Assert.Equal(1, server.AuthCalls);
        Assert.True(expiresAt > DateTimeOffset.UtcNow);

        var authBody = server.AuthBodies.Single();
        using var doc = JsonDocument.Parse(authBody);
        var methods = doc.RootElement.GetProperty("auth").GetProperty("identity")
            .GetProperty("methods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("application_credential", methods);
        Assert.DoesNotContain("password", methods);
        var appCred = doc.RootElement.GetProperty("auth").GetProperty("identity")
            .GetProperty("application_credential");
        Assert.Equal(CredentialId, appCred.GetProperty("id").GetString());
        Assert.Equal(CredentialSecret, appCred.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task Auth_UsesCachedServiceToken_ForServiceCalls()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();

        await client.GetLimitsAsync(creds, CancellationToken.None);
        await client.GetLimitsAsync(creds, CancellationToken.None);

        Assert.Equal(1, server.AuthCalls);
        Assert.All(server.ServiceTokensSeen, t => Assert.Equal("token-1", t));
    }

    [Fact]
    public async Task Auth_RefreshesToken_BeforeExpiry()
    {
        var clock = new ManualTimeProvider();
        var server = new FakeOpenStackServer { Now = clock.GetUtcNow, TokenLifetime = TimeSpan.FromMinutes(10) };
        var client = NewClient(server, clock);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        await client.AuthenticateAsync(creds, ct);
        Assert.Equal(1, server.AuthCalls);

        clock.Advance(TimeSpan.FromMinutes(8));
        await client.AuthenticateAsync(creds, ct);
        Assert.Equal(1, server.AuthCalls);

        // Now + 60s skew reaches the 10-minute expiry: must re-authenticate.
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var (token, _) = await client.AuthenticateAsync(creds, ct);
        Assert.Equal(2, server.AuthCalls);
        Assert.Equal("token-2", token);
    }

    [Fact]
    public void Catalog_ResolvesByRegionAndInterface()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var catalog = new KeystoneCatalog
        {
            Services =
            [
                new KeystoneService
                {
                    Type = "compute",
                    Endpoints =
                    [
                        new KeystoneEndpoint { Interface = "public", Region = "r1", Url = "http://localhost:9001/v2.1/" },
                        new KeystoneEndpoint { Interface = "public", Region = "r2", Url = "http://localhost:9002/v2.1/" },
                        new KeystoneEndpoint { Interface = "internal", Region = "r1", Url = "http://localhost:9003/v2.1/" },
                    ],
                },
            ],
        };

        Assert.Equal("http://localhost:9002/v2.1/",
            client.ResolveServiceEndpoint(catalog, "compute", "r2", "public").AbsoluteUri);
        Assert.Equal("http://localhost:9003/v2.1/",
            client.ResolveServiceEndpoint(catalog, "compute", "r1", "internal").AbsoluteUri);
        // Empty region takes the first interface match.
        Assert.Equal("http://localhost:9001/v2.1/",
            client.ResolveServiceEndpoint(catalog, "compute", string.Empty, "public").AbsoluteUri);

        Assert.Throws<OpenStackApiException>(
            () => client.ResolveServiceEndpoint(catalog, "compute", "no-such-region", "public"));
        Assert.Throws<OpenStackApiException>(
            () => client.ResolveServiceEndpoint(catalog, "baremetal", "r1", "public"));
    }

    [Fact]
    public void Catalog_RemoteHttpEndpoint_IsRefused_EvenWithAllowUnsafeHttp()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var catalog = new KeystoneCatalog
        {
            Services =
            [
                new KeystoneService
                {
                    Type = "compute",
                    Endpoints =
                    [
                        new KeystoneEndpoint
                        {
                            Interface = "public", Region = "r1", Url = "http://203.0.113.9:8774/v2.1/",
                        },
                    ],
                },
            ],
        };
        var ex = Assert.Throws<OpenStackApiException>(
            () => client.ResolveServiceEndpoint(catalog, "compute", "r1", "public"));
        Assert.Equal(OpenStackFailureKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task Auth_RejectedCredential_Maps401_WithoutLeakingSecret()
    {
        var server = new FakeOpenStackServer { RejectAuth = true };
        var client = NewClient(server);

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.AuthenticateAsync(TestCredentials(), CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.Unauthorized, ex.Kind);
        Assert.DoesNotContain(CredentialSecret, ex.Message);
        Assert.DoesNotContain(CredentialSecret, ex.ToString());
    }

    // ------------------------------------------------------------------
    // 401 re-auth, 409/413/429 mapping
    // ------------------------------------------------------------------

    [Fact]
    public async Task ServiceCall_401_ReauthenticatesOnce_AndRetries()
    {
        var server = new FakeOpenStackServer { RejectTokensOnce = ["token-1"] };
        var client = NewClient(server);
        var creds = TestCredentials();

        var limits = await client.GetLimitsAsync(creds, CancellationToken.None);

        Assert.Equal(2, server.AuthCalls);
        Assert.Equal(10, limits.MaxTotalInstances);
        Assert.Equal(["token-1", "token-2"], server.ServiceTokensSeen);
    }

    [Fact]
    public async Task ServiceCall_401Twice_SurfacesUnauthorized()
    {
        var server = new FakeOpenStackServer { RejectAllServiceTokens = true };
        var client = NewClient(server);

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.GetLimitsAsync(TestCredentials(), CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.Unauthorized, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Equal(2, server.AuthCalls);
    }

    [Fact]
    public async Task ServiceCall_429_MapsThrottled_WithRetryAfterAndRequestId()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        server.InterceptOnce = _ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"overLimit":{"message":"Rate limit exceeded","code":429}}""",
                Encoding.UTF8, "application/json"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)) },
        };
        server.InterceptRequestId = "req-429";

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.GetLimitsAsync(TestCredentials(), CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.Throttled, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Equal("req-429", ex.RequestId);
    }

    [Fact]
    public async Task ServiceCall_413_MapsQuota_WithBodyRetryAfter()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        server.InterceptOnce = _ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent(
                """{"overLimit":{"message":"Quota exceeded for instances","code":413,"retryAfter":5}}""",
                Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.CreateServerAsync(TestCredentials(),
                new OpenStackServerSpec("vm-1", "flavor-1", "image-1", ["net-1"]),
                CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.QuotaExhausted, ex.Kind);
        Assert.True(ex.IsQuota);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal("overLimit", ex.FaultCode);
    }

    [Fact]
    public async Task ServiceCall_409_MapsTransientConflict()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        server.InterceptOnce = _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"conflict":{"message":"Server is building","code":409}}""",
                Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.DeleteServerAsync(TestCredentials(), "srv-1", CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.Conflict, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.Equal("conflict", ex.FaultCode);
    }

    [Fact]
    public async Task ServiceCall_NeutronError_ExtractsTypedFault()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        server.InterceptOnce = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"NeutronError":{"message":"Invalid CIDR","type":"InvalidInput","detail":"x"}}""",
                Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.CreateSecurityGroupAsync(TestCredentials(), "sg-1", null,
                CancellationToken.None));
        Assert.Equal("NeutronError:InvalidInput", ex.FaultCode);
        Assert.Contains("Invalid CIDR", ex.Message);
    }

    // ------------------------------------------------------------------
    // Nova: servers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Server_CreatePollDelete_FullLifecycle()
    {
        var server = new FakeOpenStackServer { GetsUntilActive = 2 };
        var client = NewClient(server, limits: FastPollLimits());
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var created = await client.CreateServerAsync(creds,
            new OpenStackServerSpec("codeybox-vm-1", "flavor-1", "image-1", ["net-1"],
                KeyName: "codeybox-key",
                UserData: "#cloud-config\n",
                Metadata: new Dictionary<string, string> { ["codeybox.managed"] = "true" },
                Tags: ["codeybox-managed"]),
            ct);
        Assert.Equal("BUILD", created.Status);
        Assert.NotNull(created.Id);

        var createBody = server.RequestBodies("POST", "/compute/servers").Single();
        using var createDoc = JsonDocument.Parse(createBody);
        var payload = createDoc.RootElement.GetProperty("server");
        Assert.Equal("flavor-1", payload.GetProperty("flavorRef").GetString());
        Assert.Equal("image-1", payload.GetProperty("imageRef").GetString());
        Assert.Equal("codeybox-key", payload.GetProperty("key_name").GetString());
        Assert.NotNull(payload.GetProperty("user_data").GetString());
        Assert.Equal("true", payload.GetProperty("metadata").GetProperty("codeybox.managed").GetString());

        var active = await client.WaitForServerStatusAsync(
            creds, created.Id!, ["ACTIVE"], ct, TimeSpan.FromSeconds(10));
        Assert.Equal("ACTIVE", active.Status);

        var listed = await client.ListServersAsync(creds, ["codeybox-managed"],
            new Dictionary<string, string> { ["codeybox.managed"] = "true" }, ct);
        Assert.Contains(listed, s => s.Id == created.Id);
        Assert.Contains("tags=codeybox-managed", server.RequestQueries("GET", "/compute/servers/detail").Single());

        Assert.True(await client.DeleteServerAsync(creds, created.Id!, ct));
        await client.WaitForServerDeletedAsync(creds, created.Id!, ct, TimeSpan.FromSeconds(10));
        Assert.Null(await client.GetServerAsync(creds, created.Id!, ct));
        Assert.False(await client.DeleteServerAsync(creds, created.Id!, ct));
    }

    [Fact]
    public async Task Server_Wait_Timeout_ThrowsTypedError()
    {
        var server = new FakeOpenStackServer { GetsUntilActive = int.MaxValue };
        var client = NewClient(server, limits: FastPollLimits());
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var created = await client.CreateServerAsync(creds,
            new OpenStackServerSpec("vm-slow", "flavor-1", "image-1", ["net-1"]), ct);
        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.WaitForServerStatusAsync(creds, created.Id!, ["ACTIVE"], ct, TimeSpan.FromMilliseconds(80)));
        Assert.Equal(OpenStackFailureKind.Unexpected, ex.Kind);
        Assert.Contains("did not reach", ex.Message);
    }

    [Fact]
    public async Task Server_Wait_FaultStatus_FailsFastWithFaultText()
    {
        var server = new FakeOpenStackServer { NewServerStatus = "ERROR" };
        server.NewServerFault = "No valid host";
        var client = NewClient(server, limits: FastPollLimits());
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var created = await client.CreateServerAsync(creds,
            new OpenStackServerSpec("vm-bad", "flavor-1", "image-1", ["net-1"]), ct);
        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.WaitForServerStatusAsync(creds, created.Id!, ["ACTIVE"], ct, TimeSpan.FromSeconds(5)));
        Assert.Contains("No valid host", ex.Message);
    }

    [Fact]
    public async Task Flavor_Lookup_MatchesExactly()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var flavor = await client.GetFlavorByNameAsync(creds, "standard-2", ct);
        Assert.NotNull(flavor);
        Assert.Equal("flavor-2", flavor.Id);
        Assert.Equal(2, flavor.Vcpus);

        Assert.Null(await client.GetFlavorByNameAsync(creds, "no-such-flavor", ct));

        server.DuplicateFlavors = true;
        await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.GetFlavorByNameAsync(creds, "standard-2", ct));
    }

    [Fact]
    public async Task Keypair_CreateDelete_Lifecycle()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var keypair = await client.CreateKeypairAsync(creds, "codeybox-key", null, ct);
        Assert.Equal("codeybox-key", keypair.Name);
        Assert.NotNull(keypair.PrivateKey);
        Assert.DoesNotContain(CredentialSecret, server.RequestBodies("POST", "/compute/os-keypairs").Single());

        Assert.True(await client.DeleteKeypairAsync(creds, "codeybox-key", ct));
        Assert.False(await client.DeleteKeypairAsync(creds, "codeybox-key", ct));
    }

    [Fact]
    public async Task Limits_ReturnsAbsoluteQuotaAndUsage()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);

        var limits = await client.GetLimitsAsync(TestCredentials(), CancellationToken.None);
        Assert.Equal(10, limits.MaxTotalInstances);
        Assert.Equal(3, limits.TotalInstancesUsed);
        Assert.Equal(20, limits.MaxTotalCores);
        Assert.Equal(16000, limits.MaxTotalRamMb);
    }

    // ------------------------------------------------------------------
    // Glance snapshot + images
    // ------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_CreatesImage_ReturnsId_ListAndDelete()
    {
        var server = new FakeOpenStackServer { GetsUntilActive = 0 };
        var client = NewClient(server, limits: FastPollLimits());
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var created = await client.CreateServerAsync(creds,
            new OpenStackServerSpec("vm-snap", "flavor-1", "image-1", ["net-1"]), ct);
        var imageId = await client.SnapshotServerAsync(creds, created.Id!, "codeybox-snap-1",
            new Dictionary<string, string> { ["codeybox.managed"] = "true" }, ct);
        Assert.Equal("img-1", imageId);

        var actionBody = server.RequestBodies("POST", $"/compute/servers/{created.Id}/action").Single();
        using var actionDoc = JsonDocument.Parse(actionBody);
        Assert.Equal("codeybox-snap-1", actionDoc.RootElement.GetProperty("createImage").GetProperty("name").GetString());

        var image = await client.GetImageAsync(creds, imageId, ct);
        Assert.NotNull(image);
        Assert.Equal("codeybox-snap-1", image.Name);

        var listed = await client.ListImagesAsync(creds, "codeybox-snap-1", "codeybox", ct);
        Assert.Contains(listed, i => i.Id == imageId);
        var missingTag = await client.ListImagesAsync(creds, "codeybox-snap-1", "no-such-tag", ct);
        Assert.Empty(missingTag);

        Assert.True(await client.DeleteImageAsync(creds, imageId, ct));
        Assert.Null(await client.GetImageAsync(creds, imageId, ct));
        Assert.False(await client.DeleteImageAsync(creds, imageId, ct));
    }

    [Fact]
    public async Task Snapshot_FallsBackToLocationHeader_WhenBodyHasNoId()
    {
        var server = new FakeOpenStackServer { GetsUntilActive = 0, SnapshotViaLocation = true };
        var client = NewClient(server, limits: FastPollLimits());
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var created = await client.CreateServerAsync(creds,
            new OpenStackServerSpec("vm-snap-loc", "flavor-1", "image-1", ["net-1"]), ct);
        var imageId = await client.SnapshotServerAsync(creds, created.Id!, "snap-loc", null, ct);
        Assert.Equal("img-9", imageId);
    }

    // ------------------------------------------------------------------
    // Neutron
    // ------------------------------------------------------------------

    [Fact]
    public async Task SecurityGroup_CreateRuleDelete_Lifecycle()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var group = await client.CreateSecurityGroupAsync(creds, "codeybox-sg", "test group", ct);
        Assert.Equal("sg-1", group.Id);

        var rule = await client.CreateSecurityGroupRuleAsync(creds,
            new OpenStackSecurityGroupRuleSpec(group.Id!, "ingress", "IPv4", "tcp", 22, 22, "0.0.0.0/0"), ct);
        Assert.Equal("rule-1", rule.Id);

        var ruleBody = server.RequestBodies("POST", "/network/v2.0/security-group-rules").Single();
        using var ruleDoc = JsonDocument.Parse(ruleBody);
        var payload = ruleDoc.RootElement.GetProperty("security_group_rule");
        Assert.Equal("ingress", payload.GetProperty("direction").GetString());
        Assert.Equal(22, payload.GetProperty("port_range_min").GetInt32());

        Assert.True(await client.DeleteSecurityGroupAsync(creds, group.Id!, ct));
        Assert.False(await client.DeleteSecurityGroupAsync(creds, group.Id!, ct));
    }

    [Fact]
    public async Task SecurityGroupRule_Validation_RejectsBadInput_WithoutHttp()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSecurityGroupRuleAsync(creds,
            new OpenStackSecurityGroupRuleSpec("sg-1", "sideways", "IPv4", null, null, null, null), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSecurityGroupRuleAsync(creds,
            new OpenStackSecurityGroupRuleSpec("sg-1", "ingress", "IPv4", "tcp", 8080, 80, null), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSecurityGroupRuleAsync(creds,
            new OpenStackSecurityGroupRuleSpec("sg-1", "ingress", "IPv4", null, null, null, "not-a-cidr"), ct));
        Assert.Equal(0, server.CountRequests("POST", "/network/v2.0/security-group-rules"));
    }

    [Fact]
    public async Task FloatingIp_CreateAssociateDelete_Lifecycle()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        var creds = TestCredentials();
        var ct = CancellationToken.None;

        var floating = await client.CreateFloatingIpAsync(creds, "ext-net-1", ct);
        Assert.Equal("fip-1", floating.Id);
        Assert.Equal("198.51.100.10", floating.Address);

        await client.AssociateFloatingIpAsync(creds, floating.Id!, "port-1", ct);
        Assert.Equal("port-1", server.FloatingIpPorts["fip-1"]);

        var ports = await client.ListPortsByDeviceAsync(creds, "srv-1", ct);
        Assert.Contains(ports, p => p.Id == "port-1");
        Assert.DoesNotContain(ports, p => p.Id == "port-other");

        Assert.True(await client.DeleteFloatingIpAsync(creds, floating.Id!, ct));
        Assert.False(await client.DeleteFloatingIpAsync(creds, floating.Id!, ct));
    }

    // ------------------------------------------------------------------
    // Bounds, cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task OversizedResponse_IsRefused_BeforeBuffering()
    {
        var server = new FakeOpenStackServer { HugeListResponse = true };
        var client = NewClient(server, limits: new OpenStackClientLimits
        {
            AllowUnsafeHttp = true,
            MaxResponseBytes = 128,
        });

        var ex = await Assert.ThrowsAsync<OpenStackApiException>(
            () => client.ListServersAsync(TestCredentials(), null, null, CancellationToken.None));
        Assert.Equal(OpenStackFailureKind.Unexpected, ex.Kind);
        Assert.Contains("byte bound", ex.Message);
    }

    [Fact]
    public async Task CancelledToken_AbortsTheCall()
    {
        var server = new FakeOpenStackServer();
        var client = NewClient(server);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetServerAsync(TestCredentials(), "srv-1", cts.Token));
    }

    // ------------------------------------------------------------------
    // Fake OpenStack service
    // ------------------------------------------------------------------

    private sealed class FakeOpenStackServer : HttpMessageHandler
    {
        private int _serverSeq;
        private int _imageSeq;
        private readonly ConcurrentDictionary<string, FakeServer> _servers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FakeImage> _images = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _keypairs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _secGroups = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _floatingIps = new(StringComparer.Ordinal);

        public List<RequestRecord> Requests { get; } = [];
        public List<string> AuthBodies { get; } = [];
        public List<string> ServiceTokensSeen { get; } = [];
        public Dictionary<string, string> FloatingIpPorts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RejectTokensOnce { get; set; } = new(StringComparer.Ordinal);
        public bool RejectAllServiceTokens;
        public bool RejectAuth;
        public int GetsUntilActive = 1;
        public string NewServerStatus = "BUILD";
        public string? NewServerFault;
        public bool DuplicateFlavors;
        public bool SnapshotViaLocation;
        public bool HugeListResponse;
        public TimeSpan TokenLifetime = TimeSpan.FromHours(1);
        public Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;
        public Func<HttpRequestMessage, HttpResponseMessage?>? InterceptOnce;

        public int AuthCalls;
        public int CountRequests(string method, string pathPrefix) =>
            Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

        public List<string> RequestBodies(string method, string pathPrefix) =>
            Requests.Where(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                .Select(r => r.Body).ToList();

        public List<string> RequestQueries(string method, string pathPrefix) =>
            Requests.Where(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                .Select(r => r.Query).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var uri = request.RequestUri!;
            string? serviceToken = null;
            if (request.Headers.TryGetValues("X-Auth-Token", out var tokenValues))
                serviceToken = tokenValues.FirstOrDefault();
            var record = new RequestRecord(request.Method.Method, uri.AbsolutePath, uri.Query, serviceToken, body);
            lock (Requests) { Requests.Add(record); }

            var intercept = InterceptOnce;
            InterceptOnce = null;
            if (intercept?.Invoke(request) is { } fault)
            {
                if (InterceptRequestId is not null
                    && !fault.Headers.Contains("x-openstack-request-id"))
                    fault.Headers.Add("x-openstack-request-id", InterceptRequestId);
                return fault;
            }

            var path = uri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v3/auth/tokens")
                return HandleAuth(body);

            if (serviceToken is not null)
            {
                lock (ServiceTokensSeen) { ServiceTokensSeen.Add(serviceToken); }
            }
            if (RejectAllServiceTokens)
                return Unauthorized();
            if (serviceToken is not null && RejectTokensOnce.Remove(serviceToken))
                return Unauthorized();

            if (path.StartsWith("/compute/", StringComparison.Ordinal))
                return HandleCompute(request.Method, path["/compute/".Length..], uri.Query, body);
            if (path.StartsWith("/network/", StringComparison.Ordinal))
                return HandleNetwork(request.Method, path["/network/".Length..], uri.Query, body);
            if (path.StartsWith("/image/", StringComparison.Ordinal))
                return HandleImage(request.Method, path["/image/".Length..], uri.Query, body);
            return NotFound();
        }

        public string? InterceptRequestId;

        private HttpResponseMessage HandleAuth(string body)
        {
            Interlocked.Increment(ref AuthCalls);
            lock (AuthBodies) { AuthBodies.Add(body); }
            if (RejectAuth)
            {
                return Json("""{"error":{"message":"The provided credential is invalid.","code":401,"title":"Unauthorized"}}""",
                    HttpStatusCode.Unauthorized);
            }
            var token = $"token-{AuthCalls}";
            var expiresAt = (Now() + TokenLifetime).ToString("O");
            var catalog =
                "{ \"token\": { \"expires_at\": \"" + expiresAt + "\", \"catalog\": [" +
                AuthCatalogEntry("compute", "http://localhost/compute/") + "," +
                AuthCatalogEntry("network", "http://localhost/network/") + "," +
                AuthCatalogEntry("image", "http://localhost/image/") +
                "] } }";
            var response = Json(catalog);
            response.Headers.Add("X-Subject-Token", token);
            return response;
        }

        private static string AuthCatalogEntry(string type, string url) =>
            "{ \"type\": \"" + type + "\", \"name\": \"" + type + "\", \"endpoints\": [" +
            "{ \"id\": \"e1\", \"interface\": \"public\", \"region\": \"test-region\", \"url\": \"" + url + "\" }," +
            "{ \"id\": \"e2\", \"interface\": \"internal\", \"region\": \"test-region\", \"url\": \"" + url + "\" }" +
            "] }";

        private HttpResponseMessage HandleCompute(HttpMethod method, string path, string query, string body)
        {
            if (method == HttpMethod.Post && path == "servers")
            {
                var id = $"srv-{Interlocked.Increment(ref _serverSeq)}";
                using var doc = JsonDocument.Parse(body);
                var payload = doc.RootElement.GetProperty("server");
                var fake = new FakeServer(
                    payload.GetProperty("name").GetString() ?? id,
                    NewServerStatus, NewServerFault, GetsUntilActive);
                if (payload.TryGetProperty("tags", out var tags))
                    fake.Tags = tags.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                if (payload.TryGetProperty("metadata", out var metadata))
                {
                    foreach (var prop in metadata.EnumerateObject())
                        fake.Metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
                _servers[id] = fake;
                return Json(
                    $"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{fake.Name}\",\"status\":\"{fake.Status}\"}}}}",
                    HttpStatusCode.Accepted);
            }
            if (method == HttpMethod.Get && path == "servers/detail")
            {
                if (HugeListResponse)
                    return Json("{\"servers\":[{\"id\":\"srv-huge\",\"name\":\"" + new string('x', 4096) + "\"}]}");
                var items = _servers.Select(kvp =>
                    $"{{\"id\":\"{kvp.Key}\",\"name\":\"{kvp.Value.Name}\",\"status\":\"{kvp.Value.Status}\",\"tags\":{JsonSerializer.Serialize(kvp.Value.Tags)},\"metadata\":{JsonSerializer.Serialize(kvp.Value.Metadata)}}}");
                return Json($"{{\"servers\":[{string.Join(",", items)}]}}");
            }
            if (method == HttpMethod.Get && path.StartsWith("servers/", StringComparison.Ordinal)
                && !path.EndsWith("/action", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..];
                if (!_servers.TryGetValue(id, out var fake))
                    return NotFound();
                fake.Gets++;
                if (fake.Gets > fake.GetsUntilActive && fake.Status != "ERROR")
                    fake.Status = "ACTIVE";
                var fault = fake.Fault is null ? "null"
                    : $"{{\"message\":\"{fake.Fault}\",\"code\":500}}";
                return Json($"{{\"server\":{{\"id\":\"{id}\",\"name\":\"{fake.Name}\",\"status\":\"{fake.Status}\",\"fault\":{fault}}}}}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("servers/", StringComparison.Ordinal))
            {
                var id = path["servers/".Length..];
                return _servers.TryRemove(id, out _) ? NoContent() : NotFound();
            }
            if (method == HttpMethod.Post && path.EndsWith("/action", StringComparison.Ordinal))
            {
                var id = path.Substring("servers/".Length, path.Length - "servers/".Length - "/action".Length);
                if (!_servers.ContainsKey(id))
                    return NotFound();
                var imageId = SnapshotViaLocation ? "img-9" : $"img-{Interlocked.Increment(ref _imageSeq)}";
                if (!SnapshotViaLocation)
                    _images[imageId] = new FakeImage("codeybox-snap-1", ["codeybox"]);
                else
                    _images[imageId] = new FakeImage("snap-loc", []);
                if (SnapshotViaLocation)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                    response.Headers.Location = new Uri($"http://localhost/image/v2/images/{imageId}");
                    return response;
                }
                return Json($"{{\"image_id\":\"{imageId}\"}}", HttpStatusCode.Accepted);
            }
            if (method == HttpMethod.Get && path == "flavors/detail")
            {
                var items = DuplicateFlavors
                    ? """{"id":"flavor-2a","name":"standard-2","vcpus":2,"ram":4096,"disk":40},{"id":"flavor-2b","name":"standard-2","vcpus":2,"ram":4096,"disk":40}"""
                    : """{"id":"flavor-2","name":"standard-2","vcpus":2,"ram":4096,"disk":40},{"id":"flavor-4","name":"standard-4","vcpus":4,"ram":8192,"disk":80}""";
                return Json($"{{\"flavors\":[{items}]}}");
            }
            if (method == HttpMethod.Post && path == "os-keypairs")
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("keypair").GetProperty("name").GetString()!;
                _keypairs[name] = "ssh-rsa FAKE";
                return Json($"{{\"keypair\":{{\"name\":\"{name}\",\"public_key\":\"ssh-rsa FAKE\",\"private_key\":\"-----BEGIN FAKE-----\"}}}}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("os-keypairs/", StringComparison.Ordinal))
            {
                var name = path["os-keypairs/".Length..];
                return _keypairs.TryRemove(name, out _) ? NoContent() : NotFound();
            }
            if (method == HttpMethod.Get && path == "limits")
            {
                return Json("""{"limits":{"absolute":{"maxTotalInstances":10,"totalInstancesUsed":3,"maxTotalCores":20,"totalCoresUsed":6,"maxTotalRAMSize":16000,"totalRAMUsed":6144}}}""");
            }
            return NotFound();
        }

        private HttpResponseMessage HandleNetwork(HttpMethod method, string path, string query, string body)
        {
            if (method == HttpMethod.Post && path == "v2.0/security-groups")
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("security_group").GetProperty("name").GetString()!;
                var id = $"sg-{_secGroups.Count + 1}";
                _secGroups[id] = name;
                return Json($"{{\"security_group\":{{\"id\":\"{id}\",\"name\":\"{name}\"}}}}", HttpStatusCode.Created);
            }
            if (method == HttpMethod.Delete && path.StartsWith("v2.0/security-groups/", StringComparison.Ordinal))
            {
                var id = path["v2.0/security-groups/".Length..];
                return _secGroups.TryRemove(id, out _) ? NoContent() : NotFound();
            }
            if (method == HttpMethod.Post && path == "v2.0/security-group-rules")
                return Json("""{"security_group_rule":{"id":"rule-1","security_group_id":"sg-1","direction":"ingress"}}""",
                    HttpStatusCode.Created);
            if (method == HttpMethod.Post && path == "v2.0/floatingips")
            {
                var id = $"fip-{_floatingIps.Count + 1}";
                _floatingIps[id] = "198.51.100.10";
                return Json($"{{\"floatingip\":{{\"id\":\"{id}\",\"floating_ip_address\":\"198.51.100.10\",\"port_id\":null}}}}",
                    HttpStatusCode.Created);
            }
            if (method == HttpMethod.Put && path.StartsWith("v2.0/floatingips/", StringComparison.Ordinal))
            {
                var id = path["v2.0/floatingips/".Length..];
                if (!_floatingIps.ContainsKey(id))
                    return NotFound();
                using var doc = JsonDocument.Parse(body);
                FloatingIpPorts[id] = doc.RootElement.GetProperty("floatingip").GetProperty("port_id").GetString()!;
                return Json($"{{\"floatingip\":{{\"id\":\"{id}\",\"floating_ip_address\":\"198.51.100.10\",\"port_id\":\"{FloatingIpPorts[id]}\"}}}}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("v2.0/floatingips/", StringComparison.Ordinal))
            {
                var id = path["v2.0/floatingips/".Length..];
                return _floatingIps.TryRemove(id, out _) ? NoContent() : NotFound();
            }
            if (method == HttpMethod.Get && path == "v2.0/ports")
            {
                var deviceId = ExtractQueryParam(query, "device_id");
                var items = deviceId == "srv-1"
                    ? """{"id":"port-1","device_id":"srv-1","fixed_ips":[{"subnet_id":"sub-1","ip_address":"10.0.0.5"}]}"""
                    : string.Empty;
                return Json($"{{\"ports\":[{items}]}}");
            }
            return NotFound();
        }

        private HttpResponseMessage HandleImage(HttpMethod method, string path, string query, string body)
        {
            if (method == HttpMethod.Get && path == "v2/images")
            {
                var name = ExtractQueryParam(query, "name");
                var items = _images
                    .Where(kvp => name is null || string.Equals(kvp.Value.Name, name, StringComparison.Ordinal))
                    .Select(kvp =>
                        $"{{\"id\":\"{kvp.Key}\",\"name\":\"{kvp.Value.Name}\",\"status\":\"active\",\"tags\":{JsonSerializer.Serialize(kvp.Value.Tags)},\"visibility\":\"private\"}}");
                return Json($"{{\"images\":[{string.Join(",", items)}]}}");
            }
            if (method == HttpMethod.Get && path.StartsWith("v2/images/", StringComparison.Ordinal))
            {
                var id = path["v2/images/".Length..];
                if (!_images.TryGetValue(id, out var image))
                    return NotFound();
                return Json($"{{\"id\":\"{id}\",\"name\":\"{image.Name}\",\"status\":\"active\",\"tags\":{JsonSerializer.Serialize(image.Tags)},\"visibility\":\"private\"}}");
            }
            if (method == HttpMethod.Delete && path.StartsWith("v2/images/", StringComparison.Ordinal))
            {
                var id = path["v2/images/".Length..];
                return _images.TryRemove(id, out _) ? NoContent() : NotFound();
            }
            return NotFound();
        }

        private HttpResponseMessage Unauthorized() =>
            Json("""{"error":{"message":"The request you have made requires authentication.","code":401,"title":"Unauthorized"}}""",
                HttpStatusCode.Unauthorized);

        private static HttpResponseMessage NotFound() =>
            Json("""{"itemNotFound":{"message":"The resource could not be found.","code":404}}""",
                HttpStatusCode.NotFound);

        private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string? ExtractQueryParam(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = eq >= 0 ? part[..eq] : part;
                if (string.Equals(Uri.UnescapeDataString(k), key, StringComparison.Ordinal))
                    return eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : string.Empty;
            }
            return null;
        }

        private sealed class FakeServer(string name, string status, string? fault, int getsUntilActive)
        {
            public string Name { get; } = name;
            public string Status = status;
            public string? Fault = fault;
            public int Gets;
            public int GetsUntilActive = getsUntilActive;
            public List<string> Tags { get; set; } = [];
            public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);
        }

        private sealed class FakeImage(string name, List<string> tags)
        {
            public string Name { get; } = name;
            public List<string> Tags { get; } = tags;
        }
    }

    private sealed record RequestRecord(string Method, string Path, string Query, string? Token, string Body);
}
