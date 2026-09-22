using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.OnePasswordPlugin;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests.OnePassword;

/// <summary>
/// Verification for the 1Password credential plugin against the shared
/// lease-shaped contract: a granted group resolves while an ungranted one
/// is never fetched (over HTTP <em>and</em> the service-account CLI),
/// leases renew across phases longer than their TTL, teardown revokes with
/// the revocation verified (renewal refused afterwards, store state
/// checked) rather than assumed, the reconciliation sweep covers failed
/// teardowns, values never reach logs, backend faults classify as
/// infrastructure (never a diff verdict), and both retrieval transports
/// (Connect server, <c>op</c> CLI) read, renew, and revoke. HTTP is faked
/// at the transport and the CLI behind a seam; the manager, store shape,
/// and sweep path are the real production wiring. Recorded payload shapes
/// live in <c>Fixtures/onepassword/</c>, transcribed from the live Connect
/// API reference; the one live test runs only when
/// <c>OP_CONNECT_LIVE_*</c> env is set, so offline runs rely on the
/// recorded shapes plus the documented reason in the plugin README.
/// </summary>
public sealed class OnePasswordPluginTests : IDisposable
{
    private const string StaticValue = "onepassword-live-value-9f3c7a1b4e";
    private const string VaultId = "ftz4pm2xxwmwrsd7rjqn7grzfz";
    private const string ItemId = "h5a2nk7exampleitem000001";

    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal)
    {
        ["OP_CONNECT_TOKEN"] = "connect-token-automation",
        ["OP_CONNECT_TOKEN_DEV"] = "connect-token-dev",
        ["OP_SERVICE_ACCOUNT_TOKEN"] = "ops_test-service-account-token",
    };

    private readonly OnePasswordFakeHandler _handler = new();
    private readonly FakeCliRunner _cli = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handler.Dispose();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class OnePasswordFakeHandler : HttpMessageHandler
    {
        public string VaultsJson = "[]";
        public string ItemsJson = "[]";
        public string ItemJson = "{}";
        public bool Unreachable;
        public int FailListStatus;
        public int FailItemStatus;
        public readonly List<(string Method, string Path, string? Auth)> Requests = [];
        public readonly List<string> ReadItemIds = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? auth = null;
            if (request.Headers.TryGetValues("Authorization", out var values))
                auth = string.Join(",", values);
            lock (Requests)
                Requests.Add((request.Method.Method, path, auth));
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");
            if (path.EndsWith("/v1/vaults", StringComparison.Ordinal))
            {
                if (FailListStatus != 0)
                    return Task.FromResult(Failure(FailListStatus));
                return Task.FromResult(Raw(VaultsJson));
            }
            if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                if (FailListStatus != 0)
                    return Task.FromResult(Failure(FailListStatus));
                return Task.FromResult(Raw(ItemsJson));
            }
            if (path.Contains("/items/", StringComparison.Ordinal))
            {
                if (FailItemStatus != 0)
                    return Task.FromResult(Failure(FailItemStatus));
                var id = path[(path.LastIndexOf('/') + 1)..];
                lock (Requests)
                    ReadItemIds.Add(id);
                return Task.FromResult(Raw(ItemJson));
            }
            return Task.FromResult(Failure(404));
        }

        private static HttpResponseMessage Failure(int status)
        {
            var body = status == 429
                ? """{"message":"Rate limit exceeded."}"""
                : status == 404
                    ? """{"message":"Not found."}"""
                    : """{"message":"Upstream exploded."}""";
            var failure = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (status == 429)
                failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return failure;
        }

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        public int CountReadsOf(string itemId)
        {
            lock (Requests)
                return ReadItemIds.Count(id => string.Equals(id, itemId, StringComparison.Ordinal));
        }

        public bool SawReadOf(string itemId)
        {
            lock (Requests)
                return ReadItemIds.Contains(itemId, StringComparer.Ordinal);
        }
    }

    private sealed class FakeCliRunner : IOnePasswordProcessRunner
    {
        public string Stdout = StaticValue;
        public string Stderr = string.Empty;
        public int ExitCode;
        public bool TimedOut;
        public readonly List<string> References = [];
        public readonly List<string> Binaries = [];
        public readonly List<bool> TokenWasSet = [];

        public int Invocations
        {
            get { lock (References) return References.Count; }
        }

        public Task<OnePasswordProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var reference = arguments.Count > 0 ? arguments[^1] : string.Empty;
            var tokenSet = environment.TryGetValue("OP_SERVICE_ACCOUNT_TOKEN", out var token)
                && !string.IsNullOrEmpty(token);
            lock (References)
            {
                References.Add(reference);
                Binaries.Add(fileName);
                TokenWasSet.Add(tokenSet);
            }
            return Task.FromResult(new OnePasswordProcessResult(ExitCode, Stdout, Stderr, TimedOut));
        }

        public bool SawReference(string reference)
        {
            lock (References)
                return References.Contains(reference, StringComparer.Ordinal);
        }
    }

    private sealed class MemorySecretLeaseStore : ISecretLeaseStore
    {
        private readonly Dictionary<string, SecretLeaseRecord> _leases = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public Task UpsertAsync(SecretLeaseRecord lease, CancellationToken ct = default)
        {
            lock (_lock) _leases[lease.LeaseId] = lease;
            return Task.CompletedTask;
        }

        public Task<SecretLeaseRecord?> GetAsync(string leaseId, CancellationToken ct = default)
        {
            lock (_lock) return Task.FromResult(_leases.TryGetValue(leaseId, out var lease) ? lease : null);
        }

        public Task<IReadOnlyList<SecretLeaseRecord>> ListOutstandingAsync(CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult<IReadOnlyList<SecretLeaseRecord>>(
                    _leases.Values
                        .Where(l => l.Status is SecretLeaseStatus.Active or SecretLeaseStatus.RevocationFailed)
                        .ToList()
                        .AsReadOnly());
        }

        public Task<IReadOnlyList<SecretLeaseRecord>> ListForWorkItemAsync(Guid workItemId, CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult<IReadOnlyList<SecretLeaseRecord>>(
                    _leases.Values.Where(l => l.WorkItemId == workItemId).ToList().AsReadOnly());
        }

        public Task<bool> TryUpdateExpiryAsync(string leaseId, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_leases.TryGetValue(leaseId, out var lease) || lease.Status != SecretLeaseStatus.Active)
                    return Task.FromResult(false);
                _leases[leaseId] = lease with { ExpiresAt = expiresAt, LastError = null };
                return Task.FromResult(true);
            }
        }

        public Task MarkRevokedAsync(string leaseId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_leases.TryGetValue(leaseId, out var lease))
                    _leases[leaseId] = lease with { Status = SecretLeaseStatus.Revoked, LastError = null };
            }
            return Task.CompletedTask;
        }

        public Task MarkRevocationFailedAsync(string leaseId, string error, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_leases.TryGetValue(leaseId, out var lease))
                    _leases[leaseId] = lease with
                    {
                        Status = SecretLeaseStatus.RevocationFailed,
                        AttemptCount = lease.AttemptCount + 1,
                        LastError = error,
                    };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<string> Messages = [];
        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class CapturingLoggerFactory(CapturingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine("Fixtures", "onepassword", name));

    private static IConfigurationSection PluginConfig(Dictionary<string, string?> values)
    {
        var full = values.ToDictionary(
            kv => $"x:{kv.Key}", kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(full!)
            .Build()
            .GetSection("x");
    }

    private Dictionary<string, string?> BaseConfig() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enabled"] = "true",
        ["ServerUrl"] = "https://onepassword.example.com",
        ["ConnectTokenEnvVar"] = "OP_CONNECT_TOKEN",
        ["ServiceAccountTokenEnvVar"] = "OP_SERVICE_ACCOUNT_TOKEN",
        ["OpBinaryPath"] = "op",
        ["StaticLeaseTtlMinutes"] = "20",
    };

    private void UseRecordedShapes()
    {
        _handler.VaultsJson = Fixture("vaults.json");
        _handler.ItemsJson = Fixture("items.json");
        _handler.ItemJson = Fixture("item.json");
    }

    private OnePasswordSecretProvider CreateProvider(
        Dictionary<string, string?>? overrides = null,
        TimeProvider? clock = null,
        ILogger? log = null,
        Dictionary<string, string?>? env = null,
        IOnePasswordProcessRunner? cli = null)
    {
        var merged = BaseConfig();
        if (overrides is not null)
            foreach (var (k, v) in overrides)
                merged[k] = v;
        var http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new OnePasswordSecretProvider(
            http,
            PluginConfig(merged),
            clock,
            name => (env ?? _env).TryGetValue(name, out var v) ? v : null,
            log,
            cli ?? _cli);
    }

    private static SecretLeaseManager CreateManager(
        MemorySecretLeaseStore store,
        OnePasswordSecretProvider provider,
        Func<DateTimeOffset>? utcNow = null,
        ILogger? log = null)
        => new(
            store,
            [provider],
            options: () => new SecretLeasingOptions
            {
                RenewBeforeExpiry = TimeSpan.FromMinutes(5),
                DefaultLeaseTtl = TimeSpan.FromMinutes(20),
            },
            utcNow: utcNow ?? (() => DateTimeOffset.UtcNow),
            log: log);

    private static Project GrantedProject(params ProjectSandboxSecret[] secrets) => new()
    {
        Id = new ProjectId("onepassword-project"),
        DisplayName = "1Password Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        SandboxSecrets = secrets,
        SandboxSecretGrants = secrets
            .Select(secret => secret.Group)
            .Distinct(StringComparer.Ordinal)
            .Select(group => new ProjectSandboxSecretGrant { Group = group })
            .ToList(),
    };

    private static ProjectSandboxSecret Secret(string sandboxEnvVar, string group = "paid-api") => new()
    {
        HostEnvVar = $"CODEYBOX_HOST_{sandboxEnvVar}",
        SandboxEnvVar = sandboxEnvVar,
        Group = group,
    };

    private Dictionary<string, string?> ConnectMapping(
        string sandboxEnvVar, string itemId = ItemId, string group = "paid-api") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = sandboxEnvVar,
            ["Mappings:0:Group"] = group,
            ["Mappings:0:VaultId"] = VaultId,
            ["Mappings:0:ItemId"] = itemId,
        };

    private Dictionary<string, string?> ServiceAccountMapping(
        string sandboxEnvVar, string group = "paid-api") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = sandboxEnvVar,
            ["Mappings:0:Group"] = group,
            ["Mappings:0:VaultName"] = "Automation",
            ["Mappings:0:ItemTitle"] = "Paid API",
            ["Mappings:0:UseServiceAccount"] = "true",
        };

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_By_Default_Issues_Nothing()
    {
        UseRecordedShapes();
        var config = ConnectMapping("PAID_API_TOKEN");
        config["Enabled"] = "false";
        var provider = CreateProvider(config);
        Assert.False(provider.CanIssue(Secret("PAID_API_TOKEN")));
        Assert.Empty(provider.CurrentOptions().Validate());
    }

    [Fact]
    public async Task Granted_Group_Resolves_And_Ungranted_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultId"] = VaultId,
            ["Mappings:0:ItemId"] = ItemId,
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "other-group",
            ["Mappings:1:VaultId"] = VaultId,
            ["Mappings:1:ItemId"] = "k9p4exampleitem00000002",
        }, log: log);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        var project = GrantedProject(Secret("PAID_API_TOKEN"), Secret("OTHER_TOKEN", "other-group"));
        project = project with
        {
            SandboxSecretGrants = [new ProjectSandboxSecretGrant { Group = "paid-api" }],
        };
        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => "static-must-not-win",
            itemDeadline: null);

        Assert.Equal(StaticValue, material.Environment["PAID_API_TOKEN"]);
        Assert.DoesNotContain("OTHER_TOKEN", material.Environment.Keys);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.StartsWith("onepassword.c.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_handler.SawReadOf(ItemId));
        Assert.False(_handler.SawReadOf("k9p4exampleitem00000002"));
        Assert.Equal(1, _handler.CountReadsOf(ItemId));
        // The ungranted group never reached the CLI transport either.
        Assert.Equal(0, _cli.Invocations);
        Assert.DoesNotContain(StaticValue, string.Join('\n', log.Messages));
    }

    [Fact]
    public async Task Ungranted_But_Mapped_Secret_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        // The mapping exists but no grant authorises the group: the manager
        // must never call the provider, so no HTTP and no CLI happens.
        var project = new Project
        {
            Id = new ProjectId("onepassword-project"),
            DisplayName = "1Password Project",
            RepositoryUrl = "https://example.invalid/repo.git",
            SandboxSecrets = [Secret("PAID_API_TOKEN")],
            SandboxSecretGrants = [],
        };
        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => "nope",
            itemDeadline: null);

        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
        Assert.Empty(_handler.Requests);
        Assert.Equal(0, _cli.Invocations);
    }

    [Fact]
    public async Task Vault_Resolves_Into_Group_Not_Beside_It()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DEV_TOKEN",
            ["Mappings:0:Group"] = "dev-api",
            ["Mappings:0:VaultId"] = "ab12cdexamplevault00002",
            ["Mappings:0:ItemId"] = "k9p4exampleitem00000002",
            ["Mappings:0:TokenEnvVar"] = "OP_CONNECT_TOKEN_DEV",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("DEV_TOKEN", "dev-api")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);

        // Same provider, different vault, resolved into its own group: the
        // grant on "dev-api" authorised it, and the fetch named the dev
        // item with the vault-scoped token.
        Assert.Equal(StaticValue, material.Environment["DEV_TOKEN"]);
        Assert.True(_handler.SawReadOf("k9p4exampleitem00000002"));
        Assert.False(_handler.SawReadOf(ItemId));
        string? auth;
        lock (_handler.Requests)
            auth = _handler.Requests.FirstOrDefault(r => r.Path.Contains("k9p4exampleitem00000002", StringComparison.Ordinal)).Auth;
        Assert.Equal("Bearer connect-token-dev", auth);
    }

    [Fact]
    public async Task Lease_Is_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"), clock: clock);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider, utcNow: () => clock.GetUtcNow());
        var project = GrantedProject(Secret("PAID_API_TOKEN"));

        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => null,
            itemDeadline: clock.GetUtcNow() + TimeSpan.FromMinutes(240));
        var lease = Assert.Single(material.IssuedLeases);
        var firstExpiry = lease.ExpiresAt;
        Assert.Equal(start + TimeSpan.FromMinutes(20), firstExpiry);

        var renewals = 0;
        for (var elapsed = 0; elapsed < 240; elapsed += 10)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            var report = await manager.RenewDueLeasesAsync(_ => clock.GetUtcNow() + TimeSpan.FromMinutes(240 - elapsed));
            renewals += report.RenewedLeaseIds.Count;
        }

        Assert.True(renewals >= 8, $"Expected at least 8 renewals across a 240-minute phase, saw {renewals}.");
        var persisted = await store.GetAsync(lease.LeaseId);
        Assert.NotNull(persisted);
        Assert.True(persisted.ExpiresAt > firstExpiry + TimeSpan.FromMinutes(200),
            "Lease expiry did not advance across the phase.");
        Assert.Equal(SecretLeaseStatus.Active, persisted.Status);
        Assert.True(_handler.CountReadsOf(ItemId) >= 1 + renewals,
            "Renewal must re-fetch so an edit or rotation propagates within one window.");
    }

    [Fact]
    public async Task Teardown_Revokes_Verified_By_Refused_Renewal_And_Store()
    {
        UseRecordedShapes();
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();

        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), itemId,
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.Equal(StaticValue, material.Environment["PAID_API_TOKEN"]);

        // Teardown revokes: the manager reports success and the store parks
        // the handle as revoked with nothing outstanding.
        var report = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(report.AllRevoked);
        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        Assert.Empty(await store.ListOutstandingAsync());

        // Verified against the provider rather than assumed: renewal of the
        // revoked handle now fails loudly instead of silently re-fetching.
        var readsBefore = _handler.CountReadsOf(ItemId);
        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.RenewAsync(lease.LeaseId));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);
        Assert.Equal(readsBefore, _handler.CountReadsOf(ItemId));

        // Idempotent: a second teardown revoke still reports success.
        var again = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(again.AllRevoked);
    }

    [Fact]
    public async Task Restart_Rebuilds_Context_From_Lease_Handle()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var config = ConnectMapping("PAID_API_TOKEN");
        var provider = CreateProvider(config, clock: clock);
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        var firstExpiry = material.ExpiresAt;

        // Simulate an orchestrator restart: a fresh provider instance with
        // an empty issue registry renews purely from the self-describing
        // lease handle plus the current mappings.
        var restarted = CreateProvider(config, clock: clock);
        clock.Advance(TimeSpan.FromMinutes(19));
        var renewed = await restarted.RenewAsync(material.LeaseId);

        Assert.True(renewed > firstExpiry);
        Assert.Equal(2, _handler.CountReadsOf(ItemId));
        restarted.Dispose();
        provider.Dispose();
    }

    [Fact]
    public async Task Reconciliation_Sweep_Revokes_Terminal_Item_After_Failed_Teardown()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultId"] = VaultId,
            ["Mappings:0:ItemId"] = ItemId,
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "paid-api",
            ["Mappings:1:VaultId"] = VaultId,
            ["Mappings:1:ItemId"] = "k9p4exampleitem00000002",
        });
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider);
            var itemId = WorkItemId.New();
            var liveItemId = WorkItemId.New();

            var material = await manager.MaterializeForScopeAsync(
                GrantedProject(Secret("PAID_API_TOKEN")), itemId,
                ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            var lease = Assert.Single(material.IssuedLeases);
            var liveMaterial = await manager.MaterializeForScopeAsync(
                GrantedProject(Secret("OTHER_TOKEN")), liveItemId,
                ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            var liveLease = Assert.Single(liveMaterial.IssuedLeases);

            // Teardown fails: the host crashes after issue, so teardown
            // never runs. The store still holds the lease as outstanding
            // (a prior failed attempt would look the same), and the work
            // item is terminal.
            await store.MarkRevocationFailedAsync(lease.LeaseId, "simulated teardown crash");
            Assert.Equal(SecretLeaseStatus.RevocationFailed, (await store.GetAsync(lease.LeaseId))!.Status);

            // The sweep revokes the terminal item's lease and leaves the
            // live item's lease alone.
            var states = new Dictionary<Guid, WorkItemState>
            {
                [itemId.Value] = WorkItemState.Done,
                [liveItemId.Value] = WorkItemState.Working,
            };
            var report = await manager.ReconcileAsync(
                id => Task.FromResult<WorkItemState?>(states.TryGetValue(id, out var state) ? state : null),
                _ => (DateTimeOffset?)null);

            Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
            Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
            var outstanding = await store.ListOutstandingAsync();
            Assert.Contains(outstanding, l => l.LeaseId == liveLease.LeaseId);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task Service_Account_Issue_Renew_Revoke()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        var provider = CreateProvider(ServiceAccountMapping("PAID_API_TOKEN"), log: log);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();

        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), itemId,
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.Equal(StaticValue, material.Environment["PAID_API_TOKEN"]);
        Assert.StartsWith("onepassword.o.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_cli.SawReference("op://Automation/Paid API/password"));
        // The service-account token travelled in the child environment, and
        // no Connect HTTP happened for this mapping.
        Assert.True(_cli.TokenWasSet.All(set => set));
        Assert.Empty(_handler.Requests);

        var renewed = await provider.RenewAsync(lease.LeaseId);
        Assert.True(renewed > lease.ExpiresAt);
        Assert.Equal(2, _cli.Invocations);

        var report = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(report.AllRevoked);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.RenewAsync(lease.LeaseId));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);

        string all;
        lock (log.Messages)
            all = string.Join('\n', log.Messages);
        Assert.DoesNotContain(StaticValue, all);
        Assert.DoesNotContain("ops_test-service-account-token", all);
    }

    [Fact]
    public async Task Service_Account_Cli_Failure_Is_Infrastructure_Never_A_Verdict()
    {
        UseRecordedShapes();
        _cli.ExitCode = 1;
        _cli.Stderr = "[ERROR] 2026/01/01 00:00:00 unauthorized: invalid service account token";
        _cli.Stdout = string.Empty;
        var provider = CreateProvider(ServiceAccountMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.Unauthorized, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
        Assert.DoesNotContain(StaticValue, ex.Message);

        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
    }

    [Fact]
    public async Task Vault_Listing_Failure_Is_Infrastructure()
    {
        UseRecordedShapes();
        _handler.FailListStatus = 500;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultName"] = "Automation",
            ["Mappings:0:ItemTitle"] = "Paid API",
        });

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.BackendError, ex.Kind);
        Assert.True(ex.IsInfrastructure);
    }

    [Fact]
    public async Task Service_Account_Cli_Timeout_Is_Infrastructure()
    {
        UseRecordedShapes();
        _cli.TimedOut = true;
        var provider = CreateProvider(ServiceAccountMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.Unreachable, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Value_Never_Reaches_Logs()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        var factory = new CapturingLoggerFactory(log);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultId"] = VaultId,
            ["Mappings:0:ItemId"] = ItemId,
            ["Mappings:1:SandboxEnvVar"] = "OP_TOKEN",
            ["Mappings:1:Group"] = "paid-api",
            ["Mappings:1:VaultName"] = "Automation",
            ["Mappings:1:ItemTitle"] = "Paid API",
            ["Mappings:1:UseServiceAccount"] = "true",
        }, log: log);
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider, log: factory.CreateLogger("test"));
            var itemId = WorkItemId.New();
            var project = GrantedProject(Secret("PAID_API_TOKEN"), Secret("OP_TOKEN"));

            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work, _ => null,
                itemDeadline: null, log: factory.CreateLogger("test"));
            await manager.RenewDueLeasesAsync(_ => (DateTimeOffset?)null);
            await manager.RevokeWorkItemLeasesAsync(itemId);

            string all;
            lock (log.Messages)
                all = string.Join('\n', log.Messages);
            Assert.DoesNotContain(StaticValue, all);
            Assert.DoesNotContain("connect-token-automation", all);
            Assert.DoesNotContain("ops_test-service-account-token", all);
            Assert.Contains(material.IssuedLeases[0].LeaseId, all);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Theory]
    [InlineData(401, OnePasswordFailureKind.Unauthorized)]
    [InlineData(403, OnePasswordFailureKind.Unauthorized)]
    [InlineData(429, OnePasswordFailureKind.RateLimited)]
    [InlineData(500, OnePasswordFailureKind.BackendError)]
    public async Task Backend_Faults_Classify_As_Infrastructure(int status, OnePasswordFailureKind kind)
    {
        _handler.ItemJson = Fixture("item.json");
        _handler.FailItemStatus = status;
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));

        Assert.Equal(kind, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
        Assert.DoesNotContain(StaticValue, ex.Message);

        // And the manager never turns it into a verdict: the item runs
        // without the secret.
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
    }

    [Fact]
    public async Task Unknown_Secret_Is_Configuration_Not_Infrastructure()
    {
        _handler.ItemJson = Fixture("item.json");
        _handler.FailItemStatus = 404;
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN", "missing-item-id"));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.NotFound, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);

        // Still never a verdict on the diff: the item runs without it.
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        Assert.Empty(material.Environment);
    }

    [Fact]
    public async Task Unreachable_Backend_Is_Infrastructure_And_Never_A_Verdict()
    {
        _handler.Unreachable = true;
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.Unreachable, ex.Kind);
        Assert.True(ex.IsInfrastructure);

        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        Assert.Empty(material.Environment);
    }

    [Fact]
    public async Task Missing_Provider_Credentials_Are_Configuration_Not_Infrastructure()
    {
        UseRecordedShapes();
        var provider = CreateProvider(
            ConnectMapping("PAID_API_TOKEN"),
            env: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Backend_Redirect_Is_Refused_As_Infrastructure()
    {
        using var http = new HttpClient(new DelegatingHandlerStub((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://127.0.0.1:9/exfil") },
            })))
        { Timeout = TimeSpan.FromSeconds(10) };
        var api = new OnePasswordRestClient(http);
        var ex = await Assert.ThrowsAsync<OnePasswordException>(() =>
            api.GetItemFieldAsync(
                "https://onepassword.example.com", "token",
                new OnePasswordSecretMapping
                {
                    SandboxEnvVar = "PAID_API_TOKEN",
                    VaultId = VaultId,
                    ItemId = ItemId,
                },
                256 * 1024));
        // A backend 3xx is never followed: it fails closed as a backend
        // fault (infrastructure, never a diff verdict) — the bearer token
        // goes nowhere else.
        Assert.Equal(OnePasswordFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_Http_Client_Never_Follows_Redirects()
    {
        // Real loopback wiring: the redirector answers 302 to a sink that
        // records everything it receives. If the client ever followed, the
        // sink would see the token-bearing request and this test would fail.
        using var sink = new RecordingStub(_ => (200, null, "sink"));
        using var redirector = new RecordingStub(_ => (302, sink.Url + "landing", string.Empty));
        using var client = OnePasswordHttpClients.Create(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(
            redirector.Url + "v1/vaults/" + VaultId + "/items/" + ItemId);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, redirector.Hits);
        Assert.Equal(0, sink.Hits);
    }

    [Fact]
    public async Task Name_Resolution_Is_Exact_Match_And_Ambiguity_Fails_Loud()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultName"] = "Automation",
            ["Mappings:0:ItemTitle"] = "Paid API",
        });
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(StaticValue, material.Value);
        Assert.True(_handler.SawReadOf(ItemId));

        // A similarly-named vault must not shadow the match: ambiguity is a
        // loud configuration fault, never a silent wrong-vault read.
        _handler.VaultsJson = """[{"id":"aaa","name":"Automation"},{"id":"bbb","name":"Automation"}]""";
        var ambiguous = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:VaultName"] = "Automation",
            ["Mappings:0:ItemTitle"] = "Paid API",
        });
        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => ambiguous.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
    }

    [Fact]
    public async Task Renew_Across_Transports_Fails_Loud()
    {
        UseRecordedShapes();
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));

        // The operator flipped the mapping to the other transport after
        // issue: renewal must fail loudly rather than fetch with the wrong
        // credential.
        var flipped = CreateProvider(ServiceAccountMapping("PAID_API_TOKEN"));
        var ex = await Assert.ThrowsAsync<OnePasswordException>(() => flipped.RenewAsync(material.LeaseId));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);
    }

    [Fact]
    public async Task Foreign_Lease_Handle_Is_Rejected()
    {
        UseRecordedShapes();
        var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));
        var ex = await Assert.ThrowsAsync<OnePasswordException>(
            () => provider.RenewAsync("doppler.s.PAID_API_TOKEN.abc123"));
        Assert.Equal(OnePasswordFailureKind.Misconfigured, ex.Kind);
        await Assert.ThrowsAsync<OnePasswordException>(() => provider.RevokeAsync("not-a-lease"));
    }

    [Fact]
    public async Task Static_Only_Path_Still_Works_With_Provider_Present()
    {
        var hostVar = $"CODEYBOX_TEST_ONEPASSWORD_{Guid.NewGuid():N}".ToUpperInvariant();
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            UseRecordedShapes();
            var provider = CreateProvider(ConnectMapping("PAID_API_TOKEN"));
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider);
            var project = GrantedProject(new ProjectSandboxSecret
            {
                HostEnvVar = hostVar,
                SandboxEnvVar = "STATIC_TOKEN",
                Group = "paid-api",
            });

            var material = await manager.MaterializeForScopeAsync(
                project, WorkItemId.New(), ProjectSandboxSecretScopes.Work,
                name => Environment.GetEnvironmentVariable(name), itemDeadline: null);

            Assert.Equal("static-value", material.Environment["STATIC_TOKEN"]);
            Assert.Empty(material.IssuedLeases);
            Assert.Empty(material.BrokerEndpoints);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar, null);
        }
    }

    [Fact]
    public void Options_Validation_Rejects_Bad_Mappings()
    {
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ServerUrl"] = "http://onepassword.example.com",
            ["ServiceAccountTokenEnvVar"] = "",
            ["Mappings:0:SandboxEnvVar"] = "A",
            ["Mappings:0:VaultId"] = VaultId,
            ["Mappings:0:ItemId"] = ItemId,
            ["Mappings:1:SandboxEnvVar"] = "A",
            ["Mappings:1:VaultId"] = VaultId,
            ["Mappings:1:ItemId"] = ItemId,
            ["Mappings:2:SandboxEnvVar"] = "bad-name!",
            ["Mappings:2:VaultId"] = VaultId,
            ["Mappings:2:ItemId"] = ItemId,
            ["Mappings:3:SandboxEnvVar"] = "BOTH_IDS",
            ["Mappings:3:VaultId"] = VaultId,
            ["Mappings:3:VaultName"] = "Automation",
            ["Mappings:3:ItemId"] = ItemId,
            ["Mappings:4:SandboxEnvVar"] = "NO_ITEM",
            ["Mappings:4:VaultId"] = VaultId,
            ["Mappings:5:SandboxEnvVar"] = "OP_TOKEN_CLASH",
            ["Mappings:5:VaultName"] = "Automation",
            ["Mappings:5:ItemTitle"] = "Paid API",
            ["Mappings:5:UseServiceAccount"] = "true",
            ["Mappings:5:TokenEnvVar"] = "OP_CONNECT_TOKEN",
        });
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("'A'", StringComparison.Ordinal) && e.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad-name!", StringComparison.Ordinal) && e.Contains("POSIX", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("plain http", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("exactly one of VaultId and VaultName", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("exactly one of ItemId and ItemTitle", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("TokenEnvVar is meaningless", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("ServiceAccountTokenEnvVar is empty", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Live_Fetch_Against_Real_Instance()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_URL")),
            "OP_CONNECT_LIVE_URL is not set; the live 1Password integration test is opt-in (see plugins/credentials/CodeyBox.OnePasswordPlugin/README.md).");
        var url = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_URL")!;
        var token = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_TOKEN");
        var vault = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_VAULT");
        var item = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_ITEM");
        var field = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_FIELD");
        var expected = Environment.GetEnvironmentVariable("OP_CONNECT_LIVE_VALUE");
        Skip.If(string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(vault)
            || string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(field)
            || expected is null,
            "OP_CONNECT_LIVE_TOKEN/VAULT/ITEM/FIELD/VALUE are not all set.");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LIVE_TOKEN"] = token,
        };
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new OnePasswordSecretProvider(
            http,
            PluginConfig(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = "true",
                ["ServerUrl"] = url,
                ["ConnectTokenEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:SandboxEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:VaultId"] = vault,
                ["Mappings:0:ItemId"] = item,
                ["Mappings:0:Field"] = field,
            }),
            env: name => env.TryGetValue(name, out var v) ? v : null);

        var material = await provider.IssueAsync(
            new ProjectSandboxSecret
            {
                HostEnvVar = "UNUSED",
                SandboxEnvVar = "LIVE_TOKEN",
                Group = "live",
            },
            Guid.NewGuid(), "work", TimeSpan.FromMinutes(5));
        Assert.Equal(expected, material.Value);
        await provider.RevokeAsync(material.LeaseId);
    }

    private sealed class DelegatingHandlerStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    /// <summary>
    /// Minimal loopback HTTP stub: records every request it receives
    /// (method, path, Authorization header, body) and answers with a
    /// programmed status, optional redirect Location, and body.
    /// </summary>
    private sealed class RecordingStub : IDisposable
    {
        private readonly Func<HttpListenerRequest, (int Status, string? Location, string Body)> _respond;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _hits;
        private bool _disposed;

        public string Url { get; }

        public int Hits => Volatile.Read(ref _hits);

        public RecordingStub(
            Func<HttpListenerRequest, (int Status, string? Location, string Body)> respond)
        {
            _respond = respond;
            Url = $"http://127.0.0.1:{ProbeFreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = AcceptLoopAsync(_cts.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                Interlocked.Increment(ref _hits);
                try
                {
                    var (status, location, responseBody) = _respond(context.Request);
                    context.Response.StatusCode = status;
                    if (location is not null)
                        context.Response.RedirectLocation = location;
                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort stub: a guest disconnect never fails the test.
                }
                finally
                {
                    try { context.Response.Close(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
            _listener.Close();
        }
    }

    private static int ProbeFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
