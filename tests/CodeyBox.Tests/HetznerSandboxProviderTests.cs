using System.Globalization;
using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HetznerSandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;

namespace CodeyBox.Tests;

/// <summary>
/// Provider-level tests for the <c>hetzner</c> sandbox provider: full
/// acquire → exec → stage → dispose against a fake Hetzner Cloud plus a fake
/// SSH transport, disabled/config validation, explicit image and scope,
/// host-key pinning, cancellation at every provisioning boundary, remote
/// transport loss, output limits, partial and ambiguous creates, eventual
/// consistency, 401/403/429/quota/5xx, malformed/truncated/oversized
/// responses, restart adoption/orphan cleanup, repeated disposal, sync-back
/// before teardown, retained cleanup failure, and ownership collisions.
/// </summary>
public sealed class HetznerSandboxProviderTests
{
    internal const string ServerPrefix = "codeybox-";

    private static HetznerHarness NewHarness(Func<HetznerSandboxOptions, HetznerSandboxOptions>? configure = null) =>
        new(configure);

    // ------------------------------------------------------------------
    // Full lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Acquire_Exec_Stage_Dispose_FullLifecycle()
    {
        using var harness = NewHarness();
        var liveBefore = SandboxLiveCounter.Active;

        var hostDir = Directory.CreateTempSubdirectory("hetzner-prov-host-").FullName;
        try
        {
            var sourceFile = Path.Combine(hostDir, "payload.txt");
            await File.WriteAllTextAsync(sourceFile, "hello");
            var credFile = Path.Combine(hostDir, "agent.json");
            await File.WriteAllTextAsync(credFile, """{"token":"secret"}""");

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
                    new SandboxMount
                    {
                        SandboxPath = SandboxConventions.CredentialsDir + "/agent.json",
                        HostPath = credFile,
                        Tmpfs = true,
                        ReadOnly = true,
                    },
                ],
                Network = new SandboxNetworkPolicy { AllowedHosts = ["example.com"] },
            };

            var sandbox = await harness.Provider.CreateAsync(spec, CancellationToken.None);
            try
            {
                Assert.StartsWith(ServerPrefix, sandbox.Id, StringComparison.Ordinal);
                Assert.Equal(liveBefore + 1, SandboxLiveCounter.Active);

                var result = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("echo", harness.Transport.LastArgv());

                var stageIn = harness.Transport.StageInCalls.Single(c => c.RemotePath == "/work");
                Assert.Equal(hostDir, stageIn.HostPath);
                var credStage = harness.Transport.StageInCalls.Single(
                    c => c.RemotePath.EndsWith("agent.json", StringComparison.Ordinal));
                Assert.StartsWith(
                    SandboxConventions.CredentialsDir, credStage.RemotePath, StringComparison.Ordinal);
            }
            finally
            {
                await sandbox.DisposeAsync();
                await sandbox.DisposeAsync();
            }

            Assert.Equal(liveBefore, SandboxLiveCounter.Active);
            Assert.Empty(harness.Cloud.Servers);
            Assert.Empty(harness.Cloud.SshKeys);
            Assert.Empty(harness.Cloud.Firewalls);
            Assert.Empty(harness.Cloud.FloatingIps);
            Assert.False(Directory.Exists(harness.SshTempDir(sandbox.Id)));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBody_Carries_PinnedImage_Firewall_Labels_And_SafeUserData()
    {
        using var harness = NewHarness();
        harness.Dns.Hosts["example.com"] = [System.Net.IPAddress.Parse("93.184.216.34")];
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Network = new SandboxNetworkPolicy { AllowedHosts = ["example.com"] },
        };

        var sandbox = await harness.Provider.CreateAsync(spec, CancellationToken.None);
        try
        {
            var createBody = harness.Cloud.ServerCreateBodies.Single();
            using var createDoc = JsonDocument.Parse(createBody);
            var root = createDoc.RootElement;
            Assert.Equal(sandbox.Id, root.GetProperty("name").GetString());
            Assert.Equal("cx23", root.GetProperty("server_type").GetString());
            Assert.Equal(1, root.GetProperty("image").GetInt64());
            Assert.Equal("fsn1", root.GetProperty("location").GetString());
            var sshKeyName = root.GetProperty("ssh_keys").EnumerateArray().Select(e => e.GetString()).Single();
            Assert.StartsWith(ServerPrefix, sshKeyName, StringComparison.Ordinal);
            var firewallRef = root.GetProperty("firewalls").EnumerateArray().Select(e => e.GetProperty("firewall").GetInt64()).Single();
            Assert.True(firewallRef > 0);
            Assert.True(root.GetProperty("public_net").GetProperty("enable_ipv4").GetBoolean());
            Assert.False(root.GetProperty("public_net").GetProperty("enable_ipv6").GetBoolean());

            var labels = root.GetProperty("labels");
            Assert.Equal("true", labels.GetProperty("codeybox-owned").GetString());
            Assert.Equal(HetznerHarness.OwnerId, labels.GetProperty("codeybox-owner").GetString());
            Assert.False(string.IsNullOrWhiteSpace(labels.GetProperty("codeybox-request").GetString()));
            Assert.Equal("none", labels.GetProperty("codeybox-work-item").GetString());

            var userData = root.GetProperty("user_data").GetString()!;
            Assert.Contains("hostname: " + sandbox.Id, userData, StringComparison.Ordinal);
            Assert.Contains("ssh-ed25519 " + new string('B', 64), userData, StringComparison.Ordinal);
            Assert.Contains("ssh_genkeytypes: ['ed25519']", userData, StringComparison.Ordinal);
            Assert.Contains(SandboxConventions.CredentialsDir, userData, StringComparison.Ordinal);
            Assert.DoesNotContain(HetznerHarness.ApiToken, userData, StringComparison.Ordinal);
            Assert.DoesNotContain("HCLOUD", userData, StringComparison.Ordinal);

            var firewallBody = harness.Cloud.FirewallCreateBodies.Single();
            using var firewallDoc = JsonDocument.Parse(firewallBody);
            var rules = firewallDoc.RootElement.GetProperty("rules").EnumerateArray().ToList();
            var sshRule = rules.Single(r =>
                r.GetProperty("direction").GetString() == "in"
                && r.GetProperty("port").GetString() == "22");
            Assert.Contains("203.0.113.0/24",
                sshRule.GetProperty("source_ips").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains(rules, r =>
                r.GetProperty("direction").GetString() == "out"
                && r.GetProperty("destination_ips").EnumerateArray().Any(
                    e => e.GetString() == "93.184.216.34/32"));
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task FloatingIp_Allocated_Assigned_And_SshTargetsIt()
    {
        using var harness = NewHarness(configure: o => o with { FloatingIpHomeLocation = "fsn1" });
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            var floating = harness.Cloud.FloatingIps.Values.Single();
            Assert.Equal(floating.Server, harness.Cloud.Servers.Values.Single().Id);
            Assert.Equal("true", floating.Labels["codeybox-owned"]);
            Assert.Equal(HetznerHarness.OwnerId, floating.Labels["codeybox-owner"]);
            Assert.StartsWith("tester@203.0.113.", harness.TransportFactory.LastTarget, StringComparison.Ordinal);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.FloatingIps);
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task Spec_ImageReference_Overrides_OptionsImage()
    {
        using var harness = NewHarness(configure: o => o with { Image = "deprecated-img" });
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = "ubuntu-24.04" }, CancellationToken.None);
        try
        {
            using var createDoc = JsonDocument.Parse(harness.Cloud.ServerCreateBodies.Single());
            Assert.Equal(1, createDoc.RootElement.GetProperty("image").GetInt64());
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExplicitImageId_Resolves_ById()
    {
        using var harness = NewHarness(configure: o => o with { Image = "1" });
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            using var createDoc = JsonDocument.Parse(harness.Cloud.ServerCreateBodies.Single());
            Assert.Equal(1, createDoc.RootElement.GetProperty("image").GetInt64());
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // Disabled / config validation (fail loudly, no cloud side effects)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisabledProvider_RefusesProvisioning()
    {
        using var harness = NewHarness(configure: o => o with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
    }

    [Fact]
    public async Task MissingPlacementPins_Refused()
    {
        using var missingImage = NewHarness(configure: o => o with { Image = "" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => missingImage.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));

        using var missingType = NewHarness(configure: o => o with { ServerType = "" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => missingType.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));

        using var missingLocation = NewHarness(configure: o => o with { Location = "" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => missingLocation.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownServerType_Image_Location_RefusedLoudly()
    {
        using var badType = NewHarness(configure: o => o with { ServerType = "nope" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => badType.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(badType.Cloud.Servers);

        using var badImage = NewHarness(configure: o => o with { Image = "nope" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => badImage.Provider.CreateAsync(
                new SandboxSpec { ImageReference = "nope" }, CancellationToken.None));
        Assert.Empty(badImage.Cloud.Servers);
        Assert.Empty(badImage.Cloud.SshKeys);

        using var badLocation = NewHarness(configure: o => o with { Location = "nope" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => badLocation.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(badLocation.Cloud.Servers);
    }

    [Fact]
    public async Task DeprecatedImage_ByName_And_ById_RefusedLoudly()
    {
        using var byName = NewHarness(configure: o => o with { Image = "deprecated-img" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => byName.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(byName.Cloud.Servers);
        Assert.Empty(byName.Cloud.SshKeys);

        using var byId = NewHarness(configure: o => o with { Image = "2" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => byId.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(byId.Cloud.Servers);
    }

    [Fact]
    public async Task DeprecatedServerType_RefusedLoudly()
    {
        using var harness = NewHarness(configure: o => o with { ServerType = "deprecated-type" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task AmbiguousImageName_RefusedLoudly()
    {
        using var harness = NewHarness(configure: o => o with { Image = "ambiguous-img" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
    }

    [Fact]
    public async Task BadSshCidr_And_NoSshPath_Refused()
    {
        using var badCidr = NewHarness(configure: o => o with { OrchestratorSshCidrs = ["not-a-cidr"] });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => badCidr.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(badCidr.Cloud.Servers);

        using var noPath = NewHarness(configure: o => o with
        {
            EnablePublicIpv4 = false,
            EnablePublicIpv6 = false,
            FloatingIpHomeLocation = string.Empty,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => noPath.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(noPath.Cloud.Servers);
    }

    [Fact]
    public async Task GraphicalFlavor_IsRefused()
    {
        using var harness = NewHarness();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Flavor = SandboxProfileFlavor.Graphical,
        };
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Provider.CreateAsync(spec, CancellationToken.None));
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task NamedNetworkProfile_IsRefused()
    {
        using var harness = NewHarness();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Network = new SandboxNetworkPolicy { ProfileName = "prod" },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(spec, CancellationToken.None));
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task FileBackedCredentialMount_Refused()
    {
        using var harness = NewHarness();
        var hostDir = Directory.CreateTempSubdirectory("hetzner-cred-host-").FullName;
        try
        {
            var credFile = Path.Combine(hostDir, "agent.json");
            await File.WriteAllTextAsync(credFile, "{}");
            var spec = new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts =
                [
                    new SandboxMount
                    {
                        SandboxPath = SandboxConventions.CredentialsDir + "/agent.json",
                        HostPath = credFile,
                        ReadOnly = true,
                    },
                ],
            };
            await Assert.ThrowsAsync<NotSupportedException>(
                () => harness.Provider.CreateAsync(spec, CancellationToken.None));
            Assert.Empty(harness.Cloud.Servers);
            Assert.Empty(harness.Cloud.SshKeys);
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task MissingToken_FailsLoudly_NamingVariable()
    {
        using var harness = NewHarness();
        using var http = new HttpClient(harness.Cloud) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new HetznerSandboxProvider(
            () => harness.Options,
            http,
            null, null, null,
            _ => null,
            TimeProvider.System,
            null);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateAsync(
                    new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
            Assert.Contains("HCLOUD_TOKEN", ex.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Cloud.Servers);
        }
        finally
        {
            provider.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // SSH trust: pinning, strict flags, transport loss
    // ------------------------------------------------------------------

    [Fact]
    public async Task KnownHosts_Pins_GeneratedHostKey()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            var knownHosts = Path.Combine(harness.SshTempDir(sandbox.Id), "known_hosts");
            var line = (await File.ReadAllTextAsync(knownHosts)).Trim();
            var server = harness.Cloud.Servers.Values.Single();
            Assert.StartsWith(server.Ipv4 + " ", line, StringComparison.Ordinal);
            Assert.Contains(FakeHetznerKeyGenerator.HostPublicKey, line, StringComparison.Ordinal);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task RealTransport_UsesStrictHostKeys_And_MapsExit255_To_Unavailable()
    {
        var runner = new CapturingProcessRunner(
            new ProcessRunResult(255, string.Empty, "ssh: connect failed"));
        var factory = new OpenSshHetznerTransportFactory(runner, null);
        var transport = factory.Create(new HetznerSshTransportSpec(
            "tester@192.0.2.1", 22, "/tmp/fake-key", "/tmp/fake-known-hosts", "ssh", 10));

        await Assert.ThrowsAsync<RemoteSshTransportException>(
            () => transport.RunAsync(["true"], stdin: null, CancellationToken.None));

        var argv = runner.LastArgv;
        Assert.DoesNotContain("accept-new", string.Join(" ", argv), StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=yes", argv);
        Assert.Contains("UserKnownHostsFile=/tmp/fake-known-hosts", argv);
        Assert.Contains("GlobalKnownHostsFile=/dev/null", argv);
        Assert.Contains("BatchMode=yes", argv);
        Assert.Contains("IdentitiesOnly=yes", argv);
        Assert.Contains("tester@192.0.2.1", argv);
    }

    [Fact]
    public async Task MidExec_TransportLoss_Maps_To_ExecutionUnavailable()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            harness.Transport.ThrowTransportLossOnRun = true;
            var ex = await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
                () => sandbox.ExecAsync(new SandboxExec { Argv = ["x"] }, CancellationToken.None));
            Assert.NotEqual(0, ex.ExitCode);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task OutputLimits_Propagate_To_Transport_And_Result()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            harness.Transport.OnRun = (_, _) =>
                new ProcessRunResult(0, new string('x', 64), string.Empty, StdoutLimitExceeded: true);
            var result = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["yes"], MaxStdoutBytes = 16 }, CancellationToken.None);
            Assert.Equal(16, harness.Transport.LastMaxStdoutBytes);
            Assert.True(result.StdoutLimitExceeded);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // Handle semantics: disposal, sync ordering
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exec_AfterDispose_Throws_And_Sync_AfterDispose_IsNoop()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["x"] }, CancellationToken.None));
        var stageOuts = harness.Transport.StageOutCalls.Count;
        await sandbox.SyncStateToHostAsync(CancellationToken.None);
        Assert.Equal(stageOuts, harness.Transport.StageOutCalls.Count);
    }

    [Fact]
    public async Task SyncBack_Runs_Before_ServerDeletion_OnDispose()
    {
        using var harness = NewHarness();
        var hostDir = Directory.CreateTempSubdirectory("hetzner-sync-host-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, "seed.txt"), "seed");
            var sandbox = await harness.Provider.CreateAsync(new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = hostDir, ReadOnly = false }],
            }, CancellationToken.None);

            var serverId = harness.Cloud.Servers.Values.Single().Id.ToString(CultureInfo.InvariantCulture);
            harness.Transport.OnStageOut = (_, _) => harness.Cloud.Log("stageout-sync");
            await sandbox.DisposeAsync();

            Assert.Single(harness.Transport.StageOutCalls);
            var syncIndex = harness.Cloud.Events.IndexOf("stageout-sync");
            var deleteIndex = harness.Cloud.Events.FindIndex(
                e => e == "delete-server:" + serverId);
            Assert.True(syncIndex >= 0, "sync-back must run during disposal");
            Assert.True(deleteIndex > syncIndex, "sync-back must run before server deletion");
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Cloud failure classification (no synthetic success)
    // ------------------------------------------------------------------

    [Fact]
    public async Task QuotaExhausted_Defers_With_LongRecheck_And_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.Forbidden, "resource_limit_exceeded", "too many servers", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("quota-exhausted", deferred.ErrorClass);
        Assert.True(deferred.RecheckIn >= TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task Unauthorized401_Defers_With_LongRecheck()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.Unauthorized, "unauthorized", "bad token", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("unauthorized", deferred.ErrorClass);
        Assert.True(deferred.RecheckIn >= TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task Throttled429_Honors_RetryAfter()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow down", false);
        harness.Cloud.NextCreateRetryAfterSeconds = "2";

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("throttled", deferred.ErrorClass);
        Assert.True(deferred.RecheckIn >= TimeSpan.FromSeconds(2));
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task ConflictOnSshKey_Defers_AsTransient_And_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextSshKeyCreate = (
            HttpStatusCode.Conflict, "uniqueness_error", "key exists");

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("conflict", deferred.ErrorClass);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
    }

    [Fact]
    public async Task FaultStatusServer_IsDeleted_And_Deferred()
    {
        using var harness = NewHarness();
        harness.Cloud.ForceServerStatus = "unknown";

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", deferred.ErrorClass);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task ServerError500_Defers_And_CleansUp_PartialSet()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.InternalServerError, "server_error", "boom", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", deferred.ErrorClass);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    // ------------------------------------------------------------------
    // Ambiguous creates reconcile by request label, never blindly resubmit
    // ------------------------------------------------------------------

    [Fact]
    public async Task AmbiguousCreate_WithServerStored_Adopts_By_RequestLabel()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.InternalServerError, "server_error", "cut mid-create", true);

        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.Servers);
            Assert.Single(harness.Cloud.ServerCreateBodies);
            var result = await sandbox.ExecAsync(
                new SandboxExec { Argv = ["echo", "adopted"] }, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task AmbiguousCreate_WithoutServer_Rethrows_Deferred()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.InternalServerError, "server_error", "cut mid-create", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", deferred.ErrorClass);
        Assert.Single(harness.Cloud.ServerCreateBodies);
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task EventualConsistency_BlindReads_Retried_BeforeAdopt()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextServerCreate = (
            HttpStatusCode.ServiceUnavailable, "server_error", "flaky", true);
        harness.Cloud.BlindListCalls = 2;

        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.Servers);
            Assert.Single(harness.Cloud.ServerCreateBodies);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Servers);
    }

    // ------------------------------------------------------------------
    // Cancellation at every provisioning boundary
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_BeforeCreate_Cancels_WithoutSideEffects()
    {
        using var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task Cancelled_DuringRunningWait_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.RunningAfterPolls = int.MaxValue;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    [Fact]
    public async Task Cancelled_DuringSshWait_CleansUp()
    {
        using var harness = NewHarness(configure: o => o with
        {
            SshReadyTimeoutSeconds = 600,
            PollIntervalMilliseconds = 5,
            MaxPollIntervalMilliseconds = 10,
        });
        harness.TransportFactory.ConfigureTransport = t => t.ThrowTransportLossOnRun = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Servers);
        Assert.Empty(harness.Cloud.SshKeys);
        Assert.Empty(harness.Cloud.Firewalls);
    }

    // ------------------------------------------------------------------
    // Restart adoption, leak disposal, orphan sweep, ownership
    // ------------------------------------------------------------------

    [Fact]
    public async Task RestartAdoption_OrphanCleanup_RemovesFullSet_And_RepeatsCleanly()
    {
        using var harness = NewHarness();
        var orphan = harness.Cloud.SeedOrphan(HetznerHarness.OwnerId, harness.Options);
        var other = harness.Cloud.SeedOrphan(HetznerHarness.OtherOwnerId, harness.Options);

        // A fresh provider over the same cloud: the restart-adoption path
        // (empty in-memory tracking, ownership labels as the source of truth).
        using var restarted = new HetznerHarness(null, harness.Cloud);

        var inventory = await restarted.Provider.ListAllManagedAsync(CancellationToken.None);
        var found = Assert.Single(inventory, i => i.Name == orphan);
        Assert.False(found.IsTrackedActive);

        await restarted.Provider.DisposeLeakedAsync(orphan, CancellationToken.None);
        await restarted.Provider.DisposeLeakedAsync(orphan, CancellationToken.None);

        Assert.DoesNotContain(harness.Cloud.Servers.Values, s => s.Name == orphan);
        Assert.Contains(harness.Cloud.Servers.Values, s => s.Name == other);
        Assert.DoesNotContain(harness.Cloud.SshKeys.Values, k => k.Labels["codeybox-owner"] == HetznerHarness.OwnerId);
        Assert.DoesNotContain(harness.Cloud.Firewalls.Values, f => f.Labels["codeybox-owner"] == HetznerHarness.OwnerId);
        Assert.DoesNotContain(harness.Cloud.FloatingIps.Values, f => f.Labels["codeybox-owner"] == HetznerHarness.OwnerId);
        Assert.Contains(harness.Cloud.SshKeys.Values, k => k.Labels["codeybox-owner"] == HetznerHarness.OtherOwnerId);
    }

    [Fact]
    public async Task OwnershipCollision_OtherOwnersServer_Never_Touched()
    {
        using var harness = NewHarness();
        var other = harness.Cloud.SeedOrphan(HetznerHarness.OtherOwnerId, harness.Options);

        await harness.Provider.DisposeLeakedAsync(other, CancellationToken.None);

        Assert.Contains(harness.Cloud.Servers.Values, s => s.Name == other);
        var foreign = harness.Cloud.Servers.Values.Single(s => s.Name == other);
        Assert.Throws<InvalidOperationException>(
            () => HetznerSandboxProvider.VerifyOwnedServer(
                new HetznerServer
                {
                    Id = foreign.Id,
                    Name = foreign.Name,
                    Status = "running",
                    Labels = foreign.Labels,
                },
                HetznerHarness.OwnerId, other));
    }

    [Fact]
    public async Task NonPrefixedName_Refused_For_LeakDisposal()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Provider.DisposeLeakedAsync("someone-elses-server", CancellationToken.None));
    }

    [Fact]
    public async Task CleanupFailure_Retained_And_Visible_For_Reaper()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var name = sandbox.Id;
        harness.Cloud.FailServerDelete = true;

        await sandbox.DisposeAsync();

        var leaked = Assert.Single(harness.Cloud.Servers.Values, s => s.Name == name);
        Assert.Equal("true", leaked.Labels["codeybox-owned"]);
        var inventory = await harness.Provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(inventory, i => i.Name == name);

        harness.Cloud.FailServerDelete = false;
        await harness.Provider.DisposeLeakedAsync(name, CancellationToken.None);
        Assert.Empty(harness.Cloud.Servers);
    }

    [Fact]
    public async Task Sweep_Removes_Only_Unreferenced_OwnedResources()
    {
        using var harness = NewHarness();
        var live = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            var orphanKeyId = harness.Cloud.NextId();
            harness.Cloud.SshKeys[orphanKeyId] = new FakeHetznerCloud.FakeNamedResource
            {
                Id = orphanKeyId,
                Name = "codeybox-dead-beef",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox-owned"] = "true",
                    ["codeybox-owner"] = HetznerHarness.OwnerId,
                    ["codeybox-request"] = "deadbeef",
                },
            };
            var unlabeledKeyId = harness.Cloud.NextId();
            harness.Cloud.SshKeys[unlabeledKeyId] = new FakeHetznerCloud.FakeNamedResource
            {
                Id = unlabeledKeyId,
                Name = "codeybox-mystery",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox-owned"] = "true",
                    ["codeybox-owner"] = HetznerHarness.OwnerId,
                },
            };

            await harness.Provider.DisposeLeakedAsync(live.Id, CancellationToken.None);

            Assert.DoesNotContain(orphanKeyId, harness.Cloud.SshKeys.Keys);
            Assert.Contains(unlabeledKeyId, harness.Cloud.SshKeys.Keys);
            Assert.Empty(harness.Cloud.Servers);
        }
        finally
        {
            await live.DisposeAsync();
        }
    }

    private sealed class CapturingProcessRunner : CodeyBox.HostProcess.IProcessRunner
    {
        private readonly CodeyBox.HostProcess.ProcessRunResult _result;

        public CapturingProcessRunner(CodeyBox.HostProcess.ProcessRunResult result) => _result = result;

        public IReadOnlyList<string> LastArgv { get; private set; } = [];

        public Task<CodeyBox.HostProcess.ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null)
        {
            _ = stdin; _ = ct;
            _ = stdoutChunkCallback; _ = stderrChunkCallback;
            _ = maxStdoutBytes; _ = maxStderrBytes;
            _ = environment; _ = killOnOutputLimit; _ = workingDirectory;
            LastArgv = argv.ToArray();
            return Task.FromResult(_result);
        }
    }
}
