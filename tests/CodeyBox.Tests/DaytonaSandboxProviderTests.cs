using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.DaytonaSandboxPlugin;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape coverage for the Daytona sandbox provider plugin. A real
/// service integration test cannot run in this suite — it needs a Daytona API
/// key, an organization with quota, and outbound network — so the fake server
/// below mirrors the Daytona API surface the provider drives (sandbox
/// lifecycle, session exec, input, command status, labels, snapshots) with
/// response shapes recorded from daytonaio/daytona's OpenAPI definitions
/// (see docs/extending/daytona-sandbox-plugin.md, "recorded-shape fixture").
/// </summary>
public sealed class DaytonaSandboxProviderTests
{
    private const string TestApiKeyEnvVar = "CB_TEST_DAYTONA_API_KEY";
    private const string TestApiKey = "daytona-test-api-key";

    private static DaytonaSandboxOptions TestOptions(int? readyTimeout = null) => new()
    {
        Enabled = true,
        ApiUrl = "http://localhost/api/",
        ToolboxProxyUrl = "http://localhost/toolbox/",
        ApiKeyEnvVar = TestApiKeyEnvVar,
        AllowUnsafeHttp = true,
        DefaultSnapshot = "codeybox-base",
        PollIntervalMilliseconds = 1,
        ReadyTimeoutSeconds = readyTimeout ?? 30,
        TransitionTimeoutSeconds = 30,
    };

    private static DaytonaSandboxProvider NewProvider(
        FakeDaytonaServer server,
        IDaytonaWebSocketFactory wsFactory,
        DaytonaSandboxOptions? options = null,
        Func<string, string?>? env = null,
        ILogger? log = null)
    {
        var opts = options ?? TestOptions();
        return new DaytonaSandboxProvider(
            () => opts,
            new HttpClient(server),
            wsFactory,
            env ?? (name => name == TestApiKeyEnvVar ? TestApiKey : null),
            TimeProvider.System,
            timings: null,
            log ?? NullLogger.Instance);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Messages.Add($"{level}: {fmt(state, ex)} {ex}");
    }

    private static SandboxSpec WorkSpec(WorkItemId? workItem = null, string image = "codeybox-base") => new()
    {
        ImageReference = image,
        WorkingDirectory = "/work",
        TimingWorkItemId = workItem,
    };

    // ------------------------------------------------------------------
    // Kind / placement / trust
    // ------------------------------------------------------------------

    [Fact]
    public void Kind_IsConstructibleAndNamesDaytona()
    {
        var provider = new DaytonaSandboxProvider();
        Assert.Equal("daytona", provider.Name);
        Assert.Equal("codeybox.daytona-sandbox", DaytonaSandboxOptions.PluginId);
        Assert.False(SandboxProviderKinds.IsRegistered("daytona"),
            "plugin kinds must never collide with built-in provider kinds");
    }

    [Fact]
    public void EgressClassification_IsNotEnforced_RegardlessOfPluginClaims()
    {
        var provider = new DaytonaSandboxProvider();
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement(provider.Name));
        // Even a plugin claiming dedicated-kernel isolation cannot promote the kind.
        Assert.Equal(EgressEnforcementLocation.NotEnforced,
            HostPlatformSupport.GetEgressEnforcement("daytona"));
    }

    [Fact]
    public void Catalog_SelectsKindByMemberProviderKind_AndSharesInstanceAcrossMembers()
    {
        var provider = new DaytonaSandboxProvider();
        var catalog = new PluginSandboxProviderCatalog([("codeybox.daytona-sandbox", provider)]);
        Assert.True(catalog.IsPluginKind("daytona"));
        Assert.True(catalog.TryGetProvider("daytona", out var resolved));
        Assert.Same(provider, resolved);

        var registry = new SandboxProviderRegistry(
            kind => catalog.TryGetProvider(kind, out var p) ? p : throw new InvalidOperationException(kind),
            pluginKinds: catalog.Kinds);
        var member1 = new SandboxMember { MemberId = "d1", ProviderKind = "daytona", Capacity = 2, PreferenceScore = 50 };
        var member2 = new SandboxMember { MemberId = "d2", ProviderKind = "daytona", Capacity = 4, PreferenceScore = 50 };
        Assert.Same(registry.Resolve(member1), registry.Resolve(member2));
    }

    [Fact]
    public void CapabilityGate_DropsUndeclaredWellKnownTags_KeepsDeclaredAndCustom()
    {
        var provider = new DaytonaSandboxProvider();
        var member = new SandboxMember
        {
            MemberId = "d1", ProviderKind = "daytona", Capacity = 2, PreferenceScore = 50,
            Capabilities = ["suspend-resume", "disk-guard", "org-clearance-tag"],
        };
        var projected = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);
        Assert.Contains("suspend-resume", projected.Capabilities);
        Assert.Contains("org-clearance-tag", projected.Capabilities); // operator clearance tags pass through
        Assert.DoesNotContain("disk-guard", projected.Capabilities);
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementedInterfaces()
    {
        var provider = new DaytonaSandboxProvider();
        var declared = provider.DeclaredCapabilities;
        Assert.Contains(SandboxCapabilities.BaselineBake, declared);
        Assert.Contains(SandboxCapabilities.SuspendResume, declared);
        Assert.Contains(SandboxCapabilities.Teardown, declared);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, declared);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, declared);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, declared);
        // Every declared capability is backed by the matching interface.
        Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);
        Assert.IsAssignableFrom<ISuspendingSandboxProvider>(provider);
    }

    [Fact]
    public async Task Placement_RefusesWorkRequiringUndeclaredCapability()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
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
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var acquisition = new SandboxPlacementAcquisition(
            WorkItemId.New(), "work", [], RequiredCredential: null, RequiredNetworkProfile: "internal",
            WorkSpec());
        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(
            () => acquirer.AcquireAsync(acquisition, CancellationToken.None));
        Assert.Contains("egress", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Placement_SelectsDaytonaMember_AndCreatesOnProvider()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var acquirer = BuildAcquirer(provider, capacity: 2);

        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [], null, null, WorkSpec()),
            CancellationToken.None);
        Assert.IsAssignableFrom<ISandbox>(sandbox);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "POST" && r.Path == "/api/sandbox"));
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Create wire shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_PostsExpectedRequestShape_AndAuthenticatesFromEnv()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(),
            options: TestOptions() with { OrganizationId = "org-xyz" });
        var workItem = WorkItemId.New();

        var limits = new SandboxResourceLimits { CpuCount = 4, MemoryBytes = 8L * 1024 * 1024 * 1024, DiskBytes = 32L * 1024 * 1024 * 1024 };
        var spec = WorkSpec(workItem) with { Limits = limits };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/api/sandbox");
        Assert.Equal("Bearer", create.AuthorizationScheme);
        Assert.Equal(TestApiKey, create.AuthorizationParameter);
        Assert.Equal("org-xyz", create.OrganizationHeader);

        using var body = JsonDocument.Parse(create.Body);
        var root = body.RootElement;
        Assert.StartsWith("codeybox-", root.GetProperty("name").GetString());
        Assert.Equal("codeybox-base", root.GetProperty("snapshot").GetString());
        Assert.False(root.GetProperty("public").GetBoolean());
        Assert.Equal(4, root.GetProperty("cpu").GetInt32());
        Assert.Equal(8, root.GetProperty("memory").GetInt32());
        Assert.Equal(32, root.GetProperty("disk").GetInt32());
        var labels = root.GetProperty("labels");
        Assert.Equal("true", labels.GetProperty("codeybox.managed").GetString());
        Assert.Equal(workItem.Value.ToString("N"), labels.GetProperty("codeybox.work-item").GetString());
        Assert.False(root.TryGetProperty("envVars", out _), "spec env must never ride the sandbox record — it carries secrets");

        // The network-settings call lands after create (bake-then-lock ordering).
        var net = server.Requests.Single(r => r.Path.EndsWith("/network-settings", StringComparison.Ordinal));
        using var netBody = JsonDocument.Parse(net.Body);
        Assert.True(netBody.RootElement.GetProperty("networkBlockAll").GetBoolean(),
            "Denied policy must become networkBlockAll on the service");

        await sandbox.DisposeAsync();
        Assert.Equal(1, server.CountRequests("DELETE", "/api/sandbox/"));
    }

    [Fact]
    public async Task Create_RefusesWhenDisabled()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(),
            options: TestOptions() with { Enabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_RefusesWithoutCredentialEnvVar()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(),
            env: _ => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Contains(TestApiKeyEnvVar, ex.Message);
    }

    [Fact]
    public async Task Create_DeletesSandbox_WhenProvisioningFails()
    {
        var server = new FakeDaytonaServer { FailSetupCommands = true };
        var wsFactory = new QueueDaytonaWebSocketFactory(new FakeDaytonaWebSocket());
        var provider = NewProvider(server, wsFactory,
            options: TestOptions() with { SetupCommands = ["echo setup"] });

        // A non-zero setup command is a deterministic provisioning failure —
        // the sandbox is still deleted and no deferral is raised (deferrals are
        // for service-side refusals, not in-guest command exits).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(1, server.CountRequests("DELETE", "/api/sandbox/"));
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
        var server = new FakeDaytonaServer { CreateStatusOverride = (HttpStatusCode)status };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal(errorClass, ex.ErrorClass);
        Assert.True(ex.RecheckIn > TimeSpan.Zero);
    }

    [Fact]
    public async Task Create_ClassifiesUnreachableServiceAsInfrastructure()
    {
        var server = new FakeDaytonaServer { CreateFault = new HttpRequestException("connection refused") };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("unreachable", ex.ErrorClass);
    }

    [Fact]
    public async Task Create_ClassifiesQuotaExhaustionAsInfrastructure()
    {
        var server = new FakeDaytonaServer
        {
            CreateStatusOverride = HttpStatusCode.Forbidden,
            CreateErrorBody = """{"message":"sandbox quota exceeded for organization"}""",
        };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(WorkSpec(), CancellationToken.None));
        Assert.Equal("quota-exhausted", ex.ErrorClass);
    }

    // ------------------------------------------------------------------
    // Exec
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exec_StreamsOutputOverWebsocket_AndReportsExitCode()
    {
        var server = new FakeDaytonaServer();
        var socket = new FakeDaytonaWebSocket();
        socket.EnqueueStdout("hello ");
        socket.EnqueueStderr("oops");
        socket.EnqueueStdout("world");
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(socket));

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

        // Session lifecycle on the toolbox proxy path.
        var sessionCreate = server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/process/session", StringComparison.Ordinal));
        Assert.Contains("sess", sessionCreate.Path, StringComparison.Ordinal); // path sanity only
        var exec = server.Requests.Single(r => r.Path.EndsWith("/exec", StringComparison.Ordinal) && r.Method == "POST");
        using var execBody = JsonDocument.Parse(exec.Body);
        Assert.True(execBody.RootElement.GetProperty("runAsync").GetBoolean());
        var command = execBody.RootElement.GetProperty("command").GetString()!;
        Assert.Contains("dd bs=1", command, StringComparison.Ordinal);
        Assert.Contains("'echo' 'hi'", command, StringComparison.Ordinal);

        var input = server.Requests.Single(r => r.Path.Contains("/input", StringComparison.Ordinal));
        Assert.Contains("MY_VAR=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("v1")), input.Body);
        Assert.Contains("input-text", input.Body);

        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/process/session/", StringComparison.Ordinal)));
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains("/process/session/", socket.ConnectedUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("follow=true", socket.ConnectedUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal(TestApiKey, socket.BearerToken);

        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_EnvironmentRemoval_AppliedInWirePayload()
    {
        var server = new FakeDaytonaServer();
        var socket = new FakeDaytonaWebSocket();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(socket));
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
        var input = server.Requests.Single(r => r.Path.Contains("/input", StringComparison.Ordinal));
        Assert.Contains("!DROP_ME", input.Body);
        Assert.Contains("KEEP_ME=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("x")), input.Body);
        Assert.DoesNotContain("DROP_ME=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("y")), input.Body);
    }

    [Fact]
    public async Task Exec_Cancellation_KillsRemoteSession()
    {
        var server = new FakeDaytonaServer();
        var socket = new BlockingDaytonaWebSocket();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(socket));
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);
        await socket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/process/session/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_OutputLimit_KillOnOutputLimit_KillsSession()
    {
        var server = new FakeDaytonaServer();
        var socket = new FakeDaytonaWebSocket();
        socket.EnqueueStdout(new string('x', 200));
        socket.EnqueueStdout("extra");
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(socket));
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 64,
            KillOnOutputLimit = true,
        }, CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.Equal(1, server.Requests.Count(r => r.Method == "DELETE" && r.Path.Contains("/process/session/", StringComparison.Ordinal)));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_ServiceFailure_IsExecutionUnavailable_NotDiffFailure()
    {
        var server = new FakeDaytonaServer { ExecExecuteStatusOverride = HttpStatusCode.ServiceUnavailable };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.True(result.ExecutionUnavailable);
        Assert.Equal(255, result.ExitCode);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_WebsocketFallback_PollsCommandRecord()
    {
        // A daemon without WS follow: the connect upgrade fails with a service
        // error and the provider falls back to command-record polling + the
        // logs snapshot.
        var server = new FakeDaytonaServer { CommandLogsBody = """{"output":"fallback-out"}""" };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(
            new ConnectFailingDaytonaWebSocket()));
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fallback-out", result.Stdout);
        Assert.False(result.ExecutionUnavailable);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Exec_WebsocketFallback_OversizedLogsSnapshot_IsExecutionUnavailable()
    {
        // The non-follow logs snapshot is guest-influenced output: a body
        // larger than the snapshot ceiling must classify as infrastructure
        // (ExecutionUnavailable), never buffer unbounded into the host.
        var server = new FakeDaytonaServer
        {
            CommandLogsBody = "{\"output\":\"" + new string('x', 2 * 1024 * 1024) + "\"}",
        };
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(
            new ConnectFailingDaytonaWebSocket()));
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["true"],
            MaxStdoutBytes = 64,
            MaxStderrBytes = 64,
        }, CancellationToken.None);
        Assert.True(result.ExecutionUnavailable);
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Capacity / live load
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAcquires_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
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
        int ExactCreates() => server.Requests.Count(r => r.Method == "POST" && r.Path == "/api/sandbox");
        while (!third.IsCompleted
            && ExactCreates() == 2
            && DateTime.UtcNow < settleUntil)
        {
            await Task.Delay(5);
        }
        Assert.False(third.IsCompleted, "a third acquire must wait when both members are at capacity");
        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "POST" && r.Path == "/api/sandbox"));

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
                MemberId = $"daytona-{i}",
                ProviderKind = "daytona",
                Capacity = capacity,
                PreferenceScore = 50,
            })
            .ToList();
        var classes = new SandboxClassesSnapshot(
        [
            new SandboxClass { Id = "daytona-class", DisplayName = "daytona", Members = members },
        ]);
        var registry = new SandboxProviderRegistry(
            _ => provider,
            pluginKinds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "daytona" });
        return new SandboxPlacementAcquirer(classes, registry);
    }

    // ------------------------------------------------------------------
    // Suspend / retain / adopt / reconcile
    // ------------------------------------------------------------------

    [Fact]
    public async Task Suspend_PausesSandbox_AndRetainReturnsAdoptableLease()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = (DaytonaSandbox)await provider.CreateAsync(WorkSpec(), CancellationToken.None);

        await ((ISuspendableSandbox)sandbox).SuspendAsync();
        Assert.True(((ISuspendableSandbox)sandbox).IsSuspended);
        Assert.Equal(1, server.CountRequests("POST", $"/api/sandbox/{sandbox.Id}/pause"));

        var lease = await ((IPreemptibleSandbox)sandbox).RetainForInfrastructureRecoveryAsync();
        Assert.NotNull(lease);
        Assert.Equal("daytona", lease!.ProviderId);
        Assert.Equal(sandbox.Id, lease.SandboxId);
        Assert.False(string.IsNullOrWhiteSpace(lease.Token));

        // Disposal preserves (no DELETE) because the sandbox is suspended/retained.
        await sandbox.DisposeAsync();
        Assert.Equal(0, server.CountRequests("DELETE", "/api/sandbox/" + sandbox.Id));

        // Wrong token → adoption refused.
        var badLease = new SandboxRecoveryLease(lease.ProviderId, lease.SandboxId, lease.Token + "aa");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = badLease }, CancellationToken.None));

        // Correct token → start called, handle returned.
        server.SetState(sandbox.Id, "stopped");
        var adopted = await provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None);
        Assert.Equal(1, server.CountRequests("POST", $"/api/sandbox/{sandbox.Id}/start"));
        await adopted.DisposeAsync();
    }

    [Fact]
    public async Task Lease_OtherProviderKind_IsRefused()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var lease = new SandboxRecoveryLease("sprites", "codeybox-xyz", "token");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(WorkSpec() with { RecoveryLease = lease }, CancellationToken.None));
    }

    [Fact]
    public async Task Resume_ResumesStoppedSandbox()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        server.SetState(sandbox.Id, "stopped");

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync(sandbox.Id, CancellationToken.None);
        Assert.Equal(1, server.CountRequests("POST", $"/api/sandbox/{sandbox.Id}/start"));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_DeletesOrphanedStoppedSandboxes_KeepsLiveSuspended()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        server.SetState(sandbox.Id, "stopped");
        server.AddOrphan("codeybox-orphan1", "stopped");
        server.AddOrphan("foreign-sandbox", "stopped"); // unmanaged name — never listed

        var failures = await provider.ReconcileStuckSandboxesAsync(
            new HashSet<string>(StringComparer.Ordinal) { sandbox.Id }, CancellationToken.None);
        Assert.Empty(failures);
        Assert.Equal(0, server.CountRequests("DELETE", "/api/sandbox/" + sandbox.Id));
        Assert.Equal(1, server.CountRequests("DELETE", "/api/sandbox/codeybox-orphan1"));
        Assert.Equal(0, server.CountRequests("DELETE", "/api/sandbox/foreign-sandbox"));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WaitForAdoptedAgentCompletion_PollsExitMarker_AndTailsLog()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var logPath = "/work/.codeybox/agent-logs/agent.jsonl";
        server.Files[$"{logPath}"] = "line1\nline2\n";
        server.Files[$"{logPath}.exit"] = "0";

        var tail = new StringBuilder();
        var exit = await ((ISuspendingSandboxProvider)provider).WaitForAdoptedAgentCompletionAsync(
            sandbox.Id, logPath, s => tail.Append(s), deadline: TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal("line1\nline2\n", tail.ToString());
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Baselines
    // ------------------------------------------------------------------

    [Fact]
    public void BaselineRef_IsDeterministic_ContentHash()
    {
        var provider = NewProvider(new FakeDaytonaServer(), new QueueDaytonaWebSocketFactory(),
            options: TestOptions() with { BaselineSourceImage = "codeybox-base", SetupCommands = ["apt update"] });
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
        var server = new FakeDaytonaServer();
        var ws = new FakeDaytonaWebSocket(); // for the bake sandbox's setup commands
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(ws),
            options: TestOptions() with { BaselineSourceImage = "codeybox-base", SetupCommands = ["true"] });

        var name = await provider.EnsureBaselineImageAsync("p1", SandboxProfileFlavor.Headless, pinnedBaselineRef: null, CancellationToken.None);
        Assert.NotNull(name);

        var snapshotPost = server.Requests.Single(r => r.Path.EndsWith("/snapshot", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(snapshotPost.Body);
        Assert.Equal(name, body.RootElement.GetProperty("name").GetString());

        // Bake sandbox deleted; bake sandbox not the caller-visible name.
        Assert.Equal(1, server.CountRequests("DELETE", "/api/sandbox/codeybox-bake-"));

        // Second call reuses the now-active snapshot.
        var name2 = await provider.EnsureBaselineImageAsync("p1", SandboxProfileFlavor.Headless, pinnedBaselineRef: name, CancellationToken.None);
        Assert.Equal(name, name2);
    }

    [Fact]
    public async Task Create_UsesBaselineImageRefAsSnapshot()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec() with { BaselineImageRef = "codeybox-baseline-abc" }, CancellationToken.None);
        var create = server.Requests.Single(r => r.Method == "POST" && r.Path == "/api/sandbox");
        using var body = JsonDocument.Parse(create.Body);
        Assert.Equal("codeybox-baseline-abc", body.RootElement.GetProperty("snapshot").GetString());
        await sandbox.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Mounts / credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_WithCredentialMount_Refuses()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var spec = WorkSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/run/codeybox/creds", HostPath = "/tmp", ReadOnly = true }],
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Equal(0, server.Requests.Count(r => r.Method == "POST" && r.Path == "/api/sandbox"));
    }

    [Fact]
    public async Task Sandbox_ImplementsCredentialFileRefusal()
    {
        var server = new FakeDaytonaServer();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory());
        var sandbox = await provider.CreateAsync(WorkSpec(), CancellationToken.None);
        var refusal = Assert.IsAssignableFrom<IRejectsFileBackedAgentCredentials>(sandbox);
        Assert.False(string.IsNullOrWhiteSpace(refusal.FileBackedAgentCredentialsUnsupportedReason));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WritableMount_SyncsBackAtomically_OnDispose()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "seed.txt"), "seed");
        var server = new FakeDaytonaServer();

        // Exec sockets are consumed in order: (1) the create-time staging
        // upload, (2) the dispose-time sync-back exec whose stdout is the
        // sandbox's base64 tar.gz of the mount.
        var uploadSocket = new FakeDaytonaWebSocket();
        var syncBackSocket = new FakeDaytonaWebSocket();
        using var replacement = new TempDirectory();
        File.WriteAllText(Path.Combine(replacement.Path, "from-sandbox.txt"), "new-content");
        var archiveB64 = CreateTarGzBase64(replacement.Path);
        syncBackSocket.EnqueueStdout(archiveB64);

        var logger = new ListLogger();
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(uploadSocket, syncBackSocket), log: logger);
        var spec = WorkSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = dir.Path, ReadOnly = false }],
        };
        var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
        await sandbox.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(dir.Path, "from-sandbox.txt")),
            "sync-back did not land; provider log: " + string.Join(" | ", logger.Messages));

        // The host directory was atomically replaced by the sandbox archive.
        Assert.True(File.Exists(Path.Combine(dir.Path, "from-sandbox.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "seed.txt")));
        Assert.Equal("new-content", File.ReadAllText(Path.Combine(dir.Path, "from-sandbox.txt")));
    }

    [Fact]
    public async Task WritableMount_SyncRefusesArchiveWithTraversal()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "keep.txt"), "keep");
        var server = new FakeDaytonaServer();

        var syncBackSocket = new FakeDaytonaWebSocket();
        // A hostile archive: entry escaping the target dir.
        syncBackSocket.EnqueueStdout(CreateTarGzBase64WithTraversal());
        var provider = NewProvider(server, new QueueDaytonaWebSocketFactory(new FakeDaytonaWebSocket(), syncBackSocket));
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

    [Fact]
    public void Options_CarryNoSecretFields()
    {
        // The options record must never hold credential material — only the
        // env var NAME that supplies it through the credential chain.
        var secretLike = typeof(DaytonaSandboxOptions).GetProperties()
            .Where(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();
        Assert.Equal(["ApiKeyEnvVar"], secretLike);
    }

    // ------------------------------------------------------------------
    // Trust-boundary guards (audit regression)
    // ------------------------------------------------------------------

    [Fact]
    public void ServiceUrl_HttpRejected_WithoutUnsafeHttpOptIn()
    {
        Assert.Null(DaytonaApiClient.TryParseAbsoluteUrl(null, "sandbox toolboxProxyUrl"));
        Assert.Null(DaytonaApiClient.TryParseAbsoluteUrl("  ", "sandbox toolboxProxyUrl"));

        var https = DaytonaApiClient.TryParseAbsoluteUrl("https://proxy.example/toolbox", "sandbox toolboxProxyUrl");
        Assert.Equal("https://proxy.example/toolbox", https!.ToString());

        // A service-returned cleartext URL must never carry the API key unless
        // the operator opted into local-test http.
        var refused = Assert.Throws<DaytonaApiException>(() =>
            DaytonaApiClient.TryParseAbsoluteUrl("http://proxy.example/toolbox", "sandbox toolboxProxyUrl"));
        Assert.Equal(DaytonaFailureKind.Unexpected, refused.Kind);
        Assert.Contains("AllowUnsafeHttp", refused.Message);

        var allowed = DaytonaApiClient.TryParseAbsoluteUrl(
            "http://localhost/toolbox", "sandbox toolboxProxyUrl", allowUnsafeHttp: true);
        Assert.Equal("http://localhost/toolbox", allowed!.ToString());

        Assert.Throws<DaytonaApiException>(() =>
            DaytonaApiClient.TryParseAbsoluteUrl("ftp://proxy.example/toolbox", "sandbox toolboxProxyUrl"));
        Assert.Throws<DaytonaApiException>(() =>
            DaytonaApiClient.TryParseAbsoluteUrl("/relative/path", "sandbox toolboxProxyUrl"));
    }

    [Fact]
    public void ServiceUrl_RemoteHttpRejected_EvenWithUnsafeHttpOptIn()
    {
        // AllowUnsafeHttp is a loopback-only test hook: with the flag set, a
        // remote cleartext URL must still be refused so one operator edit can
        // never send the API key cleartext to an arbitrary host.
        var refused = Assert.Throws<DaytonaApiException>(() =>
            DaytonaApiClient.TryParseAbsoluteUrl(
                "http://proxy.example/toolbox", "sandbox toolboxProxyUrl", allowUnsafeHttp: true));
        Assert.Equal(DaytonaFailureKind.Unexpected, refused.Kind);
        Assert.Contains("AllowUnsafeHttp", refused.Message);

        Assert.Throws<DaytonaApiException>(() =>
            DaytonaApiClient.TryParseAbsoluteUrl(
                "http://192.168.0.9/toolbox", "sandbox toolboxProxyUrl", allowUnsafeHttp: true));

        // Loopback http stays usable for local tests under the opt-in.
        var loopback = DaytonaApiClient.TryParseAbsoluteUrl(
            "http://127.0.0.1/toolbox", "sandbox toolboxProxyUrl", allowUnsafeHttp: true);
        Assert.Equal("http://127.0.0.1/toolbox", loopback!.ToString());
    }

    [Fact]
    public void UntrustedText_SanitizedForSingleLineLogSurfaces()
    {
        Assert.Equal("a b c", DaytonaTextUtil.SanitizeForLog("a\nb\rc"));
        Assert.Equal("a b", DaytonaTextUtil.SanitizeForLog("a\tb"));
        Assert.Equal("abc", DaytonaTextUtil.SanitizeForLog("abc"));

        // Guest stderr tails reach exception messages and structured logs, so
        // Tail folds line breaks that would otherwise forge log lines.
        Assert.Equal("oops  boom", DaytonaTextUtil.Tail("oops\n boom"));
    }

    [Fact]
    public async Task LogDemuxer_ChunkedFeed_DemuxesAcrossFrameBoundaries()
    {
        var stdout = new List<byte>();
        var stderr = new List<byte>();
        var demuxer = new DaytonaLogDemuxer(
            onStdout: bytes => { stdout.AddRange(bytes); return Task.CompletedTask; },
            onStderr: bytes => { stderr.AddRange(bytes); return Task.CompletedTask; });

        const int messages = 500;
        var stream = new List<byte>();
        var expectedStdout = new StringBuilder();
        var expectedStderr = new StringBuilder();
        for (var i = 0; i < messages; i++)
        {
            var outText = "out-" + i + ";";
            var errText = "err-" + i + ";";
            expectedStdout.Append(outText);
            expectedStderr.Append(errText);
            stream.AddRange(DaytonaLogDemuxer.StdoutPrefix);
            stream.AddRange(Encoding.UTF8.GetBytes(outText));
            stream.AddRange(DaytonaLogDemuxer.StderrPrefix);
            stream.AddRange(Encoding.UTF8.GetBytes(errText));
        }

        // Odd chunk sizes guarantee channel prefixes straddle feed boundaries.
        const int chunkSize = 7;
        for (var offset = 0; offset < stream.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, stream.Count - offset);
            await demuxer.FeedAsync(new ReadOnlyMemory<byte>(stream.ToArray(), offset, count));
        }
        await demuxer.CompleteAsync();

        Assert.Equal(expectedStdout.ToString(), Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal(expectedStderr.ToString(), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public void ToolboxClient_RejectsCleartextBase_WithoutUnsafeHttpOptIn()
    {
        // Guard at the credential sink: even a caller that bypassed the
        // service-URL parse cannot send the API key over cleartext http.
        var server = new FakeDaytonaServer();
        var http = new HttpClient(server);
        var apiBase = new Uri("http://localhost/api/");
        var ex = Assert.Throws<ArgumentException>(() => new DaytonaToolboxClient(
            http,
            new DaytonaEndpoint(apiBase, new Uri("http://localhost/toolbox/"), TestApiKey, null),
            new Uri("http://attacker.example/toolbox/sandbox-1/")));
        Assert.Contains("AllowUnsafeHttp", ex.Message);

        var optedIn = new DaytonaToolboxClient(
            http,
            new DaytonaEndpoint(apiBase, new Uri("http://localhost/toolbox/"), TestApiKey, null, AllowUnsafeHttp: true),
            new Uri("http://localhost/toolbox/sandbox-1/"));
        Assert.Equal("http://localhost/toolbox/sandbox-1/", optedIn.SandboxBaseUri.ToString());
    }

    private static DaytonaSandbox StagingSandbox(
        FakeDaytonaServer server, DaytonaSandboxOptions opts, string sandboxPath, string hostPath)
    {
        var http = new HttpClient(server);
        return new DaytonaSandbox(
            "codeybox-staging-test",
            WorkSpec(),
            () => opts,
            new DaytonaApiClient(http),
            () => new DaytonaEndpoint(
                new Uri("http://localhost/api/"), new Uri("http://localhost/toolbox/"), TestApiKey, null, true),
            () => new Uri("http://localhost/toolbox/codeybox-staging-test/"),
            new QueueDaytonaWebSocketFactory(),
            http,
            [new DaytonaMountPlan(sandboxPath, hostPath, ReadOnly: false, IsPersistentTmpfsDirectory: false)],
            _ => { },
            TimeProvider.System,
            NullLogger.Instance);
    }

    [Fact]
    public async Task StageFile_RefusesOversizedHostFile_BeforeAnyTraffic()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var server = new FakeDaytonaServer();
        var sandbox = StagingSandbox(server, TestOptions() with { MaxFileSyncBytes = 100 }, "/data/big.bin", path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.PrepareFilesystemAsync(CancellationToken.None));
        Assert.Contains("100-byte", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task StageDirectory_RefusesOversizedHostTree_BeforeAnyTraffic()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(dir.Path, "big.bin"), new byte[4096]);
        var server = new FakeDaytonaServer();
        var sandbox = StagingSandbox(
            server, TestOptions() with { MaxSyncArchiveExpandedBytes = 100 }, "/data", dir.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.PrepareFilesystemAsync(CancellationToken.None));
        Assert.Contains("100-byte", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task StageDirectory_RefusesTooManyEntries_BeforeAnyTraffic()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "b.txt"), "b");
        var server = new FakeDaytonaServer();
        var sandbox = StagingSandbox(
            server, TestOptions() with { MaxSyncArchiveEntries = 1 }, "/data", dir.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.PrepareFilesystemAsync(CancellationToken.None));
        Assert.Contains("1-entry", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task RealWebSocket_SendsBearerTokenInHandshake()
    {
        // Loopback TCP server speaking just enough HTTP-upgrade to capture the
        // handshake headers the real socket sends — no live network.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? authorization = null;
        string? organization = null;
        Task serves = Task.CompletedTask;
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            serves = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[4096];
                string key = string.Empty;
                int read;
                while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    if (request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                    if (request.Length > 65536)
                        throw new InvalidOperationException("Handshake headers exceeded 64 KiB.");
                }
                foreach (var line in request.ToString().Split("\r\n", StringSplitOptions.None))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        authorization = line.Substring("Authorization:".Length).Trim();
                    else if (line.StartsWith("X-Daytona-Organization-ID:", StringComparison.OrdinalIgnoreCase))
                        organization = line.Substring("X-Daytona-Organization-ID:".Length).Trim();
                    else if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                }
                var accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cts.Token);
                try
                {
                    while (await stream.ReadAsync(buffer, cts.Token) > 0) { }
                }
                catch (OperationCanceledException)
                {
                    // Test teardown: the client socket is gone with the timeout.
                }
                catch (IOException)
                {
                    // Client abort (RST) surfaces as a reset, not a clean FIN.
                }
            });

            {
                await using var socket = new ClientWebSocketDaytonaWebSocket();
                await socket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/logs?follow=true"), "ws-test-token", "org-1", cts.Token);
                Assert.Equal(WebSocketState.Open, socket.State);
                Assert.Equal("Bearer ws-test-token", authorization);
                Assert.Equal("org-1", organization);
            }

            await serves.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
            if (!serves.IsCompleted)
            {
                await cts.CancelAsync();
                try
                {
                    await serves;
                }
                catch (OperationCanceledException)
                {
                    // Server task was still parked in accept/read at teardown.
                }
                catch (IOException)
                {
                    // Listener stop tore down a connection mid-handshake.
                }
                catch (SocketException)
                {
                    // Listener stop aborted a pending accept.
                }
                catch (ObjectDisposedException)
                {
                    // Listener stop raced a pending accept.
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Fake Daytona service (recorded shapes)
    // ------------------------------------------------------------------

    private sealed record RequestRecord(string Method, string Path, string Query,
        string? AuthorizationScheme, string? AuthorizationParameter, string? OrganizationHeader, string Body);

    private sealed class FakeDaytonaServer : HttpMessageHandler
    {
        private int _createSeq;
        private readonly ConcurrentDictionary<string, FakeSandbox> _sandboxes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _snapshots = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _commandExitCodes = new(StringComparer.Ordinal);
        private readonly List<string> _deletedSessions = new();
        public List<RequestRecord> Requests { get; } = [];
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public HttpStatusCode? CreateStatusOverride { get; set; }
        public string? CreateErrorBody { get; set; }
        public HttpStatusCode? ExecExecuteStatusOverride { get; set; }
        public Exception? CreateFault { get; set; }
        public TaskCompletionSource? CreateGate { get; set; }
        public bool FailSetupCommands { get; set; }
        public string? CommandLogsBody { get; set; }
        public int InFlightCreates;
        public int MaxInFlightCreates;
        private readonly TaskCompletionSource _createSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createSeenCount;

        public int CountRequests(string method, string pathPrefix) =>
            Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

        public async Task<bool> WaitForCreatesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _createSeenCount) < count)
            {
                if (DateTime.UtcNow >= deadline)
                    return false;
                await Task.Delay(5);
            }
            return true;
        }

        public void SetState(string name, string state)
        {
            if (_sandboxes.TryGetValue(name, out var s))
                s.State = state;
        }

        public void AddOrphan(string name, string state)
        {
            var sandbox = new FakeSandbox($"id-{name}", state);
            sandbox.Labels["codeybox.managed"] = "true";
            _sandboxes[name] = sandbox;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var uri = request.RequestUri!;
            var record = new RequestRecord(
                request.Method.Method, uri.AbsolutePath, uri.Query,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Daytona-Organization-ID", out var org) ? org.FirstOrDefault() : null,
                body);
            lock (Requests) { Requests.Add(record); }

            var path = uri.AbsolutePath;

            // ---- toolbox proxy routes (/toolbox/{sandboxId}/...) ----
            if (path.StartsWith("/toolbox/", StringComparison.Ordinal))
                return HandleToolbox(path, record);

            // ---- control plane ----
            if (request.Method == HttpMethod.Post && path == "/api/sandbox")
            {
                Interlocked.Increment(ref _createSeenCount);
                Interlocked.Increment(ref InFlightCreates);
                MaxInFlightCreates = Math.Max(MaxInFlightCreates, InFlightCreates);
                try
                {
                    if (CreateFault is not null)
                        throw CreateFault;
                    if (CreateGate is not null)
                        await CreateGate.Task.WaitAsync(ct);
                    if (CreateStatusOverride is { } st)
                        return Json(CreateErrorBody ?? """{"message":"service said no"}""", st);
                    using var doc = JsonDocument.Parse(body);
                    var name = doc.RootElement.GetProperty("name").GetString()!;
                    var id = "day-" + Interlocked.Increment(ref _createSeq).ToString();
                    var sandbox = new FakeSandbox(id, "started");
                    if (doc.RootElement.TryGetProperty("labels", out var createLabels))
                    {
                        foreach (var label in createLabels.EnumerateObject())
                            sandbox.Labels[label.Name] = label.Value.GetString() ?? "";
                    }
                    _sandboxes[name] = sandbox;
                    return Json($$"""{"id":"{{id}}","name":"{{name}}","state":"started"}""");
                }
                finally { Interlocked.Decrement(ref InFlightCreates); }
            }
            if (request.Method == HttpMethod.Get && path == "/api/sandbox")
            {
                var items = _sandboxes.Where(s => s.Key.StartsWith("codeybox-", StringComparison.Ordinal))
                    .Select(s => $$"""{"id":"{{s.Value.Id}}","name":"{{s.Key}}","state":"{{s.Value.State}}","labels":{{SerializeLabels(s.Value.Labels)}}}""");
                return Json($$"""{"items":[{{string.Join(",", items)}}],"total":{{_sandboxes.Count}}}""");
            }
            var sandboxMatch = Match(path, "/api/sandbox/");
            if (sandboxMatch is { } nameOrId)
            {
                if (path.EndsWith("/network-settings", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                    return Json("{}");
                if (path.EndsWith("/labels", StringComparison.Ordinal))
                {
                    var nm = nameOrId[..^"/labels".Length];
                    if (_sandboxes.TryGetValue(nm, out var labelled))
                    {
                        using var doc = JsonDocument.Parse(body);
                        foreach (var label in doc.RootElement.GetProperty("labels").EnumerateObject())
                            labelled.Labels[label.Name] = label.Value.GetString() ?? "";
                    }
                    return Json("{}");
                }
                if (path.EndsWith("/start", StringComparison.Ordinal))
                {
                    var nm = nameOrId[..^"/start".Length];
                    SetState(nm, "started");
                    return Json("{}");
                }
                if (path.EndsWith("/stop", StringComparison.Ordinal))
                {
                    var nm = nameOrId[..^"/stop".Length];
                    SetState(nm, "stopped");
                    return Json("{}");
                }
                if (path.EndsWith("/pause", StringComparison.Ordinal))
                {
                    var nm = nameOrId[..^"/pause".Length];
                    SetState(nm, "paused");
                    return Json("{}");
                }
                if (path.EndsWith("/snapshot", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                {
                    using var doc = JsonDocument.Parse(body);
                    var snapName = doc.RootElement.GetProperty("name").GetString()!;
                    _snapshots[snapName] = "active";
                    return Json($$"""{"id":"snap-{{snapName}}","name":"{{snapName}}","state":"active"}""");
                }
                if (request.Method == HttpMethod.Get)
                {
                    if (_sandboxes.TryGetValue(nameOrId, out var s))
                        return Json($$"""{"id":"{{s.Id}}","name":"{{nameOrId}}","state":"{{s.State}}","labels":{{SerializeLabels(s.Labels)}}}""");
                    // toolbox-proxy calls address by id — look up by id too
                    var byId = _sandboxes.FirstOrDefault(k => k.Value.Id == nameOrId);
                    if (byId.Value is not null)
                        return Json($$"""{"id":"{{byId.Value.Id}}","name":"{{nameOrId}}","state":"{{byId.Value.State}}","labels":{{SerializeLabels(byId.Value.Labels)}}}""");
                    return Json("{}", HttpStatusCode.NotFound);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    _sandboxes.TryRemove(nameOrId, out _);
                    return Json("{}");
                }
            }
            if (path == "/api/snapshots" || path.StartsWith("/api/snapshots/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get && path == "/api/snapshots")
                {
                    var items = _snapshots.Select(kvp =>
                        $$"""{"id":"snap-{{kvp.Key}}","name":"{{kvp.Key}}","state":"{{kvp.Value}}"}""");
                    return Json($$"""{"items":[{{string.Join(",", items)}}],"total":{{_snapshots.Count}}}""");
                }
                if (request.Method == HttpMethod.Get && path.StartsWith("/api/snapshots/", StringComparison.Ordinal))
                {
                    var snapName = path["/api/snapshots/".Length..];
                    if (!_snapshots.TryGetValue(snapName, out var snapState))
                        return Json("{}", HttpStatusCode.NotFound);
                    return Json($$"""{"id":"snap-{{snapName}}","name":"{{snapName}}","state":"{{snapState}}"}""");
                }
                if (request.Method == HttpMethod.Delete)
                {
                    _snapshots.TryRemove(path["/api/snapshots/".Length..], out _);
                    return Json("{}");
                }
            }
            return Json("{}", HttpStatusCode.NotFound);

            static string? Match(string p, string prefix) =>
                p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : null;
        }

        private sealed class FakeSandbox
        {
            public FakeSandbox(string id, string state) { Id = id; State = state; }
            public string Id { get; }
            public string State;
            public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        }

        private static string SerializeLabels(Dictionary<string, string> labels)
        {
            var items = labels.Select(l => $"\"{l.Key}\":\"{l.Value}\"");
            return "{" + string.Join(",", items) + "}";
        }

        private HttpResponseMessage HandleToolbox(string path, RequestRecord record)
        {
            // /toolbox/{id}/process/session...
            if (path.EndsWith("/process/session", StringComparison.Ordinal) && record.Method == "POST")
                return Json("""{"id":"sess-1"}""");
            if (path.EndsWith("/exec", StringComparison.Ordinal) && record.Method == "POST")
            {
                if (ExecExecuteStatusOverride is { } st)
                    return Json("""{"message":"service unavailable"}""", st);
                if (FailSetupCommands)
                {
                    var failId = "cmd-" + Interlocked.Increment(ref _createSeq).ToString();
                    _commandExitCodes[failId] = "17";
                    return Json($$"""{"cmdId":"{{failId}}"}""");
                }
                var cmdId = "cmd-" + Interlocked.Increment(ref _createSeq).ToString();
                _commandExitCodes[cmdId] = "0";
                return Json($$"""{"cmdId":"{{cmdId}}"}""");
            }
            if (path.Contains("/input", StringComparison.Ordinal))
                return Json("{}");
            if (path.Contains("/command/", StringComparison.Ordinal) && record.Method == "GET" && !path.EndsWith("/logs", StringComparison.Ordinal))
            {
                var cmdId = path.Split('/').Last();
                return _commandExitCodes.TryGetValue(cmdId, out var code)
                    ? Json($$"""{"id":"{{cmdId}}","exitCode":{{code}}}""")
                    : Json("{}", HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/logs", StringComparison.Ordinal))
                return Json(CommandLogsBody ?? "{}");
            if (path.Contains("/process/session/") && record.Method == "DELETE")
            {
                var parts = path.Split('/');
                var sessionIndex = Array.IndexOf(parts, "session");
                if (sessionIndex >= 0 && sessionIndex + 1 < parts.Length)
                    _deletedSessions.Add(parts[sessionIndex + 1]);
                return Json("{}");
            }
            if (path.Contains("/files/info", StringComparison.Ordinal))
            {
                var query = record.Query;
                var filePath = ExtractQueryParam(query, "path");
                if (filePath is not null && Files.ContainsKey(filePath))
                    return Json($$"""{"name":"{{Path.GetFileName(filePath)}}","isDir":false}""");
                return Json("{}", HttpStatusCode.NotFound);
            }
            if (path.Contains("/files/download", StringComparison.Ordinal))
            {
                var filePath = ExtractQueryParam(record.Query, "path");
                if (filePath is not null && Files.TryGetValue(filePath, out var content))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
                    };
                return Json("{}", HttpStatusCode.NotFound);
            }
            return Json("{}", HttpStatusCode.NotFound);
        }

        private static string? ExtractQueryParam(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = eq >= 0 ? part[..eq] : part;
                if (string.Equals(Uri.UnescapeDataString(k), key, StringComparison.Ordinal))
                    return eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : "";
            }
            return null;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class QueueDaytonaWebSocketFactory : IDaytonaWebSocketFactory
    {
        private readonly ConcurrentQueue<IDaytonaWebSocket> _queue = new();
        private IDaytonaWebSocket? _repeat;

        public QueueDaytonaWebSocketFactory(params IDaytonaWebSocket[] sockets)
        {
            foreach (var s in sockets)
                _queue.Enqueue(s);
        }

        public void Enqueue(IDaytonaWebSocket socket) => _queue.Enqueue(socket);

        public IDaytonaWebSocket Create()
        {
            if (_queue.TryDequeue(out var s))
            {
                _repeat = s;
                return s;
            }
            return _repeat ?? new FakeDaytonaWebSocket();
        }
    }

    private sealed class FakeDaytonaWebSocket : IDaytonaWebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Payload, bool EndOfMessage)> _incoming = new();
        public Uri? ConnectedUri { get; private set; }
        public string? BearerToken { get; private set; }
        public WebSocketState State { get; private set; } = WebSocketState.None;
        public WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;

        public void EnqueueStdout(string text) =>
            _incoming.Enqueue((WebSocketMessageType.Binary,
                DaytonaLogDemuxer.StdoutPrefix.Concat(Encoding.UTF8.GetBytes(text)).ToArray(), true));

        public void EnqueueStderr(string text) =>
            _incoming.Enqueue((WebSocketMessageType.Binary,
                DaytonaLogDemuxer.StderrPrefix.Concat(Encoding.UTF8.GetBytes(text)).ToArray(), true));

        public Task ConnectAsync(Uri uri, string bearerToken, string? organizationId, CancellationToken ct)
        {
            ConnectedUri = uri;
            BearerToken = bearerToken;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_incoming.Count == 0)
            {
                State = WebSocketState.Closed;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var message = _incoming.Dequeue();
            message.Payload.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(message.Payload.Length, message.Type, message.EndOfMessage));
        }

        public Task CloseAsync(CancellationToken ct)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDaytonaWebSocket : IDaytonaWebSocket
    {
        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public WebSocketCloseStatus? CloseStatus => null;

        public Task ConnectAsync(Uri uri, string bearerToken, string? organizationId, CancellationToken ct) => Task.CompletedTask;

        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ReceiveStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConnectFailingDaytonaWebSocket : IDaytonaWebSocket
    {
        public WebSocketState State => WebSocketState.None;
        public WebSocketCloseStatus? CloseStatus => null;
        public Task ConnectAsync(Uri uri, string bearerToken, string? organizationId, CancellationToken ct) =>
            throw new DaytonaApiException(DaytonaFailureKind.ServerError, "ws connect", "upgrade refused");
        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) =>
            throw new InvalidOperationException("unreachable");
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codeybox-daytona-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
