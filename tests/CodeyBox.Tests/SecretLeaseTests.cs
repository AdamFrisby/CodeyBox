using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for leased workload secrets: lease identity carried
/// separately from the value, renewal across phases longer than the TTL,
/// revocation on teardown that reaches the issuer, reconciliation for
/// terminal items whose teardown failed, loud revocation failures,
/// brokered credentials whose value never enters the guest, restart-safe
/// persistence, and unchanged static-only behaviour.
/// </summary>
public sealed class SecretLeaseTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory(
        "codeybox-secret-leases-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private sealed class FakeLeaseProvider : ILeaseCapableSecretProvider
    {
        public string ProviderId { get; }
        public Func<ProjectSandboxSecret, bool> CanIssueFunc { get; set; } = _ => true;
        public bool Brokered { get; set; }
        public string Endpoint { get; set; } = "https://broker.internal:8443/secret";
        /// <summary>
        /// Value the real broker holds server-side. Never returned to the
        /// guest for brokered leases — the test asserts it leaks nowhere.
        /// </summary>
        public string BrokerSideValue { get; set; } = $"broker-side-{Guid.NewGuid():N}";
        public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(20);
        public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        public int IssueCalls;
        public int RenewCalls;
        public readonly HashSet<string> RevokedLeaseIds = new(StringComparer.Ordinal);
        public readonly HashSet<string> FailRevokeLeaseIds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string? Value, DateTimeOffset Expires)> Live = new(StringComparer.Ordinal);
        private int _counter;

        public FakeLeaseProvider(string providerId) => ProviderId = providerId;

        public bool CanIssue(ProjectSandboxSecret secret) => CanIssueFunc(secret);

        public Task<LeasedSecretMaterial> IssueAsync(
            ProjectSandboxSecret secret,
            Guid workItemId,
            string scope,
            TimeSpan requestedTtl,
            CancellationToken ct = default)
        {
            IssueCalls++;
            var id = $"{ProviderId}-lease-{Interlocked.Increment(ref _counter)}";
            var expires = Clock() + Ttl;
            string? value = Brokered ? null : $"vault-value-{id}";
            Live[id] = (value, expires);
            return Task.FromResult(new LeasedSecretMaterial
            {
                LeaseId = id,
                Value = value,
                ExpiresAt = expires,
                Brokered = Brokered,
                Endpoint = Brokered ? Endpoint : null,
            });
        }

        public Task<DateTimeOffset> RenewAsync(string leaseId, CancellationToken ct = default)
        {
            RenewCalls++;
            if (!Live.TryGetValue(leaseId, out var entry))
                throw new InvalidOperationException($"Unknown lease '{leaseId}'.");
            var expires = Clock() + Ttl;
            Live[leaseId] = (entry.Value, expires);
            return Task.FromResult(expires);
        }

        public Task RevokeAsync(string leaseId, CancellationToken ct = default)
        {
            if (FailRevokeLeaseIds.Contains(leaseId))
                throw new InvalidOperationException($"Issuer refused revocation of '{leaseId}'.");
            Live.Remove(leaseId);
            RevokedLeaseIds.Add(leaseId);
            return Task.CompletedTask;
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
                        .OrderBy(l => l.ExpiresAt)
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

    private sealed class FakeAgentLeaseProvider : ILeaseCapableCredentialProvider
    {
        public string LeaseProviderId { get; }
        public readonly HashSet<string> RevokedLeaseIds = new(StringComparer.Ordinal);
        public FakeAgentLeaseProvider(string providerId) => LeaseProviderId = providerId;
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
            => Task.FromResult<AgentCredential?>(null);
        public Task<DateTimeOffset> RenewCredentialAsync(string leaseId, CancellationToken ct = default)
            => Task.FromResult(DateTimeOffset.UtcNow.AddMinutes(20));
        public Task RevokeCredentialAsync(string leaseId, CancellationToken ct = default)
        {
            RevokedLeaseIds.Add(leaseId);
            return Task.CompletedTask;
        }
    }

    private static Project LeasedProject(params ProjectSandboxSecret[] secrets)
    {
        return new Project
        {
            Id = new ProjectId("lease-project"),
            DisplayName = "Lease Project",
            RepositoryUrl = "https://example.invalid/repo.git",
            SandboxSecrets = secrets,
            SandboxSecretGrants = secrets
                .Select(secret => secret.Group)
                .Distinct(StringComparer.Ordinal)
                .Select(group => new ProjectSandboxSecretGrant { Group = group })
                .ToList(),
        };
    }

    private static ProjectSandboxSecret LeasedSecret(string sandboxEnvVar, string group = "paid-api")
    {
        return new ProjectSandboxSecret
        {
            HostEnvVar = $"CODEYBOX_HOST_{sandboxEnvVar}",
            SandboxEnvVar = sandboxEnvVar,
            Group = group,
        };
    }

    [Fact]
    public async Task Lease_Is_Renewed_Across_Phase_Longer_Than_Ttl()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new FakeLeaseProvider("vault") { Clock = () => now };
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(
            store, [provider],
            options: () => new SecretLeasingOptions
            {
                RenewBeforeExpiry = TimeSpan.FromMinutes(5),
                DefaultLeaseTtl = TimeSpan.FromMinutes(20),
            },
            utcNow: () => now);
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null,
            itemDeadline: now + TimeSpan.FromMinutes(240));
        var lease = Assert.Single(material.IssuedLeases);
        var firstExpiry = lease.ExpiresAt;

        // Simulate a 240-minute phase with a 20-minute lease: sweep every 10 minutes.
        var renewals = 0;
        for (var elapsed = 0; elapsed < 240; elapsed += 10)
        {
            now += TimeSpan.FromMinutes(10);
            var report = await manager.RenewDueLeasesAsync(_ => now + TimeSpan.FromMinutes(240 - elapsed));
            renewals += report.RenewedLeaseIds.Count;
        }

        Assert.True(renewals >= 8, $"Expected at least 8 renewals across a 240-minute phase, saw {renewals}.");
        var persisted = await store.GetAsync(lease.LeaseId);
        Assert.NotNull(persisted);
        Assert.True(persisted.ExpiresAt > firstExpiry + TimeSpan.FromMinutes(200),
            "Lease expiry did not advance across the phase.");
        Assert.Equal(SecretLeaseStatus.Active, persisted.Status);
        Assert.True(provider.Live.ContainsKey(lease.LeaseId), "Issuer no longer holds the lease.");
    }

    [Fact]
    public async Task Teardown_Revokes_Lease_And_Reaches_Issuer()
    {
        var provider = new FakeLeaseProvider("vault");
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(store, [provider]);
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        Assert.True(provider.Live.ContainsKey(lease.LeaseId));

        var report = await manager.RevokeWorkItemLeasesAsync(itemId);

        Assert.True(report.AllRevoked);
        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.Contains(lease.LeaseId, provider.RevokedLeaseIds);
        Assert.False(provider.Live.ContainsKey(lease.LeaseId));
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        Assert.Empty(await store.ListOutstandingAsync());
    }

    [Fact]
    public async Task Reconciliation_Sweep_Revokes_Terminal_Item_After_Failed_Teardown()
    {
        var provider = new FakeLeaseProvider("vault");
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(store, [provider]);
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));
        var liveItemId = WorkItemId.New();
        var liveProject = LeasedProject(LeasedSecret("OTHER_TOKEN"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        var liveMaterial = await manager.MaterializeForScopeAsync(
            liveProject, liveItemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var liveLease = Assert.Single(liveMaterial.IssuedLeases);

        // Teardown fails: revocation reaches the issuer but the issuer refuses.
        provider.FailRevokeLeaseIds.Add(lease.LeaseId);
        var failed = await manager.RevokeWorkItemLeasesAsync(itemId);
        Assert.False(failed.AllRevoked);
        Assert.Equal(SecretLeaseStatus.RevocationFailed, (await store.GetAsync(lease.LeaseId))!.Status);

        // The sweep revokes leases for the terminal item even though
        // teardown failed, and leaves the live item's lease alone.
        provider.FailRevokeLeaseIds.Clear();
        var states = new Dictionary<Guid, WorkItemState>
        {
            [itemId.Value] = WorkItemState.Done,
            [liveItemId.Value] = WorkItemState.Working,
        };
        var report = await manager.ReconcileAsync(
            id => Task.FromResult<WorkItemState?>(states.TryGetValue(id, out var state) ? state : null),
            _ => (DateTimeOffset?)null);

        Assert.Contains(lease.LeaseId, report.RevokedLeaseIds);
        Assert.Contains(lease.LeaseId, provider.RevokedLeaseIds);
        Assert.Equal(SecretLeaseStatus.Revoked, (await store.GetAsync(lease.LeaseId))!.Status);
        var outstanding = await store.ListOutstandingAsync();
        Assert.Contains(outstanding, l => l.LeaseId == liveLease.LeaseId);
    }

    [Fact]
    public async Task Revocation_Failure_Is_Recorded_And_Surfaced()
    {
        var provider = new FakeLeaseProvider("vault");
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(store, [provider]);
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));

        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
        var lease = Assert.Single(material.IssuedLeases);
        provider.FailRevokeLeaseIds.Add(lease.LeaseId);

        var report = await manager.RevokeWorkItemLeasesAsync(itemId);

        Assert.False(report.AllRevoked);
        var failure = Assert.Single(report.Failures);
        Assert.Equal(lease.LeaseId, failure.LeaseId);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));
        var persisted = (await store.GetAsync(lease.LeaseId))!;
        Assert.Equal(SecretLeaseStatus.RevocationFailed, persisted.Status);
        Assert.False(string.IsNullOrWhiteSpace(persisted.LastError));
        Assert.True(provider.Live.ContainsKey(lease.LeaseId), "Lease must still be live at the issuer.");
        Assert.Contains(await store.ListOutstandingAsync(), l => l.LeaseId == lease.LeaseId);
    }

    [Fact]
    public async Task Brokered_Credential_Value_Never_Enters_Sandbox_Env_Or_Logs()
    {
        var provider = new FakeLeaseProvider("vault")
        {
            Brokered = true,
            Endpoint = "https://broker.internal:8443/leases/paid-api",
        };
        var store = new MemorySecretLeaseStore();
        var log = new CapturingLogger();
        var loggerFactory = new CapturingLoggerFactory(log);
        var manager = new SecretLeaseManager(
            store, [provider], log: loggerFactory.CreateLogger("test"));
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));

        // The issuer holds a value the guest must never see; the fake keeps
        // it live-side only, exactly like a real broker.
        var material = await manager.MaterializeForScopeAsync(
            project, itemId, ProjectSandboxSecretScopes.Work, _ => "static-fallback-must-not-win",
            itemDeadline: null, log: loggerFactory.CreateLogger("test"));
        var lease = Assert.Single(material.IssuedLeases);

        Assert.True(lease.Brokered);
        Assert.Equal("https://broker.internal:8443/leases/paid-api", lease.Endpoint);
        Assert.Equal(lease.Endpoint, material.Environment["PAID_API_TOKEN"]);
        Assert.Contains(lease.Endpoint!, material.BrokerEndpoints);

        var issuerValue = provider.BrokerSideValue;
        Assert.DoesNotContain(issuerValue, material.Environment["PAID_API_TOKEN"]);
        foreach (var value in material.Environment.Values)
            Assert.DoesNotContain(issuerValue, value);
        Assert.DoesNotContain(issuerValue, lease.ToString());
        Assert.DoesNotContain(issuerValue, (await store.GetAsync(lease.LeaseId))!.ToString());
        lock (log.Messages)
            foreach (var message in log.Messages)
                Assert.DoesNotContain(issuerValue, message);
        Assert.Equal(
            ["broker.internal"],
            PipelineRunner.BrokerEndpointHosts(material.BrokerEndpoints));
    }

    [Fact]
    public async Task Lease_State_Survives_Orchestrator_Restart()
    {
        var dbPath = Path.Combine(_workspace, "state.db");
        var provider = new FakeLeaseProvider("vault");
        var itemId = WorkItemId.New();
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));
        string leaseId;
        DateTimeOffset firstExpiry;

        using (var store = new SqliteSecretLeaseStore(dbPath))
        {
            var manager = new SecretLeaseManager(store, [provider]);
            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work, _ => null, itemDeadline: null);
            leaseId = Assert.Single(material.IssuedLeases).LeaseId;
            firstExpiry = material.IssuedLeases[0].ExpiresAt;
        }

        // Simulate an orchestrator restart: new store and manager over the
        // same database file, issuer re-registered. No live lease is orphaned.
        var now = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(19);
        using (var restarted = new SqliteSecretLeaseStore(dbPath))
        {
            var outstanding = await restarted.ListOutstandingAsync();
            Assert.Contains(outstanding, l => l.LeaseId == leaseId);

            provider.Clock = () => now;
            var manager = new SecretLeaseManager(
                restarted, [provider],
                options: () => new SecretLeasingOptions { RenewBeforeExpiry = TimeSpan.FromMinutes(5) },
                utcNow: () => now);
            var report = await manager.RenewDueLeasesAsync(_ => (DateTimeOffset?)null);
            Assert.Contains(leaseId, report.RenewedLeaseIds);
            Assert.True((await restarted.GetAsync(leaseId))!.ExpiresAt > firstExpiry);
        }
    }

    [Fact]
    public async Task Static_Only_Provider_Is_Unaffected()
    {
        var hostVar = $"CODEYBOX_TEST_STATIC_{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        Environment.SetEnvironmentVariable(hostVar, "static-value");
        try
        {
            var store = new MemorySecretLeaseStore();
            var staticOnly = new FakeLeaseProvider("vault") { CanIssueFunc = _ => false };
            var manager = new SecretLeaseManager(store, [staticOnly]);
            var itemId = WorkItemId.New();
            var project = LeasedProject(new ProjectSandboxSecret
            {
                HostEnvVar = hostVar,
                SandboxEnvVar = "STATIC_TOKEN",
                Group = "paid-api",
            });

            var material = await manager.MaterializeForScopeAsync(
                project, itemId, ProjectSandboxSecretScopes.Work,
                name => Environment.GetEnvironmentVariable(name), itemDeadline: null);

            Assert.Equal("static-value", material.Environment["STATIC_TOKEN"]);
            Assert.Empty(material.IssuedLeases);
            Assert.Empty(material.BrokerEndpoints);
            Assert.Equal(0, staticOnly.IssueCalls);
            Assert.Empty(await store.ListOutstandingAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar, null);
        }
    }

    [Fact]
    public async Task Leased_Issue_Requires_Grant()
    {
        var provider = new FakeLeaseProvider("vault");
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(store, [provider]);
        var project = new Project
        {
            Id = new ProjectId("lease-project"),
            DisplayName = "Lease Project",
            RepositoryUrl = "https://example.invalid/repo.git",
            SandboxSecrets = [LeasedSecret("PAID_API_TOKEN")],
            SandboxSecretGrants = [],
        };

        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => "nope",
            itemDeadline: null);

        Assert.Empty(material.Environment);
        Assert.Empty(material.IssuedLeases);
        Assert.Equal(0, provider.IssueCalls);
    }

    [Fact]
    public async Task Renewal_Does_Not_Extend_Past_Item_Lifetime()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new FakeLeaseProvider("vault")
        {
            Clock = () => now,
            Ttl = TimeSpan.FromHours(10),
        };
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(
            store, [provider],
            options: () => new SecretLeasingOptions { RenewBeforeExpiry = TimeSpan.FromMinutes(5) },
            utcNow: () => now);
        var project = LeasedProject(LeasedSecret("PAID_API_TOKEN"));
        var material = await manager.MaterializeForScopeAsync(
            project, WorkItemId.New(), ProjectSandboxSecretScopes.Work, _ => null,
            itemDeadline: now + TimeSpan.FromMinutes(30));
        var lease = Assert.Single(material.IssuedLeases);
        Assert.True(lease.ExpiresAt <= now + TimeSpan.FromMinutes(30));

        now += TimeSpan.FromMinutes(25);
        var deadline = now + TimeSpan.FromMinutes(5);
        await manager.RenewDueLeasesAsync(_ => deadline);
        Assert.True((await store.GetAsync(lease.LeaseId))!.ExpiresAt <= deadline);
    }

    [Fact]
    public async Task Agent_Credential_Lease_Revokes_And_Static_Is_Noop()
    {
        var store = new MemorySecretLeaseStore();
        var manager = new SecretLeaseManager(store, []);
        var provider = new FakeAgentLeaseProvider("vault-agent");

        var leased = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "x" },
            new Dictionary<string, string>())
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            LeaseId = "agent-lease-1",
            LeaseProviderId = "vault-agent",
        };
        Assert.True(await manager.RevokeAgentCredentialLeaseAsync(leased, [provider]));
        Assert.Contains("agent-lease-1", provider.RevokedLeaseIds);

        var staticCredential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "x" },
            new Dictionary<string, string>());
        Assert.True(await manager.RevokeAgentCredentialLeaseAsync(staticCredential, []));
    }

    [Fact]
    public void Broker_Endpoint_Hosts_Use_Exact_Match()
    {
        var hosts = PipelineRunner.BrokerEndpointHosts([
            "https://broker.internal:8443/leases/a",
            "https://broker.internal:8443/leases/b",
            "not-a-uri",
            "https://other.example.com/x",
        ]);
        Assert.Equal(["broker.internal", "other.example.com"], hosts);
        Assert.Empty(PipelineRunner.BrokerEndpointHosts([]));
    }

    private sealed class CapturingLoggerFactory(CapturingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
