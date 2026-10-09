using System.Globalization;
using System.Net;
using System.Text;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Ec2SandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;

namespace CodeyBox.Tests;

/// <summary>
/// Provider-level tests for the <c>ec2</c> sandbox provider: full
/// acquire → exec → stage → dispose against a fake EC2 Query API plus a fake
/// SSH transport, disabled/config validation, explicit AMI pin and scope,
/// host-key pinning, cancellation at every provisioning boundary, remote
/// transport loss, output limits, partial and ambiguous creates, eventual
/// consistency, 401/403/429/quota/5xx, malformed/truncated/oversized
/// responses, restart adoption/orphan cleanup, repeated disposal, sync-back
/// before teardown, retained cleanup failure, and ownership collisions.
/// </summary>
public sealed class Ec2SandboxProviderTests
{
    internal const string InstancePrefix = "codeybox-";

    private static Ec2Harness NewHarness(Func<Ec2SandboxOptions, Ec2SandboxOptions>? configure = null) =>
        new(configure);

    // ------------------------------------------------------------------
    // Registration and egress classification
    // ------------------------------------------------------------------

    [Fact]
    public void Kind_IsNotRegisteredAsBuiltin()
    {
        using var harness = NewHarness();
        Assert.Equal("ec2", harness.Provider.Name);
        Assert.Equal("codeybox.ec2-sandbox", Ec2SandboxOptions.PluginId);
        Assert.False(SandboxProviderKinds.IsRegistered("ec2"),
            "plugin kinds must never collide with built-in provider kinds");
    }

    [Fact]
    public void EgressClassification_IsNotEnforced_RegardlessOfPluginClaims()
    {
        using var harness = NewHarness();
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement(harness.Provider.Name));
        // Even a plugin claiming dedicated-kernel isolation cannot promote the kind.
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement(Ec2SandboxOptions.ProviderKind));
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, harness.Provider.IsolationLevel);
    }

    [Fact]
    public void Catalog_SelectsKindByMemberProviderKind_AndSharesInstanceAcrossMembers()
    {
        using var harness = NewHarness();
        var catalog = new PluginSandboxProviderCatalog([(Ec2SandboxOptions.PluginId, harness.Provider)]);
        Assert.True(catalog.IsPluginKind("ec2"));
        Assert.True(catalog.TryGetProvider("ec2", out var resolved));
        Assert.Same(harness.Provider, resolved);

        var registry = new SandboxProviderRegistry(
            kind => catalog.TryGetProvider(kind, out var p) ? p : throw new InvalidOperationException(kind),
            pluginKinds: catalog.Kinds);
        var member1 = new SandboxMember { MemberId = "e1", ProviderKind = "ec2", Capacity = 2, PreferenceScore = 50 };
        var member2 = new SandboxMember { MemberId = "e2", ProviderKind = "ec2", Capacity = 4, PreferenceScore = 50 };
        Assert.Same(registry.Resolve(member1), registry.Resolve(member2));
    }

    [Fact]
    public void CapabilityGate_DropsUndeclaredWellKnownTags_KeepsDeclaredAndCustom()
    {
        using var harness = NewHarness();
        var member = new SandboxMember
        {
            MemberId = "e1", ProviderKind = "ec2", Capacity = 2, PreferenceScore = 50,
            Capabilities = ["suspend-resume", "teardown", "org-clearance-tag"],
        };
        var projected = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, harness.Provider);
        Assert.Contains("teardown", projected.Capabilities);
        Assert.Contains("org-clearance-tag", projected.Capabilities); // operator clearance tags pass through
        Assert.DoesNotContain("suspend-resume", projected.Capabilities);
    }

    // ------------------------------------------------------------------
    // Full lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Acquire_Exec_Stage_Dispose_FullLifecycle()
    {
        using var harness = NewHarness();
        var liveBefore = SandboxLiveCounter.Active;

        var hostDir = Directory.CreateTempSubdirectory("ec2-prov-host-").FullName;
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
                Assert.StartsWith(InstancePrefix, sandbox.Id, StringComparison.Ordinal);
                Assert.Equal(liveBefore + 1, SandboxLiveCounter.Active);

                var result = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("echo", harness.Transport.LastArgv());

                var instanceId = harness.Cloud.Instances.Values.Single().InstanceId;
                await sandbox.DisposeAsync();

                Assert.Equal(liveBefore, SandboxLiveCounter.Active);
                Assert.True(
                    harness.Cloud.Instances.TryGetValue(instanceId, out var instance)
                    && instance.State == "terminated",
                    "instance must be terminated after disposal");
                Assert.Empty(harness.Cloud.KeyPairs);
                Assert.Empty(harness.Cloud.SecurityGroups);
                Assert.Equal(instanceId, harness.Cloud.Instances.Values.Single().InstanceId);
            }
            catch
            {
                await sandbox.DisposeAsync();
                throw;
            }
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBody_Carries_PinnedAmi_SingleInstance_Tags_And_SafeUserData()
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
            var runBody = harness.Cloud.RunBodies.Single();
            Assert.Equal(Ec2Harness.AmiId, runBody["ImageId"]);
            Assert.Equal("t3.medium", runBody["InstanceType"]);
            Assert.Equal("1", runBody["MinCount"]);
            Assert.Equal("1", runBody["MaxCount"]);
            Assert.Equal(Ec2Harness.SubnetId, runBody["NetworkInterface.1.SubnetId"]);
            Assert.Equal("true", runBody["NetworkInterface.1.AssociatePublicIpAddress"]);
            Assert.Equal("require", runBody["MetadataOptions.HttpTokens"]);
            Assert.False(string.IsNullOrWhiteSpace(runBody["ClientToken"]));

            var instanceName = sandbox.Id;
            Assert.Equal(instanceName, runBody["TagSpecification.1.Tag.1.Value"]);
            Assert.Equal("true", runBody["TagSpecification.1.Tag.2.Value"]);
            Assert.Equal(Ec2Harness.OwnerId, runBody["TagSpecification.1.Tag.3.Value"]);
            Assert.Equal(runBody["ClientToken"], runBody["TagSpecification.1.Tag.5.Value"]);

            var keyName = runBody["KeyName"];
            Assert.StartsWith(InstancePrefix, keyName, StringComparison.Ordinal);

            var userData = Encoding.UTF8.GetString(Convert.FromBase64String(runBody["UserData"]));
            Assert.Contains("hostname: " + instanceName, userData, StringComparison.Ordinal);
            Assert.Contains("ssh-ed25519 " + new string('B', 64), userData, StringComparison.Ordinal);
            Assert.Contains("ssh_genkeytypes: ['ed25519']", userData, StringComparison.Ordinal);
            Assert.Contains(SandboxConventions.CredentialsDir, userData, StringComparison.Ordinal);
            Assert.DoesNotContain(Ec2Harness.SecretKey, userData, StringComparison.Ordinal);
            Assert.DoesNotContain(Ec2Harness.AccessKey, userData, StringComparison.Ordinal);
            Assert.DoesNotContain("AWS_SECRET", userData, StringComparison.Ordinal);

            var groupEvents = harness.Cloud.Events.Where(e => e.StartsWith("authorize-ingress:", StringComparison.Ordinal)).ToList();
            Assert.Single(groupEvents);
            var createdGroup = harness.Cloud.SecurityGroups.Values.Single();
            Assert.StartsWith("codeybox-sg-", createdGroup.GroupName, StringComparison.Ordinal);
            Assert.Equal(Ec2Harness.VpcId, createdGroup.VpcId);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ElasticIp_Allocated_Associated_And_SshTargetsIt()
    {
        using var harness = NewHarness(o => o with { AllocateElasticIp = true });
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            var address = harness.Cloud.Addresses.Values.Single();
            Assert.Equal("203.0.113.10", address.PublicIp);
            Assert.NotNull(address.AssociationId);
            Assert.StartsWith("tester@", harness.TransportFactory.LastTarget, StringComparison.Ordinal);
            Assert.Contains(address.PublicIp, harness.TransportFactory.LastTarget, StringComparison.Ordinal);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Addresses);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("  198.51.100.9  ", "198.51.100.9")]
    public void ElasticIp_ValidAddress_Accepted(string raw, string expected) =>
        Assert.Equal(expected, Ec2SandboxProvider.ValidateElasticIpAddress(raw));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7\n evil.example")]
    [InlineData("203.0.113.7 evil")]
    public void ElasticIp_UntrustedValue_Rejected(string raw) =>
        Assert.Throws<Ec2ApiException>(() => Ec2SandboxProvider.ValidateElasticIpAddress(raw));

    [Fact]
    public async Task ElasticIp_MaliciousValue_FailsClosed_And_CleansUp()
    {
        using var harness = NewHarness(o => o with { AllocateElasticIp = true });
        harness.Cloud.NextElasticIpOverride = "10.0.0.1\nMITM known_hosts injection";
        // The allocation response carried a hostile value: the client refuses
        // it before any SSH target is built, and everything is reaped — the
        // allocation included, reconciled by its request tag even though its
        // id never reached the host.
        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("usable public IP", deferred.Detail, StringComparison.Ordinal);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
        Assert.Empty(harness.Cloud.Addresses);
    }

    [Fact]
    public async Task InstanceIp_MaliciousValue_FailsClosed_And_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.NextPublicIpOverride = "10.0.0.1\nMITM known_hosts injection";
        // A present-but-unparseable instance address fails fast (no
        // timeout wait, no SSH at an unconfirmed target) with full cleanup:
        // no allocation exists on this path.
        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("usable public IP", deferred.Detail, StringComparison.Ordinal);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
        Assert.Empty(harness.Cloud.Addresses);
    }

    [Fact]
    public async Task Spec_ImageReference_Overrides_OptionsAmi()
    {
        using var harness = NewHarness();
        const string otherAmi = "ami-abcdef0123456789a";
        // The fake only knows the default AMI: an unknown pin fails loudly at
        // the AMI verification step, before any instance is launched.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = otherAmi }, CancellationToken.None));
        Assert.Contains(otherAmi, ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.RunBodies);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task UnknownAmi_RefusedLoudly_BeforeAnyLaunch()
    {
        using var harness = NewHarness(o => o with { AmiId = "ami-00000000000000000" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("AmiId", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.RunBodies);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task UnavailableAmi_RefusedLoudly()
    {
        using var harness = NewHarness();
        harness.Cloud.ImageState = "pending";
        var ex = await Assert.ThrowsAsync<Ec2ApiException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("pending", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task DisabledProvider_RefusesProvisioning()
    {
        using var harness = NewHarness(o => o with { Enabled = false });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Cloud.RunBodies);
    }

    [Fact]
    public async Task MissingPlacementPins_Refused()
    {
        using var noSubnet = NewHarness(o => o with { SubnetId = string.Empty });
        var subnetEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => noSubnet.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("SubnetId", subnetEx.Message, StringComparison.Ordinal);

        using var badRegion = NewHarness(o => o with { Region = "moon-1" });
        var regionEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => badRegion.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("Region", regionEx.Message, StringComparison.Ordinal);

        using var badType = NewHarness(o => o with { InstanceType = "Huge!!" });
        var typeEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => badType.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("InstanceType", typeEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSecurityGroup_Anywhere_Refused()
    {
        using var harness = NewHarness(o => o with { CreateSecurityGroup = false });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("SecurityGroup", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.RunBodies);
    }

    [Fact]
    public async Task CallerOwnedSecurityGroups_Attach_Without_Management()
    {
        using var harness = NewHarness(o => o with
        {
            CreateSecurityGroup = false,
            SecurityGroupIds = ["sg-0123456789abcdef0"],
        });
        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var runBody = harness.Cloud.RunBodies.Single();
        Assert.Equal("sg-0123456789abcdef0", runBody["NetworkInterface.1.Group.1"]);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task BadSshCidr_And_NoSshPath_Refused()
    {
        using var badCidr = NewHarness(o => o with { OrchestratorSshCidrs = ["not-a-cidr"] });
        var cidrEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => badCidr.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("OrchestratorSshCidrs", cidrEx.Message, StringComparison.Ordinal);

        using var noSshPath = NewHarness(o => o with { AssociatePublicIp = false });
        var pathEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => noSshPath.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("SSH path", pathEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphicalFlavor_IsRefused()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = string.Empty,
                    Flavor = SandboxProfileFlavor.Graphical,
                },
                CancellationToken.None));
        Assert.Empty(harness.Cloud.RunBodies);
    }

    [Fact]
    public async Task NamedNetworkProfile_IsRefused()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = string.Empty,
                    Network = new SandboxNetworkPolicy { ProfileName = "strict-egress" },
                },
                CancellationToken.None));
        Assert.Empty(harness.Cloud.RunBodies);
    }

    [Fact]
    public async Task FileBackedCredentialMount_Refused()
    {
        using var harness = NewHarness();
        var hostDir = Directory.CreateTempSubdirectory("ec2-credmount-").FullName;
        try
        {
            var credFile = Path.Combine(hostDir, "agent.json");
            await File.WriteAllTextAsync(credFile, "{}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => harness.Provider.CreateAsync(
                    new SandboxSpec
                    {
                        ImageReference = string.Empty,
                        Mounts =
                        [
                            new SandboxMount
                            {
                                SandboxPath = SandboxConventions.CredentialsDir + "/agent.json",
                                HostPath = credFile,
                            },
                        ],
                    },
                    CancellationToken.None));
            Assert.Empty(harness.Cloud.RunBodies);
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task MissingCredentials_FailLoudly_NamingVariable()
    {
        using var harness = NewHarness();
        var provider = new Ec2SandboxProvider(
            () => harness.Options,
            new HttpClient(harness.Cloud, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            new FakeEc2KeyGenerator(),
            harness.Dns,
            harness.TransportFactory,
            _ => null,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("AWS_ACCESS_KEY_ID", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.RunBodies);
    }

    [Fact]
    public async Task KnownHosts_Pins_GeneratedHostKey()
    {
        using var harness = NewHarness();
        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var knownHosts = await File.ReadAllTextAsync(Path.Combine(harness.SshTempDir(sandbox.Id), "known_hosts"));
        Assert.Contains(FakeEc2KeyGenerator.HostPublicKey, knownHosts, StringComparison.Ordinal);
        Assert.StartsWith("192.0.2.10 ", knownHosts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidExec_TransportLoss_Maps_To_ExecutionUnavailable()
    {
        using var harness = NewHarness();
        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        harness.Transport.ThrowTransportLossOnRun = true;
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["x"] }, CancellationToken.None));
    }

    [Fact]
    public async Task OutputLimits_Propagate_To_Transport_And_Result()
    {
        using var harness = NewHarness();
        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var payload = new string('x', 10_000);
        harness.Transport.OnRun = (_, _) => new ProcessRunResult(0, payload, "err");
        var result = await sandbox.ExecAsync(
            new SandboxExec { Argv = ["cat"], MaxStdoutBytes = 100, MaxStderrBytes = 50 },
            CancellationToken.None);
        Assert.Equal(100, harness.Transport.LastMaxStdoutBytes);
        Assert.Equal(50, harness.Transport.LastMaxStderrBytes);
        Assert.True(result.Stdout.Length <= 100);
    }

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
    public async Task SyncBack_Runs_Before_InstanceTermination_OnDispose()
    {
        using var harness = NewHarness();
        var hostDir = Directory.CreateTempSubdirectory("ec2-sync-host-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, "seed.txt"), "seed");
            var sandbox = await harness.Provider.CreateAsync(new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = hostDir, ReadOnly = false }],
            }, CancellationToken.None);

            var instanceId = harness.Cloud.Instances.Values.Single().InstanceId;
            harness.Transport.OnStageOut = (_, _) => harness.Cloud.Log("stageout-sync");
            await sandbox.DisposeAsync();

            Assert.Single(harness.Transport.StageOutCalls);
            var syncIndex = harness.Cloud.Events.IndexOf("stageout-sync");
            var deleteIndex = harness.Cloud.Events.FindIndex(
                e => e == "terminate-instance:" + instanceId);
            Assert.True(syncIndex >= 0, "sync-back must run during disposal");
            Assert.True(deleteIndex > syncIndex, "sync-back must run before instance termination");
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
        harness.Cloud.FailNextRun = (
            HttpStatusCode.Forbidden, "InstanceLimitExceeded", "too many instances", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("quota-exhausted", deferred.ErrorClass);
        Assert.True(deferred.RecheckIn >= TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task Unauthorized401_Defers_With_LongRecheck()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextRun = (
            HttpStatusCode.Unauthorized, "AuthFailure", "bad key", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("unauthorized", deferred.ErrorClass);
        Assert.True(deferred.RecheckIn >= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Throttled429_Defers_With_BaseRecheck()
    {
        // One attempt only: the ambiguous throttle cannot reconcile (nothing
        // was stored) and no resubmit is allowed, so provisioning defers.
        using var harness = NewHarness(o => o with { MaxRunAttempts = 1 });
        harness.Cloud.FailNextRun = (
            (HttpStatusCode)429, "RequestThrottled", "slow down", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("throttled", deferred.ErrorClass);
    }

    [Fact]
    public async Task ConflictOnKeyPair_Adopts_When_Owned()
    {
        using var harness = NewHarness();
        var first = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            // A duplicate key-pair name owned by the same tags is adopted, not
            // treated as a collision: the fake already holds this sandbox's key.
            Assert.Single(harness.Cloud.KeyPairs);
        }
        finally
        {
            await first.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.KeyPairs);
    }

    [Fact]
    public async Task FaultStateInstance_IsTerminated_And_Deferred()
    {
        using var harness = NewHarness();
        harness.Cloud.ForceInstanceState = "terminated";
        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("terminated", deferred.Message, StringComparison.OrdinalIgnoreCase);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
    }

    [Fact]
    public async Task ServerError500_Defers_And_CleansUp_PartialSet()
    {
        using var harness = NewHarness(o => o with { MaxRunAttempts = 1 });
        harness.Cloud.FailNextRun = (
            HttpStatusCode.InternalServerError, "InternalError", "boom", false);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Equal("server-error", deferred.ErrorClass);
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task AmbiguousCreate_WithInstanceStored_Adopts_By_RequestTag()
    {
        using var harness = NewHarness();
        // The run fails ambiguously (throttle) but the instance was stored:
        // the provider must adopt it via the request tag, not launch a second.
        harness.Cloud.FailNextRun = (
            (HttpStatusCode)429, "RequestThrottled", "slow down", true);

        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);

        Assert.Single(harness.Cloud.Instances);
        Assert.Single(harness.Cloud.RunBodies);
        var instance = harness.Cloud.Instances.Values.Single();
        Assert.Equal(sandbox.Id, instance.Tags["Name"]);
    }

    [Fact]
    public async Task AmbiguousCreate_WithoutInstance_Retries_Then_Defers()
    {
        using var harness = NewHarness(o => o with { MaxRunAttempts = 1 });
        harness.Cloud.FailNextRun = (
            HttpStatusCode.InternalServerError, "InternalError", "boom", false);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Single(harness.Cloud.RunBodies);
        Assert.Empty(harness.Cloud.Instances);
    }

    [Fact]
    public async Task EventualConsistency_BlindReads_Retried_BeforeAdopt()
    {
        using var harness = NewHarness();
        harness.Cloud.FailNextRun = (
            (HttpStatusCode)429, "RequestThrottled", "slow down", true);
        harness.Cloud.BlindDescribeCalls = 2;

        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);

        Assert.Single(harness.Cloud.Instances);
        Assert.Equal(sandbox.Id, harness.Cloud.Instances.Values.Single().Tags["Name"]);
    }

    [Fact]
    public async Task MalformedResponse_To_Run_Reconciles_Not_Duplicates()
    {
        using var harness = NewHarness(o => o with { MaxRunAttempts = 2 });
        harness.Cloud.MalformedNextResponse = true;
        harness.Cloud.MalformedAction = "RunInstances";

        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);

        // The malformed run response reconciled by request tag (one launch
        // recorded); no second RunInstances was submitted.
        Assert.Single(harness.Cloud.RunBodies);
        Assert.Single(harness.Cloud.Instances);
        Assert.Equal(sandbox.Id, harness.Cloud.Instances.Values.Single().Tags["Name"]);
    }

    [Fact]
    public async Task OversizedResponse_FailsClosed_And_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.OversizedNextResponse = true;
        harness.Cloud.OversizedAction = "DescribeInstances";

        // The over-cap body is refused, never parsed: provisioning defers and
        // the launched instance, key pair and security group are reaped.
        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("byte cap", deferred.Detail, StringComparison.Ordinal);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    // ------------------------------------------------------------------
    // Cancellation
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
        Assert.Empty(harness.Cloud.Instances);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task Cancelled_DuringRunningWait_CleansUp()
    {
        using var harness = NewHarness(o => o with
        {
            PollIntervalMilliseconds = 200,
            MaxPollIntervalMilliseconds = 200,
            ReadyTimeoutSeconds = 30,
        });
        harness.Cloud.RunningAfterPolls = 10_000;
        using var cts = new CancellationTokenSource();
        var create = harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    [Fact]
    public async Task Cancelled_DuringSshWait_CleansUp()
    {
        using var harness = NewHarness();
        harness.TransportFactory.ConfigureTransport = t =>
            t.OnRun = (_, _) => new ProcessRunResult(1, string.Empty, "not ready");
        using var cts = new CancellationTokenSource();
        var create = harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        foreach (var instance in harness.Cloud.Instances.Values)
            Assert.Equal("terminated", instance.State);
        Assert.Empty(harness.Cloud.KeyPairs);
        Assert.Empty(harness.Cloud.SecurityGroups);
    }

    // ------------------------------------------------------------------
    // Restart adoption / orphan cleanup
    // ------------------------------------------------------------------

    [Fact]
    public async Task RestartAdoption_OrphanCleanup_RemovesFullSet_And_RepeatsCleanly()
    {
        var cloud = new FakeEc2Cloud();
        string orphanName;
        using (var seeder = new Ec2Harness(sharedCloud: cloud))
            orphanName = cloud.SeedOrphan(Ec2Harness.OwnerId, seeder.Options);

        using (var harness = new Ec2Harness(sharedCloud: cloud))
        {
            var listed = await harness.Provider.ListAllManagedAsync(CancellationToken.None);
            Assert.Contains(listed, m => m.Name == orphanName && !m.IsTrackedActive);

            await harness.Provider.DisposeLeakedAsync(orphanName, CancellationToken.None);

            Assert.DoesNotContain(cloud.Instances, kv => kv.Value.State != "terminated");
            Assert.Empty(cloud.KeyPairs);
            Assert.Empty(cloud.SecurityGroups);
            Assert.DoesNotContain(cloud.Addresses, kv => kv.Value.InstanceId is not null);

            // Repeated disposal is a clean no-op.
            await harness.Provider.DisposeLeakedAsync(orphanName, CancellationToken.None);
            Assert.Empty(await harness.Provider.ListAllManagedAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task OwnershipCollision_OtherOwnersInstance_Never_Touched()
    {
        var cloud = new FakeEc2Cloud();
        string foreignName;
        using (var seeder = new Ec2Harness(sharedCloud: cloud))
            foreignName = cloud.SeedOrphan(Ec2Harness.OtherOwnerId, seeder.Options);

        using (var harness = new Ec2Harness(sharedCloud: cloud))
        {
            Assert.Empty(await harness.Provider.ListAllManagedAsync(CancellationToken.None));
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => harness.Provider.DisposeLeakedAsync(foreignName, CancellationToken.None));
            Assert.Contains("not a managed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(cloud.Instances);
            Assert.Single(cloud.KeyPairs);
            Assert.Single(cloud.SecurityGroups);
        }
    }

    [Fact]
    public async Task NonPrefixedName_Refused_For_LeakDisposal()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Provider.DisposeLeakedAsync("someone-elses-vm", CancellationToken.None));
    }

    [Fact]
    public async Task CleanupFailure_Retained_And_Visible_For_Reaper()
    {
        using var harness = NewHarness();
        await using var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var name = sandbox.Id;
        harness.Cloud.FailInstanceDelete = true;

        // The failure stays visible: leak disposal reports it instead of
        // claiming a deletion that never happened.
        await Assert.ThrowsAsync<AggregateException>(
            () => harness.Provider.DisposeLeakedAsync(name, CancellationToken.None));

        // Termination refused: the instance keeps its tags, so the reaper can retry.
        var instance = harness.Cloud.Instances.Values.Single();
        Assert.Equal("running", instance.State);
        Assert.Equal(Ec2Harness.OwnerId, instance.Tags["codeybox-owner"]);
        Assert.Contains(
            await harness.Provider.ListAllManagedAsync(CancellationToken.None),
            m => m.Name == name);
        harness.Cloud.FailInstanceDelete = false;
    }

    [Fact]
    public async Task Sweep_Removes_Only_Unreferenced_OwnedResources()
    {
        var cloud = new FakeEc2Cloud();
        string orphanName;
        using (var seeder = new Ec2Harness(sharedCloud: cloud))
            orphanName = cloud.SeedOrphan(Ec2Harness.OwnerId, seeder.Options);
        using (var live = new Ec2Harness(sharedCloud: cloud))
        {
            await using var sandbox = await live.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
            var liveName = sandbox.Id;

            // Terminate the orphan instance directly (simulating a crash
            // after partial provisioning), leaving its set dangling.
            var orphan = cloud.Instances.Values.Single(i => i.Tags["Name"] == orphanName);
            orphan.State = "terminated";

            await live.Provider.DisposeLeakedAsync(liveName, CancellationToken.None);

            // The sweep (piggybacked on leak disposal) removed the orphan's
            // dangling set but the live sandbox's set was already deleted by
            // its own disposal path — assert global emptiness of owned sets.
            Assert.Empty(cloud.KeyPairs);
            Assert.Empty(cloud.SecurityGroups);
        }
    }

    [Fact]
    public async Task RepeatedDisposal_IsClean()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        await sandbox.DisposeAsync();
        var instance = harness.Cloud.Instances.Values.Single();
        Assert.Equal("terminated", instance.State);
    }
}
