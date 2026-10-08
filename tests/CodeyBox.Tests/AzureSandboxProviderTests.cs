using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.AzureSandboxPlugin;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Provider-level tests for the <c>azure</c> sandbox provider: full
/// acquire → exec → stage → dispose against a fake ARM endpoint plus a fake
/// SSH transport, disposal idempotence, ownership-scoped cleanup, ambiguous
/// create reconciliation, LRO polling validation, and failure classification.
/// No live cloud calls: all ARM responses come from <see cref="FakeAzureCloud"/>.
/// </summary>
public sealed class AzureSandboxProviderTests
{
    private const string OwnerId = "test-host";
    private const string OtherOwnerId = "other-host";
    private const string SubscriptionId = "sub-1";
    private const string ResourceGroup = "rg-1";

    // ------------------------------------------------------------------
    // Full lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Acquire_Exec_Stage_Dispose_FullLifecycle()
    {
        using var harness = NewHarness();
        var liveBefore = SandboxLiveCounter.Active;

        var hostDir = Directory.CreateTempSubdirectory("az-prov-host-").FullName;
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
                Network = SandboxNetworkPolicy.Denied,
            };

            var sandbox = await harness.Provider.CreateAsync(spec, CancellationToken.None);
            try
            {
                Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);
                Assert.Equal(liveBefore + 1, SandboxLiveCounter.Active);

                var result = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("echo", harness.Transport.LastArgv());

                var vmBody = harness.Cloud.VmPutBodies.Single();
                using var vmDoc = JsonDocument.Parse(vmBody);
                var props = vmDoc.RootElement.GetProperty("properties");
                Assert.Equal("Standard_B1s", props.GetProperty("hardwareProfile").GetProperty("vmSize").GetString());
                var image = props.GetProperty("storageProfile").GetProperty("imageReference");
                Assert.Equal("Canonical", image.GetProperty("publisher").GetString());
                Assert.Equal("22.04.20240101120000", image.GetProperty("version").GetString());
                var tags = vmDoc.RootElement.GetProperty("tags");
                Assert.Equal("true", tags.GetProperty("codeybox.managed").GetString());
                Assert.Equal(OwnerId, tags.GetProperty("codeybox.owner").GetString());
                Assert.False(string.IsNullOrWhiteSpace(tags.GetProperty("codeybox.request-id").GetString()));

                // Cloud API credentials stay host-only: the token never enters userData.
                var customData = props.GetProperty("osProfile").GetProperty("customData").GetString()!;
                var userData = Encoding.UTF8.GetString(Convert.FromBase64String(customData));
                Assert.DoesNotContain("test-token", userData, StringComparison.Ordinal);
                Assert.Contains("ssh-ed25519", userData, StringComparison.Ordinal);
                Assert.DoesNotContain("latest", image.GetProperty("version").GetString()!, StringComparison.OrdinalIgnoreCase);

                var stageIn = harness.Transport.StageInCalls.Single(c => c.RemotePath == "/work");
                Assert.Equal(hostDir, stageIn.HostPath);
            }
            finally
            {
                await sandbox.DisposeAsync();
                await sandbox.DisposeAsync();
            }

            Assert.Equal(liveBefore, SandboxLiveCounter.Active);
            Assert.Empty(harness.Cloud.Vms);
            Assert.Empty(harness.Cloud.Nics);
            Assert.Empty(harness.Cloud.Nsgs);
            Assert.Empty(harness.Cloud.Disks);
            Assert.Empty(harness.Provider.UnreconciledResources);
            Assert.False(Directory.Exists(harness.SshTempDir(sandbox.Id)));
            Assert.Single(harness.Cloud.DeletesFor("virtualMachines"));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublicIp_Allocated_And_SshTargetsIt_WhenConfigured()
    {
        using var harness = NewHarness(configure: o => o with { AllocatePublicIp = true });
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.Pips);
            Assert.Contains("20.30.40.", harness.TransportFactory.LastTarget, StringComparison.Ordinal);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Pips);
    }

    [Fact]
    public async Task SyncBack_Runs_Before_CloudTeardown()
    {
        using var harness = NewHarness();
        var hostDir = Directory.CreateTempSubdirectory("az-sync-").FullName;
        try
        {
            var events = new List<string>();
            harness.TransportFactory.DefaultOnStageOut = (remote, host) =>
            {
                lock (events) { events.Add("stageout:" + remote); }
            };
            harness.Cloud.OnDelete = (kind, name) =>
            {
                lock (events) { events.Add("delete:" + kind + ":" + name); }
            };
            var sandbox = await harness.Provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = string.Empty,
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = hostDir, ReadOnly = false }],
                },
                CancellationToken.None);
            await sandbox.DisposeAsync();

            var stageout = events.FindIndex(e => e.StartsWith("stageout:", StringComparison.Ordinal));
            var delete = events.FindIndex(e => e.StartsWith("delete:", StringComparison.Ordinal));
            Assert.True(stageout >= 0, "expected a writable-mount sync-back, saw: " + string.Join(",", events));
            Assert.True(delete >= 0, "expected cloud teardown, saw: " + string.Join(",", events));
            Assert.True(stageout < delete, "sync-back must precede cloud teardown, saw: " + string.Join(",", events));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Disabled / config validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disabled_Refuses()
    {
        using var harness = NewHarness(configure: o => o with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Requests);
    }

    [Theory]
    [InlineData("SubscriptionId")]
    [InlineData("ResourceGroupName")]
    [InlineData("Location")]
    [InlineData("VmSize")]
    [InlineData("VirtualNetworkName")]
    [InlineData("SubnetName")]
    [InlineData("ImagePublisher")]
    [InlineData("ImageOffer")]
    [InlineData("ImageSku")]
    [InlineData("ImageVersion")]
    public async Task MissingScopeSetting_Refuses(string property)
    {
        using var harness = NewHarness(configure: o => Blank(o, property));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Vms);

        static AzureSandboxOptions Blank(AzureSandboxOptions o, string property) => property switch
        {
            "SubscriptionId" => o with { SubscriptionId = string.Empty },
            "ResourceGroupName" => o with { ResourceGroupName = string.Empty },
            "Location" => o with { Location = string.Empty },
            "VmSize" => o with { VmSize = string.Empty },
            "VirtualNetworkName" => o with { VirtualNetworkName = string.Empty },
            "SubnetName" => o with { SubnetName = string.Empty },
            "ImagePublisher" => o with { ImagePublisher = string.Empty },
            "ImageOffer" => o with { ImageOffer = string.Empty },
            "ImageSku" => o with { ImageSku = string.Empty },
            "ImageVersion" => o with { ImageVersion = string.Empty },
            _ => o,
        };
    }

    [Fact]
    public async Task ImageVersion_Latest_Rejected()
    {
        using var harness = NewHarness(configure: o => o with { ImageVersion = "latest" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task SpecImageOverride_Latest_Rejected()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = "Canonical:offer:sku:latest" }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task SpecImageOverride_ExplicitVersion_Accepted()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = "Canonical:0001-com-ubuntu-server-jammy:22_04-lts-gen2:22.04.20240201000000" },
            CancellationToken.None);
        try
        {
            var body = harness.Cloud.VmPutBodies.Single();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("22.04.20240201000000",
                doc.RootElement.GetProperty("properties").GetProperty("storageProfile")
                    .GetProperty("imageReference").GetProperty("version").GetString());
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task GraphicalFlavor_Rejected()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty, Flavor = SandboxProfileFlavor.Graphical },
                CancellationToken.None));
    }

    [Fact]
    public async Task NamedNetworkProfile_Rejected()
    {
        using var harness = NewHarness();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Network = new SandboxNetworkPolicy { ProfileName = "restricted" },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(spec, CancellationToken.None));
        Assert.Empty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task PinnedBaselineRef_Rejected()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(
                new SandboxSpec { ImageReference = string.Empty, BaselineImageRef = "azure:tc-abc:some-image" },
                CancellationToken.None));
    }

    [Fact]
    public async Task CredentialMount_OutsideTmpfs_Rejected()
    {
        using var harness = NewHarness();
        var credDir = Directory.CreateTempSubdirectory("az-cred-").FullName;
        try
        {
            var spec = new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts =
                [
                    new SandboxMount
                    {
                        SandboxPath = SandboxConventions.CredentialsDir + "/agent.json",
                        HostPath = Path.Combine(credDir, "agent.json"),
                        ReadOnly = true,
                    },
                ],
            };
            await File.WriteAllTextAsync(Path.Combine(credDir, "agent.json"), "{}");
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                harness.Provider.CreateAsync(spec, CancellationToken.None));
            Assert.Empty(harness.Cloud.Vms);
        }
        finally
        {
            Directory.Delete(credDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidTmpfsPath_Rejected()
    {
        using var harness = NewHarness();
        var spec = new SandboxSpec
        {
            ImageReference = string.Empty,
            Mounts = [new SandboxMount { SandboxPath = "/ok/../evil", Tmpfs = true }],
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Provider.CreateAsync(spec, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // SSH pinning / transport behavior
    // ------------------------------------------------------------------

    [Fact]
    public async Task HostKeyMismatch_NeverAccepted_CleansUp()
    {
        using var harness = NewHarness();
        harness.TransportFactory.DefaultOnRun = _ =>
            throw new RemoteSshTransportException("host key verification failed for '10.0.0.5'.");
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("pinned-key", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.Vms);
        Assert.Empty(harness.Cloud.Nics);
        Assert.Empty(harness.Cloud.Nsgs);
    }

    [Fact]
    public async Task TransportLoss_DuringExec_IsUnavailable_NotSuccess()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            harness.Transport.OnRun = argv =>
                argv.Count > 0 && string.Equals(argv[0], "true", StringComparison.Ordinal)
                    ? new ProcessRunResult(0, string.Empty, string.Empty)
                    : throw new RemoteSshTransportException("connection reset by peer");
            await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "run"] }, CancellationToken.None));
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocalCancellation_DoesNotProve_RemoteStop_TeardownDeletesVm()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            harness.Transport.OnRun = argv =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return new ProcessRunResult(0, string.Empty, string.Empty);
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token));
            Assert.NotEmpty(harness.Cloud.Vms);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task OutputLimits_PassedThrough_ToTransport()
    {
        using var harness = NewHarness();
        int? seenStdout = null;
        int? seenStderr = null;
        harness.TransportFactory.DefaultOnRunWithLimits = (argv, stdout, stderr) =>
        {
            seenStdout = stdout;
            seenStderr = stderr;
            return new ProcessRunResult(0, "ok", string.Empty);
        };
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            await sandbox.ExecAsync(
                new SandboxExec { Argv = ["echo", "hi"], MaxStdoutBytes = 1024, MaxStderrBytes = 512 },
                CancellationToken.None);
            Assert.Equal(1024, seenStdout);
            Assert.Equal(512, seenStderr);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // Cancellation at provisioning boundaries
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelledBeforeCreate_SendsNothing()
    {
        using var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.DoesNotContain(harness.Cloud.Requests, r => r.StartsWith("PUT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledDuringPoll_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.SucceedAfterPolls = int.MaxValue;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, cts.Token));
        Assert.Empty(harness.Cloud.Vms);
        Assert.Empty(harness.Cloud.Nics);
        Assert.Empty(harness.Cloud.Nsgs);
    }

    // ------------------------------------------------------------------
    // Partial / ambiguous creates
    // ------------------------------------------------------------------

    [Fact]
    public async Task PartialCreate_DeletesEarlierResources()
    {
        using var harness = NewHarness();
        harness.Cloud.FailPutKinds.Add("networkInterfaces");
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("server-error", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.Vms);
        Assert.Empty(harness.Cloud.Nics);
        Assert.Empty(harness.Cloud.Nsgs);
        Assert.NotEmpty(harness.Cloud.DeletesFor("networkSecurityGroups"));
    }

    [Fact]
    public async Task AmbiguousCreate_ReconcilesByRequestIdentity_WithoutResubmit()
    {
        using var harness = NewHarness();
        harness.Cloud.StoreVmThenFailFirstPut = true;
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.Single(harness.Cloud.VmPutBodies);
            var body = harness.Cloud.VmPutBodies.Single();
            using var doc = JsonDocument.Parse(body);
            var requestId = doc.RootElement.GetProperty("tags").GetProperty("codeybox.request-id").GetString();
            var vm = Assert.Single(harness.Cloud.Vms.Values);
            Assert.Equal(requestId, vm.Tags["codeybox.request-id"]);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
        Assert.Empty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task AmbiguousCreate_ForeignResourceAtIdentity_Refuses()
    {
        using var harness = NewHarness();
        harness.Cloud.StoreForeignVmThenFailFirstPut = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Single(harness.Cloud.VmPutBodies);
    }

    [Fact]
    public async Task FailedProvisioningState_CleansUp()
    {
        using var harness = NewHarness();
        harness.Cloud.OperationFinalStatus = "Failed";
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Empty(harness.Cloud.Vms);
        Assert.Empty(harness.Cloud.Nics);
        Assert.Empty(harness.Cloud.Nsgs);
        Assert.Empty(harness.Provider.UnreconciledResources);
    }

    // ------------------------------------------------------------------
    // Cloud error classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(401, """{"error":{"code":"InvalidAuthenticationToken","message":"bad token"}}""", "unauthorized")]
    [InlineData(403, """{"error":{"code":"AuthorizationFailed","message":"forbidden"}}""", "unauthorized")]
    [InlineData(409, """{"error":{"code":"QuotaExceeded","message":"quota"}}""", "quota-exhausted")]
    [InlineData(400, """{"error":{"code":"SkuNotAvailable","message":"no capacity"}}""", "quota-exhausted")]
    [InlineData(500, """{"error":{"code":"InternalError","message":"boom"}}""", "server-error")]
    public async Task CloudErrors_Defer_WithClass(int status, string body, string errorClass)
    {
        using var harness = NewHarness();
        harness.Cloud.NextPutStatus = (HttpStatusCode)status;
        harness.Cloud.NextPutBody = body;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains(errorClass, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throttled_HonoursRetryAfter_WithinBound()
    {
        using var harness = NewHarness(configure: o => o with
        {
            PollIntervalMilliseconds = 50,
            MaxPollIntervalMilliseconds = 100,
        });
        harness.Cloud.NextPutStatus = (HttpStatusCode)429;
        harness.Cloud.NextPutBody = """{"error":{"code":"Throttled","message":"slow down"}}""";
        harness.Cloud.NextPutRetryAfterSeconds = 3600;
        var start = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("throttled", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - start < TimeSpan.FromSeconds(30),
            "a bounded Retry-After must not stall provisioning");
    }

    [Fact]
    public async Task AsyncOperation_OutsideScope_Refused()
    {
        using var harness = NewHarness();
        harness.Cloud.EvilAsyncOperationUrl = true;
        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
        Assert.Contains("invalid-response", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Cloud.Vms);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"value": [truncated""")]
    public async Task MalformedResponses_FailLoudly(string body)
    {
        using var harness = NewHarness();
        harness.Cloud.NextGetBodyOverride = body;
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponse_RejectedByCap()
    {
        using var harness = NewHarness(configure: o => o with { MaxResponseBytes = 1024 });
        harness.Cloud.NextGetBodyOverride = new string('x', 2048);
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            harness.Provider.CreateAsync(new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task EventualConsistency_AddressAppears_Late()
    {
        using var harness = NewHarness();
        harness.Cloud.NicNullAddressGets = 3;
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        try
        {
            Assert.NotEmpty(harness.Cloud.Nics);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoopingNextLink_FailsLoudly()
    {
        using var harness = NewHarness();
        harness.Cloud.LoopNextLink = true;
        await Assert.ThrowsAsync<AzureApiException>(() =>
            harness.Provider.ListAllManagedAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Restart adoption / orphan cleanup / ownership
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisposeLeaked_RemovesOwnedSet_LeavesForeign()
    {
        using var harness = NewHarness();
        var orphan = await harness.Cloud.SeedOrphanAsync(OwnerId, withPublicIp: false);
        var foreign = await harness.Cloud.SeedOrphanAsync(OtherOwnerId, withPublicIp: false);

        await harness.Provider.DisposeLeakedAsync(orphan, CancellationToken.None);

        Assert.DoesNotContain(orphan, harness.Cloud.NamesOf("virtualMachines"));
        Assert.DoesNotContain(orphan + "-nic", harness.Cloud.NamesOf("networkInterfaces"));
        Assert.DoesNotContain(orphan + "-nsg", harness.Cloud.NamesOf("networkSecurityGroups"));
        Assert.Contains(foreign, harness.Cloud.NamesOf("virtualMachines"));
        Assert.Empty(harness.Provider.UnreconciledResources);
    }

    [Fact]
    public async Task DisposeLeaked_ForeignVm_Refused_WithoutDelete()
    {
        using var harness = NewHarness();
        var foreign = await harness.Cloud.SeedOrphanAsync(OtherOwnerId, withPublicIp: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Provider.DisposeLeakedAsync(foreign, CancellationToken.None));
        Assert.Contains(foreign, harness.Cloud.NamesOf("virtualMachines"));
        Assert.DoesNotContain(
            harness.Cloud.Requests,
            r => r.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeLeaked_UnknownName_Throws()
    {
        using var harness = NewHarness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Provider.DisposeLeakedAsync("someone-else-vm", CancellationToken.None));
    }

    [Fact]
    public async Task ListAllManaged_ScopesByOwner_And_Prefix()
    {
        using var harness = NewHarness();
        var mine = await harness.Cloud.SeedOrphanAsync(OwnerId, withPublicIp: false);
        var foreign = await harness.Cloud.SeedOrphanAsync(OtherOwnerId, withPublicIp: false);
        harness.Cloud.SeedUnmanagedVm("unrelated-vm");

        var listed = await harness.Provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(listed, m => m.Name == mine);
        Assert.DoesNotContain(listed, m => m.Name == foreign);
        Assert.DoesNotContain(listed, m => m.Name == "unrelated-vm");
    }

    [Fact]
    public async Task CleanupFailure_Retained_NotErased()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        var vmName = sandbox.Id;
        harness.Cloud.FailDeleteNames.Add(vmName);
        await sandbox.DisposeAsync();

        Assert.Contains(
            harness.Provider.UnreconciledResources,
            kvp => kvp.Key.EndsWith("/" + vmName, StringComparison.Ordinal));
        Assert.NotEmpty(harness.Cloud.Vms);
    }

    [Fact]
    public async Task NeverDeletes_ResourceGroup_VNet_Or_Subnet()
    {
        using var harness = NewHarness();
        var sandbox = await harness.Provider.CreateAsync(
            new SandboxSpec { ImageReference = string.Empty }, CancellationToken.None);
        await sandbox.DisposeAsync();
        var orphan = await harness.Cloud.SeedOrphanAsync(OwnerId, withPublicIp: true);
        await harness.Provider.DisposeLeakedAsync(orphan, CancellationToken.None);

        foreach (var request in harness.Cloud.Requests.Where(r => r.StartsWith("DELETE ", StringComparison.Ordinal)))
        {
            var path = request["DELETE ".Length..].Split('?')[0];
            Assert.DoesNotContain("/resourceGroups/rg-1\"", path, StringComparison.Ordinal);
            Assert.False(
                path.EndsWith("/resourceGroups/rg-1", StringComparison.OrdinalIgnoreCase),
                "must never delete the caller-owned resource group: " + path);
            Assert.DoesNotContain("/virtualNetworks/", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/subnets/", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------
    // Registration / classification
    // ------------------------------------------------------------------

    [Fact]
    public void Registration_Kind_Isolation_Capabilities()
    {
        using var harness = NewHarness();
        Assert.Equal("azure", harness.Provider.Name);
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, harness.Provider.IsolationLevel);
        Assert.Contains(SandboxCapabilities.Teardown, harness.Provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.BaselineBake, harness.Provider.DeclaredCapabilities);
        Assert.True(harness.Provider.MightOwnSandbox("codeybox-abc", hostId: null));
        Assert.False(harness.Provider.MightOwnSandbox("other-abc", hostId: null));
    }

    [Fact]
    public void EgressClassification_StaysNotEnforced()
    {
        Assert.Equal(
            EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement("azure"));
        Assert.False(SandboxEgressPolicy.IsEnforced("azure"));
    }

    [Fact]
    public void PluginAttribute_Present()
    {
        var attribute = typeof(AzureSandboxProvider).GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false);
        var single = Assert.Single(attribute);
        Assert.Equal(AzureSandboxOptions.PluginId, ((CodeyBoxPluginAttribute)single).Id);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(Func<AzureSandboxOptions, AzureSandboxOptions>? configure)
        {
            Cloud = new FakeAzureCloud();
            _http = new HttpClient(Cloud) { Timeout = Timeout.InfiniteTimeSpan };
            TransportFactory = new FakeTransportFactory();
            var options = new AzureSandboxOptions
            {
                Enabled = true,
                ManagementUrl = "http://localhost/",
                SubscriptionId = SubscriptionId,
                ResourceGroupName = ResourceGroup,
                Location = "westeurope",
                VmSize = "Standard_B1s",
                VirtualNetworkName = "vnet-1",
                SubnetName = "subnet-1",
                ImagePublisher = "Canonical",
                ImageOffer = "0001-com-ubuntu-server-jammy",
                ImageSku = "22_04-lts-gen2",
                ImageVersion = "22.04.20240101120000",
                OwnerId = "test-host",
                OrchestratorSshCidrs = ["203.0.113.0/24"],
                AllowUnsafeHttp = true,
                PollIntervalMilliseconds = 50,
                MaxPollIntervalMilliseconds = 100,
                ReadyTimeoutSeconds = 30,
                SshReadyTimeoutSeconds = 10,
                ProvisioningRecheckSeconds = 5,
                HttpTimeoutSeconds = 30,
            };
            Options = configure is null ? options : configure(options);
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AZURE_ACCESS_TOKEN"] = "test-token",
            };
            Provider = new AzureSandboxProvider(
                () => Options,
                _http,
                new FakeKeyGenerator(),
                TransportFactory,
                name => env.TryGetValue(name, out var value) ? value : null,
                TimeProvider.System,
                NullLogger.Instance);
        }

        public FakeAzureCloud Cloud { get; }

        public FakeTransportFactory TransportFactory { get; }

        public FakeTransport Transport => TransportFactory.Created.Last();

        public AzureSandboxOptions Options { get; }

        public AzureSandboxProvider Provider { get; }

        public string SshTempDir(string vmName)
        {
            var suffix = vmName["codeybox-".Length..];
            return Path.Combine(Path.GetTempPath(), "codeybox-azure-" + suffix);
        }

        public void Dispose()
        {
            Provider.Dispose();
            _http.Dispose();
        }
    }

    private static Harness NewHarness(Func<AzureSandboxOptions, AzureSandboxOptions>? configure = null) =>
        new(configure);

    private sealed class FakeKeyGenerator : IAzureKeyGenerator
    {
        public Task<AzureClientKeyMaterial> GenerateClientKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = comment; _ = ct;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "id-ed25519-test");
            File.WriteAllText(path, "fake-private");
            return Task.FromResult(new AzureClientKeyMaterial(path, "ssh-ed25519 " + new string('A', 64)));
        }

        public Task<AzureHostKeyMaterial> GenerateHostKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct)
        {
            _ = keygenBinary; _ = directory; _ = comment; _ = ct;
            return Task.FromResult(new AzureHostKeyMaterial(
                "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n",
                "ssh-ed25519 " + new string('B', 64)));
        }
    }

    private sealed class FakeTransportFactory : IAzureTransportFactory
    {
        public List<FakeTransport> Created { get; } = [];
        public string LastTarget { get; private set; } = string.Empty;
        public Func<IReadOnlyList<string>, ProcessRunResult>? DefaultOnRun { get; set; }
        public Func<IReadOnlyList<string>, int?, int?, ProcessRunResult>? DefaultOnRunWithLimits { get; set; }
        public Action<string, string>? DefaultOnStageOut { get; set; }

        public IRemoteHostTransport Create(AzureSshTransportSpec spec)
        {
            LastTarget = spec.SshTarget;
            var transport = new FakeTransport();
            if (DefaultOnRun is not null)
                transport.OnRun = DefaultOnRun;
            if (DefaultOnRunWithLimits is not null)
                transport.OnRunWithLimits = DefaultOnRunWithLimits;
            if (DefaultOnStageOut is not null)
                transport.OnStageOut = DefaultOnStageOut;
            Created.Add(transport);
            return transport;
        }
    }

    private sealed class FakeTransport : IRemoteHostTransport
    {
        public string DiagnosticId => "fake";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<(string HostPath, string RemotePath)> StageInCalls { get; } = [];
        public List<(string RemotePath, string HostPath)> StageOutCalls { get; } = [];
        public Func<IReadOnlyList<string>, ProcessRunResult> OnRun { get; set; } =
            _ => new ProcessRunResult(0, "ok", string.Empty);
        public Func<IReadOnlyList<string>, int?, int?, ProcessRunResult>? OnRunWithLimits { get; set; }
        public Action<string, string>? OnStageOut { get; set; }

        public string LastArgv() => string.Join(" ", Calls.Last());

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
            _ = stdin; _ = ct;
            _ = stdoutChunkCallback; _ = stderrChunkCallback;
            _ = killOnOutputLimit;
            Calls.Add(argv.ToArray());
            if (OnRunWithLimits is not null)
                return Task.FromResult(OnRunWithLimits(argv, maxStdoutBytes, maxStderrBytes));
            return Task.FromResult(OnRun(argv));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
        {
            _ = ct;
            StageInCalls.Add((hostPath, remotePath));
            return Task.CompletedTask;
        }

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            _ = ct;
            StageOutCalls.Add((remotePath, hostPath));
            OnStageOut?.Invoke(remotePath, hostPath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResource(
        string name,
        Dictionary<string, string> tags,
        string provisioningState = "Succeeded")
    {
        public string Name { get; } = name;
        public Dictionary<string, string> Tags { get; } = tags;
        public string ProvisioningState { get; set; } = provisioningState;
        public string? AttachedTo { get; set; }
        public string? ExtraAddress { get; set; }
        public int MissingGets;
    }

    private sealed class FakeAzureCloud : HttpMessageHandler
    {
        public ConcurrentDictionary<string, FakeResource> Vms { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeResource> Nics { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeResource> Nsgs { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeResource> Pips { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, FakeResource> Disks { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public List<string> VmPutBodies { get; } = [];
        public List<string> AllBodies { get; } = [];
        public HashSet<string> FailPutKinds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailDeleteIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailDeleteNames { get; } = new(StringComparer.Ordinal);
        public HttpStatusCode? NextPutStatus { get; set; }
        public string NextPutBody { get; set; } = """{"error":{"code":"InternalError","message":"boom"}}""";
        public int NextPutRetryAfterSeconds { get; set; }
        public string? NextGetBodyOverride { get; set; }
        public int SucceedAfterPolls { get; set; } = 1;
        public string OperationFinalStatus { get; set; } = "Succeeded";
        public bool EvilAsyncOperationUrl { get; set; }
        public bool LoopNextLink { get; set; }
        public bool StoreVmThenFailFirstPut { get; set; }
        public bool StoreForeignVmThenFailFirstPut { get; set; }
        public int NicNullAddressGets { get; set; }
        public Action<string, string>? OnDelete { get; set; }
        private int _seq;
        private int _polls;
        private bool _firstVmPutFailed;

        public IEnumerable<string> VmIds => Vms.Keys.ToList();

        public IEnumerable<string> NamesOf(string kind) => kind switch
        {
            "virtualMachines" => Vms.Values.Select(v => v.Name).ToList(),
            "networkInterfaces" => Nics.Values.Select(v => v.Name).ToList(),
            "networkSecurityGroups" => Nsgs.Values.Select(v => v.Name).ToList(),
            "publicIPAddresses" => Pips.Values.Select(v => v.Name).ToList(),
            "disks" => Disks.Values.Select(v => v.Name).ToList(),
            _ => [],
        };

        public List<string> DeletesFor(string kind) =>
            Requests.Where(r => r.StartsWith("DELETE ", StringComparison.Ordinal)
                && r.Contains("/" + kind + "/", StringComparison.Ordinal)).ToList();

        public async Task<string> SeedOrphanAsync(string owner, bool withPublicIp)
        {
            var name = "codeybox-orphan" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
            var tags = OwnerTags(owner);
            Vms[name] = new FakeResource(name, tags);
            Nics[name + "-nic"] = new FakeResource(name + "-nic", tags) { ExtraAddress = "10.0.0.9" };
            Nsgs[name + "-nsg"] = new FakeResource(name + "-nsg", tags);
            if (withPublicIp)
                Pips[name + "-pip"] = new FakeResource(name + "-pip", tags) { ExtraAddress = "20.30.40.60" };
            Disks[name + "-osdisk"] = new FakeResource(name + "-osdisk", tags);
            await Task.CompletedTask;
            return name;
        }

        public void SeedUnmanagedVm(string name) =>
            Vms[name] = new FakeResource(name, []);

        private static Dictionary<string, string> OwnerTags(string owner) => new(StringComparer.Ordinal)
        {
            ["codeybox.managed"] = "true",
            ["codeybox.owner"] = owner,
            ["codeybox.request-id"] = "seeded",
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) { Requests.Add(request.Method.Method + " " + path); }
            lock (AllBodies) { if (body.Length > 0) AllBodies.Add(body); }

            if (path.Contains("/operationStatus/", StringComparison.Ordinal))
                return HandlePollFor(path);

            var segments = path.Trim('/').Split('/');
            // .../resourceGroups/{rg}/providers/{ns}/{type}[/{name}]
            var providers = Array.IndexOf(segments, "providers");
            if (providers < 0 || providers + 2 >= segments.Length)
                return Status(HttpStatusCode.NotFound, """{"error":{"code":"NotFound","message":"unknown"}}""");
            var ns = segments[providers + 1];
            var type = segments[providers + 2];
            var name = providers + 3 < segments.Length ? Uri.UnescapeDataString(segments[providers + 3]) : null;

            if (name is null)
            {
                if (request.Method != HttpMethod.Get)
                    return Status(HttpStatusCode.MethodNotAllowed, "{}");
                return HandleList(ns, type);
            }

            var store = StoreFor(ns, type);
            if (store is null)
                return Status(HttpStatusCode.NotFound, "{}");

            if (request.Method == HttpMethod.Put)
                return HandlePut(store, ns, type, name, body);
            if (request.Method == HttpMethod.Get)
                return HandleGet(store, ns, type, name);
            if (request.Method == HttpMethod.Delete)
                return HandleDelete(store, ns, type, name);
            return Status(HttpStatusCode.NotFound, "{}");
        }

        private ConcurrentDictionary<string, FakeResource>? StoreFor(string ns, string type) =>
            (ns, type) switch
            {
                ("Microsoft.Compute", "virtualMachines") => Vms,
                ("Microsoft.Network", "networkInterfaces") => Nics,
                ("Microsoft.Network", "networkSecurityGroups") => Nsgs,
                ("Microsoft.Network", "publicIPAddresses") => Pips,
                ("Microsoft.Compute", "disks") => Disks,
                _ => null,
            };

        private HttpResponseMessage HandlePut(
            ConcurrentDictionary<string, FakeResource> store, string ns, string type, string name, string body)
        {
            if (NextPutStatus is { } status)
            {
                var failure = Status(status, NextPutBody);
                if (status == (HttpStatusCode)429 && NextPutRetryAfterSeconds > 0)
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(NextPutRetryAfterSeconds));
                NextPutStatus = null;
                return failure;
            }
            if (FailPutKinds.Contains(type))
                return Status(HttpStatusCode.InternalServerError, """{"error":{"code":"InternalError","message":"injected"}}""");
            using var doc = JsonDocument.Parse(body);
            var tags = doc.RootElement.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object
                ? tagsEl.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (StoreForeignVmThenFailFirstPut && type == "virtualMachines" && !_firstVmPutFailed)
            {
                _firstVmPutFailed = true;
                var foreign = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["codeybox.managed"] = "true",
                    ["codeybox.owner"] = OtherOwnerId,
                    ["codeybox.request-id"] = "someone-else",
                };
                store[name] = new FakeResource(name, foreign);
                lock (VmPutBodies) { VmPutBodies.Add(body); }
                return Status(HttpStatusCode.InternalServerError, """{"error":{"code":"InternalError","message":"lost"}}""");
            }
            if (StoreVmThenFailFirstPut && type == "virtualMachines" && !_firstVmPutFailed)
            {
                _firstVmPutFailed = true;
                store[name] = new FakeResource(name, new Dictionary<string, string>(tags, StringComparer.Ordinal));
                lock (VmPutBodies) { VmPutBodies.Add(body); }
                return Status(HttpStatusCode.GatewayTimeout, """{"error":{"code":"GatewayTimeout","message":"response lost"}}""");
            }
            var resource = new FakeResource(name, new Dictionary<string, string>(tags, StringComparer.Ordinal));
            if (type == "networkInterfaces")
                resource.ExtraAddress = NicNullAddressGets > 0 ? null : "10.0.0.5";
            if (type == "publicIPAddresses")
                resource.ExtraAddress = "20.30.40.50";
            if (type == "virtualMachines" && !Disks.ContainsKey(name + "-osdisk"))
                Disks[name + "-osdisk"] = new FakeResource(name + "-osdisk", new Dictionary<string, string>(tags, StringComparer.Ordinal));
            store[name] = resource;
            if (type == "virtualMachines")
            {
                lock (VmPutBodies) { VmPutBodies.Add(body); }
            }
            var response = Json(ResourceJson(ns, type, name, resource), HttpStatusCode.Created);
            if (!EvilAsyncOperationUrl)
            {
                var opId = "op-" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
                response.Headers.Add("Azure-AsyncOperation",
                    $"http://localhost/subscriptions/{SubscriptionId}/operationStatus/{opId}?api-version=2024-11-01");
            }
            else
            {
                response.Headers.Add("Azure-AsyncOperation", "https://attacker.example/steal?token=x");
            }
            return response;
        }

        private HttpResponseMessage HandleGet(
            ConcurrentDictionary<string, FakeResource> store, string ns, string type, string name)
        {
            if (NextGetBodyOverride is { } overrideBody)
            {
                NextGetBodyOverride = null;
                return Json(overrideBody);
            }
            if (!store.TryGetValue(name, out var resource))
                return Status(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"missing"}}""");
            if (type == "networkInterfaces" && resource.ExtraAddress is null && NicNullAddressGets > 0)
            {
                NicNullAddressGets--;
                if (NicNullAddressGets == 0)
                    resource.ExtraAddress = "10.0.0.5";
                return Json(ResourceJson(ns, type, name, resource));
            }
            if (resource.MissingGets > 0)
            {
                resource.MissingGets--;
                return Status(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"not yet"}}""");
            }
            return Json(ResourceJson(ns, type, name, resource));
        }

        private HttpResponseMessage HandleDelete(
            ConcurrentDictionary<string, FakeResource> store, string ns, string type, string name)
        {
            OnDelete?.Invoke(type, name);
            var id = $"http://localhost/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/{ns}/{type}/{name}";
            if (FailDeleteIds.Contains(id) || FailDeleteNames.Contains(name))
                return Status(HttpStatusCode.InternalServerError, """{"error":{"code":"InternalError","message":"delete failed"}}""");
            if (!store.TryRemove(name, out _))
                return Status(HttpStatusCode.NotFound, "{}");
            if (type == "virtualMachines")
            {
                Nics.TryRemove(name + "-nic", out _);
                Pips.TryRemove(name + "-pip", out _);
            }
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            var opId = "del-" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
            response.Headers.Add("Azure-AsyncOperation",
                $"http://localhost/subscriptions/{SubscriptionId}/operationStatus/{opId}?api-version=2024-11-01");
            response.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return response;
        }

        private HttpResponseMessage HandlePollFor(string operationPath)
        {
            // Deletion polls always converge: cleanup must not stall on them.
            if (operationPath.Contains("/del-", StringComparison.Ordinal))
                return Json("""{"status":"Succeeded"}""");
            var seen = Interlocked.Increment(ref _polls);
            return Json(
                seen >= SucceedAfterPolls
                    ? $"{{\"status\":\"{OperationFinalStatus}\"}}"
                    : """{"status":"InProgress"}""");
        }

        private HttpResponseMessage HandleList(string ns, string type)
        {
            var store = StoreFor(ns, type);
            if (store is null)
                return Status(HttpStatusCode.NotFound, "{}");
            var items = string.Join(",", store.Values.Select(v =>
                ResourceJson(ns, type, v.Name, v)));
            var nextLink = LoopNextLink
                ? $",\"nextLink\":\"http://localhost/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/{ns}/{type}?api-version=x&skip=1\""
                : string.Empty;
            return Json("{\"value\":[" + items + "]" + nextLink + "}");
        }

        private static string ResourceJson(string ns, string type, string name, FakeResource resource)
        {
            var id = $"http://localhost/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/{ns}/{type}/{name}";
            var tags = string.Join(",", resource.Tags.Select(kvp =>
                $"\"{kvp.Key}\":\"{kvp.Value}\""));
            var props = (ns, type) switch
            {
                ("Microsoft.Compute", "virtualMachines") =>
                    $"\"provisioningState\":\"{resource.ProvisioningState}\"," +
                    $"\"hardwareProfile\":{{\"vmSize\":\"Standard_B1s\"}}," +
                    $"\"networkProfile\":{{\"networkInterfaces\":[{{\"id\":\"{id.Replace("virtualMachines/" + name, "networkInterfaces/" + name + "-nic")}\"}}]}},",
                ("Microsoft.Network", "networkInterfaces") =>
                    resource.ExtraAddress is null
                        ? $"\"provisioningState\":\"{resource.ProvisioningState}\",\"ipConfigurations\":[],"
                        : $"\"provisioningState\":\"{resource.ProvisioningState}\"," +
                          $"\"ipConfigurations\":[{{\"properties\":{{\"privateIPAddress\":\"{resource.ExtraAddress}\"}}}}],",
                ("Microsoft.Network", "publicIPAddresses") =>
                    $"\"provisioningState\":\"{resource.ProvisioningState}\"," +
                    $"\"ipAddress\":\"{resource.ExtraAddress ?? "20.30.40.50"}\",",
                ("Microsoft.Compute", "disks") =>
                    $"\"provisioningState\":\"{resource.ProvisioningState}\",\"diskState\":\"Unattached\",",
                _ => $"\"provisioningState\":\"{resource.ProvisioningState}\",",
            };
            return $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"location\":\"westeurope\",\"tags\":{{{tags}}},\"properties\":{{{props.TrimEnd(',')}}}}}";
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage Status(HttpStatusCode status, string body) => Json(body, status);
    }
}
