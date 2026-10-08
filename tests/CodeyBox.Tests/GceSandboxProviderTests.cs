using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.GceSandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the GCE sandbox provider. Every test drives the real
/// <see cref="GceSandboxProvider"/> / <see cref="GceApiClient"/> / handle wiring
/// against a fake Compute Engine REST surface and a fake SSH transport — no live
/// cloud account, no real credentials, no network. Synthetic keys and tokens only.
/// </summary>
public sealed class GceSandboxProviderTests
{
    private const string OwnerId = "test-owner";

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Acquire_Exec_Stage_Sync_Dispose_FullLifecycle()
    {
        using var harness = NewHarness();
        var hostDir = NewHostDir();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Mounts =
            [
                new SandboxMount { SandboxPath = "/work", HostPath = hostDir, ReadOnly = false },
                new SandboxMount
                {
                    SandboxPath = SandboxConventions.CredentialsDir,
                    Tmpfs = true,
                    SizeBytes = 8L * 1024 * 1024,
                },
            ],
        };

        var before = SandboxLiveCounter.Active;
        var sandbox = await harness.CreateAsync(spec, CancellationToken.None);
        try
        {
            Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);
            Assert.Equal(before + 1, SandboxLiveCounter.Active);

            var insert = harness.Cloud.Inserts.Single();
            var requestId = insert.GetProperty("requestId").GetString();
            Assert.True(Guid.TryParse(requestId, out var parsed) && parsed != Guid.Empty);
            var body = JsonDocument.Parse(insert.GetProperty("body").GetString()!).RootElement;
            Assert.Equal(
                "projects/test-project/global/images/test-image-v1",
                body.GetProperty("disks")[0].GetProperty("initializeParams").GetProperty("sourceImage").GetString());
            Assert.Equal(
                "projects/test-project/zones/europe-west1-b/machineTypes/e2-medium",
                body.GetProperty("machineType").GetString());
            Assert.Equal("true", body.GetProperty("labels").GetProperty("codeybox-managed").GetString());
            Assert.Equal(OwnerId, body.GetProperty("labels").GetProperty("codeybox-owner").GetString());
            Assert.True(body.GetProperty("serviceAccounts").GetArrayLength() == 0);
            var metadata = body.GetProperty("metadata").GetProperty("items").EnumerateArray()
                .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("value").GetString()!);
            Assert.Contains("ssh-keys", metadata.Keys);
            Assert.DoesNotContain("fake-token", metadata["startup-script"], StringComparison.Ordinal);
            Assert.DoesNotContain("fake-token", metadata["ssh-keys"], StringComparison.Ordinal);
            Assert.DoesNotContain("default", metadata["startup-script"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Bearer fake-token", harness.Cloud.LastAuthorization);

            var result = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["echo", "hello"] }, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Single(harness.TransportFactory.Created);

            var stagedIn = harness.Transport.StageInCalls;
            Assert.Contains(stagedIn, c => c.HostPath == hostDir && c.RemotePath == "/work");
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        Assert.Equal(before, SandboxLiveCounter.Active);
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
        Assert.False(Directory.Exists(harness.TransportSshDir(sandbox.Id)));
        var syncIndex = harness.Cloud.EventLog.FindIndex(e => e.StartsWith("stageout", StringComparison.Ordinal));
        var deleteIndex = harness.Cloud.EventLog.FindIndex(e => e.StartsWith("delete-instance", StringComparison.Ordinal));
        Assert.True(syncIndex >= 0 && deleteIndex > syncIndex);
    }

    [Fact]
    public void Registration_ExposesTruthfulCapabilities()
    {
        using var harness = NewHarness();
        Assert.Equal("gce", harness.Provider.Name);
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, harness.Provider.IsolationLevel);
        Assert.Equal([SandboxCapabilities.Teardown], harness.Provider.DeclaredCapabilities);
        Assert.True(harness.Provider.MightOwnSandbox("codeybox-abc", hostId: null));
        Assert.False(harness.Provider.MightOwnSandbox("other-abc", hostId: null));
    }

    [Fact]
    public async Task Disabled_RefusesAllOperations()
    {
        using var harness = NewHarness(configure: o => o with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.ListAllManagedAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.DisposeLeakedAsync("codeybox-abc", CancellationToken.None));
        Assert.Empty(harness.Cloud.Inserts);
    }

    [Theory]
    [InlineData("Project", "")]
    [InlineData("Zone", "")]
    [InlineData("MachineType", "")]
    [InlineData("Network", "")]
    [InlineData("Subnetwork", "")]
    public async Task ConfigValidation_RejectsMissingScope(string property, string value)
    {
        _ = value;
        using var harness = NewHarness(configure: o => property switch
        {
            "Project" => o with { Project = string.Empty },
            "Zone" => o with { Zone = string.Empty },
            "MachineType" => o with { MachineType = string.Empty },
            "Network" => o with { Network = string.Empty },
            "Subnetwork" => o with { Subnetwork = string.Empty },
            _ => o,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Theory]
    [InlineData("projects/test-project/global/images/family/ubuntu-2204-lts")]
    [InlineData("test-image")]
    public async Task ConfigValidation_RejectsUnpinnedImage(string image)
    {
        using var harness = NewHarness(configure: o => o with { ImageName = image });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.DoesNotContain("family provisioned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigValidation_RejectsGraphicalFlavor_NamedProfile_AndBadCidrs()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            harness.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty, Flavor = SandboxProfileFlavor.Graphical },
                CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = string.Empty,
                    Network = new SandboxNetworkPolicy { ProfileName = "restricted" },
                },
                CancellationToken.None));

        using var badCidr = NewHarness(configure: o => o with { OrchestratorSshCidrs = ["not-a-cidr"] });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            badCidr.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));

        using var noCidr = NewHarness(configure: o => o with { OrchestratorSshCidrs = [] });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            noCidr.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public void ConfigValidation_RejectsBadPrefixAndZone()
    {
        Assert.Throws<ArgumentException>(() =>
            GceSandboxProvider.RegionForZone("nodasheshere"));
        Assert.Throws<ArgumentException>(() => GceSandboxProvider.RegionForZone(""));
        Assert.Equal("europe-west1", GceSandboxProvider.RegionForZone("europe-west1-b"));
        Assert.False(GceNaming.IsValidInstanceName("Codeybox-ABC"));
        Assert.False(GceNaming.IsValidInstanceName("-leading"));
        Assert.True(GceNaming.IsValidInstanceName("codeybox-abc123"));
        Assert.Equal("test_owner", GceNaming.SanitizeLabelValue("Test_Owner!"));
    }

    // ------------------------------------------------------------------
    // Cloud failures keep their distinctions
    // ------------------------------------------------------------------

    [Fact]
    public async Task QuotaExhausted_DefersAndCleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.FailInsert(403, QuotaErrorBody());
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
        Assert.Empty(harness.Provider.ListUnreconciled());
    }

    [Fact]
    public async Task Unauthorized_FailsClosedWithoutResidue()
    {
        using var harness = NewHarness();
        harness.Cloud.FailInsert(401, """{"error":{"code":401,"message":"Invalid Credentials"}}""");
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("unauthorized", ex.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task RateLimited_Defers()
    {
        using var harness = NewHarness();
        harness.Cloud.FailInsert(429, """{"error":{"code":429,"message":"Rate exceeded"}}""");
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
    }

    [Fact]
    public async Task ServerError_RetriesWithSameRequestId()
    {
        using var harness = NewHarness();
        harness.Cloud.FailInsert(1, 500, """{"error":{"code":500,"message":"backendError"}}""");
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        Assert.Equal(2, harness.Cloud.Inserts.Count);
        Assert.Equal(
            harness.Cloud.Inserts[0].GetProperty("requestId").GetString(),
            harness.Cloud.Inserts[1].GetProperty("requestId").GetString());
        Assert.Single(harness.Cloud.EventLog, e => e.StartsWith("delete-instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCreate_AdoptsOwnedInstanceWithoutDuplicating()
    {
        using var harness = NewHarness();
        harness.Cloud.DropNextInsert = true;
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.Instances);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task AmbiguousCreate_RefusesForeignInstance()
    {
        using var harness = NewHarness();
        harness.Cloud.SeedForeignInstance("codeybox-foreign1");
        harness.Cloud.FailInsert(1, 409, """{"error":{"code":409,"message":"already exists"}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.True(harness.Cloud.Instances.ContainsKey("codeybox-foreign1"));
    }

    [Fact]
    public async Task Probe_OpErrorDirect()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextOperation("quotaExceeded", "Quota exceeded.");
        var client = new GceApiClient(
            harness.Http, "http://localhost/compute/v1", harness.Clock,
            new GceClientLimits { AllowUnsafeHttp = true });
        var op = await client.InsertInstanceAsync(
            "tok", "test-project", "europe-west1-b",
            new GceInstanceSpec(
                "codeybox-probe1", "mt", "img", "net", "subnet", 20, true,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                ["codeybox-probe1"], "ssh-keys", "userdata", null),
            Guid.NewGuid().ToString(), CancellationToken.None);
        var fetched = await client.GetZoneOperationAsync(
            "tok", "test-project", "europe-west1-b", op.Name, CancellationToken.None);
        var raw = await harness.Http.GetStringAsync(
            $"http://localhost/compute/v1/projects/test-project/zones/europe-west1-b/operations/{op.Name}",
            CancellationToken.None);
        Assert.Fail($"op={op.Name} error={fetched.ErrorCode} raw={raw}");
    }

    [Fact]
    public async Task ZoneOperationError_Quota_DefersAndCleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextOperation("quotaExceeded", "Quota 'CPUS' exceeded.");
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task ZoneOperationTimeout_ReconcilesByIdentity()
    {
        using var harness = NewHarness();
        harness.Cloud.StallNextOperation = true;
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.Instances);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminalStatus_NeverCountsAsReady()
    {
        using var harness = NewHarness();
        harness.Cloud.NextInstanceStatus = "TERMINATED";
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", ex.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task MissingExternalIp_RefusesSshAtUnconfirmedTarget()
    {
        using var harness = NewHarness();
        harness.Cloud.NextInstanceNatIp = null;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", ex.ErrorClass);
        Assert.Empty(harness.TransportFactory.Created);
    }

    [Fact]
    public async Task MalformedAndOversizedResponses_FailClosed()
    {
        using var harness = NewHarness();
        harness.Cloud.CorruptNextInstanceGet = true;
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));

        using var oversized = NewHarness(configure: o => o with { MaxResponseBytes = 64 * 1024 });
        oversized.Cloud.OversizeNextInstanceGet = true;
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            oversized.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task PaginationCap_FailsLoudlyInsteadOfTruncating()
    {
        using var harness = NewHarness(configure: o => o with { MaxListPages = 1 });
        harness.Cloud.PageSize = 1;
        harness.Cloud.SeedOwnedInstance("codeybox-a1", OwnerId);
        harness.Cloud.SeedOwnedInstance("codeybox-a2", OwnerId);
        await Assert.ThrowsAsync<GceApiException>(() =>
            harness.Provider.ListAllManagedAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // SSH / guest plane
    // ------------------------------------------------------------------

    [Fact]
    public async Task SshNeverReady_DefersAndCleansUp()
    {
        using var harness = NewHarness();
        harness.Transport.FailAllRuns = true;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("ssh-unready", ex.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task HostKeyMismatch_FailsClosed()
    {
        using var harness = NewHarness();
        harness.Transport.FailWithMismatch = true;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("ssh-unready", ex.ErrorClass);
        Assert.Single(harness.TransportFactory.Created);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public void StartupScript_PinsHostKeyAndMountsTmpfsWithoutSecrets()
    {
        var script = GceCloudInit.Build(new GceStartupScriptSpec(
            "codeybox-test", "ubuntu", "ssh-ed25519 AAAA",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nxyz\n",
            [new GceTmpfsMount("/run/codeybox/creds", 8L * 1024 * 1024)]));
        Assert.Contains("ssh_host_ed25519_key", script, StringComparison.Ordinal);
        Assert.Contains("HostKey /etc/ssh/codeybox/ssh_host_ed25519_key", script, StringComparison.Ordinal);
        Assert.Contains("mount -t tmpfs", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GCE_ACCESS_TOKEN", script, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => GceCloudInit.Build(new GceStartupScriptSpec(
            "x", "u", "k", "p", [new GceTmpfsMount("relative", 8L * 1024 * 1024)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => GceCloudInit.Build(new GceStartupScriptSpec(
            "x", "u", "k", "p", [new GceTmpfsMount("/ok", 1)])));
    }

    [Fact]
    public async Task InvalidTmpfsCredentials_RefusedBeforeAnyCloudCall()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            harness.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = string.Empty,
                    Mounts = [new SandboxMount { SandboxPath = "/run/codeybox/creds/token", HostPath = NewHostDir() }],
                },
                CancellationToken.None));
        Assert.Empty(harness.Cloud.Inserts);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task TransportLoss_SurfacesUnavailable_AndTeardownStillWorks()
    {
        using var harness = NewHarness();
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            harness.Transport.FailWithTransportLoss = true;
            await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
        }
        finally
        {
            harness.Transport.FailWithTransportLoss = false;
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task OutputLimitFlags_Preserved()
    {
        using var harness = NewHarness();
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            harness.Transport.NextResult = new ProcessRunResult(0, "out", "err", StdoutLimitExceeded: true);
            var result = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["true"], MaxStdoutBytes = 3 }, CancellationToken.None);
            Assert.True(result.StdoutLimitExceeded);
            Assert.True(result.OutputLimitExceeded);
            Assert.False(result.Success);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelledToken_CreatesNothing()
    {
        using var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
        Assert.Empty(harness.Cloud.Inserts);
    }

    [Fact]
    public async Task CancelDuringSshReady_CleansUpPartialProvisioning()
    {
        using var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        harness.Transport.CancelOnFirstRun = cts;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.Firewalls);
        Assert.Empty(harness.Provider.ListUnreconciled());
    }

    // ------------------------------------------------------------------
    // Teardown / orphans / ownership
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReservedAddress_LifecycleOwned()
    {
        using var harness = NewHarness(configure: o => o with { ReserveStaticAddress = true });
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            var address = Assert.Single(harness.Cloud.Addresses);
            Assert.Equal(OwnerId, address.Value.Labels!["codeybox-owner"]);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Addresses);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task BootDiskAutoDeleteFalse_DeletesOwnedDiskExplicitly()
    {
        using var harness = NewHarness(configure: o => o with { BootDiskAutoDelete = false });
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        string diskName;
        try
        {
            diskName = Assert.Single(harness.Cloud.Disks).Key;
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        _ = diskName;
        Assert.Empty(harness.Cloud.Disks);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task CleanupFailure_RetainsIdentity_UntilReconciled()
    {
        using var harness = NewHarness();
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var name = sandbox.Id;
        harness.Cloud.LeaveDeletedInstance = true;
        await sandbox.DisposeAsync();
        Assert.Contains(name, harness.Provider.ListUnreconciled());

        harness.Cloud.LeaveDeletedInstance = false;
        await harness.Provider.DisposeLeakedAsync(name, CancellationToken.None);
        Assert.Empty(harness.Provider.ListUnreconciled());
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task DisposeLeaked_RefusesForeignResources()
    {
        using var harness = NewHarness();
        harness.Cloud.SeedForeignInstance("codeybox-victim");
        harness.Cloud.SeedForeignFirewall("codeybox-victim-fw", "projects/test-project/global/networks/test-net", "codeybox-victim");
        await harness.Provider.DisposeLeakedAsync("codeybox-victim", CancellationToken.None);
        Assert.True(harness.Cloud.Instances.ContainsKey("codeybox-victim"));
        Assert.True(harness.Cloud.Firewalls.ContainsKey("codeybox-victim-fw"));
    }

    [Fact]
    public async Task RestartAdoption_OrphanSweep_RemovesOwned_KeepsForeign()
    {
        using var harness = NewHarness();
        harness.Cloud.SeedOwnedInstance("codeybox-orphan1", OwnerId, firewall: "codeybox-orphan1-fw", address: "codeybox-orphan1-ip");
        harness.Cloud.SeedOwnedFirewall("codeybox-orphan1-fw", "projects/test-project/global/networks/test-net", "codeybox-orphan1");
        harness.Cloud.SeedOwnedFirewall("codeybox-stray-fw", "projects/test-project/global/networks/test-net", "codeybox-gone");
        harness.Cloud.SeedForeignFirewall("codeybox-alien-fw", "projects/other/global/networks/other", "codeybox-alien");
        harness.Cloud.SeedOwnedAddress("codeybox-orphan1-ip", OwnerId);
        harness.Cloud.SeedOwnedAddress("codeybox-stray-ip", OwnerId);
        harness.Cloud.SeedForeignInstance("codeybox-alien");

        var inventory = await harness.Provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(inventory, i => i.Name == "codeybox-orphan1");
        Assert.DoesNotContain(inventory, i => i.Name == "codeybox-alien");

        await harness.Provider.DisposeLeakedAsync("codeybox-orphan1", CancellationToken.None);
        Assert.False(harness.Cloud.Instances.ContainsKey("codeybox-orphan1"));
        Assert.False(harness.Cloud.Firewalls.ContainsKey("codeybox-orphan1-fw"));
        Assert.False(harness.Cloud.Firewalls.ContainsKey("codeybox-stray-fw"));
        Assert.True(harness.Cloud.Firewalls.ContainsKey("codeybox-alien-fw"));
        Assert.False(harness.Cloud.Addresses.ContainsKey("codeybox-orphan1-ip"));
        Assert.False(harness.Cloud.Addresses.ContainsKey("codeybox-stray-ip"));
        Assert.True(harness.Cloud.Instances.ContainsKey("codeybox-alien"));
    }

    [Fact]
    public async Task RepeatedDisposal_IsIdempotent()
    {
        using var harness = NewHarness();
        var before = SandboxLiveCounter.Active;
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        await sandbox.DisposeAsync();
        await harness.Provider.DisposeLeakedAsync(sandbox.Id, CancellationToken.None);
        Assert.Equal(before, SandboxLiveCounter.Active);
        Assert.Empty(harness.Provider.ListUnreconciled());
    }

    [Fact]
    public async Task ActiveSnapshot_TracksLiveSandboxes()
    {
        using var harness = NewHarness();
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty, TimingWorkItemId = new WorkItemId(Guid.NewGuid()) },
            CancellationToken.None);
        try
        {
            var snapshot = harness.Provider.SnapshotActiveSandboxes();
            Assert.Contains(snapshot, e => e.Sandbox.Id == sandbox.Id);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Provider.SnapshotActiveSandboxes());
    }

    [Fact]
    public async Task ExecAfterDispose_Throws()
    {
        using var harness = NewHarness();
        var sandbox = await harness.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        public FakeGceCloud Cloud { get; } = new();
        public FakeTransportFactory TransportFactory { get; } = new();
        public FakeTransport Transport => TransportFactory.Created.Last();
        public ControllableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public GceSandboxOptions Options { get; private set; } = null!;
        public GceSandboxProvider Provider { get; private set; } = null!;
        public HttpClient Http => _http;
        private readonly HttpClient _http;

        public Harness(Func<GceSandboxOptions, GceSandboxOptions>? configure)
        {
            Cloud.EventLog.Add("harness-start");
            _http = new HttpClient(Cloud) { Timeout = Timeout.InfiniteTimeSpan };
            TransportFactory.EventLog = Cloud.EventLog;
            var options = new GceSandboxOptions
            {
                Enabled = true,
                ComputeBaseUrl = "http://localhost/compute/v1",
                AllowUnsafeHttp = true,
                Project = "test-project",
                Zone = "europe-west1-b",
                MachineType = "e2-medium",
                ImageName = "projects/test-project/global/images/test-image-v1",
                Network = "projects/test-project/global/networks/test-net",
                Subnetwork = "projects/test-project/regions/europe-west1/subnetworks/test-subnet",
                OwnerId = OwnerId,
                OrchestratorSshCidrs = ["203.0.113.0/24"],
                PollIntervalMilliseconds = 200,
                MaxPollIntervalMilliseconds = 1000,
                ReadyTimeoutSeconds = 30,
                SshReadyTimeoutSeconds = 30,
                SshUser = "tester",
            };
            Options = configure is null ? options : configure(options);
            Provider = new GceSandboxProvider(
                () => Options,
                _http,
                new FakeKeyGenerator(),
                new FakeDns(),
                TransportFactory,
                new FakeCredentialSource(),
                name => name == "GCE_ACCESS_TOKEN" ? "unused" : null,
                Clock,
                NullLogger.Instance);
        }

        public string TransportSshDir(string instanceName)
        {
            var suffix = instanceName["codeybox-".Length..];
            return Path.Combine(Path.GetTempPath(), "codeybox-gce-" + suffix);
        }

        /// <summary>
        /// Runs a provider call while advancing the fake clock past any bounded
        /// wait (the fake clock only fires timers on <c>Advance</c>). Happy paths
        /// register no timers and complete without advancing; timeout paths are
        /// driven purely by virtual time with a wall-clock backstop.
        /// </summary>
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            RunWithClockAsync(token => Provider.CreateAsync(spec, token), ct);

        public async Task<T> RunWithClockAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken ct)
        {
            var task = run(ct);
            var backstop = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted)
            {
                Clock.Advance(TimeSpan.FromSeconds(30));
                await Task.Yield();
                if (task.IsCompleted)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(2), CancellationToken.None);
                if (DateTime.UtcNow > backstop)
                    break;
            }
            return await task;
        }

        public void Dispose()
        {
            Provider.Dispose();
            _http.Dispose();
        }
    }

    private static Harness NewHarness(Func<GceSandboxOptions, GceSandboxOptions>? configure = null) =>
        new(configure);

    private static string NewHostDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codeybox-gce-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "hello");
        return dir;
    }

    private static string QuotaErrorBody() =>
        """{"error":{"code":403,"message":"Quota 'CPUS' exceeded. Limit: 24.0 in region europe-west1.","errors":[{"reason":"quotaExceeded","message":"Quota 'CPUS' exceeded."}]}}""";

    private sealed class FakeCredentialSource : IGceCredentialSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            _ = ct;
            return Task.FromResult("fake-token");
        }
    }

    private sealed class FakeKeyGenerator : IGceKeyGenerator
    {
        public Task<GceClientKeyMaterial> GenerateClientKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = comment; _ = ct;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "id-ed25519-test");
            File.WriteAllText(path, "fake-private");
            return Task.FromResult(new GceClientKeyMaterial(path, "ssh-ed25519 " + new string('A', 64)));
        }

        public Task<GceHostKeyMaterial> GenerateHostKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = directory; _ = comment; _ = ct;
            return Task.FromResult(new GceHostKeyMaterial(
                "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n",
                "ssh-ed25519 " + new string('B', 64)));
        }
    }

    private sealed class FakeDns : IGceDnsResolver
    {
        public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            _ = host; _ = ct;
            return Task.FromResult(Array.Empty<System.Net.IPAddress>());
        }
    }

    private sealed class FakeTransportFactory : IGceTransportFactory
    {
        public List<FakeTransport> Created { get; } = [];
        public List<string> EventLog { get; set; } = [];

        public IRemoteHostTransport Create(GceSshTransportSpec spec)
        {
            var transport = new FakeTransport { EventLog = EventLog, SshTarget = spec.SshTarget };
            Created.Add(transport);
            return transport;
        }
    }

    private sealed class FakeTransport : IRemoteHostTransport
    {
        public string DiagnosticId => "fake";
        public List<string> EventLog { get; set; } = [];
        public string SshTarget { get; set; } = string.Empty;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<(string HostPath, string RemotePath)> StageInCalls { get; } = [];
        public List<(string RemotePath, string HostPath)> StageOutCalls { get; } = [];
        public bool FailAllRuns { get; set; }
        public bool FailWithMismatch { get; set; }
        public bool FailWithTransportLoss { get; set; }
        public CancellationTokenSource? CancelOnFirstRun { get; set; }
        public ProcessRunResult? NextResult { get; set; }

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
            _ = stdin; _ = stdoutChunkCallback; _ = stderrChunkCallback;
            _ = maxStdoutBytes; _ = maxStderrBytes; _ = killOnOutputLimit;
            Calls.Add(argv.ToArray());
            if (CancelOnFirstRun is not null)
            {
                CancelOnFirstRun.Cancel();
                CancelOnFirstRun = null;
                ct.ThrowIfCancellationRequested();
            }
            ct.ThrowIfCancellationRequested();
            if (FailWithTransportLoss)
                throw new RemoteSshTransportException("simulated transport loss");
            if (FailWithMismatch)
                throw new RemoteSshTransportException("host key mismatch: expected ssh-ed25519 BBBB, got CCCC");
            if (FailAllRuns)
                throw new RemoteSshTransportException("connection refused");
            if (NextResult is { } next)
            {
                NextResult = null;
                return Task.FromResult(next);
            }
            return Task.FromResult(new ProcessRunResult(0, "ok", string.Empty));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
        {
            _ = ct;
            StageInCalls.Add((hostPath, remotePath));
            lock (EventLog) EventLog.Add($"stagein {hostPath}->{remotePath}");
            return Task.CompletedTask;
        }

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            _ = ct;
            StageOutCalls.Add((remotePath, hostPath));
            lock (EventLog) EventLog.Add($"stageout {remotePath}->{hostPath}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInstance
    {
        public required string Name { get; set; }
        public string Status { get; set; } = "PROVISIONING";
        public int GetsUntilRunning { get; set; } = 1;
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
        public List<string> Tags { get; set; } = [];
        public string? NatIp { get; set; } = "203.0.113.10";
        public string NetworkIp { get; set; } = "10.0.0.2";
        public bool Deleted { get; set; }
    }

    private sealed class FakeOp
    {
        public int RemainingPolls { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Stall { get; set; }
    }

    private sealed class FakeGceCloud : HttpMessageHandler
    {
        public ConcurrentDictionary<string, FakeInstance> Instances { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeOp> ZoneOps { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeOp> RegionOps { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeOp> GlobalOps { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, (Dictionary<string, string> Labels, string Ip)> Addresses { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, (string Network, List<string> TargetTags, List<string> SourceRanges)> Firewalls { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, Dictionary<string, string>> Disks { get; } = new(StringComparer.Ordinal);
        public List<JsonElement> Inserts { get; } = [];
        public List<string> EventLog { get; } = [];
        public string? LastAuthorization { get; private set; }

        public bool DropNextInsert { get; set; }
        public int FailInsertTimes { get; set; }
        public int FailInsertStatus { get; set; }
        public string FailInsertBody { get; set; } = "{}";
        public bool FailNextOp { get; set; }
        private string? _failOpCode;
        private string? _failOpMessage;
        public bool StallNextOperation { get; set; }
        public string NextInstanceStatus { get; set; } = "RUNNING";
        public string? NextInstanceNatIp { get; set; } = "keep-default";
        public bool CorruptNextInstanceGet { get; set; }
        public bool OversizeNextInstanceGet { get; set; }
        public bool LeaveDeletedInstance { get; set; }
        public int PageSize { get; set; }

        public void FailInsert(int times, int status, string body)
        {
            FailInsertTimes = times;
            FailInsertStatus = status;
            FailInsertBody = body;
        }

        public void FailInsert(int status, string body) => FailInsert(1, status, body);

        public void FailNextOperation(string code, string message)
        {
            FailNextOp = true;
            _failOpCode = code;
            _failOpMessage = message;
        }

        public void SeedForeignInstance(string name)
        {
            Instances[name] = new FakeInstance
            {
                Name = name,
                Status = "RUNNING",
                GetsUntilRunning = 0,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox-managed"] = "true",
                    ["codeybox-owner"] = "someone-else",
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        public void SeedOwnedInstance(string name, string owner, string? firewall = null, string? address = null)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["codeybox-managed"] = "true",
                ["codeybox-owner"] = owner,
                ["codeybox-created"] = "2026-01-01T00:00:00Z",
            };
            if (firewall is not null)
                metadata["codeybox-firewall"] = firewall;
            if (address is not null)
                metadata["codeybox-address"] = address;
            Instances[name] = new FakeInstance
            {
                Name = name,
                Status = "RUNNING",
                GetsUntilRunning = 0,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox-managed"] = "true",
                    ["codeybox-owner"] = owner,
                },
                Metadata = metadata,
            };
        }

        public void SeedOwnedFirewall(string name, string network, string targetTag) =>
            Firewalls[name] = (network, [targetTag], ["203.0.113.0/24"]);

        public void SeedForeignFirewall(string name, string network, string targetTag) =>
            Firewalls[name] = (network, [targetTag], ["198.51.100.0/24"]);

        public void SeedOwnedAddress(string name, string owner) =>
            Addresses[name] = (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["codeybox-managed"] = "true",
                ["codeybox-owner"] = owner,
            }, "203.0.113.11");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            const string prefix = "/compute/v1/";
            Assert.StartsWith(prefix, path, StringComparison.Ordinal);
            var rest = path[prefix.Length..];
            var query = ParseQuery(request.RequestUri.Query);

            if (rest.Contains("/operations/", StringComparison.Ordinal))
            {
                if (rest.Contains("/zones/", StringComparison.Ordinal))
                    return HandleZoneOp(rest);
                if (rest.Contains("/regions/", StringComparison.Ordinal))
                    return HandleRegionOp(rest);
                return HandleGlobalOp(rest);
            }
            if (request.Method == HttpMethod.Post && rest.Contains("/instances", StringComparison.Ordinal) && !rest.Contains("/operations", StringComparison.Ordinal))
                return await HandleInsertInstanceAsync(request, rest, query, ct);
            if (request.Method == HttpMethod.Get && TryMatch(rest, "/instances/", out var instanceName) && !rest.Contains("/operations", StringComparison.Ordinal))
                return HandleGetInstance(instanceName);
            if (request.Method == HttpMethod.Delete && TryMatch(rest, "/instances/", out var deleteName))
                return HandleDeleteInstance(deleteName, query);
            if (request.Method == HttpMethod.Get && rest.EndsWith("/instances", StringComparison.Ordinal))
                return HandleListInstances(query);
            if (request.Method == HttpMethod.Post && rest.EndsWith("/addresses", StringComparison.Ordinal))
                return await HandleInsertAddressAsync(request, query, ct);
            if (request.Method == HttpMethod.Get && TryMatch(rest, "/addresses/", out var addressName))
                return HandleGetAddress(addressName);
            if (request.Method == HttpMethod.Delete && TryMatch(rest, "/addresses/", out var deleteAddress))
                return HandleDeleteAddress(deleteAddress);
            if (request.Method == HttpMethod.Get && rest.EndsWith("/addresses", StringComparison.Ordinal))
                return HandleListAddresses(query);
            if (request.Method == HttpMethod.Post && rest.EndsWith("/firewalls", StringComparison.Ordinal))
                return await HandleInsertFirewallAsync(request, query, ct);
            if (request.Method == HttpMethod.Get && TryMatch(rest, "/firewalls/", out var firewallName))
                return HandleGetFirewall(firewallName);
            if (request.Method == HttpMethod.Delete && TryMatch(rest, "/firewalls/", out var deleteFirewall))
                return HandleDeleteFirewall(deleteFirewall);
            if (request.Method == HttpMethod.Get && rest.EndsWith("/firewalls", StringComparison.Ordinal))
                return HandleListFirewalls(query);
            if (request.Method == HttpMethod.Get && TryMatch(rest, "/disks/", out var diskName))
                return HandleGetDisk(diskName);
            if (request.Method == HttpMethod.Delete && TryMatch(rest, "/disks/", out var deleteDisk))
                return HandleDeleteDisk(deleteDisk, query);
            if (request.Method == HttpMethod.Get && rest.EndsWith("/disks", StringComparison.Ordinal))
                return HandleListDisks(query);
            return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
        }

        private async Task<HttpResponseMessage> HandleInsertInstanceAsync(
            HttpRequestMessage request, string rest, Dictionary<string, string> query, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            query.TryGetValue("requestId", out var requestId);
            lock (Inserts)
            {
                var wrapper = JsonDocument.Parse(
                    $"{{\"requestId\":{JsonSerializer.Serialize(requestId)},\"body\":{JsonSerializer.Serialize(body)}}}").RootElement.Clone();
                Inserts.Add(wrapper);
            }
            if (FailInsertTimes > 0)
            {
                FailInsertTimes--;
                return JsonResponse((HttpStatusCode)FailInsertStatus, FailInsertBody);
            }
            var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
            if (Instances.TryGetValue(name, out var existing) && !existing.Deleted)
                return JsonResponse(HttpStatusCode.Conflict, """{"error":{"code":409,"message":"already exists"}}""");
            if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"invalid requestId"}}""");
            var instanceBody = JsonDocument.Parse(body).RootElement;
            var labels = instanceBody.GetProperty("labels").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            var metadata = instanceBody.GetProperty("metadata").GetProperty("items").EnumerateArray()
                .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("value").GetString()!, StringComparer.Ordinal);
            var natIp = NextInstanceNatIp == "keep-default" ? "203.0.113.10" : NextInstanceNatIp;
            var instance = new FakeInstance
            {
                Name = name,
                Status = "PROVISIONING",
                GetsUntilRunning = 1,
                Labels = labels,
                Metadata = metadata,
                Tags = instanceBody.GetProperty("tags").GetProperty("items").EnumerateArray()
                    .Select(e => e.GetString()!).ToList(),
                NatIp = natIp,
            };
            Instances[name] = instance;
            Disks[name] = new Dictionary<string, string>(labels, StringComparer.Ordinal);
            lock (EventLog) EventLog.Add($"insert-instance {name} requestId={requestId}");
            if (DropNextInsert)
            {
                DropNextInsert = false;
                throw new HttpRequestException("simulated connection reset (server processed the insert)");
            }
            var opName = "operation-insert-" + name;
            ZoneOps[opName] = MakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private FakeOp MakeOp()
        {
            if (StallNextOperation)
            {
                StallNextOperation = false;
                return new FakeOp { Stall = true };
            }
            if (FailNextOp)
            {
                FailNextOp = false;
                return new FakeOp { ErrorCode = _failOpCode, ErrorMessage = _failOpMessage };
            }
            return new FakeOp();
        }

        private HttpResponseMessage HandleGetInstance(string name)
        {
            if (CorruptNextInstanceGet)
            {
                CorruptNextInstanceGet = false;
                return JsonResponse(HttpStatusCode.OK, """{"name":""");
            }
            if (OversizeNextInstanceGet)
            {
                OversizeNextInstanceGet = false;
                return JsonResponse(HttpStatusCode.OK, "{\"name\":\"" + new string('x', 100_000) + "\"}");
            }
            if (!Instances.TryGetValue(name, out var instance))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            if (instance.GetsUntilRunning > 0)
            {
                instance.GetsUntilRunning--;
                if (instance.GetsUntilRunning == 0 && instance.Status == "PROVISIONING")
                    instance.Status = NextInstanceStatus;
            }
            return JsonResponse(HttpStatusCode.OK, SerializeInstance(instance));
        }

        private HttpResponseMessage HandleDeleteInstance(string name, Dictionary<string, string> query)
        {
            query.TryGetValue("requestId", out var requestId);
            if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"invalid requestId"}}""");
            if (!Instances.TryGetValue(name, out var instance))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            lock (EventLog) EventLog.Add($"delete-instance {name}");
            var opName = "operation-delete-" + name + "-" + Guid.NewGuid().ToString("N");
            ZoneOps[opName] = new FakeOp();
            if (LeaveDeletedInstance)
                instance.Deleted = true;
            else
                Instances.TryRemove(name, out _);
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleListInstances(Dictionary<string, string> query)
        {
            var items = Instances.Values.Where(i => !i.Deleted).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            query.TryGetValue("pageToken", out var pageToken);
            var start = 0;
            if (pageToken is not null)
                start = int.Parse(pageToken, CultureInfo.InvariantCulture);
            var page = PageSize > 0 ? PageSize : items.Count;
            var slice = items.Skip(start).Take(page).ToList();
            var next = start + slice.Count < items.Count ? (start + slice.Count).ToString(CultureInfo.InvariantCulture) : null;
            var sb = new StringBuilder("{\"items\":[");
            sb.Append(string.Join(",", slice.Select(SerializeInstance)));
            sb.Append(']');
            if (next is not null)
                sb.Append(",\"nextPageToken\":").Append(JsonSerializer.Serialize(next));
            sb.Append('}');
            return JsonResponse(HttpStatusCode.OK, sb.ToString());
        }

        private HttpResponseMessage HandleZoneOp(string rest)
        {
            var name = rest[(rest.LastIndexOf('/') + 1)..];
            if (!ZoneOps.TryGetValue(name, out var op))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"no such operation"}}""");
            return SerializeOp(name, op);
        }

        private HttpResponseMessage HandleRegionOp(string rest)
        {
            var name = rest[(rest.LastIndexOf('/') + 1)..];
            if (!RegionOps.TryGetValue(name, out var op))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"no such operation"}}""");
            return SerializeOp(name, op);
        }

        private HttpResponseMessage HandleGlobalOp(string rest)
        {
            var name = rest[(rest.LastIndexOf('/') + 1)..];
            if (!GlobalOps.TryGetValue(name, out var op))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"no such operation"}}""");
            return SerializeOp(name, op);
        }

        private static HttpResponseMessage SerializeOp(string name, FakeOp op)
        {
            if (op.Stall)
                return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(name)},\"status\":\"RUNNING\"}}");
            if (op.RemainingPolls > 0)
            {
                op.RemainingPolls--;
                return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(name)},\"status\":\"RUNNING\"}}");
            }
            if (op.ErrorCode is not null || op.ErrorMessage is not null)
            {
                return JsonResponse(HttpStatusCode.OK,
                    $"{{\"name\":{JsonSerializer.Serialize(name)},\"status\":\"DONE\"," +
                    $"\"error\":{{\"errors\":[{{\"code\":{JsonSerializer.Serialize(op.ErrorCode)},\"message\":{JsonSerializer.Serialize(op.ErrorMessage)}}}]}}}}");
            }
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(name)},\"status\":\"DONE\"}}");
        }

        private async Task<HttpResponseMessage> HandleInsertAddressAsync(
            HttpRequestMessage request, Dictionary<string, string> query, CancellationToken ct)
        {
            query.TryGetValue("requestId", out var requestId);
            if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"invalid requestId"}}""");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var name = body.RootElement.GetProperty("name").GetString()!;
            if (Addresses.ContainsKey(name))
                return JsonResponse(HttpStatusCode.Conflict, """{"error":{"code":409,"message":"already exists"}}""");
            var labels = body.RootElement.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object
                ? labelsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            Addresses[name] = (labels, "203.0.113.11");
            lock (EventLog) EventLog.Add($"insert-address {name}");
            var opName = "operation-insert-address-" + name;
            RegionOps[opName] = new FakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleGetAddress(string name)
        {
            if (!Addresses.TryGetValue(name, out var address))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            return JsonResponse(HttpStatusCode.OK,
                $"{{\"name\":{JsonSerializer.Serialize(name)}," +
                $"\"address\":{JsonSerializer.Serialize(address.Ip)}," +
                $"\"status\":\"RESERVED\"," +
                $"\"labels\":{JsonSerializer.Serialize(address.Labels)}}}");
        }

        private HttpResponseMessage HandleDeleteAddress(string name)
        {
            if (!Addresses.ContainsKey(name))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            Addresses.TryRemove(name, out _);
            lock (EventLog) EventLog.Add($"delete-address {name}");
            var opName = "operation-delete-address-" + name;
            RegionOps[opName] = new FakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleListAddresses(Dictionary<string, string> query)
        {
            _ = query;
            var items = Addresses.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                $"{{\"name\":{JsonSerializer.Serialize(kv.Key)}," +
                $"\"address\":{JsonSerializer.Serialize(kv.Value.Ip)}," +
                $"\"labels\":{JsonSerializer.Serialize(kv.Value.Labels)}}}");
            return JsonResponse(HttpStatusCode.OK, "{\"items\":[" + string.Join(",", items) + "]}");
        }

        private async Task<HttpResponseMessage> HandleInsertFirewallAsync(
            HttpRequestMessage request, Dictionary<string, string> query, CancellationToken ct)
        {
            query.TryGetValue("requestId", out var requestId);
            if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"invalid requestId"}}""");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var name = body.RootElement.GetProperty("name").GetString()!;
            if (Firewalls.ContainsKey(name))
                return JsonResponse(HttpStatusCode.Conflict, """{"error":{"code":409,"message":"already exists"}}""");
            var network = body.RootElement.GetProperty("network").GetString()!;
            var targetTags = body.RootElement.GetProperty("targetTags").EnumerateArray()
                .Select(e => e.GetString()!).ToList();
            var sourceRanges = body.RootElement.GetProperty("sourceRanges").EnumerateArray()
                .Select(e => e.GetString()!).ToList();
            Firewalls[name] = (network, targetTags, sourceRanges);
            lock (EventLog) EventLog.Add($"insert-firewall {name}");
            var opName = "operation-insert-firewall-" + name;
            GlobalOps[opName] = new FakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleGetFirewall(string name)
        {
            if (!Firewalls.TryGetValue(name, out var firewall))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            return JsonResponse(HttpStatusCode.OK,
                $"{{\"name\":{JsonSerializer.Serialize(name)}," +
                $"\"network\":{JsonSerializer.Serialize(firewall.Network)}," +
                $"\"targetTags\":{JsonSerializer.Serialize(firewall.TargetTags)}," +
                $"\"sourceRanges\":{JsonSerializer.Serialize(firewall.SourceRanges)}}}");
        }

        private HttpResponseMessage HandleDeleteFirewall(string name)
        {
            if (!Firewalls.ContainsKey(name))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            Firewalls.TryRemove(name, out _);
            lock (EventLog) EventLog.Add($"delete-firewall {name}");
            var opName = "operation-delete-firewall-" + name;
            GlobalOps[opName] = new FakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleListFirewalls(Dictionary<string, string> query)
        {
            _ = query;
            var items = Firewalls.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                $"{{\"name\":{JsonSerializer.Serialize(kv.Key)}," +
                $"\"network\":{JsonSerializer.Serialize(kv.Value.Network)}," +
                $"\"targetTags\":{JsonSerializer.Serialize(kv.Value.TargetTags)}}}");
            return JsonResponse(HttpStatusCode.OK, "{\"items\":[" + string.Join(",", items) + "]}");
        }

        private HttpResponseMessage HandleGetDisk(string name)
        {
            if (!Disks.TryGetValue(name, out var labels))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            return JsonResponse(HttpStatusCode.OK,
                $"{{\"name\":{JsonSerializer.Serialize(name)}," +
                $"\"labels\":{JsonSerializer.Serialize(labels)}}}");
        }

        private HttpResponseMessage HandleDeleteDisk(string name, Dictionary<string, string> query)
        {
            query.TryGetValue("requestId", out var requestId);
            if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
                return JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"invalid requestId"}}""");
            if (!Disks.ContainsKey(name))
                return JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""");
            Disks.TryRemove(name, out _);
            lock (EventLog) EventLog.Add($"delete-disk {name}");
            var opName = "operation-delete-disk-" + name;
            ZoneOps[opName] = new FakeOp();
            return JsonResponse(HttpStatusCode.OK, $"{{\"name\":{JsonSerializer.Serialize(opName)},\"status\":\"DONE\"}}");
        }

        private HttpResponseMessage HandleListDisks(Dictionary<string, string> query)
        {
            _ = query;
            var items = Disks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                $"{{\"name\":{JsonSerializer.Serialize(kv.Key)}," +
                $"\"labels\":{JsonSerializer.Serialize(kv.Value)}}}");
            return JsonResponse(HttpStatusCode.OK, "{\"items\":[" + string.Join(",", items) + "]}");
        }

        private static string SerializeInstance(FakeInstance instance)
        {
            var metadata = string.Join(",", instance.Metadata.Select(kv =>
                $"{{\"key\":{JsonSerializer.Serialize(kv.Key)},\"value\":{JsonSerializer.Serialize(kv.Value)}}}"));
            var tags = string.Join(",", instance.Tags.Select(t => JsonSerializer.Serialize(t)));
            var natIp = instance.NatIp is null ? "null" : JsonSerializer.Serialize(instance.NatIp);
            return $"{{\"name\":{JsonSerializer.Serialize(instance.Name)}," +
                $"\"status\":{JsonSerializer.Serialize(instance.Status)}," +
                $"\"selfLink\":\"https://compute.googleapis.com/compute/v1/projects/test-project/zones/europe-west1-b/instances/{instance.Name}\"," +
                $"\"labels\":{JsonSerializer.Serialize(instance.Labels)}," +
                $"\"tags\":{{\"items\":[{tags}]}}," +
                $"\"metadata\":{{\"items\":[{metadata}]}}," +
                $"\"networkInterfaces\":[{{\"networkIP\":{JsonSerializer.Serialize(instance.NetworkIp)}," +
                $"\"accessConfigs\":[{{\"natIP\":{natIp}}}]}}]," +
                $"\"disks\":[{{\"source\":\"https://compute.googleapis.com/compute/v1/projects/test-project/zones/europe-west1-b/disks/{instance.Name}\"," +
                $"\"autoDelete\":true,\"deviceName\":{JsonSerializer.Serialize(instance.Name)}}}]}}";
        }

        private static bool TryMatch(string rest, string marker, out string name)
        {
            var index = rest.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                name = string.Empty;
                return false;
            }
            name = rest[(index + marker.Length)..];
            return !name.Contains('/', StringComparison.Ordinal);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query))
                return result;
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals < 0)
                    continue;
                result[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
            return result;
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
