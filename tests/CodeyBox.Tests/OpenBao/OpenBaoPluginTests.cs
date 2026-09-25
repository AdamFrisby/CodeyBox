using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.OpenBaoPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FakeClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests.OpenBao;

/// <summary>
/// Verification for the OpenBao credential plugin against the shared
/// lease-shaped contract: a granted group resolves while an ungranted one
/// is never fetched, dynamic leases renew across phases longer than their
/// TTL, teardown revokes against the backend (verified, not assumed — the
/// fake server rejects a renew of the dead lease), the reconciliation
/// sweep covers failed teardowns, values and tokens never reach logs,
/// backend faults classify as infrastructure (never a diff verdict), and
/// renew/revoke keep working after a restart from the self-describing
/// handle alone. REST is faked at the transport; the manager, store shape,
/// and sweep path are the real production wiring. Recorded payload shapes
/// live in <c>Fixtures/openbao/</c>; the one live test runs only when
/// <c>OPENBAO_LIVE_*</c> env is set, so offline runs rely on the recorded
/// shapes plus the documented reason in the plugin README.
/// </summary>
public sealed class OpenBaoPluginTests : IDisposable
{
    private const string StaticValue = "openbao-live-value-6c5d4e3f2a";
    private const string Kv1Value = "openbao-kv1-value-5e6f7a8b9c";
    private const string DynamicValue = "openbao-dynamic-value-1a2b3c4d";
    private const string ProviderToken = "test-bao-token-7f3a9c21";

    /// <summary>The lease id recorded in the fixture; the fake mints a fresh one per issue.</summary>
    private const string FixtureLeaseId = "database/creds/readonly/9f8e7d6c-1234-4a5b-8c7d-0123456789ab";

    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal)
    {
        ["OPENBAO_ROLE_ID"] = "test-role-id",
        ["OPENBAO_SECRET_ID"] = "test-secret-id",
    };

    private readonly OpenBaoFakeHandler _handler = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handler.Dispose();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class OpenBaoFakeHandler : HttpMessageHandler
    {
        public string LoginJson = "{}";
        public string Kv2Json = "{}";
        public string Kv1Json = "{}";
        public string LeaseIssueJson = "{}";
        public string LeaseRenewJson = "{}";
        public bool FailLoginUnauthorized;
        public bool Unreachable;
        public int FailReadStatus;
        public bool FailRevokeOnce;
        public string? LeaseIdOverride;
        public long? LeaseDurationOverride;
        public readonly List<(string Method, string Path, string Body)> Requests = [];
        public readonly List<string> IssuedLeaseIds = [];
        public readonly HashSet<string> LiveLeases = new(StringComparer.Ordinal);
        public readonly HashSet<string> RevokedLeases = new(StringComparer.Ordinal);
        private int _issueCounter;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            lock (Requests)
                Requests.Add((request.Method.Method, path, body));
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");

            if (path.EndsWith("/login", StringComparison.Ordinal)
                && path.StartsWith("/v1/auth/", StringComparison.Ordinal))
            {
                if (FailLoginUnauthorized)
                    return Errors("invalid role or secret ID", HttpStatusCode.Unauthorized);
                return Raw(LoginJson);
            }

            if (path == "/v1/sys/leases/renew")
            {
                var leaseId = BodyField(body, "lease_id");
                lock (Requests)
                {
                    if (!LiveLeases.Contains(leaseId))
                        return Errors("lease not found or expired", HttpStatusCode.BadRequest);
                }
                return Raw(LeaseRenewJson.Replace(
                    FixtureLeaseId, JsonEncodedText.Encode(leaseId).Value, StringComparison.Ordinal));
            }

            if (path == "/v1/sys/leases/revoke")
            {
                if (FailRevokeOnce)
                {
                    FailRevokeOnce = false;
                    return Errors("backend exploded", HttpStatusCode.InternalServerError);
                }
                var leaseId = BodyField(body, "lease_id");
                lock (Requests)
                {
                    if (!LiveLeases.Remove(leaseId))
                        return Errors("unknown lease", HttpStatusCode.NotFound);
                    RevokedLeases.Add(leaseId);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/", StringComparison.Ordinal))
            {
                if (FailReadStatus != 0)
                {
                    var failure = Errors(
                        FailReadStatus == 429 ? "rate limit exceeded" : "upstream exploded",
                        (HttpStatusCode)FailReadStatus);
                    if (FailReadStatus == 429)
                        failure.Headers.RetryAfter =
                            new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                    return failure;
                }
                if (path == "/v1/database/creds/readonly")
                {
                    string minted;
                    lock (Requests)
                    {
                        minted = LeaseIdOverride ?? $"database/creds/readonly/lease-{++_issueCounter:D4}";
                        LiveLeases.Add(minted);
                        IssuedLeaseIds.Add(minted);
                    }
                    // Encoded so an override carrying control characters
                    // still produces a valid JSON body.
                    var json = LeaseIssueJson.Replace(
                        FixtureLeaseId, JsonEncodedText.Encode(minted).Value, StringComparison.Ordinal);
                    if (LeaseDurationOverride is { } durationOverride)
                    {
                        json = json.Replace(
                            "\"lease_duration\": 1200",
                            $"\"lease_duration\": {durationOverride}",
                            StringComparison.Ordinal);
                    }
                    return Raw(json);
                }
                if (path == "/v1/secret/data/myapp")
                    return Raw(Kv2Json);
                if (path == "/v1/kv/myapp")
                    return Raw(Kv1Json);
                return Errors("no secret at that path", HttpStatusCode.NotFound);
            }

            return Errors("unsupported path", HttpStatusCode.NotFound);
        }

        private static string BodyField(string body, string field)
        {
            if (string.IsNullOrEmpty(body))
                return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Errors(string message, HttpStatusCode status) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { errors = new[] { message } }),
                Encoding.UTF8, "application/json"),
        };

        public int CountRequests(string method, string pathPrefix)
        {
            lock (Requests)
                return Requests.Count(r =>
                    string.Equals(r.Method, method, StringComparison.Ordinal)
                    && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));
        }

        public bool SawPath(string method, string exactPath)
        {
            lock (Requests)
                return Requests.Any(r =>
                    string.Equals(r.Method, method, StringComparison.Ordinal)
                    && string.Equals(r.Path, exactPath, StringComparison.Ordinal));
        }

        public bool RevokeBodySaw(string fragment)
        {
            lock (Requests)
                return Requests.Any(r =>
                    r.Path == "/v1/sys/leases/revoke" && r.Body.Contains(fragment, StringComparison.Ordinal));
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
            // Real sinks render the exception too — capture it so a secret
            // riding an exception message is still observed by tests.
            var text = formatter(state, exception);
            if (exception is not null)
                text += " " + exception;
            lock (Messages) Messages.Add(text);
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
        File.ReadAllText(Path.Combine("Fixtures", "openbao", name));

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
        ["Address"] = "https://bao.example.com",
        ["StaticLeaseTtlMinutes"] = "20",
    };

    private void UseRecordedShapes()
    {
        _handler.LoginJson = Fixture("approle-login.json");
        _handler.Kv2Json = Fixture("kv2-secret.json");
        _handler.Kv1Json = Fixture("kv1-secret.json");
        _handler.LeaseIssueJson = Fixture("database-creds.json");
        _handler.LeaseRenewJson = Fixture("lease-renew.json");
    }

    private OpenBaoSecretProvider CreateProvider(
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
        return new OpenBaoSecretProvider(
            http,
            PluginConfig(merged),
            clock,
            name => (env ?? _env).TryGetValue(name, out var v) ? v : null,
            log);
    }

    private SecretLeaseManager CreateManager(
        MemorySecretLeaseStore store,
        OpenBaoSecretProvider provider,
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
        Id = new ProjectId("openbao-project"),
        DisplayName = "OpenBao Project",
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
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
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
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_TOKEN",
            ["Mappings:1:Group"] = "other-group",
            ["Mappings:1:SecretPath"] = "kv/myapp",
            ["Mappings:1:KvVersion"] = "1",
            ["Mappings:1:DataField"] = "api_key",
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
        Assert.StartsWith("openbao.s.", lease.LeaseId, StringComparison.Ordinal);
        Assert.True(_handler.SawPath(HttpMethod.Get.Method, "/v1/secret/data/myapp"));
        Assert.False(_handler.SawPath(HttpMethod.Get.Method, "/v1/kv/myapp"));
        Assert.DoesNotContain(StaticValue, string.Join('\n', log.Messages));
    }

    [Fact]
    public async Task Ungranted_But_Mapped_Secret_Is_Never_Fetched()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);

        // The mapping exists but no grant authorises the group: the manager
        // must never call the provider, so no HTTP happens at all — not
        // even the AppRole login.
        var project = new Project
        {
            Id = new ProjectId("openbao-project"),
            DisplayName = "OpenBao Project",
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
    public async Task Dynamic_Lease_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        }, clock: clock);
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider, utcNow: () => clock.GetUtcNow());
        var project = GrantedProject(Secret("DB_PASSWORD"));

        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => null,
            itemDeadline: clock.GetUtcNow() + TimeSpan.FromMinutes(240));
        var lease = Assert.Single(material.IssuedLeases);
        Assert.StartsWith("openbao.d.", lease.LeaseId, StringComparison.Ordinal);
        // Server lease_duration 1200s minus the 60s skew.
        var firstExpiry = lease.ExpiresAt;
        Assert.Equal(start + TimeSpan.FromMinutes(19), firstExpiry);

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
        Assert.True(
            _handler.CountRequests(HttpMethod.Post.Method, "/v1/sys/leases/renew") >= renewals,
            "Dynamic renewal must hit sys/leases/renew once per renewal.");
        // The phase (240 min) outlives the AppRole token TTL (1200s), so the
        // provider re-logged in rather than riding a dead token.
        Assert.True(_handler.CountRequests(HttpMethod.Post.Method, "/v1/auth/approle/login") >= 2,
            "Expected the provider to re-login after the AppRole token expired mid-phase.");
    }

    [Fact]
    public async Task Static_Lease_Renewed_Across_Phase_Re_Fetches()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
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
        Assert.True(persisted.ExpiresAt > firstExpiry + TimeSpan.FromMinutes(200));
        Assert.True(_handler.CountRequests(HttpMethod.Get.Method, "/v1/secret/data/myapp") >= 1 + renewals,
            "Static renewal must re-fetch so rotation propagates within one window.");
    }

    [Fact]
    public async Task Dynamic_Renew_Returns_Server_Expiry()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        }, clock: clock);
        var material = await provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        Assert.Equal(DynamicValue, material.Value);
        Assert.StartsWith("openbao.d.", material.LeaseId, StringComparison.Ordinal);
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);
        Assert.EndsWith(serverLeaseId, material.LeaseId, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(10));
        var renewed = await provider.RenewAsync(material.LeaseId);
        // lease-renew.json grants 1200s minus the 60s skew from the new now.
        Assert.Equal(start + TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(19), renewed);
        Assert.Equal(1, _handler.CountRequests(HttpMethod.Post.Method, "/v1/sys/leases/renew"));
        Assert.Contains(_handler.Requests, r =>
            r.Path == "/v1/sys/leases/renew" && r.Body.Contains(serverLeaseId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Teardown_Revokes_Verified_Against_Backend()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });
        var store = new MemorySecretLeaseStore();
        var manager = CreateManager(store, provider);
        var itemId = WorkItemId.New();
        var project = GrantedProject(Secret("DB_PASSWORD"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);
        Assert.Contains(serverLeaseId, _handler.LiveLeases);

        var report = await manager.RevokeWorkItemLeasesAsync(itemId);

        Assert.True(report.AllRevoked);
        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        // Verified against the backend, not assumed: the server-side lease
        // is gone, the revoke call asked for synchronous revocation, and a
        // renew of the dead lease is rejected.
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.True(_handler.RevokeBodySaw("\"sync\":true"),
            "Teardown revocation must ask the backend for sync=true (immediate, not queued).");
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        Assert.Empty(await store.ListOutstandingAsync());

        using var probe = new HttpClient(_handler, disposeHandler: false);
        using var probeResponse = await probe.PostAsync(
            "https://bao.example.com/v1/sys/leases/renew",
            new StringContent(
                $$"""{"lease_id":"{{serverLeaseId}}","increment":1200}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, probeResponse.StatusCode);

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
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });
        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));

        // No server call carries a static revocation (KV secrets have no
        // server-side lease); revocation must still succeed and stay
        // idempotent.
        await provider.RevokeAsync(material.LeaseId);
        await provider.RevokeAsync(material.LeaseId);
        Assert.Equal(0, _handler.CountRequests(HttpMethod.Post.Method, "/v1/sys/leases/revoke"));
    }

    [Fact]
    public async Task Reconciliation_Sweep_Revokes_Terminal_Item_After_Failed_Teardown()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
            ["Mappings:1:SandboxEnvVar"] = "OTHER_PASSWORD",
            ["Mappings:1:DynamicPath"] = "database/creds/readonly",
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
        var terminalServerLease = _handler.IssuedLeaseIds[0];
        var liveMaterial = await manager.MaterializeForScopeAsync(
            liveProject, liveItemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var liveLease = Assert.Single(liveMaterial.IssuedLeases);
        var liveServerLease = _handler.IssuedLeaseIds[1];

        // Teardown fails: the backend refuses once.
        _handler.FailRevokeOnce = true;
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
        Assert.Contains(terminalServerLease, _handler.RevokedLeases);
        Assert.DoesNotContain(terminalServerLease, _handler.LiveLeases);
        Assert.Contains(liveServerLease, _handler.LiveLeases);
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
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
            ["Mappings:1:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:1:DynamicPath"] = "database/creds/readonly",
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
        Assert.DoesNotContain(ProviderToken, all);
        Assert.DoesNotContain("test-secret-id", all);
        Assert.Contains(material.IssuedLeases[0].LeaseId, all);
    }

    [Theory]
    [InlineData(401, CredentialFailureKind.Unauthorized)]
    [InlineData(403, CredentialFailureKind.Unauthorized)]
    [InlineData(429, CredentialFailureKind.RateLimited)]
    [InlineData(500, CredentialFailureKind.BackendError)]
    public async Task Backend_Faults_Classify_As_Infrastructure(int status, CredentialFailureKind kind)
    {
        _handler.LoginJson = Fixture("approle-login.json");
        _handler.Kv2Json = Fixture("kv2-secret.json");
        _handler.FailReadStatus = status;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
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
        _handler.LoginJson = Fixture("approle-login.json");
        _handler.FailLoginUnauthorized = true;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.Unauthorized, ex.Kind);
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
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.Unreachable, ex.Kind);
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
                ["Mappings:0:SecretPath"] = "secret/data/myapp",
                ["Mappings:0:KvVersion"] = "2",
                ["Mappings:0:DataField"] = "password",
            },
            env: new Dictionary<string, string?>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(WorkItemFailureKinds.Configuration, ex.FailureKindForWorkItem);
    }

    [Fact]
    public async Task Direct_Token_Path_Works_Without_Login()
    {
        UseRecordedShapes();
        var provider = CreateProvider(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TokenEnvVar"] = "MY_BAO_TOKEN",
                ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
                ["Mappings:0:SecretPath"] = "kv/myapp",
                ["Mappings:0:KvVersion"] = "1",
                ["Mappings:0:DataField"] = "api_key",
            },
            env: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MY_BAO_TOKEN"] = "direct-bao-token",
            });

        var material = await provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));

        Assert.Equal(Kv1Value, material.Value);
        Assert.Equal(0, _handler.CountRequests(HttpMethod.Post.Method, "/v1/auth/"));
        Assert.True(_handler.SawPath(HttpMethod.Get.Method, "/v1/kv/myapp"));
    }

    [Fact]
    public async Task Foreign_Or_Malformed_Lease_Handle_Is_Rejected()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:SecretPath"] = "secret/data/myapp",
            ["Mappings:0:KvVersion"] = "2",
            ["Mappings:0:DataField"] = "password",
        });

        var foreign = await Assert.ThrowsAsync<OpenBaoException>(
            () => provider.RenewAsync("doppler.s.PAID_API_TOKEN.abc123"));
        Assert.Equal(CredentialFailureKind.Misconfigured, foreign.Kind);
        var malformed = await Assert.ThrowsAsync<OpenBaoException>(
            () => provider.RevokeAsync("not-a-lease-handle"));
        Assert.Equal(CredentialFailureKind.Misconfigured, malformed.Kind);
    }

    [Fact]
    public async Task Renew_And_Revoke_After_Restart_Use_Only_The_Handle()
    {
        UseRecordedShapes();
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        };
        var provider = CreateProvider(config, clock: clock);
        var material = await provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        var firstExpiry = material.ExpiresAt;
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);

        // Simulate an orchestrator restart with the mapping REMOVED: a
        // fresh provider instance with no mappings still renews and revokes
        // the dynamic lease, because the handle tail carries the server
        // lease id. A live credential can never be stranded by config
        // drift.
        var restarted = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = "true",
            ["Address"] = "https://bao.example.com",
        }, clock: clock);
        clock.Advance(TimeSpan.FromMinutes(10));
        var renewed = await restarted.RenewAsync(material.LeaseId);
        Assert.True(renewed > firstExpiry - TimeSpan.FromMinutes(10));

        await restarted.RevokeAsync(material.LeaseId);
        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
        restarted.Dispose();
        provider.Dispose();
    }

    [Fact]
    public async Task Static_Mapping_Served_A_Dynamic_Lease_Is_Refused()
    {
        UseRecordedShapes();
        // Operator declared SecretPath but the endpoint actually leases:
        // holding the value under a client-side window would leave a live
        // server lease unrevoked — fail closed instead.
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:SecretPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);

        // The endpoint minted a real server lease under a mapping that
        // declared static: it must be handed back, not orphaned until its
        // server TTL.
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);
        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
    }

    [Fact]
    public async Task Rejected_Dynamic_Issue_Returns_The_Server_Lease()
    {
        UseRecordedShapes();
        // The credential was minted server-side but the mapping names a
        // field that is not in the response: the issue must fail AND the
        // live server lease must be handed back, not orphaned.
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "no-such-field",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.Misconfigured, ex.Kind);

        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);
        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
    }

    [Fact]
    public async Task Hostile_Server_Lease_Id_Is_Handed_Back_And_Never_Logged_Raw()
    {
        UseRecordedShapes();
        // A hostile or compromised backend mints a lease id carrying a
        // newline: the credential must be refused and handed back, and the
        // raw id must never reach a log line — it would forge entries in
        // the audit log.
        const string hostileId = "database/creds/readonly/evil\nFORGED-LOG-LINE";
        _handler.LeaseIdOverride = hostileId;
        var log = new CapturingLogger();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        }, log: log);

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));

        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        // The minted credential was handed back — the wire body carries
        // the real id — while logs carry only the control-flattened form.
        Assert.Contains(hostileId, _handler.RevokedLeases);
        Assert.DoesNotContain(hostileId, _handler.LiveLeases);
        var all = string.Join('\n', log.Messages);
        Assert.DoesNotContain("evil\nFORGED", all);
        Assert.Contains("evil FORGED-LOG-LINE", all);
        Assert.DoesNotContain(hostileId, ex.Message);
    }

    [Fact]
    public async Task Oversized_Lease_Duration_Is_Handed_Back_Then_Fails_Typed()
    {
        UseRecordedShapes();
        // A lease_duration above Int32 range must fail as a typed
        // InvalidResponse — and still hand the minted credential back,
        // not orphan it until its server TTL.
        _handler.LeaseDurationOverride = (long)int.MaxValue + 1;
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));

        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);
        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
    }

    [Fact]
    public async Task Disabled_Plugin_Still_Revokes_An_Issued_Dynamic_Lease()
    {
        UseRecordedShapes();
        // Disabling the plugin — a natural response to a suspect backend —
        // must not strand an already-issued lease: revocation needs a
        // valid configuration, not an enabled one.
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = "false",
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });
        const string serverLeaseId = "database/creds/readonly/still-live-lease";
        lock (_handler.Requests) _handler.LiveLeases.Add(serverLeaseId);
        var handle = OpenBaoLeaseIds.BuildDynamic(
            "DB_PASSWORD", "https://bao.example.com", serverLeaseId);

        await provider.RevokeAsync(handle);

        Assert.Contains(serverLeaseId, _handler.RevokedLeases);
        Assert.DoesNotContain(serverLeaseId, _handler.LiveLeases);
    }

    [Fact]
    public async Task Renew_And_Revoke_Refuse_A_Repointed_Address()
    {
        UseRecordedShapes();
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });
        var material = await provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20));
        var serverLeaseId = Assert.Single(_handler.IssuedLeaseIds);

        // The operator re-pointed Address at a different cluster: the lease
        // still lives on the issuer, so renew and revoke must refuse loudly
        // rather than POST the provider token there — a 404 from the wrong
        // cluster would otherwise be misread as 'already revoked' while the
        // credential stays live.
        var repointed = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Address"] = "https://other-bao.example.com",
        });
        var renewEx = await Assert.ThrowsAsync<OpenBaoException>(
            () => repointed.RenewAsync(material.LeaseId));
        Assert.Equal(CredentialFailureKind.Misconfigured, renewEx.Kind);
        var revokeEx = await Assert.ThrowsAsync<OpenBaoException>(
            () => repointed.RevokeAsync(material.LeaseId));
        Assert.Equal(CredentialFailureKind.Misconfigured, revokeEx.Kind);

        // Nothing for the lease left the process: the wrong cluster never
        // saw a renew or revoke, and the credential stays live on the issuer.
        Assert.Equal(0, _handler.CountRequests(HttpMethod.Post.Method, "/v1/sys/leases/"));
        Assert.DoesNotContain(serverLeaseId, _handler.RevokedLeases);
        Assert.Contains(serverLeaseId, _handler.LiveLeases);
        repointed.Dispose();
        provider.Dispose();
    }

    [Fact]
    public async Task Revoke_Refuses_When_Address_Is_Not_Configured()
    {
        UseRecordedShapes();
        // A present-but-empty Address key falls back to the loopback default,
        // which never issued anything: revocation must refuse rather than
        // POST the provider token to whatever squats on the default port.
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = "false",
            ["Address"] = "",
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });
        const string serverLeaseId = "database/creds/readonly/default-endpoint-lease";
        lock (_handler.Requests) _handler.LiveLeases.Add(serverLeaseId);
        var handle = OpenBaoLeaseIds.BuildDynamic(
            "DB_PASSWORD", "http://127.0.0.1:8200", serverLeaseId);

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.RevokeAsync(handle));

        Assert.Equal(CredentialFailureKind.Misconfigured, ex.Kind);
        Assert.Equal(0, _handler.CountRequests(HttpMethod.Post.Method, "/v1/sys/leases/revoke"));
        Assert.Contains(serverLeaseId, _handler.LiveLeases);
    }

    [Fact]
    public async Task Lease_Id_With_Issuer_Separator_Is_Handed_Back_And_Refused()
    {
        UseRecordedShapes();
        // A '~' inside the server lease id would split the handle tail
        // wrongly: the response is refused as invalid and the minted
        // credential handed back, never orphaned.
        _handler.LeaseIdOverride = "database/creds/readonly/lease~with-separator";
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = "database/creds/readonly",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("DB_PASSWORD"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));

        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        Assert.Contains("database/creds/readonly/lease~with-separator", _handler.RevokedLeases);
        Assert.DoesNotContain("database/creds/readonly/lease~with-separator", _handler.LiveLeases);
    }

    [Fact]
    public async Task Dynamic_Mapping_Served_No_Lease_Is_Refused()
    {
        UseRecordedShapes();
        // Operator declared DynamicPath but the endpoint returned an
        // unleased read — never silently degrade to a static window.
        var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
            ["Mappings:0:DynamicPath"] = "secret/data/myapp",
            ["Mappings:0:DataField"] = "password",
        });

        var ex = await Assert.ThrowsAsync<OpenBaoException>(() => provider.IssueAsync(
            Secret("PAID_API_TOKEN"), Guid.NewGuid(), "work", TimeSpan.FromMinutes(20)));
        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
    }

    [Fact]
    public async Task Static_Only_Path_Still_Works_With_Provider_Present()
    {
        var hostVar = $"CODEYBOX_TEST_OPENBAO_{Guid.NewGuid():N}".ToUpperInvariant();
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            UseRecordedShapes();
            var provider = CreateProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mappings:0:SandboxEnvVar"] = "PAID_API_TOKEN",
                ["Mappings:0:SecretPath"] = "secret/data/myapp",
                ["Mappings:0:KvVersion"] = "2",
                ["Mappings:0:DataField"] = "password",
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
            ["Mappings:0:SandboxEnvVar"] = "A",
            ["Mappings:0:SecretPath"] = "secret/data/x",
            ["Mappings:0:DynamicPath"] = "database/creds/y",
            ["Mappings:0:DataField"] = "password",
            ["Mappings:1:SandboxEnvVar"] = "B",
            ["Mappings:1:SecretPath"] = "secret/data/x",
            ["Mappings:2:SandboxEnvVar"] = "bad-name!",
            ["Mappings:2:SecretPath"] = "secret/data/x",
            ["Mappings:2:DataField"] = "password",
            ["Mappings:3:SandboxEnvVar"] = "C",
            ["Mappings:3:SecretPath"] = "../escape",
            ["Mappings:3:DataField"] = "password",
            ["Mappings:4:SandboxEnvVar"] = "D",
            ["Mappings:4:SecretPath"] = "secret/data/x",
            ["Mappings:4:DataField"] = "password",
            ["Mappings:4:KvVersion"] = "7",
        });
        var errors = provider.CurrentOptions().Validate();
        Assert.Contains(errors, e => e.Contains("'A'", StringComparison.Ordinal) && e.Contains("exactly one", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'B'", StringComparison.Ordinal) && e.Contains("DataField", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad-name!", StringComparison.Ordinal) && e.Contains("POSIX", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'C'", StringComparison.Ordinal) && e.Contains("SecretPath", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'D'", StringComparison.Ordinal) && e.Contains("KvVersion", StringComparison.Ordinal));
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
        var api = new OpenBaoRestClient(http);
        var ex = await Assert.ThrowsAsync<OpenBaoException>(() =>
            api.LoginAppRoleAsync(
                "https://bao.example.com", "approle", "test-role", "test-secret", TimeSpan.FromSeconds(30)));
        // A backend 3xx is never followed: it fails closed as a backend
        // fault (infrastructure, never a diff verdict), with exactly one
        // request sent — the role/secret go nowhere else.
        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Contains("redirect", ex.Message);
        Assert.Equal(1, Volatile.Read(ref followed));
    }

    [SkippableFact]
    public async Task Live_Issue_Renew_Revoke_Against_Real_Instance()
    {
        var address = Environment.GetEnvironmentVariable("OPENBAO_LIVE_ADDR");
        Skip.If(string.IsNullOrWhiteSpace(address),
            "OPENBAO_LIVE_ADDR is not set; the live OpenBao integration test is opt-in (see plugins/credentials/CodeyBox.OpenBaoPlugin/README.md).");
        var token = Environment.GetEnvironmentVariable("OPENBAO_LIVE_TOKEN");
        var roleId = Environment.GetEnvironmentVariable("OPENBAO_LIVE_ROLE_ID");
        var secretId = Environment.GetEnvironmentVariable("OPENBAO_LIVE_SECRET_ID");
        var dynamicPath = Environment.GetEnvironmentVariable("OPENBAO_LIVE_DYNAMIC_PATH");
        var field = Environment.GetEnvironmentVariable("OPENBAO_LIVE_FIELD") ?? "password";
        Skip.If(string.IsNullOrWhiteSpace(token)
            && (string.IsNullOrWhiteSpace(roleId) || string.IsNullOrWhiteSpace(secretId)),
            "Set OPENBAO_LIVE_TOKEN or OPENBAO_LIVE_ROLE_ID + OPENBAO_LIVE_SECRET_ID.");
        Skip.If(string.IsNullOrWhiteSpace(dynamicPath),
            "OPENBAO_LIVE_DYNAMIC_PATH is not set (e.g. database/creds/readonly).");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = "true",
            ["Address"] = address,
            ["Mappings:0:SandboxEnvVar"] = "LIVE_DB_PASSWORD",
            ["Mappings:0:DynamicPath"] = dynamicPath,
            ["Mappings:0:DataField"] = field,
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            env["LIVE_BAO_TOKEN"] = token;
            config["TokenEnvVar"] = "LIVE_BAO_TOKEN";
            config["AppRoleIdEnvVar"] = "LIVE_UNUSED_ROLE";
            config["AppRoleSecretIdEnvVar"] = "LIVE_UNUSED_SECRET";
        }
        else
        {
            env["LIVE_BAO_ROLE"] = roleId;
            env["LIVE_BAO_SECRET"] = secretId;
            config["AppRoleIdEnvVar"] = "LIVE_BAO_ROLE";
            config["AppRoleSecretIdEnvVar"] = "LIVE_BAO_SECRET";
        }

        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new OpenBaoSecretProvider(
            http,
            PluginConfig(config),
            env: name => env.TryGetValue(name, out var v) ? v : null);

        var material = await provider.IssueAsync(
            new ProjectSandboxSecret
            {
                HostEnvVar = "UNUSED",
                SandboxEnvVar = "LIVE_DB_PASSWORD",
                Group = "live",
            },
            Guid.NewGuid(), "work", TimeSpan.FromMinutes(5));
        Assert.False(string.IsNullOrEmpty(material.Value));
        Assert.StartsWith("openbao.d.", material.LeaseId, StringComparison.Ordinal);

        var renewed = await provider.RenewAsync(material.LeaseId);
        Assert.True(renewed > DateTimeOffset.UtcNow);

        await provider.RevokeAsync(material.LeaseId);
        // The credential genuinely stopped working: the server rejects a
        // renew of the dead lease rather than the client assuming success.
        await Assert.ThrowsAsync<OpenBaoException>(() => provider.RenewAsync(material.LeaseId));
    }

    private sealed class DelegatingHandlerStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
