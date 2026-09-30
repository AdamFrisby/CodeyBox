using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.BoxLiteSandboxPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape coverage for the BoxLite sandbox provider plugin. A real
/// service integration test cannot run in this suite — it needs a BoxLite
/// daemon binary, host virtualization (KVM/Hypervisor.framework/WHPX), and
/// OCI pull network — so the fake server below mirrors the daemon surface
/// the provider drives (VM lifecycle, exec start/poll/kill, files, archives,
/// labels, network, snapshots) with the response shapes recorded in
/// <c>BoxLiteApiClient</c> ("Wire shapes"; see
/// docs/extending/boxlite-sandbox-plugin.md, "recorded-shape fixture").
/// </summary>
public sealed class BoxLiteSandboxProviderTests
{
    private const string TestTokenEnvVar = "CB_TEST_BOXLITE_API_TOKEN";
    private const string TestToken = "boxlite-test-token";

    private static BoxLiteSandboxOptions TestOptions() => new()
    {
        Enabled = true,
        DaemonUrl = "http://localhost/",
        ApiTokenEnvVar = TestTokenEnvVar,
        AllowUnsafeHttp = true,
        DefaultImage = "test-image:latest",
        PollIntervalMilliseconds = 1,
        ReadyTimeoutSeconds = 30,
        TransitionTimeoutSeconds = 30,
    };

    private static BoxLiteSandboxProvider NewProvider(
        FakeBoxLiteServer server,
        BoxLiteSandboxOptions? options = null,
        Func<string, string?>? env = null,
        ILogger? log = null)
    {
        var opts = options ?? TestOptions();
        return new BoxLiteSandboxProvider(
            () => opts,
            new HttpClient(server),
            env ?? (name => name == TestTokenEnvVar ? TestToken : null),
            TimeProvider.System,
            timings: null,
            log ?? NullLogger.Instance);
    }

    private static SandboxSpec WorkSpec(WorkItemId? workItem = null, string image = "test-image:latest") => new()
    {
        ImageReference = image,
        WorkingDirectory = "/work",
        TimingWorkItemId = workItem,
    };

    // ------------------------------------------------------------------
    // Kind / placement / trust
    // ------------------------------------------------------------------

    [Fact]
    public void Kind_IsConstructibleAndNamesBoxLite()
    {
        var provider = new BoxLiteSandboxProvider();
        Assert.Equal("boxlite", provider.Name);
        Assert.Equal("codeybox.boxlite-sandbox", BoxLiteSandboxOptions.PluginId);
        Assert.False(SandboxProviderKinds.IsRegistered("boxlite"),
            "plugin kinds must never collide with built-in provider kinds");
    }

    [Fact]
    public void IsolationLevel_IsDedicatedKernel_HardwareIsolatedMicroVm()
    {
        var provider = new BoxLiteSandboxProvider();
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, provider.IsolationLevel);
    }

    [Fact]
    public void EgressClassification_IsNotEnforced_RegardlessOfPluginClaims()
    {
        var provider = new BoxLiteSandboxProvider();
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement(provider.Name));
        // Even a plugin claiming dedicated-kernel isolation cannot promote the kind.
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement("boxlite"));
    }

    [Fact]
    public void Catalog_SelectsKindByMemberProviderKind_AndSharesInstanceAcrossMembers()
    {
        var provider = new BoxLiteSandboxProvider();
        var catalog = new PluginSandboxProviderCatalog([("codeybox.boxlite-sandbox", provider)]);
        Assert.True(catalog.IsPluginKind("boxlite"));
        Assert.True(catalog.TryGetProvider("boxlite", out var resolved));
        Assert.Same(provider, resolved);

        var registry = new SandboxProviderRegistry(
            kind => catalog.TryGetProvider(kind, out var p) ? p : throw new InvalidOperationException(kind),
            pluginKinds: catalog.Kinds);
        var member1 = new SandboxMember { MemberId = "b1", ProviderKind = "boxlite", Capacity = 2, PreferenceScore = 50 };
        var member2 = new SandboxMember { MemberId = "b2", ProviderKind = "boxlite", Capacity = 4, PreferenceScore = 50 };
        Assert.Same(registry.Resolve(member1), registry.Resolve(member2));
    }

    [Fact]
    public void CapabilityGate_DropsUndeclaredWellKnownTags_KeepsDeclaredAndCustom()
    {
        var provider = new BoxLiteSandboxProvider();
        var member = new SandboxMember
        {
            MemberId = "b1", ProviderKind = "boxlite", Capacity = 2, PreferenceScore = 50,
            Capabilities = ["suspend-resume", "disk-guard", "port-publishing", "org-clearance-tag"],
        };
        var projected = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);
        Assert.Contains("suspend-resume", projected.Capabilities);
        Assert.Contains("org-clearance-tag", projected.Capabilities); // operator clearance tags pass through
        Assert.DoesNotContain("disk-guard", projected.Capabilities);
        Assert.DoesNotContain("port-publishing", projected.Capabilities);
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementedInterfaces()
    {
        var provider = new BoxLiteSandboxProvider();
        var declared = provider.DeclaredCapabilities;
        Assert.Contains(SandboxCapabilities.BaselineBake, declared);
        Assert.Contains(SandboxCapabilities.SuspendResume, declared);
        Assert.Contains(SandboxCapabilities.Teardown, declared);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, declared);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, declared);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, declared);
        // Every declared capability is backed by the matching interface.
        Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);
        Assert.IsAssignableFrom<IBaselineImageResolver>(provider);
        Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
    }

    [Fact]
    public void Options_CarryNoSecretFields()
    {
        // The options record must never hold credential material — only the
        // env var NAME that supplies it through the credential chain.
        var secretLike = typeof(BoxLiteSandboxOptions).GetProperties()
            .Where(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();
        Assert.Equal(["ApiTokenEnvVar"], secretLike);
    }

    [Fact]
    public async Task Placement_RefusesWorkRequiringUndeclaredCapability()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var acquisition = new SandboxPlacementAcquisition(
            WorkItemId.New(), "work", ["disk-guard"], RequiredCredential: null, RequiredNetworkProfile: null,
            WorkSpec());
        await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(
            () => acquirer.AcquireAsync(acquisition, CancellationToken.None));
    }

    [Fact]
    public async Task Placement_RefusesProfiledWorkOnNotEnforcedKind()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var acquisition = new SandboxPlacementAcquisition(
            WorkItemId.New(), "work", [], RequiredCredential: null, RequiredNetworkProfile: "internal",
            WorkSpec());
        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(
            () => acquirer.AcquireAsync(acquisition, CancellationToken.None));
        Assert.Contains("egress", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Placement_SelectsBoxLiteMember_AndCreatesOnProvider()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);
        Assert.IsAssignableFrom<ISandbox>(sandbox);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/vms"));
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Create wire shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_PostsExpectedRequestShape_AndAuthenticatesFromEnv()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var workItem = WorkItemId.New();

        var limits = new SandboxResourceLimits { CpuCount = 4, MemoryBytes = 8L * 1024 * 1024 * 1024, DiskBytes = 32L * 1024 * 1024 * 1024 };
        var spec = WorkSpec(workItem) with { Limits = limits };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/vms");
        Assert.Equal("Bearer", create.AuthScheme);
        Assert.Equal(TestToken, create.AuthParam);

        using var body = JsonDocument.Parse(create.Body);
        var root = body.RootElement;
        Assert.StartsWith("codeybox-", root.GetProperty("name").GetString());
        Assert.Equal("test-image:latest", root.GetProperty("image").GetString());
        Assert.Equal(4, root.GetProperty("cpu").GetInt32());
        Assert.Equal(8192, root.GetProperty("memoryMib").GetInt32());
        Assert.Equal(32, root.GetProperty("diskGib").GetInt32());
        Assert.True(root.GetProperty("persistent").GetBoolean());
        var labels = root.GetProperty("labels");
        Assert.Equal("true", labels.GetProperty("codeybox.managed").GetString());
        Assert.Equal(workItem.Value.ToString("N"), labels.GetProperty("codeybox.work-item").GetString());
        Assert.False(root.TryGetProperty("env", out var envProp) && envProp.ValueKind != JsonValueKind.Null,
            "spec env must never ride the VM record — it carries secrets");
        Assert.Equal("isolated", root.GetProperty("network").GetProperty("mode").GetString());

        // The network restriction is re-asserted after setup (bake-then-lock ordering).
        var net = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/network", StringComparison.Ordinal));
        using var netBody = JsonDocument.Parse(net.Body);
        Assert.Equal("isolated", netBody.RootElement.GetProperty("mode").GetString());

        await sandbox.DisposeAsync();
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/vms/"));
    }

    [Fact]
    public async Task Create_WithAllowedHosts_RequestsRestrictedNetwork()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var spec = WorkSpec() with { Network = new SandboxNetworkPolicy { AllowedHosts = ["proxy.example", "registry.example:5000"] } };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/vms");
        using var body = JsonDocument.Parse(create.Body);
        var network = body.RootElement.GetProperty("network");
        Assert.Equal("restricted", network.GetProperty("mode").GetString());
        Assert.Equal(2, network.GetProperty("allowedHosts").GetArrayLength());
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Create_WithNetworkProfile_IsRefused()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var spec = WorkSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "internal" } };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message);
        Assert.Equal(0, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/vms"));
    }

    [Fact]
    public async Task Create_RefusesWhenDisabled()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server, options: TestOptions() with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_RefusesWithoutCredentialEnvVar()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server, env: _ => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Contains(TestTokenEnvVar, ex.Message);
    }

    [Fact]
    public async Task Create_DeletesVm_WhenProvisioningFails()
    {
        var server = new FakeBoxLiteServer { FailExecs = true };
        var provider = NewProvider(server, options: TestOptions() with { SetupCommands = ["echo setup"] });

        // A non-zero setup command is a deterministic provisioning failure —
        // the VM is still deleted and no deferral is raised (deferrals are
        // for service-side refusals, not in-guest command exits).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/vms/"));
    }

    // ------------------------------------------------------------------
    // Failure classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "unauthorized")]
    [InlineData(429, "throttled")]
    [InlineData(503, "server-error")]
    public async Task Create_ClassifiesServiceFailuresAsInfrastructure(int status, string errorClass)
    {
        var server = new FakeBoxLiteServer { CreateStatusOverride = (HttpStatusCode)status };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(errorClass, ex.ErrorClass);
        Assert.True(ex.RecheckIn > TimeSpan.Zero);
    }

    [Fact]
    public async Task Create_ClassifiesUnreachableServiceAsInfrastructure()
    {
        var server = new FakeBoxLiteServer { CreateFault = new HttpRequestException("connection refused") };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("unreachable", ex.ErrorClass);
    }

    [Fact]
    public async Task Create_ClassifiesQuotaExhaustionAsInfrastructure()
    {
        var server = new FakeBoxLiteServer
        {
            CreateStatusOverride = HttpStatusCode.PaymentRequired,
            CreateErrorBody = """{"message":"vm quota exceeded for host"}""",
        };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
    }

    [Fact]
    public async Task Create_ClassifiesQuotaShapedForbiddenAsInfrastructure()
    {
        var server = new FakeBoxLiteServer
        {
            CreateStatusOverride = HttpStatusCode.Forbidden,
            CreateErrorBody = """{"message":"quota limit exceeded: too many live vms"}""",
        };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
    }

    // ------------------------------------------------------------------
    // Exec
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exec_StreamsOutputOverPolls_AndReportsExitCode()
    {
        var server = new FakeBoxLiteServer();
        server.ScriptExec(
            stdoutChunks: ["hello ", "world"],
            stderrChunks: ["oops"],
            exitCode: 0);
        var provider = NewProvider(server);

        var chunks = new List<string>();
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["echo", "hi"],
            Stdin = "input-text",
            ExtraEnvironment = new Dictionary<string, string> { ["MY_VAR"] = "v1" },
            StdoutChunkCallback = c => chunks.Add(c),
        }, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello world", result.Stdout);
        Assert.Equal("oops", result.Stderr);
        Assert.Equal("hello world", string.Concat(chunks));
        Assert.False(result.ExecutionUnavailable);

        var exec = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/exec", StringComparison.Ordinal));
        using var execBody = JsonDocument.Parse(exec.Body);
        var argv = execBody.RootElement.GetProperty("argv");
        Assert.Equal("echo", argv[0].GetString());
        Assert.Equal("hi", argv[1].GetString());
        Assert.Equal("/work", execBody.RootElement.GetProperty("cwd").GetString());
        var env = execBody.RootElement.GetProperty("env");
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("v1")), env.GetProperty("MY_VAR").GetString());
        var stdin = Encoding.UTF8.GetString(Convert.FromBase64String(execBody.RootElement.GetProperty("stdinBase64").GetString()!));
        Assert.Equal("input-text", stdin);

        // The completed exec is polled at least twice (streaming), then left
        // for daemon-side reaping — no kill is issued on the success path.
        Assert.True(server.Requests.Count(r => r.Method == "GET" && r.Path.Contains("/exec/", StringComparison.Ordinal)) >= 2);
        Assert.Equal(0, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/exec/", StringComparison.Ordinal)));

        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_EnvironmentRemoval_AppliedInWirePayload()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var spec = WorkSpec() with
        {
            Environment = new Dictionary<string, string> { ["KEEP_ME"] = "x", ["DROP_ME"] = "y" },
        };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["true"],
            EnvironmentVariablesToUnset = ["DROP_ME"],
        }, CancellationToken.None);
        Assert.True(result.Success);
        var exec = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/exec", StringComparison.Ordinal));
        using var execBody = JsonDocument.Parse(exec.Body);
        var env = execBody.RootElement.GetProperty("env");
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("x")), env.GetProperty("KEEP_ME").GetString());
        Assert.False(env.TryGetProperty("DROP_ME", out _));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_Cancellation_KillsGuestProcess()
    {
        var server = new FakeBoxLiteServer { NextExecNeverCompletes = true };
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.Requests.Count(r => r.Method == "GET" && r.Path.Contains("/exec/", StringComparison.Ordinal)) == 0
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/exec/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_OutputLimit_KillOnOutputLimit_KillsExec()
    {
        var server = new FakeBoxLiteServer();
        server.ScriptExec(stdoutChunks: [new string('x', 200)], stderrChunks: [], exitCode: 0);
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 64,
            KillOnOutputLimit = true,
        }, CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/exec/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_OutputLimit_WithoutKill_TruncatesAndFlags()
    {
        // Overflow without KillOnOutputLimit is a normal truncated result —
        // still a verdict on the completed command, never infra.
        var server = new FakeBoxLiteServer();
        server.ScriptExec(stdoutChunks: [new string('x', 200)], stderrChunks: [], exitCode: 0);
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 64,
            KillOnOutputLimit = false,
        }, CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.ExecutionUnavailable);
        Assert.Equal(0, result.ExitCode);
        Assert.True(Encoding.UTF8.GetByteCount(result.Stdout) <= 64);
        Assert.Equal(0, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/exec/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_SnapshotPastByteCeiling_KillsAndClassifiesInfra()
    {
        // 'é' is 2 bytes in UTF-8: 600K chars decode to ~1.2 MiB, past the
        // cap+slack byte ceiling while staying under it in chars. The byte
        // guard must kill the guest process and classify the exec as
        // infrastructure before the host buffers the snapshot — never a
        // verdict on the completed command.
        var server = new FakeBoxLiteServer();
        server.ScriptExec(stdoutChunks: [new string('é', 600_000)], stderrChunks: [], exitCode: 0);
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 64,
            KillOnOutputLimit = false,
        }, CancellationToken.None);

        Assert.True(result.ExecutionUnavailable);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/exec/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ReleaseActiveTracking_RemovesProviderTracking_AndDisposeStillCompletes()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(WorkItemId.New()), CancellationToken.None);
        var lease = Assert.IsAssignableFrom<IActiveSandboxLease>(sandbox);

        Assert.Single(provider.SnapshotActiveSandboxes());
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(managed, m => m.Name == sandbox.Id && m.IsTrackedActive);

        lease.ReleaseActiveTracking();

        Assert.Empty(provider.SnapshotActiveSandboxes());
        managed = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Contains(managed, m => m.Name == sandbox.Id && !m.IsTrackedActive);

        // A second release is a no-op, and dispose still completes exactly once.
        lease.ReleaseActiveTracking();
        await sandbox.DisposeAsync();
        Assert.Empty(provider.SnapshotActiveSandboxes());
    }

    [Fact]
    public async Task Exec_ServiceFailure_IsExecutionUnavailable_NotDiffFailure()
    {
        var server = new FakeBoxLiteServer { ExecStartStatusOverride = HttpStatusCode.ServiceUnavailable };
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.True(result.ExecutionUnavailable);
        Assert.Equal(255, result.ExitCode);
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Capacity / live load
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAcquires_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 1, memberCount: 2);

        var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);
        var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);

        // Both member gates are at cap: a third acquisition must wait, and the
        // wait is visible to placement as live load. Poll for a violation
        // (a third create request, or a completed third acquire) with a
        // timeout instead of a fixed sleep, so the test cannot pass vacuously
        // when the machine is slow: a correct implementation never issues the
        // third create while both members are at cap.
        var third = Task.Run(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None));
        var settleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        int ExactCreates() => server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/vms");
        while (!third.IsCompleted
            && ExactCreates() == 2
            && DateTime.UtcNow < settleUntil)
        {
            await Task.Delay(5);
        }
        Assert.False(third.IsCompleted, "a third acquire must wait when both members are at capacity");
        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/vms"));

        await first.DisposeAsync();
        var thirdSandbox = await third.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, acquirer.InFlight);

        await second.DisposeAsync();
        await thirdSandbox.DisposeAsync();
        Assert.Equal(0, acquirer.InFlight);
    }

    private static SandboxPlacementAcquirer BuildAcquirer(ISandboxProvider provider, int capacity = 1, int memberCount = 1)
    {
        var members = Enumerable.Range(0, memberCount)
            .Select(i => new SandboxMember
            {
                MemberId = $"boxlite-{i}",
                ProviderKind = "boxlite",
                Capacity = capacity,
                PreferenceScore = 50,
            })
            .ToList();
        var classes = new SandboxClassesSnapshot(
        [
            new SandboxClass { Id = "boxlite-class", DisplayName = "boxlite", Members = members },
        ]);
        var registry = new SandboxProviderRegistry(
            _ => provider,
            pluginKinds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "boxlite" });
        return new SandboxPlacementAcquirer(classes, registry);
    }

    // ------------------------------------------------------------------
    // Suspend / retain / adopt / reconcile
    // ------------------------------------------------------------------

    [Fact]
    public async Task Suspend_PausesVm_AndRetainReturnsAdoptableLease()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = (BoxLiteSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        await ((ISuspendableSandbox)sandbox).SuspendAsync();
        Assert.True(((ISuspendableSandbox)sandbox).IsSuspended);
        Assert.Equal(1, server.CountRequests("POST", $"/v1/vms/{sandbox.Id}/pause"));

        var lease = await ((IPreemptibleSandbox)sandbox).RetainForInfrastructureRecoveryAsync();
        Assert.NotNull(lease);
        Assert.Equal("boxlite", lease!.ProviderId);
        Assert.Equal(sandbox.Id, lease.SandboxId);
        Assert.False(string.IsNullOrWhiteSpace(lease.Token));

        // Disposal preserves (no DELETE) because the VM is suspended/retained.
        await sandbox.DisposeAsync();
        Assert.Equal(0, server.CountRequests("DELETE", "/v1/vms/" + sandbox.Id));

        // Wrong token → adoption refused.
        var badLease = new SandboxRecoveryLease(lease.ProviderId, lease.SandboxId, lease.Token + "aa");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = badLease }, CancellationToken.None));

        // Correct token → start called, handle returned.
        server.SetState(sandbox.Id, "stopped");
        var adopted = await provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None);
        Assert.Equal(1, server.CountRequests("POST", $"/v1/vms/{sandbox.Id}/start"));
        await adopted.DisposeAsync();
    }

    [Fact]
    public async Task Lease_OtherProviderKind_IsRefused()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var lease = new SandboxRecoveryLease("sprites", "codeybox-xyz", "token");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None));
    }

    [Fact]
    public async Task Resume_ResumesStoppedVm()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        server.SetState(sandbox.Id, "stopped");

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync(sandbox.Id, CancellationToken.None);
        Assert.True(
            server.CountRequests("POST", $"/v1/vms/{sandbox.Id}/resume") == 1
            || server.CountRequests("POST", $"/v1/vms/{sandbox.Id}/start") == 1);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_DeletesOrphanedStoppedVms_KeepsLive()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        server.SetState(sandbox.Id, "stopped");
        server.AddOrphan("codeybox-orphan1", "stopped");
        server.AddOrphan("foreign-vm", "stopped"); // unmanaged name — never listed

        var failures = await provider.ReconcileStuckSandboxesAsync(
            new HashSet<string>(StringComparer.Ordinal) { sandbox.Id }, CancellationToken.None);
        Assert.Empty(failures);
        Assert.Equal(0, server.CountRequests("DELETE", "/v1/vms/" + sandbox.Id));
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/vms/codeybox-orphan1"));
        Assert.Equal(0, server.CountRequests("DELETE", "/v1/vms/foreign-vm"));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WaitForAdoptedAgentCompletion_PollsExitMarker_AndTailsLog()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var logPath = "/work/.codeybox/agent-logs/agent.jsonl";
        server.Files[$"{sandbox.Id}:{logPath}"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("line1\nline2\n"));
        server.Files[$"{sandbox.Id}:{logPath}.exit"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));

        var tail = new StringBuilder();
        var exit = await ((ISuspendingSandboxProvider)provider).WaitForAdoptedAgentCompletionAsync(
            sandbox.Id, logPath, s => tail.Append(s), deadline: TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal("line1\nline2\n", tail.ToString());
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WaitForAdoptedAgentCompletion_SkipsOverCapLog_StillReturnsExitCode()
    {
        // Regression: guest-controlled adopt logs are bounded per read, so a
        // log past the cap is skipped for the tick instead of buffered.
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server, TestOptions() with { MaxFileSyncBase64Bytes = 64 });
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var logPath = "/work/.codeybox/agent-logs/agent.jsonl";
        server.Files[$"{sandbox.Id}:{logPath}"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 1024)));
        server.Files[$"{sandbox.Id}:{logPath}.exit"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("3"));

        var tail = new StringBuilder();
        var exit = await ((ISuspendingSandboxProvider)provider).WaitForAdoptedAgentCompletionAsync(
            sandbox.Id, logPath, s => tail.Append(s), deadline: TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(3, exit);
        Assert.Equal(string.Empty, tail.ToString());
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WaitForAdoptedAgentCompletion_SkipsMalformedLog_StillReturnsExitCode()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var logPath = "/work/.codeybox/agent-logs/agent.jsonl";
        server.Files[$"{sandbox.Id}:{logPath}"] = "!!!not-base64!!!";
        server.Files[$"{sandbox.Id}:{logPath}.exit"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("0"));

        var tail = new StringBuilder();
        var exit = await ((ISuspendingSandboxProvider)provider).WaitForAdoptedAgentCompletionAsync(
            sandbox.Id, logPath, s => tail.Append(s), deadline: TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, tail.ToString());
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Baselines
    // ------------------------------------------------------------------

    [Fact]
    public void BaselineRef_IsDeterministic_ContentHash()
    {
        var provider = NewProvider(new FakeBoxLiteServer(),
            options: TestOptions() with { BaselineSourceImage = "test-image:latest", SetupCommands = ["apt update"] });
        var r1 = provider.ResolveBaselineRef("p1", SandboxProfileFlavor.Headless);
        var r2 = provider.ResolveBaselineRef("p1", SandboxProfileFlavor.Headless);
        var r3 = provider.ResolveBaselineRef("p2", SandboxProfileFlavor.Headless);
        Assert.NotNull(r1);
        Assert.Equal(r1, r2);
        Assert.NotEqual(r1, r3);
        Assert.StartsWith("codeybox-baseline-", r1);
    }

    [Fact]
    public async Task BaselineBake_CreatesBakesAndSnapshots()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server,
            options: TestOptions() with { BaselineSourceImage = "test-image:latest", SetupCommands = ["true"] });

        var name = await provider.EnsureBaselineImageAsync("p1", SandboxProfileFlavor.Headless, pinnedBaselineRef: null, CancellationToken.None);
        Assert.NotNull(name);

        var snapshotPost = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/snapshots");
        using var body = JsonDocument.Parse(snapshotPost.Body);
        Assert.Equal(name, body.RootElement.GetProperty("name").GetString());

        // Bake VM deleted; the bake VM is not the caller-visible name.
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/vms/codeybox-bake-"));

        // Second call reuses the now-active snapshot.
        var name2 = await provider.EnsureBaselineImageAsync("p1", SandboxProfileFlavor.Headless, pinnedBaselineRef: name, CancellationToken.None);
        Assert.Equal(name, name2);
    }

    [Fact]
    public async Task Create_UsesBaselineImageRefAsImage()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec() with { BaselineImageRef = "codeybox-baseline-abc" }, CancellationToken.None);
        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/vms");
        using var body = JsonDocument.Parse(create.Body);
        Assert.Equal("codeybox-baseline-abc", body.RootElement.GetProperty("image").GetString());
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Mounts / files
    // ------------------------------------------------------------------

    [Fact]
    public async Task WritableFileMount_SyncsBackAtomically_OnDispose()
    {
        using var dir = new TempDirectory();
        var hostFile = Path.Combine(dir.Path, "notes.txt");
        File.WriteAllText(hostFile, "orig");
        var server = new FakeBoxLiteServer();

        var provider = NewProvider(server);
        var spec = WorkSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/data/notes.txt", HostPath = hostFile, ReadOnly = false }],
        };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        // The guest rewrites the file; disposal reads it back atomically.
        server.Files[$"{sandbox.Id}:/data/notes.txt"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("updated"));
        await sandbox.DisposeAsync();

        Assert.Equal("updated", File.ReadAllText(hostFile));
    }

    [Fact]
    public async Task WritableMount_SyncsBackAtomically_OnDispose()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "seed.txt"), "seed");
        var server = new FakeBoxLiteServer();

        using var replacement = new TempDirectory();
        File.WriteAllText(Path.Combine(replacement.Path, "from-guest.txt"), "new-content");
        server.ArchiveResponses.Enqueue(CreateTarGzBase64(replacement.Path));

        var provider = NewProvider(server);
        var spec = WorkSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = dir.Path, ReadOnly = false }],
        };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        // Staging-in reached the daemon as an archive extract.
        Assert.Equal(1, server.Requests.Count(r => r.Method == "POST" && r.Path.Contains("/archive", StringComparison.Ordinal)));

        await sandbox.DisposeAsync();

        // The host directory was atomically replaced by the guest archive.
        Assert.True(File.Exists(Path.Combine(dir.Path, "from-guest.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "seed.txt")));
        Assert.Equal("new-content", File.ReadAllText(Path.Combine(dir.Path, "from-guest.txt")));
    }

    [Fact]
    public async Task WritableMount_SyncRefusesArchiveWithTraversal()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "keep.txt"), "keep");
        var server = new FakeBoxLiteServer();
        server.ArchiveResponses.Enqueue(CreateTarGzBase64WithTraversal());

        var provider = NewProvider(server);
        var spec = WorkSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = dir.Path, ReadOnly = false }],
        };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        await sandbox.DisposeAsync();

        // Refused: original directory untouched (disposal logs a warning and proceeds).
        Assert.True(File.Exists(Path.Combine(dir.Path, "keep.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dir.Path, "keep.txt")));
    }

    [Fact]
    public async Task StageFile_RefusesOversizedHostFile_BeforeAnyTraffic()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var server = new FakeBoxLiteServer();
        var sandbox = StagingSandbox(server, TestOptions() with { MaxFileSyncBytes = 100 }, "/data/big.bin", path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.PrepareFilesystemAsync(CancellationToken.None));
        Assert.Contains("exceeds the file bound", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task File_RoundTrip_ReadWrite()
    {
        var server = new FakeBoxLiteServer();
        server.AddOrphan("codeybox-staging-test", "running");
        var sandbox = StagingSandbox(server, TestOptions(), "/data", null);

        await sandbox.WriteGuestFileAsync("/work/hello.txt", "hello guest", mode: null, CancellationToken.None);
        var put = server.Requests.Single(r => r.Method == "PUT" && r.Path.EndsWith("/files", StringComparison.Ordinal));
        using var putBody = JsonDocument.Parse(put.Body);
        Assert.Equal("/work/hello.txt", putBody.RootElement.GetProperty("path").GetString());

        var content = await sandbox.ReadGuestFileAsync("/work/hello.txt", CancellationToken.None);
        Assert.Equal("hello guest", content);

        var missing = await sandbox.ReadGuestFileAsync("/work/nope.txt", CancellationToken.None);
        Assert.Null(missing);
    }

    private static BoxLiteSandbox StagingSandbox(
        FakeBoxLiteServer server, BoxLiteSandboxOptions opts, string sandboxPath, string? hostPath)
    {
        var http = new HttpClient(server);
        return new BoxLiteSandbox(
            "codeybox-staging-test",
            WorkSpec(),
            () => opts,
            new BoxLiteApiClient(http),
            () => new BoxLiteEndpoint(new Uri("http://localhost/"), TestToken, AllowUnsafeHttp: true),
            hostPath is null
                ? []
                : [new BoxLiteMountPlan(sandboxPath, hostPath, ReadOnly: false, IsGuestDir: false)],
            _ => { },
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static string CreateTarGzBase64(string dir)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            System.Formats.Tar.TarFile.CreateFromDirectory(dir, gzip, includeBaseDirectory: false);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string CreateTarGzBase64WithTraversal()
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true))
        {
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "../escape.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("pwned")),
            };
            writer.WriteEntry(entry);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    // ------------------------------------------------------------------
    // Teardown modes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Teardown_StopAndPreserve_SkipsDelete_OnDispose()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server);
        var sandbox = (BoxLiteSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();
        Assert.Equal(1, server.CountRequests("POST", $"/v1/vms/{sandbox.Id}/stop"));
        await sandbox.DisposeAsync();
        Assert.Equal(0, server.CountRequests("DELETE", "/v1/vms/" + sandbox.Id));

        // Reverting to delete-on-dispose destroys on the next dispose path:
        // a fresh VM without preserve deletes normally.
        var second = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var secondId = second.Id;
        ((IPreserveOnDisposeSandbox)((BoxLiteSandbox)second)).DisablePreserveOnDispose();
        await second.DisposeAsync();
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/vms/" + secondId));
    }

    // ------------------------------------------------------------------
    // Trust-boundary guards (audit regression)
    // ------------------------------------------------------------------

    [Fact]
    public void DaemonUrl_CleartextHttp_PermittedOnlyForLoopbackWithOptIn()
    {
        Assert.True(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("https://daemon.example:8899/"), allowUnsafeHttp: false));
        Assert.False(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("http://daemon.example/v1"), allowUnsafeHttp: true));
        Assert.False(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("http://192.168.0.9/v1"), allowUnsafeHttp: true));
        Assert.False(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("http://localhost/v1"), allowUnsafeHttp: false));
        Assert.True(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("http://localhost/v1"), allowUnsafeHttp: true));
        Assert.True(BoxLiteApiClient.IsCleartextHttpPermitted(new Uri("http://127.0.0.1:8899/v1"), allowUnsafeHttp: true));
    }

    [Fact]
    public async Task RemoteHttpDaemonUrl_IsRefused_EvenWithUnsafeHttpOptIn()
    {
        var server = new FakeBoxLiteServer();
        var provider = NewProvider(server,
            options: TestOptions() with { DaemonUrl = "http://daemon.example/", AllowUnsafeHttp = true });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Contains("https://", ex.Message);
        Assert.Empty(server.Requests);
    }

    // ------------------------------------------------------------------
    // Transport ceilings (audit regression): guest-influenced bodies must
    // throw past an option-derived ceiling before the client buffers them.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ApiClient_ExecPollBodyPastCeiling_ThrowsWithoutDrainingGuestOutput()
    {
        // Valid JSON framing around a 1 MiB guest payload: a buffering reader
        // must consume it all before parsing, so served bytes prove whether
        // the ceiling stopped the read early.
        var wire = new CountingStream(
            Encoding.UTF8.GetBytes("{\"execId\":\"e\",\"running\":true,\"stdoutBase64\":\""),
            totalBytes: 1024 * 1024,
            fill: (byte)'A');
        var server = new InlineHandler(_ => StreamJsonResponse(wire));
        var client = new BoxLiteApiClient(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<BoxLiteApiException>(() =>
            client.GetExecAsync(TestEndpoint(), "vm", "exec", CancellationToken.None, maxResponseBytes: 1024));
        // Unexpected is an infrastructure signal, never a diff verdict.
        Assert.Equal(BoxLiteFailureKind.Unexpected, ex.Kind);
        // The client stopped at the ceiling instead of draining the 1 MiB
        // body: host memory stays O(cap) no matter how much the guest wrote.
        Assert.True(wire.BytesRead <= 1024 + 16384, $"read {wire.BytesRead} bytes past a 1 KiB ceiling");
    }

    [Fact]
    public async Task ApiClient_DeclaredLengthPastCeiling_ThrowsBeforeTouchingBody()
    {
        var wire = new CountingStream([], totalBytes: 1024 * 1024);
        var server = new InlineHandler(_ => StreamJsonResponse(wire,
            declaredLength: BoxLiteApiClient.DefaultMaxResponseBytes + 1));
        var client = new BoxLiteApiClient(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<BoxLiteApiException>(() =>
            client.GetExecAsync(TestEndpoint(), "vm", "exec", CancellationToken.None));
        Assert.Equal(BoxLiteFailureKind.Unexpected, ex.Kind);
        Assert.Equal(0, wire.BytesRead);
    }

    [Fact]
    public async Task ApiClient_ResponseAtCeiling_IsAccepted_OneByteOverIsRefused()
    {
        var json = """{"execId":"e","running":false,"exitCode":0}""";
        var body = Encoding.UTF8.GetBytes(json);
        var server = new InlineHandler(_ => BufferedJsonResponse(body));
        var client = new BoxLiteApiClient(new HttpClient(server));

        var dto = await client.GetExecAsync(
            TestEndpoint(), "vm", "exec", CancellationToken.None, maxResponseBytes: body.Length);
        Assert.Equal("e", dto.ExecId);
        Assert.Equal(0, dto.ExitCode);

        var ex = await Assert.ThrowsAsync<BoxLiteApiException>(() =>
            client.GetExecAsync(
                TestEndpoint(), "vm", "exec", CancellationToken.None, maxResponseBytes: body.Length - 1));
        Assert.Equal(BoxLiteFailureKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task ApiClient_OversizedErrorBody_IsTruncatedBeforeBuffering()
    {
        var wire = new CountingStream([], totalBytes: 1024 * 1024, fill: (byte)'e');
        var server = new InlineHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway);
            response.Content = new StreamContent(wire);
            return response;
        });
        var client = new BoxLiteApiClient(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<BoxLiteApiException>(() =>
            client.CreateVmAsync(
                TestEndpoint(),
                new BoxLiteCreateVmRequest("x", "i", null, null, null, false, null, null, null),
                CancellationToken.None));
        Assert.Equal(BoxLiteFailureKind.ServerError, ex.Kind);
        // Diagnostics stay bounded and the 1 MiB error page is never drained.
        Assert.True(ex.Message.Length < 8192, $"error message runs {ex.Message.Length} chars");
        Assert.True(wire.BytesRead <= BoxLiteApiClient.MaxErrorBodyBytes + 8192,
            $"read {wire.BytesRead} error bytes past the truncate ceiling");
    }

    private static BoxLiteEndpoint TestEndpoint() =>
        new(new Uri("http://localhost/"), TestToken, AllowUnsafeHttp: true);

    private static HttpResponseMessage StreamJsonResponse(Stream wire, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(wire);
        if (declaredLength is { } length)
            response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpResponseMessage BufferedJsonResponse(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(body);
        return response;
    }

    private sealed class InlineHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>
    /// A finite stream serving a valid prefix followed by fill bytes that
    /// counts what the client actually pulled, so ceiling tests prove the
    /// client stopped reading early instead of draining guest-controlled
    /// output. The prefix lets tests serve parseable framing a buffering
    /// reader would have to consume in full.
    /// </summary>
    private sealed class CountingStream(byte[] prefix, long totalBytes, byte fill = 0) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = totalBytes - BytesRead;
            if (remaining <= 0)
                return 0;
            var n = (int)Math.Min(count, remaining);
            var fromPrefix = (int)Math.Min(n, Math.Max(0L, prefix.Length - BytesRead));
            if (fromPrefix > 0)
                Array.Copy(prefix, BytesRead, buffer, offset, fromPrefix);
            if (fromPrefix < n)
                Array.Fill(buffer, fill, offset + fromPrefix, n - fromPrefix);
            BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // Fake BoxLite daemon (recorded shapes)
    // ------------------------------------------------------------------

    private sealed record RequestRecord(string Method, string Path, string Query,
        string? AuthScheme, string? AuthParam, string Body);

    private sealed class FakeExecSpec
    {
        public Queue<string> StdoutChunks { get; } = new();
        public Queue<string> StderrChunks { get; } = new();
        public int ExitCode { get; set; }
    }

    private sealed class FakeBoxLiteServer : HttpMessageHandler
    {
        private int _vmSeq;
        private int _execSeq;
        private readonly ConcurrentDictionary<string, FakeVm> _vms = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FakeExec> _execs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _snapshots = new(StringComparer.Ordinal);
        public List<RequestRecord> Requests { get; } = [];
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Queue<FakeExecSpec> ScriptedExecs { get; } = new();
        public Queue<string> ArchiveResponses { get; } = new();
        public HttpStatusCode? CreateStatusOverride { get; set; }
        public string? CreateErrorBody { get; set; }
        public HttpStatusCode? ExecStartStatusOverride { get; set; }
        public Exception? CreateFault { get; set; }
        public bool FailExecs { get; set; }
        public bool NextExecNeverCompletes { get; set; }

        public void ScriptExec(string[] stdoutChunks, string[] stderrChunks, int exitCode)
        {
            var spec = new FakeExecSpec { ExitCode = exitCode };
            foreach (var c in stdoutChunks) spec.StdoutChunks.Enqueue(c);
            foreach (var c in stderrChunks) spec.StderrChunks.Enqueue(c);
            ScriptedExecs.Enqueue(spec);
        }

        public int CountRequests(string method, string pathPrefix) =>
            Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

        public void SetState(string name, string state)
        {
            if (_vms.TryGetValue(name, out var vm))
                vm.State = state;
        }

        public void AddOrphan(string name, string state)
        {
            var vm = new FakeVm($"id-{name}", state);
            vm.Labels["codeybox.managed"] = "true";
            _vms[name] = vm;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var uri = request.RequestUri!;
            var record = new RequestRecord(
                request.Method.Method, uri.AbsolutePath, uri.Query,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                body);
            lock (Requests) { Requests.Add(record); }

            var path = uri.AbsolutePath;
            if (path.StartsWith("/v1/snapshots", StringComparison.Ordinal))
                return HandleSnapshots(request, path, body);
            if (path.StartsWith("/v1/vms", StringComparison.Ordinal))
                return HandleVms(request, path, uri.Query, body);
            return Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
        }

        private HttpResponseMessage HandleSnapshots(HttpRequestMessage request, string path, string body)
        {
            if (request.Method == HttpMethod.Post && path == "/v1/snapshots")
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("name").GetString()!;
                _snapshots[name] = "active";
                return Json($$"""{"name":"{{name}}","state":"active"}""", HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && path == "/v1/snapshots")
            {
                var items = _snapshots.Select(s => $$"""{"name":"{{s.Key}}","state":"{{s.Value}}"}""");
                return Json($$"""{"snapshots":[{{string.Join(",", items)}}]}""");
            }
            var prefix = "/v1/snapshots/";
            if (request.Method == HttpMethod.Delete && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path[prefix.Length..]);
                return _snapshots.TryRemove(name, out _) ? Json("{}") : Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
            }
            return Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
        }

        private HttpResponseMessage HandleVms(HttpRequestMessage request, string path, string query, string body)
        {
            if (request.Method == HttpMethod.Post && path == "/v1/vms")
            {
                if (CreateFault is not null)
                    throw CreateFault;
                if (CreateStatusOverride is { } st)
                    return Json(CreateErrorBody ?? """{"message":"service said no"}""", st);
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("name").GetString()!;
                var id = "box-" + Interlocked.Increment(ref _vmSeq).ToString();
                var vm = new FakeVm(id, "running");
                if (doc.RootElement.TryGetProperty("image", out var image))
                    vm.Image = image.GetString();
                if (doc.RootElement.TryGetProperty("labels", out var labels))
                {
                    foreach (var label in labels.EnumerateObject())
                        vm.Labels[label.Name] = label.Value.GetString() ?? "";
                }
                if (doc.RootElement.TryGetProperty("network", out var network)
                    && network.TryGetProperty("mode", out var mode))
                    vm.NetworkMode = mode.GetString();
                _vms[name] = vm;
                return Json($$"""{"id":"{{id}}","name":"{{name}}","state":"running","labels":{{SerializeLabels(vm.Labels)}}}""",
                    HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && path == "/v1/vms")
            {
                var items = _vms
                    .Where(v => v.Key.StartsWith("codeybox-", StringComparison.Ordinal))
                    .Select(v => $$"""{"id":"{{v.Value.Id}}","name":"{{v.Key}}","state":"{{v.Value.State}}","labels":{{SerializeLabels(v.Value.Labels)}}}""");
                return Json($$"""{"vms":[{{string.Join(",", items)}}],"nextCursor":null}""");
            }

            var rest = path["/v1/vms/".Length..];
            var slash = rest.IndexOf('/');
            var vmName = slash < 0 ? Uri.UnescapeDataString(rest) : Uri.UnescapeDataString(rest[..slash]);
            var tail = slash < 0 ? string.Empty : rest[slash..];
            if (!_vms.TryGetValue(vmName, out var target))
                return Json("""{"message":"vm not found"}""", HttpStatusCode.NotFound);

            if (tail == string.Empty)
            {
                if (request.Method == HttpMethod.Get)
                    return Json($$"""{"id":"{{target.Id}}","name":"{{vmName}}","state":"{{target.State}}","image":"{{target.Image}}","labels":{{SerializeLabels(target.Labels)}}}""");
                if (request.Method == HttpMethod.Delete)
                {
                    _vms.TryRemove(vmName, out _);
                    return Json("{}");
                }
                return Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
            }
            if (tail is "/start" or "/resume" && request.Method == HttpMethod.Post)
            {
                target.State = "running";
                return Json($$"""{"id":"{{target.Id}}","name":"{{vmName}}","state":"running"}""");
            }
            if (tail == "/stop" && request.Method == HttpMethod.Post)
            {
                target.State = "stopped";
                return Json($$"""{"id":"{{target.Id}}","name":"{{vmName}}","state":"stopped"}""");
            }
            if (tail == "/pause" && request.Method == HttpMethod.Post)
            {
                target.State = "paused";
                return Json($$"""{"id":"{{target.Id}}","name":"{{vmName}}","state":"paused"}""");
            }
            if (tail == "/labels" && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var label in doc.RootElement.GetProperty("labels").EnumerateObject())
                    target.Labels[label.Name] = label.Value.GetString() ?? "";
                return Json("{}");
            }
            if (tail == "/network" && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("mode", out var mode))
                    target.NetworkMode = mode.GetString();
                return Json("{}");
            }
            if (tail == "/exec" && request.Method == HttpMethod.Post)
            {
                if (ExecStartStatusOverride is { } st)
                    return Json("""{"message":"service said no"}""", st);
                var execId = "exec-" + Interlocked.Increment(ref _execSeq).ToString();
                var spec = ScriptedExecs.Count > 0 ? ScriptedExecs.Dequeue() : new FakeExecSpec();
                var exec = new FakeExec(execId, FailExecs ? 1 : spec.ExitCode)
                {
                    NeverCompletes = NextExecNeverCompletes,
                };
                NextExecNeverCompletes = false;
                foreach (var c in spec.StdoutChunks) exec.StdoutChunks.Enqueue(c);
                foreach (var c in spec.StderrChunks) exec.StderrChunks.Enqueue(c);
                _execs[vmName + ":" + execId] = exec;
                return Json($$"""{"execId":"{{execId}}","running":true}""", HttpStatusCode.Created);
            }
            if (tail.StartsWith("/exec/", StringComparison.Ordinal))
            {
                var execId = Uri.UnescapeDataString(tail["/exec/".Length..]);
                var key = vmName + ":" + execId;
                if (request.Method == HttpMethod.Delete)
                {
                    if (_execs.TryGetValue(key, out var killed))
                        killed.Killed = true;
                    _execs.TryRemove(key, out _);
                    return Json("{}");
                }
                if (request.Method == HttpMethod.Get && _execs.TryGetValue(key, out var polled))
                {
                    if (polled is { NeverCompletes: true, Killed: false })
                        return Json($$"""{"execId":"{{execId}}","running":true,"stdoutBase64":"","stderrBase64":""}""");
                    if (polled.StdoutChunks.Count > 0)
                        polled.StdoutSoFar += polled.StdoutChunks.Dequeue();
                    if (polled.StderrChunks.Count > 0)
                        polled.StderrSoFar += polled.StderrChunks.Dequeue();
                    if (polled.StdoutChunks.Count == 0 && polled.StderrChunks.Count == 0)
                        return Json($$"""{"execId":"{{execId}}","running":false,"exitCode":{{polled.ExitCode}},"stdoutBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(polled.StdoutSoFar))}}","stderrBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(polled.StderrSoFar))}}"}""");
                    return Json($$"""{"execId":"{{execId}}","running":true,"stdoutBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(polled.StdoutSoFar))}}","stderrBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(polled.StderrSoFar))}}"}""");
                }
                return Json("""{"message":"exec not found"}""", HttpStatusCode.NotFound);
            }
            if (tail == "/files" && request.Method == HttpMethod.Put)
            {
                using var doc = JsonDocument.Parse(body);
                var filePath = doc.RootElement.GetProperty("path").GetString()!;
                var content = doc.RootElement.GetProperty("contentBase64").GetString()!;
                Files[vmName + ":" + filePath] = content;
                var bytes = Convert.FromBase64String(content).Length;
                return Json($$"""{"bytes":{{bytes}}}""");
            }
            if (tail.StartsWith("/files", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var filePath = ParseQuery(query, "path");
                if (filePath is not null && Files.TryGetValue(vmName + ":" + filePath, out var content))
                    return Json($$"""{"path":"{{filePath}}","contentBase64":"{{content}}"}""");
                return Json("""{"message":"file not found"}""", HttpStatusCode.NotFound);
            }
            if (tail == "/archive" && request.Method == HttpMethod.Post)
                return Json("""{"files":1}""");
            if (tail.StartsWith("/archive", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (ArchiveResponses.Count > 0)
                {
                    var archive = ArchiveResponses.Dequeue();
                    var filePath = ParseQuery(query, "path") ?? "/data";
                    return Json($$"""{"path":"{{filePath}}","archiveBase64":"{{archive}}","files":1}""");
                }
                return Json("""{"message":"path not found"}""", HttpStatusCode.NotFound);
            }
            return Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
        }

        private static string? ParseQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return null;
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0)
                    continue;
                if (Uri.UnescapeDataString(part[..eq]) == key)
                    return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return null;
        }

        private static string SerializeLabels(Dictionary<string, string> labels) =>
            "{" + string.Join(",", labels.Select(kvp =>
                JsonSerializer.Serialize(kvp.Key) + ":" + JsonSerializer.Serialize(kvp.Value))) + "}";

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status);
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }

        private sealed class FakeVm(string id, string state)
        {
            public string Id { get; } = id;
            public string State { get; set; } = state;
            public string? Image { get; set; }
            public string? NetworkMode { get; set; }
            public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        }

        private sealed class FakeExec(string execId, int exitCode)
        {
            public string ExecId { get; } = execId;
            public int ExitCode { get; } = exitCode;
            public bool NeverCompletes { get; set; }
            public bool Killed { get; set; }
            public Queue<string> StdoutChunks { get; } = new();
            public Queue<string> StderrChunks { get; } = new();
            public string StdoutSoFar { get; set; } = string.Empty;
            public string StderrSoFar { get; set; } = string.Empty;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "boxlite-test-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
