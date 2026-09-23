using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.InfisicalPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests.Infisical;

/// <summary>
/// Verification for the Infisical credential plugin against the shared
/// lease-shaped contract: a granted group resolves while an ungranted one
/// is never fetched, leases renew across phases longer than their TTL,
/// teardown revokes against the backend (not by assumption), the
/// reconciliation sweep covers failed teardowns, values never reach logs,
/// backend faults classify as infrastructure (never a diff verdict), and
/// brokered mode lets the workload use the credential without receiving
/// it. REST is faked at the transport; the manager, store shape, and sweep
/// path are the real production wiring. Recorded payload shapes live in
/// <c>Fixtures/infisical/</c>; the one live test runs only when
/// <c>INFISICAL_LIVE_*</c> env is set, so offline runs rely on the recorded
/// shapes plus the documented reason in the plugin README.
/// </summary>
public sealed class InfisicalPluginTests : IDisposable
{
    private const string StaticValue = "infisical-live-value-9f8e7d6c5b";
    private const string DynamicValue = "infisical-dynamic-value-4a3b2c1d";
    private const string ServerLeaseId = "11111111-2222-4333-8444-555555555555";

    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal)
    {
        ["INFISICAL_CLIENT_ID"] = "test-client-id",
        ["INFISICAL_CLIENT_SECRET"] = "test-client-secret",
    };

    private readonly InfisicalFakeHandler _handler = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handler.Dispose();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class InfisicalFakeHandler : HttpMessageHandler
    {
        public string LoginJson = "{}";
        public string RawJson = "{}";
        public string LeaseCreateJson = "{}";
        public string LeaseRenewJson = "{}";
        public bool FailLoginUnauthorized;
        public bool Unreachable;
        public int FailFetchStatus;
        public bool FailDeleteOnce;
        public readonly List<(string Method, string Path)> Requests = [];
        public readonly HashSet<string> ServerLeases = new(StringComparer.Ordinal);
        public readonly HashSet<string> RevokedServerLeases = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (Requests)
                Requests.Add((request.Method.Method, path));
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");
            if (path.EndsWith("/api/v1/auth/universal-auth/login", StringComparison.Ordinal))
            {
                if (FailLoginUnauthorized)
                    return Json(new { message = "Invalid client credentials.", error = "Unauthorized", statusCode = 401 }, HttpStatusCode.Unauthorized);
                return Raw(LoginJson);
            }
            if (path.StartsWith("/api/v3/secrets/raw/", StringComparison.Ordinal))
            {
                if (FailFetchStatus != 0)
                {
                    var failure = new HttpResponseMessage((HttpStatusCode)FailFetchStatus)
                    {
                        Content = new StringContent(
                            FailFetchStatus == 429
                                ? """{"message":"Rate limit exceeded.","error":"Too Many Requests","statusCode":429}"""
                                : """{"message":"Upstream exploded.","error":"Internal Server Error","statusCode":500}""",
                            Encoding.UTF8, "application/json"),
                    };
                    if (FailFetchStatus == 429)
                        failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                    return failure;
                }
                return Raw(RawJson);
            }
            if (path.EndsWith("/api/v1/dynamic-secrets/leases", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                lock (Requests)
                    ServerLeases.Add(ServerLeaseId);
                return Raw(LeaseCreateJson);
            }
            if (path.Contains("/api/v1/dynamic-secrets/leases/", StringComparison.Ordinal))
            {
                var leaseId = path[(path.LastIndexOf('/') + 1)..];
                if (leaseId == "renew")
                    leaseId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2];
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                    return Raw(LeaseRenewJson);
                if (request.Method == HttpMethod.Delete)
                {
                    if (FailDeleteOnce)
                    {
                        FailDeleteOnce = false;
                        return Json(new { message = "Upstream exploded.", error = "Internal Server Error", statusCode = 500 },
                            HttpStatusCode.InternalServerError);
                    }
                    lock (Requests)
                    {
                        ServerLeases.Remove(leaseId);
                        RevokedServerLeases.Add(leaseId);
                    }
                    return Json(new { }, HttpStatusCode.OK);
                }
            }
            return Json(new { message = "Not here.", error = "Not Found", statusCode = 404 }, HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Json(object value, HttpStatusCode status) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        public int CountRequests(string method, string pathPrefix)
        {
            lock (Requests)
                return Requests.Count(r =>
                    string.Equals(r.Method, method, StringComparison.Ordinal)
                    && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));
        }

        public bool SawFetchOf(string secretKey)
        {
            lock (Requests)
                return Requests.Any(r =>
                    r.Method == HttpMethod.Get.Method
                    && r.Path.EndsWith("/api/v3/secrets/raw/" + secretKey, StringComparison.Ordinal));
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
        File.ReadAllText(Path.Combine("Fixtures", "infisical", name));

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
        ["SiteUrl"] = "https://infisical.example.com",
        ["WorkspaceId"] = "12ab34cd56ef78ab90cd12ef34",
        ["ProjectSlug"] = "acme",
        ["Environment"] = "prod",
        ["StaticLeaseTtlMinutes"] = "20",
    };

    private void UseRecordedShapes()
    {
        _handler.LoginJson = Fixture("login.json");
        _handler.RawJson = Fixture("raw-secret.json");
        _handler.LeaseCreateJson = Fixture("lease-create.json");
        _handler.LeaseRenewJson = Fixture("lease-renew.json");
    }

    private InfisicalSecretProvider CreateProvider(
        Dictionary<string, string?>? overrides = null,
        TimeProvider? clock = null,
        ILogger? log = null,
        HttpClient? brokerForward = null,
        Dictionary<string, string?>? env = null)
    {
        var merged = BaseConfig();
        if (overrides is not null)
            foreach (var (k, v) in overrides)
                merged[k] = v;
        var http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new InfisicalSecretProvider(
            http,
            PluginConfig(merged),
            clock,
            name => (env ?? _env).TryGetValue(name, out var v) ? v : null,
            brokerForward,
            log);
    }

    private SecretLeaseManager CreateManager(
        MemorySecretLeaseStore store,
        InfisicalSecretProvider provider,
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
        Id = new ProjectId("infisical-project"),
        DisplayName = "Infisical Project",
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

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_By_Default_Issues_Nothing()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = "false",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });
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
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "other-group",
            ["Mappings:1:SecretKey"] = "OTHER_API_KEY",
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
        Assert.StartsWith("infisical.s.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_handler.SawFetchOf("PAID_API_KEY"));
        Assert.False(_handler.SawFetchOf("OTHER_API_KEY"));
        Assert.Equal(1, _handler.CountRequests(HttpMethod.Get.Method, "/api/v3/secrets/raw/"));
        Assert.DoesNotContain(StaticValue, string.Join('\n', log.Messages));
    }

    [Fact]
    public async Task Ungranted_But_Mapped_Secret_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        // The mapping exists but no grant authorises the group: the manager
        // must never call the provider, so no HTTP happens at all.
        var project = new Project
        {
            Id = new ProjectId("infisical-project"),
            DisplayName = "Infisical Project",
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
    }

    [Fact]
    public async Task Lease_Is_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        }, clock: clock);
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
        Assert.True(_handler.CountRequests(HttpMethod.Get.Method, "/api/v3/secrets/raw/") >= 1 + renewals,
            "Renewal must re-fetch so rotation propagates within one window.");
    }

    [Fact]
    public async Task Dynamic_Lease_Renew_Returns_Server_Expiry()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicSecretName"] = "ci-database",
            ["Mappings:0:DataField"] = "password",
        });
        var material = await provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(DynamicValue, material.Value);
        Assert.StartsWith("infisical.d.", material.LeaseId, StringComparison.Ordinal);
        Assert.EndsWith(ServerLeaseId, material.LeaseId, StringComparison.Ordinal);

        var renewed = await provider.RenewAsync(material.LeaseId);
        Assert.Equal(DateTimeOffset.Parse("2030-05-01T00:40:00.000Z"), renewed);
        Assert.Equal(1, _handler.CountRequests(HttpMethod.Post.Method, "/api/v1/dynamic-secrets/leases/"));
    }

    [Fact]
    public async Task Teardown_Revokes_Verified_Against_Backend()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicSecretName"] = "ci-database",
            ["Mappings:0:DataField"] = "password",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();
        var project = GrantedProject(Secret("DB_PASSWORD"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.Contains(ServerLeaseId, _handler.ServerLeases);

        var report = await manager.RevokeWorkItemLeasesAsync(itemId);

        Assert.True(report.AllRevoked);
        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.DoesNotContain(ServerLeaseId, _handler.ServerLeases);
        Assert.Contains(ServerLeaseId, _handler.RevokedServerLeases);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        Assert.Empty(await store.ListOutstandingAsync());

        // Idempotent: a second teardown revoke still reports success.
        var again = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(again.AllRevoked);
    }

    [Fact]
    public async Task Static_Revoke_Is_Local_Invalidation_And_Succeeds()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));

        // No server call carries a static revocation (Infisical static
        // secrets have no server-side lease); revocation must still succeed
        // and stay idempotent.
        var rawBefore = _handler.CountRequests(HttpMethod.Get.Method, "/api/v3/secrets/raw/");
        await provider.RevokeAsync(material.LeaseId);
        await provider.RevokeAsync(material.LeaseId);
        Assert.Equal(rawBefore, _handler.CountRequests(HttpMethod.Get.Method, "/api/v3/secrets/raw/"));
    }

    [Fact]
    public async Task Reconciliation_Sweep_Revokes_Terminal_Item_After_Failed_Teardown()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicSecretName"] = "ci-database",
            ["Mappings:0:DataField"] = "password",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_PASSWORD",
            ["Mappings:1:DynamicSecretName"] = "ci-database",
            ["Mappings:1:DataField"] = "password",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();
        var liveItemId = WorkItemId.New();
        var project = GrantedProject(Secret("DB_PASSWORD"));
        var liveProject = GrantedProject(Secret("OTHER_PASSWORD"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        var liveMaterial = await manager.MaterializeForScopeAsync(
            liveProject, liveItemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var liveLease = Assert.Single(liveMaterial.IssuedLeases);

        // Teardown fails: the issuer refuses once.
        _handler.FailDeleteOnce = true;
        var failed = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.False(failed.AllRevoked);
        Assert.Equal(SecretLeaseStatus.RevocationFailed, (await store.GetAsync(lease.LeaseId))!.Status);

        // The sweep revokes the terminal item's lease against the backend
        // and leaves the live item's lease alone.
        var states = new Dictionary<Guid, WorkItemState>
        {
            [itemId.Value] = WorkItemState.Done,
            [liveItemId.Value] = WorkItemState.Working,
        };
        var report = await manager.ReconcileAsync(
            id => Task.FromResult<WorkItemState?>(states.TryGetValue(id, out var state) ? state : null),
            _ => (DateTimeOffset?)null);

        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.Contains(ServerLeaseId, _handler.RevokedServerLeases);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        var outstanding = await store.ListOutstandingAsync();
        Assert.Contains(outstanding, l => l.LeaseId == liveLease.LeaseId);
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
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            ["Mappings:1:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:1:DynamicSecretName"] = "ci-database",
            ["Mappings:1:DataField"] = "password",
        }, log: log);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider, log: factory.CreateLogger("test"));
        var itemId = WorkItemId.New();
        var project = GrantedProject(Secret("PAID_API_TOKEN"), Secret("DB_PASSWORD"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null,
            itemDeadline: null, log: factory.CreateLogger("test"));
        await manager.RenewDueLeasesAsync(_ => (DateTimeOffset?)null);
        await manager.RevokeWorkItemLeasesAsync(itemId);

        string all;
        lock (log.Messages)
            all = string.Join('\n', log.Messages);
        Assert.DoesNotContain(StaticValue, all);
        Assert.DoesNotContain(DynamicValue, all);
        Assert.DoesNotContain("test-client-secret", all);
        Assert.DoesNotContain("test-access-token", all);
        Assert.Contains(material.IssuedLeases[0].LeaseId, all);
    }

    [Theory]
    [InlineData(401, InfisicalFailureKind.Unauthorized)]
    [InlineData(403, InfisicalFailureKind.Unauthorized)]
    [InlineData(429, InfisicalFailureKind.RateLimited)]
    [InlineData(500, InfisicalFailureKind.BackendError)]
    public async Task Backend_Faults_Classify_As_Infrastructure(int status, InfisicalFailureKind kind)
    {
        _handler.LoginJson = Fixture("login.json");
        _handler.RawJson = Fixture("raw-secret.json");
        _handler.FailFetchStatus = status;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });

        var ex = await Assert.ThrowsAsync<InfisicalException>(() => provider.IssueAsync(
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
    public async Task Unauthorized_Login_Is_Infrastructure()
    {
        _handler.LoginJson = Fixture("login.json");
        _handler.FailLoginUnauthorized = true;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });

        var ex = await Assert.ThrowsAsync<InfisicalException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(InfisicalFailureKind.Unauthorized, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Unreachable_Backend_Is_Infrastructure_And_Never_A_Verdict()
    {
        _handler.Unreachable = true;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        });

        var ex = await Assert.ThrowsAsync<InfisicalException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(InfisicalFailureKind.Unreachable, ex.Kind);
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
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
                ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            },
            env: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<InfisicalException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(InfisicalFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Brokered_Proxy_Uses_Credential_Without_Receiving_Value()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        string? seenAuth = null;
        var upstream = new HttpClient(new DelegatingHandlerStub((request, ct) =>
        {
            seenAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rows":[]}""", Encoding.UTF8, "application/json"),
            });
        }));
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BrokerEnabled"] = "true",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            ["Mappings:0:Brokered"] = "true",
            ["Mappings:0:BrokerUpstreamBaseUrl"] = "https://api.example.com",
            ["Mappings:0:BrokerInjectHeader"] = "Authorization",
            ["Mappings:0:BrokerInjectScheme"] = "Bearer",
            ["Mappings:0:BrokerAllowedPaths:0"] = "/v1/query",
        }, log: log, brokerForward: upstream);
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider);
            var itemId = WorkItemId.New();
            var material = await manager.MaterializeForScopeAsync(
                GrantedProject(Secret("PAID_API_TOKEN")), itemId,
                ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);

            var lease = Assert.Single(material.IssuedLeases);
            Assert.True(lease.Brokered);
            Assert.NotNull(lease.Endpoint);
            // The sandbox environment carries only the endpoint — the value
            // appears nowhere in it.
            Assert.Equal(lease.Endpoint, material.Environment["PAID_API_TOKEN"]);
            Assert.DoesNotContain(StaticValue, material.Environment["PAID_API_TOKEN"]);
            Assert.Contains(lease.Endpoint!, material.BrokerEndpoints);
            Assert.Equal(
                ["127.0.0.1"],
                PipelineRunner.BrokerEndpointHosts(material.BrokerEndpoints));

            // The workload uses the credential through the proxy: the
            // upstream sees the injected header, the guest sees only the
            // upstream response.
            using var guest = new HttpClient();
            using var guestResponse = await guest.PostAsync(
                lease.Endpoint + "/v1/query", new StringContent("""{"q":1}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, guestResponse.StatusCode);
            var guestBody = await guestResponse.Content.ReadAsStringAsync();
            Assert.True(guestBody == """{"rows":[]}""", $"Unexpected guest body: {guestBody}.");
            Assert.DoesNotContain(StaticValue, guestBody);
            Assert.Equal("Bearer " + StaticValue, seenAuth);

            string all;
            lock (log.Messages)
                all = string.Join('\n', log.Messages);
            Assert.DoesNotContain(StaticValue, all);
            Assert.Contains(lease.LeaseId, all);

            // Teardown revokes the endpoint: the guest gets 404 afterwards.
            await manager.RevokeWorkItemLeasesAsync(itemId);
            using var after = await guest.GetAsync(lease.Endpoint + "/v1/query");
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }
        finally
        {
            (provider as IDisposable).Dispose();
            upstream.Dispose();
        }
    }

    [Fact]
    public async Task Brokered_Mapping_With_Disabled_Broker_Refuses_Silent_Downgrade()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            ["Mappings:0:Brokered"] = "true",
            ["Mappings:0:BrokerUpstreamBaseUrl"] = "https://api.example.com",
        });

        // Validation flags it, and issue refuses rather than leaking the
        // value into the guest as a direct lease.
        Assert.Contains(provider.CurrentOptions().Validate(),
            e => e.Contains("BrokerEnabled", StringComparison.Ordinal));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
    }

    [Fact]
    public async Task Backend_Redirect_Is_Refused_As_Infrastructure()
    {
        var followed = 0;
        using var http = new HttpClient(new DelegatingHandlerStub((request, ct) =>
        {
            Interlocked.Increment(ref followed);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://127.0.0.1:9/exfil");
            return Task.FromResult(redirect);
        }))
        { Timeout = TimeSpan.FromSeconds(10) };
        var api = new InfisicalRestClient(http);
        var ex = await Assert.ThrowsAsync<InfisicalException>(() =>
            api.LoginUniversalAuthAsync(
                "https://infisical.example.com", "test-id", "test-secret", TimeSpan.FromSeconds(30)));
        // A backend 3xx is never followed: it fails closed as a backend
        // fault (infrastructure, never a diff verdict), with exactly one
        // request sent — the client secret goes nowhere else.
        Assert.Equal(InfisicalFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Contains("redirect", ex.Message);
        Assert.Equal(1, Volatile.Read(ref followed));
    }

    [Fact]
    public async Task Production_Http_Client_Never_Follows_Redirects()
    {
        // Real loopback wiring: the redirector answers 302 to a sink that
        // records everything it receives. If the client ever followed, the
        // sink would see the secret-bearing body and this test would fail.
        using var sink = new RecordingStub(_ => (200, null, "sink"));
        using var redirector = new RecordingStub(_ => (302, sink.Url + "landing", string.Empty));
        using var client = CredentialHttp.CreateNoRedirectClient(TimeSpan.FromSeconds(10));
        using var response = await client.PostAsync(
            redirector.Url,
            new StringContent("""{"clientSecret":"must-not-leak"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, redirector.Hits);
        Assert.Equal(0, sink.Hits);
        Assert.False(sink.SawText("must-not-leak"));
    }

    [Fact]
    public async Task Broker_Returns_Upstream_Redirect_Without_Following()
    {
        using var sink = new RecordingStub(_ => (200, null, "sink"));
        using var upstream = new RecordingStub(_ => (302, sink.Url + "landing", string.Empty));
        using var forward = CredentialHttp.CreateNoRedirectClient(TimeSpan.FromSeconds(10));
        using var server = new InfisicalBrokerServer(forward, log: NullLogger.Instance);
        server.Start("127.0.0.1", 0);
        const string leaseId = "redirect-lease-handle";
        const string secret = "broker-secret-value-7c1d";
        server.Register(new InfisicalBrokerEntry
        {
            LeaseId = leaseId,
            Value = secret,
            UpstreamBaseUrl = upstream.Url.TrimEnd('/'),
            InjectHeader = "Authorization",
            InjectScheme = "Bearer",
            AllowedPaths = [],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        using var guest = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var first = await guest.GetAsync(
            $"http://127.0.0.1:{server.ActualPort}/v1/proxy/{leaseId}/v1/query");
        // The upstream 3xx reaches the guest untouched — no follow, no
        // credential at the redirect target, no Location forwarded.
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Null(first.Headers.Location);
        Assert.Equal(0, sink.Hits);
        Assert.False(sink.SawText(secret));
        // The entry survives the redirect: still a proxied 302, not a 404.
        using var second = await guest.GetAsync(
            $"http://127.0.0.1:{server.ActualPort}/v1/proxy/{leaseId}/v1/query");
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Equal(2, upstream.Hits);
        Assert.Equal(0, sink.Hits);
    }

    [Fact]
    public async Task Static_Only_Path_Still_Works_With_Provider_Present()
    {
        var hostVar = $"CODEYBOX_TEST_INFISICAL_{Guid.NewGuid():N}".ToUpperInvariant();
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            UseRecordedShapes();
            var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
                ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            });
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
            ["ProjectSlug"] = "",
            ["Mappings:0:SandboxEnvVar"] = "A",
            ["Mappings:0:SecretKey"] = "K1",
            ["Mappings:0:DynamicSecretName"] = "D1",
            ["Mappings:1:SandboxEnvVar"] = "B",
            ["Mappings:1:DynamicSecretName"] = "D2",
            ["Mappings:2:SandboxEnvVar"] = "bad-name!",
            ["Mappings:2:SecretKey"] = "K3",
        });
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("'A'", StringComparison.Ordinal) && e.Contains("exactly one", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'B'", StringComparison.Ordinal) && e.Contains("DataField", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("ProjectSlug", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad-name!", StringComparison.Ordinal) && e.Contains("POSIX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renew_After_Restart_Rebuilds_Context_From_Lease_Handle()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
        };
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
        Assert.Equal(2, _handler.CountRequests(HttpMethod.Get.Method, "/api/v3/secrets/raw/"));
        restarted.Dispose();
        provider.Dispose();
    }

    [Fact]
    public async Task Brokered_Static_Endpoint_Is_Restored_After_Restart()
    {
        UseRecordedShapes();
        // Brokered endpoints embed the port, so restart recovery needs a
        // pinned BrokerBindPort (documented in the README); an ephemeral
        // port cannot keep a previously issued endpoint alive.
        var port = ProbeFreePort();
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BrokerEnabled"] = "true",
            ["BrokerBindPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretKey"] = "PAID_API_KEY",
            ["Mappings:0:Brokered"] = "true",
            ["Mappings:0:BrokerUpstreamBaseUrl"] = "https://api.example.com",
        };
        var upstream = new HttpClient(new DelegatingHandlerStub((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            })));
        try
        {
            var provider = CreateProvider(config, clock: clock, brokerForward: upstream);
            var material = await provider.IssueAsync(
                Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
            Assert.True(material.Brokered);
            provider.Dispose();

            // Fresh instance, empty broker: renew re-fetches and restores
            // the endpoint, so the guest keeps working.
            var restarted = CreateProvider(config, clock: clock, brokerForward: upstream);
            try
            {
                clock.Advance(TimeSpan.FromMinutes(19));
                await restarted.RenewAsync(material.LeaseId!);
                using var guest = new HttpClient();
                using var response = await guest.GetAsync(material.Endpoint + "/status");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                restarted.Dispose();
            }
        }
        finally
        {
            upstream.Dispose();
        }
    }

    [SkippableFact]
    public async Task Live_Fetch_Against_Real_Instance()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INFISICAL_LIVE_SITE")),
            "INFISICAL_LIVE_SITE is not set; the live Infisical integration test is opt-in (see plugins/credentials/CodeyBox.InfisicalPlugin/README.md).");
        var site = Environment.GetEnvironmentVariable("INFISICAL_LIVE_SITE")!;
        var clientId = Environment.GetEnvironmentVariable("INFISICAL_LIVE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("INFISICAL_LIVE_CLIENT_SECRET");
        var workspace = Environment.GetEnvironmentVariable("INFISICAL_LIVE_WORKSPACE");
        var environment = Environment.GetEnvironmentVariable("INFISICAL_LIVE_ENV") ?? "dev";
        var key = Environment.GetEnvironmentVariable("INFISICAL_LIVE_KEY");
        Skip.If(string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret),
            "INFISICAL_LIVE_CLIENT_ID/INFISICAL_LIVE_CLIENT_SECRET are not set.");
        Skip.If(string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(key),
            "INFISICAL_LIVE_WORKSPACE/INFISICAL_LIVE_KEY are not set.");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LIVE_ID"] = clientId,
            ["LIVE_SECRET"] = clientSecret,
        };
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new InfisicalSecretProvider(
            http,
            PluginConfig(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = "true",
                ["SiteUrl"] = site,
                ["ClientIdEnvVar"] = "LIVE_ID",
                ["ClientSecretEnvVar"] = "LIVE_SECRET",
                ["WorkspaceId"] = workspace,
                ["Environment"] = environment,
                ["Mappings:0:SandboxEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:SecretKey"] = key,
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
        Assert.False(string.IsNullOrEmpty(material.Value));
        await provider.RevokeAsync(material.LeaseId);
    }

    private static int ProbeFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class DelegatingHandlerStub(        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)        : HttpMessageHandler
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
        private readonly List<string> _seen = [];
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

        public bool SawText(string text)
        {
            lock (_seen)
                return _seen.Any(s => s.Contains(text, StringComparison.Ordinal));
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
                    var request = context.Request;
                    string body = string.Empty;
                    if (request.HasEntityBody)
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                        body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    }
                    lock (_seen)
                        _seen.Add($"{request.HttpMethod} {request.Url?.AbsolutePath} auth={request.Headers["Authorization"]} body={body}");
                    var (status, location, responseBody) = _respond(request);
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
}
