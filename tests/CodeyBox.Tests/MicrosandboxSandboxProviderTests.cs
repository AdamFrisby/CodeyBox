using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.MicrosandboxPlugin;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape coverage for the microsandbox sandbox provider plugin. A real
/// service integration test cannot run in this suite — it needs a microsandbox
/// server binary plus host virtualization (KVM/Hypervisor.framework/WHPX) — so
/// the fake server below mirrors the <c>/v1</c> surface the provider drives
/// (sandbox lifecycle, exec start/poll/kill, files, labels, network,
/// snapshots, branch) with the response shapes recorded in
/// <c>MicrosandboxApiClient</c> ("Wire shapes"; see
/// docs/extending/microsandbox-sandbox-plugin.md, "recorded-shape fixture").
/// </summary>
public sealed class MicrosandboxSandboxProviderTests
{
    private const string TestTokenEnvVar = "CB_TEST_MICROSANDBOX_API_KEY";
    private const string TestToken = "microsandbox-test-key";

    private static MicrosandboxSandboxOptions TestOptions() => new()
    {
        Enabled = true,
        ServerUrl = "http://localhost/",
        ApiKeyEnvVar = TestTokenEnvVar,
        AllowUnsafeHttp = true,
        DefaultImage = "test-image:latest",
        PollIntervalMilliseconds = 100,
        ReadyTimeoutSeconds = 30,
        TransitionTimeoutSeconds = 30,
    };

    private static MicrosandboxSandboxProvider NewProvider(
        FakeMicrosandboxServer server,
        MicrosandboxSandboxOptions? options = null,
        Func<string, string?>? env = null,
        ILogger? log = null)
    {
        var opts = options ?? TestOptions();
        return new MicrosandboxSandboxProvider(
            () => opts,
            new HttpClient(server),
            env ?? (name => name == TestTokenEnvVar ? TestToken : null),
            TimeProvider.System,
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
    public void Kind_IsConstructibleAndNamesMicrosandbox()
    {
        var provider = new MicrosandboxSandboxProvider();
        Assert.Equal("microsandbox", provider.Name);
        Assert.Equal("codeybox.microsandbox-sandbox", MicrosandboxSandboxOptions.PluginId);
        Assert.False(SandboxProviderKinds.IsRegistered("microsandbox"),
            "plugin kinds must never collide with built-in provider kinds");
    }

    [Fact]
    public void IsolationLevel_IsDedicatedKernel_HardwareIsolatedMicroVm()
    {
        var provider = new MicrosandboxSandboxProvider();
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, provider.IsolationLevel);
    }

    [Fact]
    public void EgressClassification_IsNotEnforced_RegardlessOfPluginClaims()
    {
        var provider = new MicrosandboxSandboxProvider();
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement(provider.Name));
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement("microsandbox"));
    }

    [Fact]
    public void Catalog_SelectsKindByMemberProviderKind_AndSharesInstanceAcrossMembers()
    {
        var provider = new MicrosandboxSandboxProvider();
        var catalog = new PluginSandboxProviderCatalog([("codeybox.microsandbox-sandbox", provider)]);
        Assert.True(catalog.IsPluginKind("microsandbox"));
        Assert.True(catalog.TryGetProvider("microsandbox", out var resolved));
        Assert.Same(provider, resolved);

        var registry = new SandboxProviderRegistry(
            kind => catalog.TryGetProvider(kind, out var p) ? p : throw new InvalidOperationException(kind),
            pluginKinds: catalog.Kinds);
        var member1 = new SandboxMember { MemberId = "m1", ProviderKind = "microsandbox", Capacity = 2, PreferenceScore = 50 };
        var member2 = new SandboxMember { MemberId = "m2", ProviderKind = "microsandbox", Capacity = 4, PreferenceScore = 50 };
        Assert.Same(registry.Resolve(member1), registry.Resolve(member2));
    }

    [Fact]
    public void CapabilityGate_DropsUndeclaredWellKnownTags_KeepsDeclaredAndCustom()
    {
        var provider = new MicrosandboxSandboxProvider();
        var member = new SandboxMember
        {
            MemberId = "m1", ProviderKind = "microsandbox", Capacity = 2, PreferenceScore = 50,
            Capabilities = ["suspend-resume", "disk-guard", "port-publishing", "org-clearance-tag"],
        };
        var projected = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);
        Assert.Contains("suspend-resume", projected.Capabilities);
        Assert.Contains("org-clearance-tag", projected.Capabilities);
        Assert.DoesNotContain("disk-guard", projected.Capabilities);
        Assert.DoesNotContain("port-publishing", projected.Capabilities);
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementedInterfaces()
    {
        var provider = new MicrosandboxSandboxProvider();
        var declared = provider.DeclaredCapabilities;
        Assert.Contains(SandboxCapabilities.BaselineBake, declared);
        Assert.Contains(SandboxCapabilities.SuspendResume, declared);
        Assert.Contains(SandboxCapabilities.Teardown, declared);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, declared);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, declared);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, declared);
        Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);
        Assert.IsAssignableFrom<IBaselineImageResolver>(provider);
        Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
    }

    [Fact]
    public void Options_CarryNoSecretFields()
    {
        var secretLike = typeof(MicrosandboxSandboxOptions).GetProperties()
            .Where(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();
        Assert.Equal(["ApiKeyEnvVar"], secretLike);
    }

    [Fact]
    public async Task Placement_RefusesWorkRequiringUndeclaredCapability()
    {
        var server = new FakeMicrosandboxServer();
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
        var server = new FakeMicrosandboxServer();
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
    public async Task Placement_SelectsMicrosandboxMember_AndCreatesOnProvider()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);
        Assert.IsAssignableFrom<ISandbox>(sandbox);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/sandboxes"));
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Create wire shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_PostsExpectedRequestShape_AndAuthenticatesFromEnv()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var workItem = WorkItemId.New();

        var limits = new SandboxResourceLimits { CpuCount = 4, MemoryBytes = 8L * 1024 * 1024 * 1024, DiskBytes = 32L * 1024 * 1024 * 1024 };
        var spec = WorkSpec(workItem) with { Limits = limits };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/sandboxes");
        Assert.Equal("Bearer", create.AuthScheme);
        Assert.Equal(TestToken, create.AuthParam);

        using var body = JsonDocument.Parse(create.Body);
        var root = body.RootElement;
        Assert.StartsWith("codeybox-", root.GetProperty("name").GetString());
        Assert.Equal("test-image:latest", root.GetProperty("image").GetString());
        Assert.Equal(4, root.GetProperty("cpu").GetInt32());
        Assert.Equal(8192, root.GetProperty("memoryMib").GetInt32());
        Assert.Equal(32, root.GetProperty("diskGib").GetInt32());
        var labels = root.GetProperty("labels");
        Assert.Equal("true", labels.GetProperty("codeybox.managed").GetString());
        Assert.Equal(workItem.Value.ToString("N"), labels.GetProperty("codeybox.work-item").GetString());
        Assert.False(root.TryGetProperty("env", out _),
            "spec env must never ride the sandbox record — it carries secrets");
        Assert.Equal("isolated", root.GetProperty("network").GetProperty("mode").GetString());

        var net = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/network", StringComparison.Ordinal));
        using var netBody = JsonDocument.Parse(net.Body);
        Assert.Equal("isolated", netBody.RootElement.GetProperty("mode").GetString());

        await sandbox.DisposeAsync();
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/sandboxes/"));
    }

    [Fact]
    public async Task Create_WithAllowedHosts_RequestsRestrictedNetwork()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var spec = WorkSpec() with { Network = new SandboxNetworkPolicy { AllowedHosts = ["proxy.example", "registry.example:5000"] } };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/v1/sandboxes");
        using var body = JsonDocument.Parse(create.Body);
        var network = body.RootElement.GetProperty("network");
        Assert.Equal("restricted", network.GetProperty("mode").GetString());
        Assert.Equal(2, network.GetProperty("allowedHosts").GetArrayLength());
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Create_WithNetworkProfile_IsRefused()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var spec = WorkSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "internal" } };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message);
        Assert.Equal(0, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/sandboxes"));
    }

    [Fact]
    public async Task Create_RefusesWhenDisabled()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server, options: TestOptions() with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_RefusesWithoutCredentialEnvVar()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server, env: _ => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Contains(TestTokenEnvVar, ex.Message);
    }

    [Fact]
    public async Task Create_RefusesRemoteCleartextServerUrl()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server, options: TestOptions() with
        {
            ServerUrl = "http://192.0.2.10:5555/",
            AllowUnsafeHttp = true,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_DeletesSandbox_WhenProvisioningFails()
    {
        var server = new FakeMicrosandboxServer { FailExecs = true };
        var provider = NewProvider(server, options: TestOptions() with { SetupCommands = ["echo setup"] });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(1, server.CountRequests("DELETE", "/v1/sandboxes/"));
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
        var server = new FakeMicrosandboxServer { CreateStatusOverride = (HttpStatusCode)status };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(errorClass, ex.ErrorClass);
        Assert.True(ex.RecheckIn > TimeSpan.Zero);
    }

    [Fact]
    public async Task Create_ClassifiesUnreachableServiceAsInfrastructure()
    {
        var server = new FakeMicrosandboxServer { CreateFault = new HttpRequestException("connection refused") };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("unreachable", ex.ErrorClass);
    }

    [Fact]
    public async Task Create_ClassifiesQuotaExhaustionAsInfrastructure()
    {
        var server = new FakeMicrosandboxServer
        {
            CreateStatusOverride = HttpStatusCode.PaymentRequired,
            CreateErrorBody = """{"message":"sandbox quota exceeded for host"}""",
        };
        var provider = NewProvider(server);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
    }

    [Fact]
    public async Task Create_ClassifiesQuotaShapedForbiddenAsInfrastructure()
    {
        var server = new FakeMicrosandboxServer
        {
            CreateStatusOverride = HttpStatusCode.Forbidden,
            CreateErrorBody = """{"message":"quota limit exceeded: too many live sandboxes"}""",
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
        var server = new FakeMicrosandboxServer();
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

        var start = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/exec", StringComparison.Ordinal));
        using var startBody = JsonDocument.Parse(start.Body);
        Assert.Equal("echo", startBody.RootElement.GetProperty("argv")[0].GetString());
        Assert.True(startBody.RootElement.TryGetProperty("stdinBase64", out _));
        Assert.Equal("v1", startBody.RootElement.GetProperty("env").GetProperty("MY_VAR").GetString());
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_Cancellation_KillsGuestProcess()
    {
        var server = new FakeMicrosandboxServer();
        server.ScriptExec(stdoutChunks: ["partial"], stderrChunks: [], exitCode: 0, neverComplete: true);
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "30"] }, cts.Token));
        Assert.True(server.Requests.Any(r => r.Method == "POST" && r.Path.Contains("/kill", StringComparison.Ordinal)),
            "cancellation must kill the guest process");
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_OutputLimit_KillsAndFlags()
    {
        var server = new FakeMicrosandboxServer();
        server.ScriptExec(
            stdoutChunks: [new string('x', 4096)],
            stderrChunks: [],
            exitCode: 0);
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 16,
        }, CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.Path.Contains("/kill", StringComparison.Ordinal));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_ServiceFailure_IsExecutionUnavailable_NotDiffFailure()
    {
        var server = new FakeMicrosandboxServer { ExecStartStatusOverride = HttpStatusCode.ServiceUnavailable };
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.True(result.ExecutionUnavailable);
        Assert.Equal(255, result.ExitCode);
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // File in/out
    // ------------------------------------------------------------------

    [Fact]
    public async Task File_WriteThenRead_RoundTrips()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var sandbox = (MicrosandboxSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var content = Encoding.UTF8.GetBytes("file-contents-123");
        await sandbox.WriteFileAsync("/work/hello.txt", content, CancellationToken.None);
        var read = await sandbox.ReadFileAsync("/work/hello.txt", CancellationToken.None);
        Assert.Equal(content, read);

        var missing = await sandbox.ReadFileAsync("/work/nope.txt", CancellationToken.None);
        Assert.Null(missing);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task File_PathsMustBeAbsoluteContained()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var sandbox = (MicrosandboxSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sandbox.WriteFileAsync("../escape.txt", [1, 2, 3], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sandbox.ReadFileAsync("relative.txt", CancellationToken.None));
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Capacity / live load
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAcquires_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var acquirer = BuildAcquirer(provider, capacity: 1, memberCount: 2);

        var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);
        var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);

        var third = Task.Run(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None));
        var settleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        int ExactCreates() => server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/sandboxes");
        while (!third.IsCompleted
            && ExactCreates() == 2
            && DateTime.UtcNow < settleUntil)
        {
            await Task.Delay(5);
        }
        Assert.False(third.IsCompleted, "a third acquire must wait when both members are at capacity");
        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/sandboxes"));

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
                MemberId = $"microsandbox-{i}",
                ProviderKind = "microsandbox",
                Capacity = capacity,
                PreferenceScore = 50,
            })
            .ToList();
        var classes = new SandboxClassesSnapshot(
        [
            new SandboxClass { Id = "microsandbox-class", DisplayName = "microsandbox", Members = members },
        ]);
        var registry = new SandboxProviderRegistry(
            _ => provider,
            pluginKinds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "microsandbox" });
        return new SandboxPlacementAcquirer(classes, registry);
    }

    // ------------------------------------------------------------------
    // Suspend / retain / adopt / reconcile / branch / baselines
    // ------------------------------------------------------------------

    [Fact]
    public async Task Suspend_PausesSandbox_AndRetainReturnsAdoptableLease()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var sandbox = (MicrosandboxSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        await ((ISuspendableSandbox)sandbox).SuspendAsync();
        Assert.True(((ISuspendableSandbox)sandbox).IsSuspended);
        Assert.Equal(1, server.CountRequests("POST", $"/v1/sandboxes/{sandbox.Id}/pause"));

        var lease = await ((IPreemptibleSandbox)sandbox).RetainForInfrastructureRecoveryAsync();
        Assert.NotNull(lease);
        Assert.Equal("microsandbox", lease!.ProviderId);
        Assert.Equal(sandbox.Id, lease.SandboxId);
        Assert.False(string.IsNullOrWhiteSpace(lease.Token));

        await sandbox.DisposeAsync();
        Assert.Equal(0, server.CountRequests("DELETE", "/v1/sandboxes/" + sandbox.Id));

        var badLease = new SandboxRecoveryLease(lease.ProviderId, lease.SandboxId, lease.Token + "aa");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = badLease }, CancellationToken.None));

        server.SetState(sandbox.Id, "stopped");
        var adopted = await provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None);
        Assert.Equal(1, server.CountRequests("POST", $"/v1/sandboxes/{sandbox.Id}/start"));
        await adopted.DisposeAsync();
    }

    [Fact]
    public async Task Lease_OtherProviderKind_IsRefused()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var lease = new SandboxRecoveryLease("sprites", "codeybox-xyz", "token");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None));
    }

    [Fact]
    public async Task Resume_ResumesStoppedSandbox()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        server.SetState(sandbox.Id, "stopped");

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync(sandbox.Id, CancellationToken.None);
        Assert.True(
            server.CountRequests("POST", $"/v1/sandboxes/{sandbox.Id}/resume") == 1
            || server.CountRequests("POST", $"/v1/sandboxes/{sandbox.Id}/start") == 1);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Branch_ForksRunningSandbox_WithOwnIdentity()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var parent = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var branch = await provider.BranchSandboxAsync(parent.Id, WorkSpec(), CancellationToken.None);
        Assert.NotEqual(parent.Id, branch.Id);
        Assert.StartsWith("codeybox-", branch.Id);
        Assert.Equal(1, server.CountRequests("POST", $"/v1/sandboxes/{parent.Id}/branch"));
        Assert.Equal(2, provider.SnapshotActiveSandboxes().Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.BranchSandboxAsync("unmanaged-other", WorkSpec(), CancellationToken.None));

        await branch.DisposeAsync();
        await parent.DisposeAsync();
        Assert.Empty(provider.SnapshotActiveSandboxes());
    }

    [Fact]
    public async Task Baseline_EnsureListDispose_RoundTrip()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);

        var pinned = await provider.EnsureBaselineImageAsync("default", SandboxProfileFlavor.Headless, null, CancellationToken.None);
        Assert.Equal("codeybox-baseline-default-headless", pinned);
        var again = await provider.EnsureBaselineImageAsync("default", SandboxProfileFlavor.Headless, null, CancellationToken.None);
        Assert.Equal(pinned, again);
        Assert.True(
            server.Requests.Count(r => r.Method == "POST" && r.Path == "/v1/sandboxes") == 1,
            "the second ensure must reuse the existing snapshot, not bake again");

        var listed = await provider.ListBaselineImagesAsync(CancellationToken.None);
        Assert.Contains(listed, b => b.Name == pinned);

        await provider.DisposeBaselineImageAsync(pinned!, CancellationToken.None);
        Assert.DoesNotContain((await provider.ListBaselineImagesAsync(CancellationToken.None)).Select(b => b.Name), n => n == pinned);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeBaselineImageAsync("other-snapshot", CancellationToken.None));
    }

    [Fact]
    public void MightOwnSandbox_ScopesToManagedNamespace()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        Assert.True(provider.MightOwnSandbox($"codeybox-{Guid.NewGuid():N}", null));
        Assert.False(provider.MightOwnSandbox("someone-elses-vm", null));
    }

    [Fact]
    public async Task Inventory_ListsManaged_AndRefusesUnmanagedDisposal()
    {
        var server = new FakeMicrosandboxServer();
        var provider = NewProvider(server);
        var first = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var second = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var inventory = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Equal(2, inventory.Count);
        Assert.All(inventory, i => Assert.True(i.IsTrackedActive));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync("someone-elses-vm", CancellationToken.None));

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Recorded-shape fake server
    // ------------------------------------------------------------------

    private sealed class RecordedRequest(string method, string path, string body, string? authScheme, string? authParam)
    {
        public string Method { get; } = method;
        public string Path { get; } = path;
        public string Body { get; } = body;
        public string? AuthScheme { get; } = authScheme;
        public string? AuthParam { get; } = authParam;
    }

    private sealed class FakeSandboxState
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "running";
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FakeExecState
    {
        public string[] StdoutChunks { get; set; } = [];
        public string[] StderrChunks { get; set; } = [];
        public int ExitCode { get; set; }
        public bool NeverComplete { get; set; }
        public int Polls { get; set; }
        public bool Killed { get; set; }
    }

    private sealed class FakeMicrosandboxServer : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private int _execCounter;

        public ConcurrentQueue<RecordedRequest> RequestQueue { get; } = new();
        public List<RecordedRequest> Requests => RequestQueue.ToList();
        public HttpStatusCode? CreateStatusOverride { get; set; }
        public HttpStatusCode? ExecStartStatusOverride { get; set; }
        public string CreateErrorBody { get; set; } = string.Empty;
        public Exception? CreateFault { get; set; }
        public bool FailExecs { get; set; }

        private readonly ConcurrentDictionary<string, FakeSandboxState> _sandboxes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FakeExecState> _execs = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<FakeExecState> _scripts = new();
        private readonly ConcurrentDictionary<string, string> _snapshots = new(StringComparer.Ordinal);

        public void ScriptExec(string[] stdoutChunks, string[] stderrChunks, int exitCode, bool neverComplete = false) =>
            _scripts.Enqueue(new FakeExecState
            {
                StdoutChunks = stdoutChunks,
                StderrChunks = stderrChunks,
                ExitCode = exitCode,
                NeverComplete = neverComplete,
            });

        public void SetState(string name, string state)
        {
            if (_sandboxes.TryGetValue(name, out var sandbox))
                sandbox.State = state;
        }

        public int CountRequests(string method, string pathPrefix) =>
            Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            RequestQueue.Enqueue(new RecordedRequest(
                request.Method.Method, path, body,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));

            if (request.Headers.Authorization?.Parameter != TestToken)
                return Status(HttpStatusCode.Unauthorized, """{"message":"missing or invalid api key"}""");

            var segments = path.Trim('/').Split('/');
            // segments[0] == "v1"
            if (segments.Length >= 2 && segments[0] == "v1" && segments[1] == "sandboxes")
                return await HandleSandboxesAsync(segments[2..], request, body, ct);
            if (segments.Length == 2 && segments[0] == "v1" && segments[1] == "snapshots" && request.Method == HttpMethod.Get)
                return Json200(new
                {
                    snapshots = _snapshots.Select(kvp => new { snapshot = kvp.Key, state = "ready" }).ToList(),
                });
            if (segments.Length == 3 && segments[0] == "v1" && segments[1] == "snapshots" && request.Method == HttpMethod.Delete)
            {
                _snapshots.TryRemove(Uri.UnescapeDataString(segments[2]), out _);
                return Status(HttpStatusCode.OK, "{}");
            }

            return Status(HttpStatusCode.NotFound, """{"message":"unknown route"}""");
        }

        private async Task<HttpResponseMessage> HandleSandboxesAsync(
            string[] rest, HttpRequestMessage request, string body, CancellationToken ct)
        {
            _ = ct;
            if (rest.Length == 0)
            {
                if (request.Method == HttpMethod.Post)
                    return await HandleCreateAsync(body);
                if (request.Method == HttpMethod.Get)
                {
                    var query = request.RequestUri!.Query;
                    var prefix = GetQuery(query, "prefix") ?? string.Empty;
                    return Json200(new
                    {
                        sandboxes = _sandboxes.Values
                            .Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal))
                            .Select(s => new { name = s.Name, state = s.State, labels = s.Labels })
                            .ToList(),
                        nextCursor = (string?)null,
                    });
                }

                return Status(HttpStatusCode.MethodNotAllowed, "{}");
            }

            var name = Uri.UnescapeDataString(rest[0]);
            if (rest.Length == 1)
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (!_sandboxes.TryGetValue(name, out var sandbox))
                        return Status(HttpStatusCode.NotFound, """{"message":"not found"}""");
                    return Json200(new { name = sandbox.Name, state = sandbox.State, labels = sandbox.Labels });
                }

                if (request.Method == HttpMethod.Delete)
                {
                    _sandboxes.TryRemove(name, out _);
                    return Status(HttpStatusCode.OK, "{}");
                }

                return Status(HttpStatusCode.MethodNotAllowed, "{}");
            }

            if (!_sandboxes.TryGetValue(name, out var target))
                return Status(HttpStatusCode.NotFound, """{"message":"not found"}""");

            var action = rest[1];
            if (rest.Length == 2)
            {
                switch (action)
                {
                    case "pause" when request.Method == HttpMethod.Post:
                        target.State = "paused";
                        return Status(HttpStatusCode.OK, "{}");
                    case "resume" or "start" when request.Method == HttpMethod.Post:
                        target.State = "running";
                        return Status(HttpStatusCode.OK, "{}");
                    case "stop" when request.Method == HttpMethod.Post:
                        target.State = "stopped";
                        return Status(HttpStatusCode.OK, "{}");
                    case "exec" when request.Method == HttpMethod.Post:
                        return HandleStartExec(name, target, body);
                    case "files" when request.Method == HttpMethod.Put:
                        return HandleWriteFile(target, body);
                    case "files" when request.Method == HttpMethod.Get:
                        return HandleReadFile(target, request);
                    case "network" when request.Method == HttpMethod.Post:
                        return Status(HttpStatusCode.OK, "{}");
                    case "labels" when request.Method == HttpMethod.Post:
                        return HandleLabels(target, body);
                    case "snapshot" when request.Method == HttpMethod.Post:
                        return HandleSnapshot(body);
                    case "branch" when request.Method == HttpMethod.Post:
                        return HandleBranch(target, body);
                }

                return Status(HttpStatusCode.NotFound, """{"message":"unknown action"}""");
            }

            if (rest.Length == 3 && action == "exec" && request.Method == HttpMethod.Get)
            {
                var execId = Uri.UnescapeDataString(rest[2]);
                if (!_execs.TryGetValue(execId, out var exec))
                    return Status(HttpStatusCode.NotFound, """{"message":"exec not found"}""");
                return HandlePollExec(exec);
            }

            if (rest.Length == 4 && action == "exec" && rest[3] == "kill" && request.Method == HttpMethod.Post)
            {
                var execId = Uri.UnescapeDataString(rest[2]);
                if (_execs.TryGetValue(execId, out var exec))
                    exec.Killed = true;
                return Status(HttpStatusCode.OK, "{}");
            }

            return Status(HttpStatusCode.NotFound, """{"message":"unknown route"}""");
        }

        private Task<HttpResponseMessage> HandleCreateAsync(string body)
        {
            if (CreateFault is not null)
                return Task.FromException<HttpResponseMessage>(CreateFault);
            if (CreateStatusOverride is { } status)
                return Task.FromResult(Status(status, string.IsNullOrEmpty(CreateErrorBody) ? "{}" : CreateErrorBody));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString()!;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in root.GetProperty("labels").EnumerateObject())
                labels[prop.Name] = prop.Value.GetString() ?? string.Empty;
            _sandboxes[name] = new FakeSandboxState { Name = name, State = "running", };
            foreach (var kv in labels)
                _sandboxes[name].Labels[kv.Key] = kv.Value;
            return Task.FromResult(Json200(new { name, state = "starting", labels }));
        }

        private HttpResponseMessage HandleStartExec(string sandboxName, FakeSandboxState target, string body)
        {
            _ = target;
            if (ExecStartStatusOverride is { } status)
                return Status(status, """{"message":"service unavailable"}""");
            var execId = $"exec-{Interlocked.Increment(ref _execCounter)}";
            FakeExecState exec;
            if (FailExecs)
            {
                exec = new FakeExecState { StdoutChunks = [], StderrChunks = ["setup failed"], ExitCode = 1 };
            }
            else if (!_scripts.TryDequeue(out exec!))
            {
                exec = new FakeExecState { StdoutChunks = [], StderrChunks = [], ExitCode = 0 };
            }

            _ = sandboxName;
            _ = body;
            _execs[execId] = exec;
            return Json200(new { execId });
        }

        private static HttpResponseMessage HandlePollExec(FakeExecState exec)
        {
            if (exec.NeverComplete && !exec.Killed)
                return Json200(new
                {
                    status = "running",
                    exitCode = (int?)null,
                    stdout = exec.StdoutChunks.Length > 0 ? exec.StdoutChunks[0] : string.Empty,
                    stderr = string.Empty,
                });
            if (exec.Polls == 0 && (exec.StdoutChunks.Length > 1 || exec.StderrChunks.Length > 1))
            {
                exec.Polls++;
                return Json200(new
                {
                    status = "running",
                    exitCode = (int?)null,
                    stdout = exec.StdoutChunks.Length > 0 ? exec.StdoutChunks[0] : string.Empty,
                    stderr = exec.StderrChunks.Length > 0 ? exec.StderrChunks[0] : string.Empty,
                });
            }

            exec.Polls++;
            return Json200(new
            {
                status = "done",
                exitCode = (int?)exec.ExitCode,
                stdout = string.Concat(exec.StdoutChunks),
                stderr = string.Concat(exec.StderrChunks),
            });
        }

        private static HttpResponseMessage HandleWriteFile(FakeSandboxState target, string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var path = root.GetProperty("path").GetString()!;
            var content = root.GetProperty("contentBase64").GetString() ?? string.Empty;
            target.Files[path] = content;
            return Status(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage HandleReadFile(FakeSandboxState target, HttpRequestMessage request)
        {
            var path = GetQuery(request.RequestUri!.Query, "path") ?? string.Empty;
            path = Uri.UnescapeDataString(path);
            if (!target.Files.TryGetValue(path, out var content))
                return Json200(new { path, contentBase64 = (string?)null, exists = false });
            return Json200(new { path, contentBase64 = content, exists = true });
        }

        private static HttpResponseMessage HandleLabels(FakeSandboxState target, string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("labels", out var labels))
            {
                foreach (var prop in labels.EnumerateObject())
                    target.Labels[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }

            return Status(HttpStatusCode.OK, "{}");
        }

        private HttpResponseMessage HandleSnapshot(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var snapshot = doc.RootElement.GetProperty("snapshot").GetString()!;
            _snapshots[snapshot] = "ready";
            return Json200(new { snapshot, state = "ready" });
        }

        private HttpResponseMessage HandleBranch(FakeSandboxState target, string body)
        {
            using var doc = JsonDocument.Parse(body);
            var branchName = doc.RootElement.GetProperty("name").GetString()!;
            var branch = new FakeSandboxState { Name = branchName, State = "running" };
            foreach (var kv in target.Labels)
                branch.Labels[kv.Key] = kv.Value;
            _sandboxes[branchName] = branch;
            return Json200(new { name = branchName, state = "running", labels = branch.Labels });
        }

        private static string? GetQuery(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = part.IndexOf('=');
                if (index < 0)
                    continue;
                if (Uri.UnescapeDataString(part[..index]) == key)
                    return Uri.UnescapeDataString(part[(index + 1)..]);
            }

            return null;
        }

        private static HttpResponseMessage Json200(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage Status(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
