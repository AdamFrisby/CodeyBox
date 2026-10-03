using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.ModalPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the Modal Sandboxes provider plugin:
/// the kind is constructible and selectable by placement (shared across
/// members naming it); it is classified NotEnforced regardless of what it
/// reports; declared capabilities match the implementation and placement
/// refuses work requiring anything undeclared; service-side failures are
/// infrastructure (never diff verdicts); concurrent use never exceeds member
/// capacity and live load is visible to placement; credentials resolve from
/// the environment chain, never from config files.
/// </summary>
public sealed class ModalSandboxProviderTests
{
    private const string TestTokenId = "test-token-id";
    private const string TestTokenSecret = "test-token-secret-never-in-config";

    private static ModalSandboxOptions TestOptions() => new()
    {
        TokenId = TestTokenId,
        TokenSecret = TestTokenSecret,
        ApiBaseUrl = "https://api.modal.com",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        ExecPollInterval = TimeSpan.FromMilliseconds(10),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static ModalSandboxProvider NewProvider(FakeModalHandler handler, ModalSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new ModalSandboxProvider(
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
    public async Task CreateAsync_ProvisionsSandbox_StagesMounts_AndTerminatesOnDispose()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            var hostFile = Path.Combine(hostDir, "input.txt");
            await File.WriteAllTextAsync(hostFile, "hello-mount");
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = hostDir }],
            };

            await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
            Assert.StartsWith("sb_", sandbox.Id, StringComparison.Ordinal);

            var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v1/sandboxes");
            Assert.Equal(TestTokenId, create.TokenId);
            Assert.Equal(TestTokenSecret, create.TokenSecret);
            foreach (var request in handler.Requests)
            {
                Assert.DoesNotContain(TestTokenSecret, request.Path, StringComparison.Ordinal);
                Assert.DoesNotContain(TestTokenSecret, request.Body, StringComparison.Ordinal);
            }

            using (var doc = JsonDocument.Parse(create.Body))
            {
                Assert.StartsWith("codeybox-", doc.RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);
                var metadata = doc.RootElement.GetProperty("metadata");
                Assert.Equal("true", metadata.GetProperty("codeybox-managed").GetString());
                Assert.Equal("modal", metadata.GetProperty("codeybox-provider").GetString());
            }

            var write = Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Post && r.Path.EndsWith("/files:write", StringComparison.Ordinal));
            var written = Convert.FromBase64String(
                JsonDocument.Parse(write.Body).RootElement.GetProperty("content_b64").GetString()!);
            Assert.Equal("hello-mount", Encoding.UTF8.GetString(written));

            Assert.Equal(1, provider.ActiveSandboxCount);

            await sandbox.DisposeAsync();
            Assert.Equal(0, provider.ActiveSandboxCount);

            Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Post && r.Path.EndsWith("/terminate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_BaselineImageRef_RestoresPinnedSnapshot()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        var spec = BasicSpec() with { BaselineImageRef = "snap-pinned-1" };
        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v1/sandboxes");
        using var doc = JsonDocument.Parse(create.Body);
        Assert.Equal("snap-pinned-1", doc.RootElement.GetProperty("snapshot_id").GetString());
    }

    [Fact]
    public async Task ExecAsync_StreamsOutputChunks_AndReturnsExitCode()
    {
        var handler = new FakeModalHandler();
        handler.ExecPollScript.Enqueue(new FakeModalHandler.PollScriptStep("out-", "err-", Completed: false, Exit: 0));
        handler.ExecPollScript.Enqueue(new FakeModalHandler.PollScriptStep("data", "data", Completed: true, Exit: 3));
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();
        var result = await sandbox.ExecAsync(
            new SandboxExec
            {
                Argv = ["echo", "hi"],
                StdoutChunkCallback = stdoutChunks.Add,
                StderrChunkCallback = stderrChunks.Add,
            },
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out-data", result.Stdout);
        Assert.Equal("err-data", result.Stderr);
        Assert.Equal("out-data", string.Concat(stdoutChunks));
        Assert.Equal("err-data", string.Concat(stderrChunks));
        Assert.False(result.ExecutionUnavailable);
    }

    [Fact]
    public async Task ExecAsync_Cancellation_KillsExecution_AndThrowsCancelled()
    {
        var handler = new FakeModalHandler { GatePolls = true };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var exec = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);
        await Task.Delay(250, CancellationToken.None);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exec);
        Assert.NotEmpty(handler.KilledExecs);
    }

    [Fact]
    public async Task ExecAsync_OutputLimitExceeded_Kills_AndSetsFlags()
    {
        var handler = new FakeModalHandler();
        handler.ExecutionResponder = (_, _) => (0, new string('x', 4096), string.Empty);
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(
            new SandboxExec { Argv = ["yes"], MaxStdoutBytes = 64 },
            CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.ExecutionUnavailable);
        Assert.NotEmpty(handler.KilledExecs);
    }

    [Fact]
    public async Task ExecAsync_StreamingWithoutKill_RetainsBoundedTail()
    {
        var handler = new FakeModalHandler();
        var full = string.Concat(Enumerable.Repeat("0123456789", 20));
        handler.ExecutionResponder = (_, _) => (0, full, string.Empty);
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var streamed = new StringBuilder();
        var result = await sandbox.ExecAsync(
            new SandboxExec
            {
                Argv = ["seq", "1", "200"],
                StreamOutputWithoutKill = true,
                KillOnOutputLimit = false,
                MaxRetainedStdoutBytes = 16,
                StdoutChunkCallback = chunk => streamed.Append(chunk),
            },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.ExecutionUnavailable);
        Assert.Equal(full, streamed.ToString());
        Assert.True(result.Stdout.Length <= 16);
        Assert.EndsWith(result.Stdout, full, StringComparison.Ordinal);
        Assert.Empty(handler.KilledExecs);
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_StagedNotInline()
    {
        const string Secret = "s3cr3t-value-that-must-not-appear";
        var handler = new FakeModalHandler();
        handler.ExecutionResponder = (_, _) => (0, "ok", string.Empty);
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(
            new SandboxExec
            {
                Argv = ["printenv", "SECRET_KEY"],
                ExtraEnvironment = new Dictionary<string, string> { ["SECRET_KEY"] = Secret },
                EnvironmentContainsSecrets = true,
            },
            CancellationToken.None);

        Assert.True(result.Success);
        foreach (var request in handler.Requests)
        {
            Assert.DoesNotContain(Secret, request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, request.Path, StringComparison.Ordinal);
        }

        Assert.Contains(
            handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.EndsWith("/files:write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAndPreserve_SnapshotsThenTerminates_DisposeIsNoop()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var modal = Assert.IsType<ModalSandbox>(sandbox);
        await modal.StopAndPreserveAsync(CancellationToken.None);

        Assert.Equal("snap_000001", modal.LastSnapshotId);
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/snapshot", StringComparison.Ordinal));

        await sandbox.DisposeAsync();
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/terminate", StringComparison.Ordinal));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task DisposeAsync_WritableMounts_SyncedBackToHost()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, "notes.txt"), "v1");
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = hostDir, ReadOnly = false }],
            };

            await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
            var modal = Assert.IsType<ModalSandbox>(sandbox);
            await modal.WriteFileAsync("/data/notes.txt", "v2", CancellationToken.None);
            await sandbox.DisposeAsync();

            Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(hostDir, "notes.txt")));
            Assert.Single(handler.Requests, r => r.Path.EndsWith("/terminate", StringComparison.Ordinal));

            // The teardown-internal exec bypass is narrow: the public surface
            // still refuses once disposed.
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAndReadFile_RoundTrips()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var modal = Assert.IsType<ModalSandbox>(sandbox);
        await modal.WriteFileAsync("/work/note.txt", "round-trip", CancellationToken.None);
        Assert.Equal("round-trip", await modal.ReadFileAsync("/work/note.txt", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => modal.WriteFileAsync("relative/path", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => modal.ReadFileAsync("/work/../escape", CancellationToken.None));
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementation()
    {
        var provider = NewProvider(new FakeModalHandler());
        Assert.Equal([SandboxCapabilities.Teardown], provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.BaselineBake, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.SuspendResume, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, provider.DeclaredCapabilities);
    }

    [Fact]
    public async Task Placement_UndeclaredCapability_RefusedNamingCapability()
    {
        var provider = NewProvider(new FakeModalHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("mo", "modal", capabilities: [SandboxCapabilities.BaselineBake])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.BaselineBake], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));
        Assert.Contains(SandboxCapabilities.BaselineBake, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placement_DeclaredCapability_PlacesOnModal()
    {
        var provider = NewProvider(new FakeModalHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("mo", "modal", capabilities: [SandboxCapabilities.Teardown])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.Teardown], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("sb_", sandbox.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginCatalog_AcceptsKind_SharesInstance_RegardlessOfOrder()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);
        var catalog = new PluginSandboxProviderCatalog([("codeybox.modal", provider)]);
        Assert.True(catalog.IsPluginKind("modal"));
        Assert.True(catalog.TryGetProvider("MODAL ", out var resolved));
        Assert.Same(provider, resolved);

        var builds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var registry = new SandboxProviderRegistry(
            kind =>
            {
                if (catalog.TryGetProvider(kind, out var pluginProvider))
                {
                    builds[kind] = builds.TryGetValue(kind, out var count) ? count + 1 : 1;
                    return pluginProvider;
                }

                throw new InvalidOperationException($"Unregistered kind '{kind}'.");
            },
            pluginKinds: catalog.Kinds);

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", "modal"));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "MODAL "));
        Assert.Same(provider, first);
        Assert.Same(first, second);
        Assert.Equal(1, builds["modal"]);
    }

    [Fact]
    public void PluginAttribute_DeclaresCompatibleHostApi()
    {
        var attribute = typeof(ModalSandboxProvider)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();

        Assert.Equal(ModalSandboxOptions.PluginId, attribute.Id);
        Assert.True(
            Version.TryParse(attribute.MinHostApiVersion, out _),
            $"MinHostApiVersion '{attribute.MinHostApiVersion}' must parse so the host gate can compare it.");
    }

    [Fact]
    public void Kind_ClassifiedNotEnforced()
    {
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("modal"));
        Assert.False(SandboxEgressPolicy.IsEnforced("modal"));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("modal"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfiledWork_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeModalHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("mo", "modal")),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", BasicSpec()),
            CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ProfiledSpec_RefusedAtSink()
    {
        var provider = NewProvider(new FakeModalHandler());
        var spec = BasicSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "llm" } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("modal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_GraphicalSpec_Refused()
    {
        var provider = NewProvider(new FakeModalHandler());
        var spec = BasicSpec() with { Flavor = SandboxProfileFlavor.Graphical };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RecoveryLease_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeModalHandler());
        var spec = BasicSpec() with
        {
            RecoveryLease = new SandboxRecoveryLease("modal", "sb_000001", "token"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("modal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_MissingMountSource_SurfacesForOrchestratorRetry()
    {
        var provider = NewProvider(new FakeModalHandler());
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount
            {
                SandboxPath = "/data",
                HostPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            }],
        };

        await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_CredentialTmpfs_Refused()
    {
        var provider = NewProvider(new FakeModalHandler());
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/run/codeybox/creds", Tmpfs = true }],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("environment variables", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialMountPath_MatchesHostConvention()
    {
        Assert.Equal(SandboxConventions.CredentialsDir, ModalSandboxProvider.CredentialMountPath);
    }

    [Fact]
    public void ResolveCredentials_MissingEnvironment_FailsClosedNamingVariable()
    {
        var idVar = "CODEYBOX_TEST_MODAL_ID_" + Guid.NewGuid().ToString("N");
        var secretVar = "CODEYBOX_TEST_MODAL_SECRET_" + Guid.NewGuid().ToString("N");
        var provider = NewProvider(new FakeModalHandler());
        var opts = TestOptions() with
        {
            TokenId = null,
            TokenSecret = null,
            TokenIdEnvironmentVariable = idVar,
            TokenSecretEnvironmentVariable = secretVar,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveCredentials(opts));
        Assert.Contains(idVar, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCredentials_EnvironmentChain_UsedAtUseTime()
    {
        var idVar = "CODEYBOX_TEST_MODAL_ID_" + Guid.NewGuid().ToString("N");
        var secretVar = "CODEYBOX_TEST_MODAL_SECRET_" + Guid.NewGuid().ToString("N");
        var provider = NewProvider(new FakeModalHandler());
        var opts = TestOptions() with
        {
            TokenId = null,
            TokenSecret = null,
            TokenIdEnvironmentVariable = idVar,
            TokenSecretEnvironmentVariable = secretVar,
        };

        try
        {
            Environment.SetEnvironmentVariable(idVar, "rotated-id");
            Environment.SetEnvironmentVariable(secretVar, "rotated-secret");
            var credentials = provider.ResolveCredentials(opts);
            Assert.Equal("rotated-id", credentials.TokenId);
            Assert.Equal("rotated-secret", credentials.TokenSecret);

            Environment.SetEnvironmentVariable(idVar, "rotated-again");
            Assert.Equal("rotated-again", provider.ResolveCredentials(opts).TokenId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(idVar, null);
            Environment.SetEnvironmentVariable(secretVar, null);
        }
    }

    [Fact]
    public void CredentialOptions_CarryNoSecretsByDefault()
    {
        var defaults = new ModalSandboxOptions();
        Assert.Null(defaults.TokenId);
        Assert.Null(defaults.TokenSecret);

        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:codeybox.modal:AppName"] = "custom",
            })
            .Build()
            .GetSection("CodeyBox:Plugins:codeybox.modal");
        var bound = ModalSandboxOptions.FromConfiguration(section);
        Assert.Null(bound.TokenId);
        Assert.Null(bound.TokenSecret);
        Assert.Equal("custom", bound.AppName);
    }

    [Fact]
    public async Task CreateAsync_Unauthorised_DefersAsInfrastructure()
    {
        var handler = new FakeModalHandler { FailCreateStatus = HttpStatusCode.Unauthorized };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("modal", ex.Provider);
        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_Throttled_DefersWithLongerBackoff()
    {
        var handler = new FakeModalHandler
        {
            FailCreateStatus = (HttpStatusCode)429,
            FailRetryAfterSeconds = 300,
        };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("throttled", ex.ErrorClass);
        Assert.True(ex.RecheckIn >= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task CreateAsync_QuotaExhausted_DefersAsInfrastructure()
    {
        var handler = new FakeModalHandler { FailCreateStatus = HttpStatusCode.PaymentRequired };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("quota-exhausted", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
    }

    [Fact]
    public async Task CreateAsync_Unreachable_DefersAsInfrastructure()
    {
        var handler = new FakeModalHandler { ThrowOnCreate = true };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unreachable", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ExecAsync_ServiceError_IsUnavailable_NotDiffVerdict()
    {
        var handler = new FakeModalHandler { FailExecStatus = HttpStatusCode.InternalServerError };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);

        Assert.True(result.ExecutionUnavailable);
        Assert.Equal(255, result.ExitCode);
        Assert.False(result.Success);
    }

    [Fact]
    public void FailureClassification_ServiceFailures_AreInfrastructure()
    {
        Assert.True(ModalFailureClassification.IsInfrastructure(
            new ModalApiException(HttpStatusCode.Unauthorized, "unauthorised", "no")));
        Assert.True(ModalFailureClassification.IsInfrastructure(new HttpRequestException("down")));
        Assert.True(ModalFailureClassification.IsInfrastructure(new TimeoutException()));

        var throttled = ModalFailureClassification.Classify((HttpStatusCode)429, "create-sandbox");
        Assert.Equal("throttled", throttled.ErrorClass);

        var unauthorised = ModalFailureClassification.Classify(HttpStatusCode.Unauthorized, "create-sandbox");
        Assert.Equal("unauthorised", unauthorised.ErrorClass);

        var quota = ModalFailureClassification.Classify(HttpStatusCode.PaymentRequired, "create-sandbox");
        Assert.Equal("quota-exhausted", quota.ErrorClass);

        var hinted = ModalFailureClassification.Classify((HttpStatusCode)429, "create-sandbox", TimeSpan.FromMinutes(10));
        Assert.True(hinted.RecheckIn >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task ConcurrentAcquisitions_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var handler = new FakeModalHandler { GateCreates = true };
        var provider = NewProvider(handler);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("mo-a", "modal", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("mo-b", "modal", preferenceScore: 100, capacity: 5)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        Assert.Equal(8, acquirer.MaxConcurrent);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                CancellationToken.None))
            .ToList();

        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (handler.EnteredCreates < 8)
        {
            await Task.Delay(10, enteredCts.Token);
        }

        Assert.Equal(8, handler.MaxConcurrentCreates);
        handler.ReleaseCreates();

        var sandboxes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(8, sandboxes.Length);
        Assert.Equal(8, acquirer.InFlight);
        Assert.Equal(8, provider.ActiveSandboxCount);

        using var overflowCts = new CancellationTokenSource();
        var overflow = Enumerable.Range(0, 2)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                overflowCts.Token))
            .ToList();
        await Task.Delay(500, CancellationToken.None);
        Assert.Equal(8, handler.MaxConcurrentCreates);
        overflowCts.Cancel();
        foreach (var pending in overflow)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        foreach (var sandbox in sandboxes)
        {
            await sandbox.DisposeAsync();
        }

        Assert.Equal(0, acquirer.InFlight);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task FullMember_SpillsOverToLeastLoadedMember()
    {
        var provider = NewProvider(new FakeModalHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("mo-a", "modal", preferenceScore: 100, capacity: 1),
                SandboxPlacementTestMembers.Member("mo-b", "modal", preferenceScore: 100, capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None);

        await using var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersToOwnedSandboxes()
    {
        var handler = new FakeModalHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var entry = Assert.Single(managed);
        Assert.Equal(sandbox.Id, entry.Name);
        Assert.True(entry.IsTrackedActive);
        Assert.False(entry.IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public void ShellCommand_BuildsQuotedCommand_WithBase64Environment()
    {
        var command = ModalShellCommand.Build(
            new Dictionary<string, string> { ["BASE"] = "1", ["DROP"] = "x" },
            new SandboxExec
            {
                Argv = ["echo", "a'b"],
                ExtraEnvironment = new Dictionary<string, string> { ["EXTRA"] = "a b" },
                EnvironmentVariablesToUnset = ["DROP"],
            },
            "/work",
            maxEnvironmentBytes: 4096,
            maxCommandBytes: 8192,
            maxStdinBytes: 1024);

        Assert.Contains("cd -- '/work'", command, StringComparison.Ordinal);
        Assert.Contains("'a'\\''b'", command, StringComparison.Ordinal);
        Assert.Contains("unset -- DROP", command, StringComparison.Ordinal);
        Assert.DoesNotContain("a b", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCommand_SecretStaging_CarriesNoCleartextValue()
    {
        const string Secret = "top-secret-value";
        var (merged, removals) = ModalShellCommand.MergeEnvironment(
            new Dictionary<string, string>(),
            new SandboxExec
            {
                Argv = ["true"],
                ExtraEnvironment = new Dictionary<string, string> { ["SECRET_KEY"] = Secret },
            });
        var content = ModalShellCommand.BuildEnvFileContent(merged, removals, maxEnvironmentBytes: 4096);
        Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
        Assert.Contains("SECRET_KEY", content, StringComparison.Ordinal);

        var sourcing = ModalShellCommand.BuildSourcingCommand(
            "/tmp/.codeybox-exec-env/env-abc",
            new SandboxExec { Argv = ["true"] },
            "/work",
            maxCommandBytes: 8192,
            maxStdinBytes: 1024);
        Assert.Contains("/tmp/.codeybox-exec-env/env-abc", sourcing, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, sourcing, StringComparison.Ordinal);
    }

    [Fact]
    public void GuestPath_ValidationRejectsEscapes()
    {
        ModalGuestPath.ValidateAbsolute("/work/file.txt");
        Assert.Equal("file.txt", ModalGuestPath.GetRelativePath("/work", "/work/file.txt"));

        Assert.Throws<ArgumentException>(() => ModalGuestPath.ValidateAbsolute("relative/path"));
        Assert.Throws<ArgumentException>(() => ModalGuestPath.ValidateAbsolute("/work/../escape"));
        Assert.Throws<ArgumentException>(() => ModalGuestPath.ValidateAbsolute("/work/\0null"));
        Assert.Throws<ArgumentException>(() => ModalGuestPath.GetRelativePath("/work", "/other/file"));
    }

    [Fact]
    public void ValidateOptions_RejectsUnsafeControlPlane()
    {
        Assert.Throws<InvalidOperationException>(() => ModalSandboxProvider.ValidateOptions(
            TestOptions() with { ApiBaseUrl = "http://api.modal.com" }));
        Assert.Throws<InvalidOperationException>(() => ModalSandboxProvider.ValidateOptions(
            TestOptions() with { ApiBaseUrl = "http://api.modal.com", AllowUnsafeHttp = true }));
        ModalSandboxProvider.ValidateOptions(
            TestOptions() with { ApiBaseUrl = "http://127.0.0.1:8080", AllowUnsafeHttp = true });
        Assert.Throws<InvalidOperationException>(() => ModalSandboxProvider.ValidateOptions(
            TestOptions() with { AppName = " " }));
        Assert.Throws<InvalidOperationException>(() => ModalSandboxProvider.ValidateOptions(
            TestOptions() with { CpuCount = 0 }));
    }

    [Fact]
    public async Task ReadFileBytes_ContentLengthOverBound_RejectedBeforeBuffering()
    {
        var tiny = JsonSerializer.Serialize(new
        {
            path = "/work/big.bin",
            content_b64 = Convert.ToBase64String("hi"u8.ToArray()),
        });
        using var httpClient = new HttpClient(new FixedBodyHandler(tiny, contentLength: 1_000_000));
        var client = new ModalApiClient(httpClient);
        var ex = await Assert.ThrowsAsync<ModalApiException>(() => client.ReadFileBytesAsync(
            "https://api.modal.com",
            new ModalCredentials(TestTokenId, TestTokenSecret),
            "sbx-1",
            "/work/big.bin",
            10,
            TimeSpan.FromSeconds(5),
            CancellationToken.None));
        Assert.Equal("limit-exceeded", ex.ErrorClass);
    }

    [Fact]
    public async Task ReadFileBytes_OversizeStreamedBody_RejectedAsLimitExceeded()
    {
        var big = JsonSerializer.Serialize(new
        {
            path = "/work/big.bin",
            content_b64 = Convert.ToBase64String(new byte[16 * 1024]),
        });
        using var httpClient2 = new HttpClient(new FixedBodyHandler(big, contentLength: null));
        var client2 = new ModalApiClient(httpClient2);
        var ex = await Assert.ThrowsAsync<ModalApiException>(() => client2.ReadFileBytesAsync(
            "https://api.modal.com",
            new ModalCredentials(TestTokenId, TestTokenSecret),
            "sbx-1",
            "/work/big.bin",
            10,
            TimeSpan.FromSeconds(5),
            CancellationToken.None));
        Assert.Equal("limit-exceeded", ex.ErrorClass);
    }

    private sealed class FixedBodyHandler(string body, long? contentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.ContentLength = contentLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
