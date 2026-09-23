using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.DopplerPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests.Doppler;

/// <summary>
/// Verification for the Doppler credential plugin against the shared
/// lease-shaped contract: a granted group resolves while an ungranted one
/// is never fetched, leases renew across phases longer than their TTL,
/// teardown revokes against the backend (not by assumption), the
/// reconciliation sweep covers failed teardowns, values never reach logs,
/// backend faults classify as infrastructure (never a diff verdict), and
/// short-lived service-account identity tokens exchange, renew, and revoke
/// server-side. REST is faked at the transport; the manager, store shape,
/// and sweep path are the real production wiring. Recorded payload shapes
/// live in <c>Fixtures/doppler/</c>, transcribed from the live OpenAPI +
/// docs; the one live test runs only when <c>DOPPLER_LIVE_*</c> env is set,
/// so offline runs rely on the recorded shapes plus the documented reason
/// in the plugin README.
/// </summary>
public sealed class DopplerPluginTests : IDisposable
{
    private const string StaticValue = "doppler-live-value-7a4b9c2d1e";
    private const string IdentityToken = "dp.sa.ident.test-placeholder-token";
    private static readonly DateTimeOffset IdentityExpiry =
        new(2030, 5, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal)
    {
        ["DOPPLER_TOKEN"] = "dp.st.prd.test-token",
        ["DOPPLER_OIDC_TOKEN"] = "test-oidc-token",
    };

    private readonly DopplerFakeHandler _handler = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handler.Dispose();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class DopplerFakeHandler : HttpMessageHandler
    {
        public string SecretJson = "{}";
        public string OidcJson = "{}";

        /// <summary>
        /// When set, the OIDC endpoint mints a token expiring at this
        /// instant instead of the recorded shape — models the server
        /// issuing a fresh short-lived token on re-exchange.
        /// </summary>
        public DateTimeOffset? OidcExpiresAt;
        public bool Unreachable;
        public bool FailOidcUnauthorized;
        public bool FailRevokeOnce;
        public int FailFetchStatus;
        public readonly List<(string Method, string Path)> Requests = [];
        public readonly List<string> FetchedSecretNames = [];
        public readonly HashSet<string> RevokedTokens = new(StringComparer.Ordinal);
        public int Exchanges;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            lock (Requests)
                Requests.Add((request.Method.Method, path));
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");
            if (path.EndsWith("/v3/auth/oidc", StringComparison.Ordinal))
            {
                if (FailOidcUnauthorized)
                    return Json(new { message = "Invalid identity.", error = "Unauthorized", statusCode = 401 }, HttpStatusCode.Unauthorized);
                Interlocked.Increment(ref Exchanges);
                if (OidcExpiresAt is { } fresh)
                    return Json(new { token = "dp.sa.ident.test-placeholder-token", expires_at = fresh.ToString("O") }, HttpStatusCode.OK);
                return Raw(OidcJson);
            }
            if (path.EndsWith("/v3/auth/revoke", StringComparison.Ordinal))
            {
                if (FailRevokeOnce)
                {
                    FailRevokeOnce = false;
                    return Json(new { message = "Upstream exploded.", error = "Internal Server Error", statusCode = 500 },
                        HttpStatusCode.InternalServerError);
                }
                var body = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
                string? token = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("token", out var t))
                        token = t.GetString();
                }
                catch (JsonException) { }
                lock (Requests)
                    RevokedTokens.Add(token ?? string.Empty);
                return Json(new { }, HttpStatusCode.OK);
            }
            if (path.EndsWith("/v3/configs/config/secret", StringComparison.Ordinal))
            {
                if (FailFetchStatus != 0)
                {
                    var failure = new HttpResponseMessage((HttpStatusCode)FailFetchStatus)
                    {
                        Content = new StringContent(
                            FailFetchStatus == 429
                                ? """{"message":"Rate limit exceeded.","error":"Too Many Requests","statusCode":429}"""
                                : FailFetchStatus == 404
                                    ? """{"message":"Secret not found.","error":"Not Found","statusCode":404}"""
                                    : """{"message":"Upstream exploded.","error":"Internal Server Error","statusCode":500}""",
                            Encoding.UTF8, "application/json"),
                    };
                    if (FailFetchStatus == 429)
                        failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                    return failure;
                }
                lock (Requests)
                    FetchedSecretNames.Add(QueryValue(query, "name"));
                return Raw(SecretJson);
            }
            return Json(new { message = "Not here.", error = "Not Found", statusCode = 404 }, HttpStatusCode.NotFound);
        }

        private static string QueryValue(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var name = eq < 0 ? part : part[..eq];
                if (string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(eq < 0 ? string.Empty : part[(eq + 1)..]);
            }
            return string.Empty;
        }

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Json(object value, HttpStatusCode status) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        public int CountRequests(string method, string pathSuffix)
        {
            lock (Requests)
                return Requests.Count(r =>
                    string.Equals(r.Method, method, StringComparison.Ordinal)
                    && r.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
        }

        public bool SawFetchOf(string secretName)
        {
            lock (Requests)
                return FetchedSecretNames.Contains(secretName, StringComparer.Ordinal);
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
        File.ReadAllText(Path.Combine("Fixtures", "doppler", name));

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
        ["ApiUrl"] = "https://doppler.example.com",
        ["DefaultProject"] = "acme",
        ["DefaultConfig"] = "prd",
        ["ServiceTokenEnvVar"] = "DOPPLER_TOKEN",
        ["StaticLeaseTtlMinutes"] = "20",
    };

    private void UseRecordedShapes()
    {
        _handler.SecretJson = Fixture("secret.json");
        _handler.OidcJson = Fixture("oidc.json");
    }

    private DopplerSecretProvider CreateProvider(
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
        return new DopplerSecretProvider(
            http,
            PluginConfig(merged),
            clock,
            name => (env ?? _env).TryGetValue(name, out var v) ? v : null,
            log);
    }

    private static SecretLeaseManager CreateManager(
        MemorySecretLeaseStore store,
        DopplerSecretProvider provider,
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
        Id = new ProjectId("doppler-project"),
        DisplayName = "Doppler Project",
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

    private Dictionary<string, string?> StaticMapping(string sandboxEnvVar, string secretName, string group = "paid-api") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = sandboxEnvVar,
            ["Mappings:0:Group"] = group,
            ["Mappings:0:SecretName"] = secretName,
        };

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_By_Default_Issues_Nothing()
    {
        UseRecordedShapes();
        var config = StaticMapping("PAID_API_TOKEN", "PAID_API_KEY");
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
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "other-group",
            ["Mappings:1:SecretName"] = "OTHER_API_KEY",
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
        Assert.StartsWith("doppler.s.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_handler.SawFetchOf("PAID_API_KEY"));
        Assert.False(_handler.SawFetchOf("OTHER_API_KEY"));
        Assert.Equal(1, _handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret"));
        Assert.DoesNotContain(StaticValue, string.Join('\n', log.Messages));
    }

    [Fact]
    public async Task Ungranted_But_Mapped_Secret_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"));
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        // The mapping exists but no grant authorises the group: the manager
        // must never call the provider, so no HTTP happens at all.
        var project = new Project
        {
            Id = new ProjectId("doppler-project"),
            DisplayName = "Doppler Project",
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
    public async Task Doppler_Config_Resolves_Into_Group_Not_Beside_It()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DEV_TOKEN",
            ["Mappings:0:Group"] = "dev-api",
            ["Mappings:0:SecretName"] = "DEV_KEY",
            ["Mappings:0:Config"] = "dev",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("DEV_TOKEN", "dev-api")), WorkItemId.New(),
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);

        // Same provider, different config, resolved into its own group: the
        // grant on "dev-api" authorised it, and the fetch named DEV_KEY.
        Assert.Equal(StaticValue, material.Environment["DEV_TOKEN"]);
        Assert.True(_handler.SawFetchOf("DEV_KEY"));
        Assert.False(_handler.SawFetchOf("PAID_API_KEY"));
    }

    [Fact]
    public async Task Lease_Is_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"), clock: clock);
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
        Assert.True(_handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret") >= 1 + renewals,
            "Renewal must re-fetch so rotation propagates within one window.");
    }

    [Fact]
    public async Task Identity_Issue_Renew_Revoke_Against_Backend()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:Group"] = "paid-api",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
        }, clock: clock);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider, utcNow: () => clock.GetUtcNow());
        var itemId = WorkItemId.New();

        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), itemId,
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.Equal(StaticValue, material.Environment["PAID_API_TOKEN"]);
        Assert.StartsWith("doppler.i.", lease.LeaseId, StringComparison.Ordinal);
        Assert.Equal(1, _handler.Exchanges);
        // Lease expiry is the server's expires_at minus skew, capped by the
        // manager at its max lease lifetime — not a client guess either way.
        Assert.Equal(start + TimeSpan.FromMinutes(480), lease.ExpiresAt);

        // Renewal while the minted token is valid re-fetches without a new
        // exchange and keeps the server expiry.
        var renewed = await provider.RenewAsync(lease.LeaseId);
        Assert.Equal(IdentityExpiry - TimeSpan.FromSeconds(60), renewed);
        Assert.Equal(1, _handler.Exchanges);
        Assert.Equal(2, _handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret"));

        // Teardown revokes the minted token against the backend — verified
        // by the fake's server state, not by assuming the call succeeded.
        var report = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(report.AllRevoked);
        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.Contains(IdentityToken, _handler.RevokedTokens);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        Assert.Empty(await store.ListOutstandingAsync());

        // Idempotent: a second teardown revoke still reports success.
        var again = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.True(again.AllRevoked);
        provider.Dispose();
    }

    [Fact]
    public async Task Identity_Renew_After_Token_Expiry_Reexchanges()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
        }, clock: clock);
        try
        {
            var material = await provider.IssueAsync(
                Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
            Assert.StartsWith("doppler.i.", material.LeaseId, StringComparison.Ordinal);

            // Past the minted token's lifetime the provider exchanges again
            // (the host staged a fresh OIDC token) instead of trusting the
            // dead one. The fake mints a fresh server expiry for the
            // re-exchange, as the real endpoint would.
            _handler.OidcExpiresAt = IdentityExpiry + TimeSpan.FromDays(30);
            clock.Advance(IdentityExpiry - start + TimeSpan.FromMinutes(1));
            var renewed = await provider.RenewAsync(material.LeaseId);
            Assert.Equal(2, _handler.Exchanges);
            Assert.Equal(IdentityExpiry + TimeSpan.FromDays(30) - TimeSpan.FromSeconds(60), renewed);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task Identity_Renew_When_Exchange_Fails_Is_Infrastructure()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
        }, clock: clock);
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider, utcNow: () => clock.GetUtcNow());
            var itemId = WorkItemId.New();
            var material = await manager.MaterializeForScopeAsync(
                GrantedProject(Secret("PAID_API_TOKEN")), itemId,
                ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            var lease = Assert.Single(material.IssuedLeases);

            // The minted token lapses and the OIDC endpoint now refuses:
            // renewal fails loudly as infrastructure — never a diff
            // verdict — and the lease stays outstanding for the next sweep.
            clock.Advance(IdentityExpiry - start + TimeSpan.FromMinutes(1));
            _handler.FailOidcUnauthorized = true;
            var ex = await Assert.ThrowsAsync<DopplerException>(() => provider.RenewAsync(lease.LeaseId));
            Assert.Equal(DopplerFailureKind.Unauthorized, ex.Kind);
            Assert.True(ex.IsInfrastructure);
            Assert.Equal(WorkItemFailureKinds.Infrastructure, ex.FailureKindForWorkItem);
            Assert.Equal(SecretLeaseStatus.Active, (await store.GetAsync(lease.LeaseId))!.Status);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task Static_Revoke_Is_Local_Invalidation_And_Succeeds()
    {
        UseRecordedShapes();
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"));
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));

        // No server call carries a static revocation (Doppler static
        // secrets have no server-side lease); revocation must still succeed
        // and stay idempotent.
        var secretCallsBefore = _handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret");
        await provider.RevokeAsync(material.LeaseId);
        await provider.RevokeAsync(material.LeaseId);
        Assert.Equal(secretCallsBefore, _handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DopplerFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DopplerFailureKind.Unauthorized)]
    [InlineData((HttpStatusCode)429, DopplerFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, DopplerFailureKind.Misconfigured)]
    [InlineData(HttpStatusCode.InternalServerError, DopplerFailureKind.BackendError)]
    public async Task Revoke_Propagates_Non_NotFound_Failures_For_Retry(
        HttpStatusCode status, DopplerFailureKind kind)
    {
        // Only a genuinely absent token (404) is success-by-definition.
        // 401/403/429 — and any other non-404 failure — must propagate so
        // the sweep retries a revocation that never happened instead of
        // dropping the token and recording a revocation that never was.
        using var http = new HttpClient(new DelegatingHandlerStub((request, ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"message":"Revocation did not happen.","error":"Revocation did not happen"}""",
                    Encoding.UTF8, "application/json"),
            })))
        { Timeout = TimeSpan.FromSeconds(10) };
        var api = new DopplerRestClient(http);

        var ex = await Assert.ThrowsAsync<DopplerException>(() =>
            api.RevokeTokenAsync("https://doppler.example.com", "doomed-token"));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task Revoke_Treats_Unknown_Token_As_Revoked()
    {
        using var http = new HttpClient(new DelegatingHandlerStub((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"message":"Unknown token.","error":"Not Found"}""",
                    Encoding.UTF8, "application/json"),
            })))
        { Timeout = TimeSpan.FromSeconds(10) };
        var api = new DopplerRestClient(http);

        // Must not throw: an unknown or already-revoked token means the
        // revocation goal is already met (idempotent teardown).
        await api.RevokeTokenAsync("https://doppler.example.com", "doomed-token");
    }

    [Fact]
    public async Task Restart_Loses_Identity_Token_So_Revocation_Fails_Loud()
    {
        UseRecordedShapes();
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
        };
        var provider = CreateProvider(config);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();
        var material = await manager.MaterializeForScopeAsync(
            GrantedProject(Secret("PAID_API_TOKEN")), itemId,
            ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        provider.Dispose();

        // Simulate an orchestrator restart: a fresh provider instance has an
        // empty issue registry, so the memory-only minted token is
        // unaddressable. Revocation must fail loudly — never fake success —
        // while the short server-side expiry bounds the exposure.
        var restarted = CreateProvider(config);
        var restartedManager = CreateManager(store, restarted);
        try
        {
            var report = await restartedManager.RevokeWorkItemLeasesAsync(itemId);
            Assert.False(report.AllRevoked);
            Assert.Equal(SecretLeaseStatus.RevocationFailed, (await store.GetAsync(lease.LeaseId))!.Status);
            Assert.DoesNotContain(IdentityToken, _handler.RevokedTokens);
            var failure = Assert.Single(report.Failures);
            Assert.True(failure.Error.Contains("expires", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            restarted.Dispose();
        }
    }

    [Fact]
    public async Task Reconciliation_Sweep_Revokes_Terminal_Item_After_Failed_Teardown()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:SecretName"] = "OTHER_API_KEY",
        });
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider);
            var itemId = WorkItemId.New();
            var liveItemId = WorkItemId.New();
            var project = GrantedProject(Secret("PAID_API_TOKEN"));
            var liveProject = GrantedProject(Secret("OTHER_TOKEN"));

            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            var lease = Assert.Single(material.IssuedLeases);
            var liveMaterial = await manager.MaterializeForScopeAsync(
                liveProject, liveItemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            var liveLease = Assert.Single(liveMaterial.IssuedLeases);

            // Teardown fails: the issuer refuses once.
            _handler.FailRevokeOnce = true;
            var failed = await manager.RevokeWorkItemLeasesAsync(itemId);
            Assert.False(failed.AllRevoked);
            Assert.Equal(SecretLeaseStatus.RevocationFailed, (await store.GetAsync(lease.LeaseId))!.Status);

            // The sweep revokes the terminal item's lease against the
            // backend and leaves the live item's lease alone.
            var states = new Dictionary<Guid, WorkItemState>
            {
                [itemId.Value] = WorkItemState.Done,
                [liveItemId.Value] = WorkItemState.Working,
            };
            var report = await manager.ReconcileAsync(
                id => Task.FromResult<WorkItemState?>(states.TryGetValue(id, out var state) ? state : null),
                _ => (DateTimeOffset?)null);

            Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
            Assert.Contains(IdentityToken, _handler.RevokedTokens);
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
    public async Task Value_Never_Reaches_Logs()
    {
        UseRecordedShapes();
        var log = new CapturingLogger();
        var factory = new CapturingLoggerFactory(log);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["IdentityId"] = "00000000-0000-0000-0000-000000000000",
            ["OidcTokenEnvVar"] = "DOPPLER_OIDC_TOKEN",
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretName"] = "PAID_API_KEY",
            ["Mappings:1:SandboxEnvVar"] = "STATIC_TOKEN",
            ["Mappings:1:SecretName"] = "STATIC_KEY",
            ["Mappings:1:TokenEnvVar"] = "DOPPLER_TOKEN",
        }, log: log);
        try
        {
            var store = new MemorySecretLeaseStore();
            var manager = CreateManager(store, provider, log: factory.CreateLogger("test"));
            var itemId = WorkItemId.New();
            var project = GrantedProject(Secret("PAID_API_TOKEN"), Secret("STATIC_TOKEN"));

            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work, _ => null,
                itemDeadline: null, log: factory.CreateLogger("test"));
            await manager.RenewDueLeasesAsync(_ => (DateTimeOffset?)null);
            await manager.RevokeWorkItemLeasesAsync(itemId);

            string all;
            lock (log.Messages)
                all = string.Join('\n', log.Messages);
            Assert.DoesNotContain(StaticValue, all);
            Assert.DoesNotContain(IdentityToken, all);
            Assert.DoesNotContain("test-oidc-token", all);
            Assert.DoesNotContain("dp.st.prd.test-token", all);
            Assert.Contains(material.IssuedLeases[0].LeaseId, all);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Theory]
    [InlineData(401, DopplerFailureKind.Unauthorized)]
    [InlineData(403, DopplerFailureKind.Unauthorized)]
    [InlineData(429, DopplerFailureKind.RateLimited)]
    [InlineData(500, DopplerFailureKind.BackendError)]
    public async Task Backend_Faults_Classify_As_Infrastructure(int status, DopplerFailureKind kind)
    {
        _handler.SecretJson = Fixture("secret.json");
        _handler.FailFetchStatus = status;
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"));

        var ex = await Assert.ThrowsAsync<DopplerException>(() => provider.IssueAsync(
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
        _handler.SecretJson = Fixture("secret.json");
        _handler.FailFetchStatus = 404;
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "MISSING_KEY"));

        var ex = await Assert.ThrowsAsync<DopplerException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(DopplerFailureKind.NotFound, ex.Kind);
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
        var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"));

        var ex = await Assert.ThrowsAsync<DopplerException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(DopplerFailureKind.Unreachable, ex.Kind);
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
            StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"),
            env: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<DopplerException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(DopplerFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);
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
        var api = new DopplerRestClient(http);
        var ex = await Assert.ThrowsAsync<DopplerException>(() =>
            api.GetSecretAsync(
                "https://doppler.example.com", "token", "acme", "prd", "PAID_API_KEY", 256 * 1024));
        // A backend 3xx is never followed: it fails closed as a backend
        // fault (infrastructure, never a diff verdict), with exactly one
        // request sent — the bearer token goes nowhere else.
        Assert.Equal(DopplerFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Contains("redirect", ex.Message);
        Assert.Equal(1, Volatile.Read(ref followed));
    }

    [Fact]
    public async Task Production_Http_Client_Never_Follows_Redirects()
    {
        // Real loopback wiring: the redirector answers 302 to a sink that
        // records everything it receives. If the client ever followed, the
        // sink would see the token-bearing request and this test would fail.
        using var sink = new RecordingStub(_ => (200, null, "sink"));
        using var redirector = new RecordingStub(_ => (302, sink.Url + "landing", string.Empty));
        using var client = CredentialHttp.CreateNoRedirectClient(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(
            redirector.Url + "v3/configs/config/secret?project=acme&config=prd&name=PAID_API_KEY");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, redirector.Hits);
        Assert.Equal(0, sink.Hits);
    }

    [Fact]
    public async Task Static_Only_Path_Still_Works_With_Provider_Present()
    {
        var hostVar = $"CODEYBOX_TEST_DOPPLER_{Guid.NewGuid():N}".ToUpperInvariant();
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            UseRecordedShapes();
            var provider = CreateProvider(StaticMapping("PAID_API_TOKEN", "PAID_API_KEY"));
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
            ["DefaultProject"] = "",
            ["DefaultConfig"] = "",
            ["ApiUrl"] = "http://doppler.example.com",
            ["IdentityId"] = "some-id",
            ["Mappings:0:SandboxEnvVar"] = "A",
            ["Mappings:0:SecretName"] = "K1",
            ["Mappings:1:SandboxEnvVar"] = "A",
            ["Mappings:1:SecretName"] = "K2",
            ["Mappings:2:SandboxEnvVar"] = "bad-name!",
            ["Mappings:2:SecretName"] = "K3",
        });
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("'A'", StringComparison.Ordinal) && e.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad-name!", StringComparison.Ordinal) && e.Contains("POSIX", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("project", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("config", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("plain http", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("OidcTokenEnvVar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renew_After_Restart_Rebuilds_Context_From_Lease_Handle()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var config = StaticMapping("PAID_API_TOKEN", "PAID_API_KEY");
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
        Assert.Equal(2, _handler.CountRequests(HttpMethod.Get.Method, "/v3/configs/config/secret"));
        restarted.Dispose();
        provider.Dispose();
    }

    [SkippableFact]
    public async Task Live_Fetch_Against_Real_Instance()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOPPLER_LIVE_TOKEN")),
            "DOPPLER_LIVE_TOKEN is not set; the live Doppler integration test is opt-in (see plugins/credentials/CodeyBox.DopplerPlugin/README.md).");
        var token = Environment.GetEnvironmentVariable("DOPPLER_LIVE_TOKEN")!;
        var project = Environment.GetEnvironmentVariable("DOPPLER_LIVE_PROJECT");
        var config = Environment.GetEnvironmentVariable("DOPPLER_LIVE_CONFIG");
        var secret = Environment.GetEnvironmentVariable("DOPPLER_LIVE_SECRET");
        Skip.If(string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(secret),
            "DOPPLER_LIVE_PROJECT/DOPPLER_LIVE_CONFIG/DOPPLER_LIVE_SECRET are not set.");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LIVE_TOKEN"] = token,
        };
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new DopplerSecretProvider(
            http,
            PluginConfig(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = "true",
                ["DefaultProject"] = project,
                ["DefaultConfig"] = config,
                ["ServiceTokenEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:SandboxEnvVar"] = "LIVE_TOKEN",
                ["Mappings:0:SecretName"] = secret,
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

    private static int ProbeFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
