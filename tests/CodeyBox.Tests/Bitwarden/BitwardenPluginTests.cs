using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.BitwardenPlugin;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests.Bitwarden;

/// <summary>
/// Verification for the Bitwarden Secrets Manager credential plugin against
/// the shared lease-shaped contract: a granted group resolves while an
/// ungranted one is never fetched, leases renew across phases longer than
/// their TTL, teardown revokes with the revocation verified (renewal
/// refused afterwards, store state checked) rather than assumed, the
/// reconciliation sweep covers failed teardowns, values never reach logs,
/// backend faults classify as infrastructure (never a diff verdict), the
/// machine-account token is cached only within its own lifetime and the
/// lease never outlives it, and key-based resolution is exact-match only.
/// HTTP is faked at the transport; the manager, store shape, and sweep path
/// are the real production wiring. Recorded payload shapes live in
/// <c>Fixtures/bitwarden/</c>, transcribed from the live Secrets Manager
/// API shapes; the one live test runs only when <c>BW_SM_LIVE_*</c> env is
/// set, so offline runs rely on the recorded shapes plus the documented
/// reason in the plugin README (including why the native C# SDK was not
/// taken as a dependency).
/// </summary>
public sealed class BitwardenPluginTests : IDisposable
{
    private const string StaticValue = "bitwarden-live-value-7d2e9a4b1c";
    private const string SecretId = "2c4c8a1e-3f5b-4a6c-9d7e-8f0a1b2c3d4e";
    private const string OtherSecretId = "9d7e8f0a-1b2c-4d5e-8f0a-1b2c3d4e5f6a";
    private const string OrganizationId = "a1b2c3d4-e5f6-4a7b-8c9d-e0f1a2b3c4d5";
    private const string ClientId = "test-machine-account-client-id";
    private const string AccessToken = "bitwarden-test-access-token";
    private const string ClientSecret = "bw_test-machine-account-client-secret";

    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal)
    {
        ["BITWARDEN_CLIENT_SECRET"] = ClientSecret,
        ["BITWARDEN_CLIENT_SECRET_DEV"] = "bw_test-dev-client-secret",
    };

    private readonly BitwardenFakeHandler _handler = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handler.Dispose();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class BitwardenFakeHandler : HttpMessageHandler
    {
        public string TokenJson = "{}";
        public string SecretJson = "{}";
        public string ListJson = """{"object":"list","data":[]}""";
        public bool Unreachable;
        public int FailTokenStatus;
        public int FailSecretStatus;
        public int FailListStatus;
        public readonly List<(string Method, string Path)> Requests = [];
        public readonly List<string> ReadSecretIds = [];
        public readonly List<string> TokenClientIds = [];
        public int AuthCalls;
        public int ListCalls;
        public readonly List<bool> SecretBearerAttached = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (Requests)
                Requests.Add((request.Method.Method, path));
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");
            if (path.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                // The form body carries the client secret: parse out only
                // the non-secret client id for assertions, never the body.
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string? clientId = null;
                foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = pair.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    if (string.Equals(
                        Uri.UnescapeDataString(pair[..equals]), "client_id", StringComparison.Ordinal))
                        clientId = Uri.UnescapeDataString(pair[(equals + 1)..]);
                }
                lock (Requests)
                {
                    AuthCalls++;
                    if (clientId is not null)
                        TokenClientIds.Add(clientId);
                }
                if (FailTokenStatus != 0)
                    return Failure(FailTokenStatus, TokenFailureBody);
                return Raw(TokenJson);
            }
            if (path.EndsWith("/secrets-manager/secrets/list", StringComparison.Ordinal))
            {
                lock (Requests)
                    ListCalls++;
                if (FailListStatus != 0)
                    return Failure(FailListStatus);
                return Raw(ListJson);
            }
            if (path.Contains("/secrets-manager/secrets/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                var bearer = string.Equals(
                    request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);
                lock (Requests)
                {
                    ReadSecretIds.Add(id);
                    SecretBearerAttached.Add(bearer);
                }
                if (FailSecretStatus != 0)
                    return Failure(FailSecretStatus);
                return Raw(SecretJson);
            }
            return Failure(404);
        }

        private const string TokenFailureBody =
            """{"error":"invalid_client","error_description":"Client authentication failed."}""";

        private static HttpResponseMessage Failure(int status, string? body = null)
        {
            var payload = body
                ?? (status == 429
                    ? """{"message":"Rate limit exceeded."}"""
                    : status == 404
                        ? """{"message":"Not found."}"""
                        : """{"message":"Upstream exploded."}""");
            var failure = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (status == 429)
                failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return failure;
        }

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        public int CountReadsOf(string secretId)
        {
            lock (Requests)
                return ReadSecretIds.Count(id => string.Equals(id, secretId, StringComparison.Ordinal));
        }

        public bool SawReadOf(string secretId)
        {
            lock (Requests)
                return ReadSecretIds.Contains(secretId, StringComparer.Ordinal);
        }

        public int RequestCount
        {
            get { lock (Requests) return Requests.Count; }
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
        File.ReadAllText(Path.Combine("Fixtures", "bitwarden", name));

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
        ["ApiUrl"] = "https://bitwarden.example.com",
        ["IdentityUrl"] = "https://identity.bitwarden.example.com",
        ["ClientId"] = ClientId,
        ["ClientSecretEnvVar"] = "BITWARDEN_CLIENT_SECRET",
        ["OrganizationId"] = OrganizationId,
        ["StaticLeaseTtlMinutes"] = "20",
    };

    private void UseRecordedShapes()
    {
        _handler.TokenJson = Fixture("token.json");
        _handler.SecretJson = Fixture("secret.json");
        _handler.ListJson = Fixture("secrets-list.json");
    }

    private BitwardenSecretProvider CreateProvider(
        Dictionary<string, string?>? overrides = null,
        TimeProvider? clock = null,
        ILogger? log = null,
        Dictionary<string, string?>? env = null)
    {
        var merged = BaseConfig();
        if (overrides is not null)
            foreach (var (k, v) in overrides)
                merged[k] = v;
        var http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new BitwardenSecretProvider(
            http,
            PluginConfig(merged),
            clock,
            name => (env ?? _env).TryGetValue(name, out var v) ? v : null,
            log);
    }

    private static SecretLeaseManager CreateManager(
        MemorySecretLeaseStore store,
        BitwardenSecretProvider provider,
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
        Id = new ProjectId("bitwarden-project"),
        DisplayName = "Bitwarden Project",
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

    private Dictionary<string, string?> IdMapping(
        string sandboxEnvVar, string secretId = SecretId, string group = "paid-api") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = sandboxEnvVar,
            ["Mappings:0:Group"] = group,
            ["Mappings:0:SecretId"] = secretId,
        };

    private Dictionary<string, string?> KeyMapping(
        string sandboxEnvVar, string key, string group = "paid-api") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = sandboxEnvVar,
            ["Mappings:0:Group"] = group,
            ["Mappings:0:SecretKey"] = key,
        };

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_By_Default_Issues_Nothing()
    {
        UseRecordedShapes();
        var config = IdMapping("PAID_API_TOKEN");
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
            ["Mappings:0:SecretId"] = SecretId,
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "other-group",
            ["Mappings:1:SecretId"] = OtherSecretId,
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
        Assert.StartsWith("bitwarden.s.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_handler.SawReadOf(SecretId));
        Assert.False(_handler.SawReadOf(OtherSecretId));
        Assert.Equal(1, _handler.CountReadsOf(SecretId));
        // One token mint for the one issue; the ungranted group never even
        // authenticated, let alone fetched.
        Assert.Equal(1, _handler.AuthCalls);
        Assert.Equal(0, _handler.ListCalls);
        // The bearer token travelled on the secret read; the machine
        // account authenticated as the configured client.
        Assert.All(_handler.SecretBearerAttached, attached => Assert.True(attached));
        Assert.Equal([ClientId], _handler.TokenClientIds);
        Assert.DoesNotContain(StaticValue, string.Join('\n', log.Messages));
    }

    [Fact]
    public async Task Ungranted_But_Mapped_Secret_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        // The mapping exists but no grant authorises the group: the manager
        // must never call the provider, so no token mint and no secret read
        // happens.
        var project = new Project
        {
            Id = new ProjectId("bitwarden-project"),
            DisplayName = "Bitwarden Project",
            RepositoryUrl = "https://example.invalid/repo.git",
            SandboxSecrets = [Secret("PAID_API_TOKEN")],
            SandboxSecretGrants = [],
        };
        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => "nope",
            itemDeadline: null);

        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
        Assert.Equal(0, _handler.RequestCount);
        Assert.Equal(0, _handler.AuthCalls);
    }

    [Fact]
    public async Task Key_Based_Mapping_Resolves_Through_Listing()
    {
        UseRecordedShapes();
        var provider = CreateProvider(KeyMapping("PAID_API_TOKEN", "PAID_API_TOKEN"));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);

        Assert.Equal(StaticValue, material.Environment["PAID_API_TOKEN"]);
        Assert.Equal(1, _handler.ListCalls);
        Assert.True(_handler.SawReadOf(SecretId));
    }

    [Fact]
    public async Task Lease_Is_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"), clock: clock);
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
        Assert.True(_handler.CountReadsOf(SecretId) >= 1 + renewals,
            "Renewal must re-fetch so an edit or rotation propagates within one window.");
        // The 60-minute access token is re-minted across a 240-minute phase.
        Assert.True(_handler.AuthCalls >= 2,
            $"Expected re-authentication across a 240-minute phase, saw {_handler.AuthCalls} token mint(s).");
    }

    [Fact]
    public async Task Teardown_Revokes_Verified_By_Refused_Renewal_And_Store()
    {
        UseRecordedShapes();
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));
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
        var readsBefore = _handler.CountReadsOf(SecretId);
        var authBefore = _handler.AuthCalls;
        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.RenewAsync(lease.LeaseId));
        Assert.Equal(BitwardenFailureKind.Misconfigured, ex.Kind);
        Assert.Equal(readsBefore, _handler.CountReadsOf(SecretId));
        Assert.Equal(authBefore, _handler.AuthCalls);

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
        var config = IdMapping("PAID_API_TOKEN");
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
        Assert.Equal(2, _handler.CountReadsOf(SecretId));
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
            ["Mappings:0:SecretId"] = SecretId,
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "paid-api",
            ["Mappings:1:SecretId"] = OtherSecretId,
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
    public async Task Token_Is_Cached_Until_Expiry_Then_Reauthenticated()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
                ["Mappings:0:Group"] = "paid-api",
                ["Mappings:0:SecretId"] = SecretId,
                ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
                ["Mappings:1:Group"] = "paid-api",
                ["Mappings:1:SecretId"] = OtherSecretId,
            },
            clock: clock);

        // Two issues against the same machine account mint once: the token
        // is cached within its own lifetime, never beyond it.
        await provider.IssueAsync(Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        await provider.IssueAsync(Secret("OTHER_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(1, _handler.AuthCalls);

        // Past the 60-minute token lifetime the next renewal re-mints.
        clock.Advance(TimeSpan.FromMinutes(61));
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(2, _handler.AuthCalls);
        Assert.Equal(StaticValue, material.Value);
        provider.Dispose();
    }

    [Fact]
    public async Task Lease_Expiry_Never_Exceeds_Token_Lifetime()
    {
        UseRecordedShapes();
        _handler.TokenJson = """{"access_token":"short-lived","expires_in":120,"token_type":"Bearer"}""";
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"), clock: clock);

        // The token lives 120s with 60s skew: the lease is capped at 60s,
        // not the 20-minute static window — a secret is never cached beyond
        // its lease.
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(start + TimeSpan.FromSeconds(60), material.ExpiresAt);
        provider.Dispose();
    }

    [Fact]
    public async Task Value_Never_Reaches_Logs()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        var factory = new CapturingLoggerFactory(log);
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"), log: log);
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider, log: factory.CreateLogger("test"));
            var itemId = WorkItemId.New();
            var project = GrantedProject(Secret("PAID_API_TOKEN"));

            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work, _ => null,
                itemDeadline: null, log: factory.CreateLogger("test"));
            await manager.RenewDueLeasesAsync(_ => (DateTimeOffset?)null);
            await manager.RevokeWorkItemLeasesAsync(itemId);

            string all;
            lock (log.Messages)
                all = string.Join('\n', log.Messages);
            Assert.DoesNotContain(StaticValue, all);
            Assert.DoesNotContain(ClientSecret, all);
            Assert.DoesNotContain(AccessToken, all);
            Assert.Contains(material.IssuedLeases[0].LeaseId, all);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Theory]
    [InlineData(401, BitwardenFailureKind.Unauthorized)]
    [InlineData(403, BitwardenFailureKind.Unauthorized)]
    [InlineData(429, BitwardenFailureKind.RateLimited)]
    [InlineData(500, BitwardenFailureKind.BackendError)]
    public async Task Backend_Faults_Classify_As_Infrastructure(int status, BitwardenFailureKind kind)
    {
        _handler.TokenJson = Fixture("token.json");
        _handler.SecretJson = Fixture("secret.json");
        _handler.FailSecretStatus = status;
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
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
    public async Task Secret_Listing_Failure_Is_Infrastructure()
    {
        UseRecordedShapes();
        _handler.FailListStatus = 500;
        var provider = CreateProvider(KeyMapping("PAID_API_TOKEN", "PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.BackendError, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Identity_Rejection_Is_Unauthorized_Infrastructure()
    {
        UseRecordedShapes();
        _handler.FailTokenStatus = 400;
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.Unauthorized, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
        Assert.DoesNotContain(ClientSecret, ex.Message);
    }

    [Fact]
    public async Task Unknown_Secret_Is_Configuration_Not_Infrastructure()
    {
        UseRecordedShapes();
        _handler.FailSecretStatus = 404;
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.NotFound, ex.Kind);
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
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.Unreachable, ex.Kind);
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
            IdMapping("PAID_API_TOKEN"),
            env: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);
        Assert.Equal(0, _handler.AuthCalls);
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
        var api = new BitwardenRestClient(http);
        var ex = await Assert.ThrowsAsync<BitwardenException>(() =>
            api.GetSecretAsync(
                "https://bitwarden.example.com", AccessToken, SecretId, null, 256 * 1024));
        // A backend 3xx is never followed: it fails closed as a backend
        // fault (infrastructure, never a diff verdict) — the bearer token
        // goes nowhere else.
        Assert.Equal(BitwardenFailureKind.InvalidResponse, ex.Kind);
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
        using var client = BitwardenHttpClients.Create(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(
            redirector.Url + "secrets-manager/secrets/" + SecretId);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, redirector.Hits);
        Assert.Equal(0, sink.Hits);
    }

    [Fact]
    public async Task Key_Resolution_Is_Exact_Match_And_Ambiguity_Fails_Loud()
    {
        UseRecordedShapes();
        var provider = CreateProvider(KeyMapping("PAID_API_TOKEN", "PAID_API_TOKEN"));
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(StaticValue, material.Value);
        Assert.True(_handler.SawReadOf(SecretId));

        // A similarly-named key must not shadow the match: ambiguity is a
        // loud configuration fault, never a silent wrong-secret read.
        _handler.ListJson = """{"object":"list","data":[{"id":"aaaaaaaa-0000-4000-8000-000000000001","key":"PAID_API_TOKEN"},{"id":"bbbbbbbb-0000-4000-8000-000000000002","key":"PAID_API_TOKEN"}]}""";
        var ambiguous = CreateProvider(KeyMapping("PAID_API_TOKEN", "PAID_API_TOKEN"));
        var ex = await Assert.ThrowsAsync<BitwardenException>(() => ambiguous.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);

        // Case-insensitive near-misses never resolve either.
        _handler.ListJson = Fixture("secrets-list.json");
        var wrongCase = CreateProvider(KeyMapping("PAID_API_TOKEN", "paid_api_token"));
        var missing = await Assert.ThrowsAsync<BitwardenException>(() => wrongCase.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task Secret_From_Wrong_Project_Fails_Loud()
    {
        UseRecordedShapes();
        _handler.SecretJson = """{"object":"secret","id":"2c4c8a1e-3f5b-4a6c-9d7e-8f0a1b2c3d4e","projectId":"ffffffff-0000-4000-8000-000000000099","key":"PAID_API_TOKEN","value":"wrong-project-value"}""";
        var config = IdMapping("PAID_API_TOKEN");
        config["Mappings:0:ProjectId"] = "11111111-2222-4333-8444-555555555555";
        var provider = CreateProvider(config);

        var ex = await Assert.ThrowsAsync<BitwardenException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(BitwardenFailureKind.Misconfigured, ex.Kind);
        Assert.DoesNotContain("wrong-project-value", ex.Message);
    }

    [Fact]
    public async Task Foreign_Lease_Handle_Is_Rejected()
    {
        UseRecordedShapes();
        var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));
        var ex = await Assert.ThrowsAsync<BitwardenException>(
            () => provider.RenewAsync("doppler.s.PAID_API_TOKEN.abc123"));
        Assert.Equal(BitwardenFailureKind.Misconfigured, ex.Kind);
        await Assert.ThrowsAsync<BitwardenException>(() => provider.RevokeAsync("not-a-lease"));
    }

    [Fact]
    public async Task Static_Only_Path_Still_Works_With_Provider_Present()
    {
        var hostVar = $"CODEYBOX_TEST_BITWARDEN_{Guid.NewGuid():N}".ToUpperInvariant();
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            UseRecordedShapes();
            var provider = CreateProvider(IdMapping("PAID_API_TOKEN"));
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
            ["ApiUrl"] = "http://bitwarden.example.com",
            ["IdentityUrl"] = "not-a-url",
            ["ClientSecretEnvVar"] = "",
            ["Mappings:0:SandboxEnvVar"] = "A",
            ["Mappings:0:SecretId"] = SecretId,
            ["Mappings:1:SandboxEnvVar"] = "A",
            ["Mappings:1:SecretId"] = SecretId,
            ["Mappings:2:SandboxEnvVar"] = "bad-name!",
            ["Mappings:2:SecretId"] = SecretId,
            ["Mappings:3:SandboxEnvVar"] = "BOTH",
            ["Mappings:3:SecretId"] = SecretId,
            ["Mappings:3:SecretKey"] = "BOTH",
            ["Mappings:4:SandboxEnvVar"] = "NEITHER",
            ["Mappings:5:SandboxEnvVar"] = "BAD_ID",
            ["Mappings:5:SecretId"] = "not-a-uuid",
            ["Mappings:6:SandboxEnvVar"] = "KEY_NO_ORG",
            ["Mappings:6:SecretKey"] = "SOME_KEY",
            ["Mappings:6:OrganizationId"] = "",
            ["Mappings:7:SandboxEnvVar"] = "BAD_PROJECT",
            ["Mappings:7:SecretId"] = SecretId,
            ["Mappings:7:ProjectId"] = "not-a-uuid",
        });
        // KEY_NO_ORG needs an empty provider organisation to trip the
        // key-without-org error: clear it for this case.
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("'A'", StringComparison.Ordinal) && e.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad-name!", StringComparison.Ordinal) && e.Contains("POSIX", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("plain http", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("IdentityUrl", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("exactly one of SecretId and SecretKey", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("not-a-uuid", StringComparison.Ordinal) && e.Contains("SecretId", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("not-a-uuid", StringComparison.Ordinal) && e.Contains("ProjectId", StringComparison.Ordinal));
    }

    [Fact]
    public void Key_Without_Organisation_Is_Rejected()
    {
        var config = KeyMapping("PAID_API_TOKEN", "PAID_API_TOKEN");
        config["OrganizationId"] = "";
        var provider = CreateProvider(config);
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("organisation", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Live_Fetch_Against_Real_Instance()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BW_SM_LIVE_API_URL")),
            "BW_SM_LIVE_API_URL is not set; the live Bitwarden integration test is opt-in (see plugins/credentials/CodeyBox.BitwardenPlugin/README.md).");
        var apiUrl = Environment.GetEnvironmentVariable("BW_SM_LIVE_API_URL")!;
        var identityUrl = Environment.GetEnvironmentVariable("BW_SM_LIVE_IDENTITY_URL");
        var clientId = Environment.GetEnvironmentVariable("BW_SM_LIVE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("BW_SM_LIVE_CLIENT_SECRET");
        var secretId = Environment.GetEnvironmentVariable("BW_SM_LIVE_SECRET_ID");
        var expected = Environment.GetEnvironmentVariable("BW_SM_LIVE_VALUE");
        Skip.If(string.IsNullOrWhiteSpace(identityUrl) || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(secretId)
            || expected is null,
            "BW_SM_LIVE_IDENTITY_URL/CLIENT_ID/CLIENT_SECRET/SECRET_ID/VALUE are not all set.");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LIVE_SECRET"] = clientSecret,
        };
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new BitwardenSecretProvider(
            http,
            PluginConfig(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = "true",
                ["ApiUrl"] = apiUrl,
                ["IdentityUrl"] = identityUrl,
                ["ClientId"] = clientId,
                ["ClientSecretEnvVar"] = "LIVE_SECRET",
                ["Mappings:0:SandboxEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:SecretId"] = secretId,
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
