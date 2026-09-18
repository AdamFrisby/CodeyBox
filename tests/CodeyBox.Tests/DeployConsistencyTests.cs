using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Deploy consistency: the binaries in <c>bin/</c> must be comparable against
/// the checkout they were built from, so a <c>git pull</c> under a no-rebuild
/// deploy warns while the service is healthy instead of surfacing as a fatal
/// unbound-key failure on the next restart.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class DeployConsistencyTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Evaluate_Consistent_WhenRevisionsAgree()
    {
        var sha = new string('a', 40);

        var report = DeployConsistency.Evaluate(sha, sha.ToUpperInvariant());

        Assert.Equal(DeployConsistencyStatus.Consistent, report.Status);
        Assert.False(report.IsDiverged);
    }

    [Fact]
    public void Evaluate_Diverged_WhenRevisionsDiffer()
    {
        var report = DeployConsistency.Evaluate(new string('a', 40), new string('b', 40));

        Assert.Equal(DeployConsistencyStatus.Diverged, report.Status);
        Assert.True(report.IsDiverged);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("unknown", "unknown")]
    [InlineData("  UNKNOWN  ", null)]
    public void Evaluate_Unknown_WhenEitherSideIsNotKnown(string? built, string? checkout)
    {
        var known = new string('c', 40);

        Assert.Equal(DeployConsistencyStatus.Unknown, DeployConsistency.Evaluate(built, checkout).Status);
        Assert.Equal(DeployConsistencyStatus.Unknown, DeployConsistency.Evaluate(built, known).Status);
        Assert.Equal(DeployConsistencyStatus.Unknown, DeployConsistency.Evaluate(known, checkout).Status);
    }

    [Fact]
    public void FormatStaleBuildNote_NamesCauseAndFix()
    {
        var report = DeployConsistency.Evaluate(new string('a', 40), new string('b', 40));

        var note = DeployConsistency.FormatStaleBuildNote(report);

        Assert.Contains("stale", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ebuild", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.BuiltRevision, note, StringComparison.Ordinal);
        Assert.Contains(report.CheckoutRevision, note, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckoutReader_ResolvesRefHead()
    {
        var root = NewTempDir();
        WriteFile(root, ".git/HEAD", "ref: refs/heads/main\n");
        WriteFile(root, ".git/refs/heads/main", new string('d', 40) + "\n");

        Assert.Equal(new string('d', 40), CheckoutRevisionReader.ReadCheckoutRevision(root));
    }

    [Fact]
    public void CheckoutReader_ResolvesDetachedHead()
    {
        var root = NewTempDir();
        WriteFile(root, ".git/HEAD", new string('e', 40) + "\n");

        Assert.Equal(new string('e', 40), CheckoutRevisionReader.ReadCheckoutRevision(root));
    }

    [Fact]
    public void CheckoutReader_FallsBackToPackedRefs()
    {
        var root = NewTempDir();
        WriteFile(root, ".git/HEAD", "ref: refs/heads/main\n");
        WriteFile(root, ".git/packed-refs", "# pack-refs with: peeled fully-peeled sorted\n" + new string('f', 40) + " refs/heads/main\n");

        Assert.Equal(new string('f', 40), CheckoutRevisionReader.ReadCheckoutRevision(root));
    }

    [Fact]
    public void CheckoutReader_ResolvesGitFilePointer()
    {
        var root = NewTempDir();
        var realGit = Path.Combine(root, "real-git");
        WriteFile(realGit, "HEAD", "ref: refs/heads/main\n");
        WriteFile(realGit, "refs/heads/main", new string('1', 40) + "\n");
        WriteFile(root, ".git", "gitdir: real-git\n");

        Assert.Equal(new string('1', 40), CheckoutRevisionReader.ReadCheckoutRevision(root));
    }

    [Fact]
    public void CheckoutReader_ReturnsUnknown_WhenNoCheckout()
    {
        Assert.Equal(DeployConsistency.UnknownRevision, CheckoutRevisionReader.ReadCheckoutRevision(NewTempDir()));
        Assert.Equal(DeployConsistency.UnknownRevision, CheckoutRevisionReader.ReadCheckoutRevision(Path.Combine(NewTempDir(), "missing")));
    }

    [Fact]
    public void CheckoutReader_ReturnsUnknown_ForMalformedHead()
    {
        var root = NewTempDir();
        WriteFile(root, ".git/HEAD", "not-a-sha\n");

        Assert.Equal(DeployConsistency.UnknownRevision, CheckoutRevisionReader.ReadCheckoutRevision(root));
    }

    [Fact]
    public void BuildRevision_FallsBackToUnknown_ForAssemblyWithoutMetadata()
    {
        Assert.Equal(DeployConsistency.UnknownRevision, BuildRevision.GetBuiltRevision(typeof(string).Assembly));
    }

    [Fact]
    public async Task StartupCheck_WarnsOnDivergence_WhileStartupSucceeds_WhenConfigBinds()
    {
        // The brief's outage shape: the checkout moved ahead of bin/, but the
        // operator's current configuration still binds. Startup must succeed
        // (no unbound keys) AND carry a divergence warning naming the rebuild.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:MaxTemplateChecks"] = "50",
        });
        var log = new ListLogger<DeployConsistencyService>();
        var service = new DeployConsistencyService(
            config,
            log,
            () => new string('a', 40),
            () => new string('b', 40));

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(UnboundConfigKeyHostedValidator.Inspect(config));
        var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("ebuild", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(new string('a', 40), warning.Message, StringComparison.Ordinal);
        Assert.Contains(new string('b', 40), warning.Message, StringComparison.Ordinal);
        Assert.True(service.Current.IsDiverged);
    }

    [Fact]
    public async Task StartupCheck_StaysQuiet_WhenRevisionsAgree()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var log = new ListLogger<DeployConsistencyService>();
        var service = new DeployConsistencyService(
            config,
            log,
            () => new string('a', 40),
            () => new string('a', 40));

        await service.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Equal(DeployConsistencyStatus.Consistent, service.Current.Status);
    }

    [Fact]
    public async Task UnboundValidator_StaleBuild_MessageNamesCauseAndFix()
    {
        var kvs = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
        };
        var staleService = new DeployConsistencyService(
            BuildConfig(kvs),
            NullLogger<DeployConsistencyService>.Instance,
            () => new string('a', 40),
            () => new string('b', 40));
        var validator = new UnboundConfigKeyHostedValidator(
            BuildConfig(kvs),
            NullLogger<UnboundConfigKeyHostedValidator>.Instance,
            staleService);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ebuild", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnboundValidator_GenuineUnknownKey_MessageOmitsStaleBuild()
    {
        var kvs = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentStreams:RootDirectory"] = "/var/log/codeybox",
        };
        var agreeingService = new DeployConsistencyService(
            BuildConfig(kvs),
            NullLogger<DeployConsistencyService>.Instance,
            () => new string('a', 40),
            () => new string('a', 40));
        var validator = new UnboundConfigKeyHostedValidator(
            BuildConfig(kvs),
            NullLogger<UnboundConfigKeyHostedValidator>.Instance,
            agreeingService);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.DoesNotContain("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeployConsistencyStatus.Consistent, true)]
    [InlineData(DeployConsistencyStatus.Diverged, false)]
    [InlineData(DeployConsistencyStatus.Unknown, null)]
    public void ConsistencySurface_ReportsAgreementCorrectly(
        DeployConsistencyStatus status, bool? expectedConsistent)
    {
        var report = new DeployConsistencyReport(
            new string('a', 40),
            status == DeployConsistencyStatus.Diverged ? new string('b', 40) : new string('a', 40),
            status);

        var payload = DeployConsistencyEndpoints.BuildPayload(report);

        Assert.Equal(report.BuiltRevision, PayloadValue<string>(payload, "builtRevision"));
        Assert.Equal(report.CheckoutRevision, PayloadValue<string>(payload, "checkoutRevision"));
        Assert.Equal(expectedConsistent, PayloadValue<bool?>(payload, "consistent"));
        Assert.Equal(status.ToString(), PayloadValue<string>(payload, "status"));
    }

    [Fact]
    public async Task ReloadPath_WarnsOnDivergence()
    {
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(new CodeyBoxOptions());
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var log = new ListLogger<AgentConfigHotReload>();
        var consistency = new DeployConsistencyService(
            BuildConfig(new Dictionary<string, string?>()),
            NullLogger<DeployConsistencyService>.Instance,
            () => new string('a', 40),
            () => new string('b', 40));
        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator, log,
            deployConsistency: consistency);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions());

        Assert.Contains(
            log.Lines.Where(l => l.Level == LogLevel.Warning),
            l => l.Message.Contains("ebuild", StringComparison.OrdinalIgnoreCase));

        await coordinator.StopAsync(CancellationToken.None);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> kvs)
        => new ConfigurationBuilder().AddInMemoryCollection(kvs).Build();

    private static T? PayloadValue<T>(object payload, string name)
        => (T?)payload.GetType().GetProperty(name)?.GetValue(payload);

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cb-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class ManualOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public ManualOptionsMonitor(T initial)
        {
            _value = initial;
        }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate)
            {
                _listeners.Add(listener);
            }

            return new Subscription(() =>
            {
                lock (_gate)
                {
                    _listeners.Remove(listener);
                }
            });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate)
            {
                snapshot = _listeners.ToArray();
            }

            foreach (var listener in snapshot)
            {
                listener(next, null);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;

            public Subscription(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose() => _onDispose();
        }
    }

    private sealed class OrchestratorFixture : IDisposable
    {
        public OrchestratorService Orchestrator { get; private init; } = null!;
        private SqliteWorkItemStore? _store;
        private string? _dbPath;

        public static OrchestratorFixture Build(AgentConcurrencyOptions concurrency)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"cb-deploy-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipelineRunner(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = 4 },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: concurrency);
            return new OrchestratorFixture { Orchestrator = orch, _store = store, _dbPath = dbPath };
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (_dbPath is not null)
            {
                try { File.Delete(_dbPath); } catch { }
            }
        }
    }

    private sealed class NoopPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken phaseCt, CancellationToken hostCt) =>
            Task.CompletedTask;
    }

    private sealed class InertCostStore : IWorkItemCostStore, IRecentCostsByAgentQueryable
    {
        public Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
            string agentKind, int limit, CancellationToken ct = default) =>
            Task.FromResult<(long, int)>((0L, 0));

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, double)>>(Array.Empty<(string, double)>());

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }
}
