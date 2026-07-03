using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// First-class statistics plugin. Its initial metric stream is a per-agent
/// quota snapshot time-series — captured on a configurable interval by
/// invoking each registered <see cref="IAgentQuotaProbe"/> and persisting both
/// a normalised row-set and the raw <see cref="AgentQuotaSnapshot"/> JSON.
///
/// <para>The plugin replaces the standalone external poller historically used
/// to track subscription quota burn-down — see <c>docs/statistics-plugin.md</c>
/// for migration notes. Operators query the resulting time-series through
/// <c>GET /quota/history</c> on the orchestrator's API; the endpoint resolves
/// the plugin's <see cref="IQuotaTimeSeriesStore"/> registration through DI
/// and gracefully degrades to 503 when the plugin is not loaded.</para>
///
/// <para>The sampler reuses the orchestrator's internal quota probes via DI
/// rather than self-HTTPing the <c>/quota</c> REST endpoint — this keeps the
/// captured availability authoritative (probe → snapshot → row), avoids a
/// loopback dependency between the API and the plugin, and stays cheap even
/// when the API is degraded.</para>
///
/// <para>This class is the only public extension point the plugin loader sees.
/// It implements <see cref="IMetricSampler"/> (the host's per-tick driver),
/// <see cref="IQuotaTimeSeriesStore"/> (the read-side the API queries), and
/// <see cref="IPluginInitializer"/> (so it can open its SQLite store before
/// the first tick). Persistence and pruning live in the
/// <see cref="QuotaTimeSeriesSqliteStore"/> helper so they remain
/// unit-testable without spinning up the plugin lifecycle.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Statistics",
    minHostApiVersion: "1.2")]
public sealed class StatisticsQuotaPlugin
    : IMetricSampler, IQuotaTimeSeriesStore, ICapacityCalculator, IResetCreditExpiryEstimator, IResetOptimalityAdvisor, IPluginInitializer, IAsyncDisposable
{
    public const string PluginId = "codeybox.statistics";
    public const string QuotaSamplerKind = "quota";

    private const string DefaultStateDatabasePath = "/var/lib/codeybox/state.db";
    private const string DefaultStatsDatabaseFileName = "codeybox-stats.db";

    private readonly IReadOnlyList<IAgentQuotaProbe> _probes;
    private readonly IAgentQuotaGate? _quotaGate;
    private readonly IAgentUsageStore? _usageStore;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    private IConfigurationSection? _scopedConfig;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private QuotaTimeSeriesSqliteStore? _store;
    private CapacityCalculator? _capacity;

    private readonly object _stateLock = new();
    private StatisticsPluginOptions _options = new();
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private IDisposable? _configChangeRegistration;

    /// <summary>Frequency at which we prune expired rows from inside <see cref="SampleOnceAsync"/>.</summary>
    private static readonly TimeSpan PruneCadence = TimeSpan.FromHours(1);

    public StatisticsQuotaPlugin(
        IEnumerable<IAgentQuotaProbe> probes,
        IConfiguration configuration,
        IAgentQuotaGate? quotaGate = null,
        IAgentUsageStore? usageStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(configuration);
        _probes = probes.ToList();
        _quotaGate = quotaGate;
        _usageStore = usageStore;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Kind => QuotaSamplerKind;

    /// <inheritdoc/>
    public TimeSpan Interval
    {
        get
        {
            lock (_stateLock) return _options.QuotaSamplerInterval;
        }
    }

    /// <inheritdoc/>
    public bool Enabled
    {
        get
        {
            lock (_stateLock) return _options.QuotaSamplerEnabled;
        }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _scopedConfig = context.ScopedConfig;
        ReloadOptions();

        // Re-bind on any change to the plugin's scoped config so interval /
        // retention / enable flips take effect without a host restart. The
        // change token fires for every config-source reload that touches our
        // section; we de-dup by snapshotting only when the bound values differ.
        _configChangeRegistration = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
            () => _scopedConfig!.GetReloadToken(),
            () =>
            {
                try
                {
                    ReloadOptions();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Statistics plugin: hot-reload of options failed");
                }
            });

        var dbPath = ResolveDatabasePath();
        _store = new QuotaTimeSeriesSqliteStore(dbPath);
        _logger.LogInformation(
            "Statistics plugin initialised: db={DatabasePath}, samplerEnabled={Enabled}, interval={IntervalSeconds}s, retentionHours={RetentionHours}",
            dbPath,
            _options.QuotaSamplerEnabled,
            (int)_options.QuotaSamplerInterval.TotalSeconds,
            (int)_options.Retention.TotalHours);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SampleOnceAsync(CancellationToken ct)
    {
        var store = _store;
        if (store is null)
        {
            _logger.LogDebug("Statistics plugin: SampleOnceAsync skipped — store not yet initialised");
            return;
        }

        // PayPerApiQuotaProbe and NullQuotaProbe sit beside the real subscription
        // probes in DI but always report 100%/Unknown — sampling them would
        // pollute the time-series with synthetic data. Filter by their stable
        // Kind values rather than by concrete type so the plugin can live in
        // its own assembly without referencing CodeyBox.Orchestrator.
        var realProbes = _probes
            .Where(p =>
                !string.Equals(p.Kind.Value, "pay-per-api", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Kind.Value, "null", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (realProbes.Count == 0)
        {
            _logger.LogDebug("Statistics plugin: no real quota probes registered, skipping sample");
            return;
        }

        var sampledAt = _timeProvider.GetUtcNow();
        var sampled = 0;
        foreach (var probe in realProbes)
        {
            ct.ThrowIfCancellationRequested();

            // Use a synthetic Subscription membership (no model id) per probe.
            // This mirrors the second pass in /quota that iterates probes with
            // no class context — it gives us the per-agent overall snapshot,
            // which already includes per-model and per-window breakdowns in
            // the snapshot itself.
            var member = new AgentMembership
            {
                Agent = probe.Kind,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            };

            AgentQuotaSnapshot snapshot;
            try
            {
                snapshot = await probe.GetAvailabilityAsync(member, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Statistics plugin: probe for {Agent} threw; recording unknown snapshot",
                    probe.Kind.Value);
                snapshot = AgentQuotaSnapshot.UnknownSnapshot(
                    QuotaUnknownReason.Transient,
                    $"probe threw: {ex.GetType().Name}: {ex.Message}");
            }

            var wouldAllow = false;
            if (snapshot.IsKnown)
            {
                try
                {
                    wouldAllow = _quotaGate?.Allows(member, snapshot, sampledAt) ?? snapshot.AvailablePct > 0;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Statistics plugin: quota gate threw evaluating {Agent}; recording wouldAllow=false",
                        probe.Kind.Value);
                }
            }

            try
            {
                await store.WriteSnapshotAsync(probe.Kind.Value, snapshot, wouldAllow, sampledAt, ct);
                sampled++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Statistics plugin: failed to persist snapshot for {Agent}",
                    probe.Kind.Value);
            }
        }

        _logger.LogDebug(
            "Statistics plugin: sampled {Count}/{Total} probes at {Time:O}",
            sampled,
            realProbes.Count,
            sampledAt);

        await MaybePruneAsync(sampledAt, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<QuotaSampleRow>> QueryAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default)
    {
        var store = _store
            ?? throw new InvalidOperationException("Statistics plugin: store not initialised — InitializeAsync has not run yet");
        var clamped = ClampFilterLimit(filter);
        return store.QueryAsync(clamped, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<QuotaRawSnapshotRow>> QueryRawAsync(
        QuotaTimeSeriesFilter filter,
        CancellationToken ct = default)
    {
        var store = _store
            ?? throw new InvalidOperationException("Statistics plugin: store not initialised — InitializeAsync has not run yet");
        var clamped = ClampFilterLimit(filter);
        return store.QueryRawAsync(clamped, ct);
    }

    /// <inheritdoc/>
    public Task<CapacityReport> ComputeAsync(CapacityFilter filter, CancellationToken ct = default)
    {
        if (_store is null)
            throw new InvalidOperationException("Statistics plugin: store not initialised — InitializeAsync has not run yet");
        if (_usageStore is null)
        {
            // No agent_usage_events backing store registered — capacity is
            // undefined. Returning an empty report keeps the API surface
            // stable (200 OK + zero entries + a clear note) instead of
            // 500-ing on a missing optional dependency.
            var now = _timeProvider.GetUtcNow();
            return Task.FromResult(new CapacityReport(
                GeneratedAt: now,
                FromUtc: filter.FromUtc ?? now - TimeSpan.FromHours(CapacityFilter.DefaultHorizonHours),
                ToUtc: filter.ToUtc ?? now,
                Entries: Array.Empty<CapacityEntry>()));
        }

        var calc = _capacity ??= new CapacityCalculator(
            timeSeries: this,
            usage: _usageStore,
            clock: _timeProvider);
        return calc.ComputeAsync(filter, ct);
    }

    /// <inheritdoc/>
    public async Task<ResetCreditExpiryReport> EstimateAsync(
        ResetCreditExpiryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var store = _store
            ?? throw new InvalidOperationException("Statistics plugin: store not initialised — InitializeAsync has not run yet");

        ResetCreditExpiryOptions opts;
        int maxRows;
        lock (_stateLock)
        {
            opts = _options.ResetCreditExpiry;
            maxRows = _options.MaxQueryRows;
        }

        var agent = string.IsNullOrWhiteSpace(query.Agent) ? opts.Agent : query.Agent.Trim();
        var now = _timeProvider.GetUtcNow();
        var toUtc = query.ToUtc?.ToUniversalTime() ?? now;
        var fromUtc = query.FromUtc?.ToUniversalTime() ?? toUtc - opts.Lookback;

        var filter = new QuotaTimeSeriesFilter
        {
            Agent = agent,
            FromUtc = fromUtc,
            // The store treats ToUtc as exclusive; nudge it past the requested
            // instant so the most recent sample (written at ~now) is not excluded.
            ToUtc = InclusiveUpperBound(toUtc),
            Limit = maxRows,
        };

        var rawRows = await store.QueryRawAsync(filter, ct);

        var observations = new List<ResetCreditObservation>(rawRows.Count);
        foreach (var row in rawRows)
        {
            var count = TryReadResetCreditsAvailable(row.RawJson);
            if (count is { } value)
                observations.Add(new ResetCreditObservation(row.SampledAt, value));
        }

        // Seeds are pre-observation credits for the single configured agent
        // (opts.Agent). They must NOT be attributed to an unrelated agent
        // queried via ?agent=... — doing so would fabricate banked credits for
        // an agent that has none. Only inject seeds when the effective series
        // agent matches the configured one.
        var seedsApply = string.Equals(agent, opts.Agent, StringComparison.OrdinalIgnoreCase);

        var config = new ResetCreditExpiryConfig
        {
            ExpiryPeriod = opts.ExpiryPeriod,
            SafetyBuffer = opts.SafetyBuffer,
            SeededCredits = seedsApply
                ? opts.Seeds
                    .Select(s => new SeededResetCredit { EstimatedExpiresAt = s.EstimatedExpiresAt, Label = s.Label })
                    .ToList()
                : new List<SeededResetCredit>(),
        };

        return ResetCreditExpiryTracker.Track(observations, config);
    }

    /// <summary>
    /// Reads <c>ResetCreditsAvailable</c> from a persisted raw snapshot without
    /// deserialising the whole <see cref="AgentQuotaSnapshot"/>. Returns null
    /// when the field is absent/null (a gap in the series) or the JSON is
    /// malformed — a bad row must not abort the whole derivation.
    /// </summary>
    private int? TryReadResetCreditsAvailable(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ResetCreditsAvailable", out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Statistics plugin: skipping malformed raw snapshot while deriving reset-credit expiry");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<ResetSpendAdvice> AdviseAsync(
        ResetAdviceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = _store
            ?? throw new InvalidOperationException("Statistics plugin: store not initialised — InitializeAsync has not run yet");

        ResetOptimalityConfigOptions opts;
        int maxRows;
        TimeSpan lookback;
        lock (_stateLock)
        {
            opts = _options.ResetOptimality;
            maxRows = _options.MaxQueryRows;
            lookback = _options.ResetCreditExpiry.Lookback;
        }

        var agent = string.IsNullOrWhiteSpace(request.Agent)
            ? (opts.Agents.Count > 0 ? opts.Agents[0] : "codex")
            : request.Agent.Trim();

        var now = _timeProvider.GetUtcNow();
        var toUtc = request.ToUtc?.ToUniversalTime() ?? now;
        var fromUtc = request.FromUtc?.ToUniversalTime() ?? toUtc - lookback;

        // Reuse the credit-expiry derivation (2/5) for the banked-credit reading.
        var credits = await EstimateAsync(
            new ResetCreditExpiryQuery { Agent = agent, FromUtc = fromUtc, ToUtc = toUtc },
            ct);

        // Latest quota snapshot (1/5): the most recent raw row for the agent.
        var quota = await ReadLatestSnapshotAsync(store, agent, fromUtc, toUtc, ct);

        // Self-calibrate the cadence anchor from observed weekly resets in the
        // logger, when enabled and an anchor is configured to refine.
        var anchor = opts.CadenceAnchor;
        if (anchor is { } configuredAnchor && opts.RefineAnchorFromLogger)
        {
            var observedResets = await DetectWeeklyResetsAsync(store, agent, fromUtc, toUtc, maxRows, ct);
            anchor = NaturalResetCadence.RefineAnchor(
                configuredAnchor, opts.CadencePeriod, observedResets, opts.AnchorRefineTolerance);
        }

        var config = new ResetOptimalityConfig
        {
            PlanEndsAt = opts.PlanEndsAt,
            CadenceAnchor = anchor,
            CadencePeriod = opts.CadencePeriod,
            ResetTargetWindow = opts.ResetTargetWindow,
            DustThresholdPct = opts.DustThresholdPct,
            TimeTolerance = opts.TimeTolerance,
            Agents = opts.Agents,
        };

        return ResetOptimalityEvaluator.Evaluate(agent, quota, credits, config, now);
    }

    /// <summary>
    /// Returns the most recent persisted <see cref="AgentQuotaSnapshot"/> for
    /// <paramref name="agent"/> in the window, or an unknown snapshot when the
    /// series is empty / unparseable — so the advisor holds (burn-first cannot
    /// be evaluated) rather than acting on a fabricated reading.
    /// </summary>
    private async Task<AgentQuotaSnapshot> ReadLatestSnapshotAsync(
        QuotaTimeSeriesSqliteStore store,
        string agent,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        // Newest-first, single row: burn-first must run against the true latest
        // snapshot. An ascending + LIMIT query would return the OLDEST rows and
        // hand back a stale reading once the window exceeds MaxQueryRows, so this
        // read is descending + LIMIT 1 (O(1) rather than materialising the window).
        var filter = new QuotaTimeSeriesFilter
        {
            Agent = agent,
            FromUtc = fromUtc,
            ToUtc = InclusiveUpperBound(toUtc),
            Descending = true,
            Limit = 1,
        };

        var rawRows = await store.QueryRawAsync(filter, ct);
        if (rawRows.Count == 0)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient, "no quota snapshot logged for the requested window");

        // Descending order → the first (only) row is the most recent.
        var rawJson = rawRows[0].RawJson;
        try
        {
            var snapshot = JsonSerializer.Deserialize<AgentQuotaSnapshot>(rawJson);
            return snapshot ?? AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Permanent, "latest quota snapshot deserialised to null");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Statistics plugin: latest quota snapshot for {Agent} is unparseable", agent);
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Permanent, "latest quota snapshot is unparseable");
        }
    }

    /// <summary>
    /// Detects weekly natural-reset instants from the logged <c>weekly</c>-window
    /// series: a sample whose usable % jumps up across the reset thresholds
    /// (from a spent window to a fresh one) marks a reset. These feed the
    /// cadence-anchor self-calibration.
    /// </summary>
    private async Task<IReadOnlyList<DateTimeOffset>> DetectWeeklyResetsAsync(
        QuotaTimeSeriesSqliteStore store,
        string agent,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxRows,
        CancellationToken ct)
    {
        // Fetch newest-first so a series exceeding the row limit keeps the most
        // recent (most relevant) reset boundaries instead of silently dropping
        // them; the detector needs oldest-first, so reverse before handing off.
        var filter = new QuotaTimeSeriesFilter
        {
            Agent = agent,
            WindowName = "weekly",
            FromUtc = fromUtc,
            ToUtc = InclusiveUpperBound(toUtc),
            Descending = true,
            Limit = maxRows,
        };

        var rows = await store.QueryAsync(filter, ct);
        var ascending = rows.Reverse().ToList();
        return WeeklyNaturalResetDetector.Detect(ascending);
    }

    /// <summary>
    /// Converts the advice window's inclusive upper bound into the store's
    /// exclusive <c>sampled_at &lt; ToUtc</c> form. The store excludes the upper
    /// bound, so nudge it just past the requested instant to keep a sample taken
    /// exactly at <paramref name="toUtc"/> (samples are whole-second). One place,
    /// so the three advisor reads stay consistent.
    /// </summary>
    private static DateTimeOffset InclusiveUpperBound(DateTimeOffset toUtc)
        => toUtc + TimeSpan.FromSeconds(1);

    private QuotaTimeSeriesFilter ClampFilterLimit(QuotaTimeSeriesFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int maxRows;
        lock (_stateLock) maxRows = _options.MaxQueryRows;
        if (filter.Limit > maxRows)
            return filter with { Limit = maxRows };
        return filter;
    }

    private async Task MaybePruneAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var store = _store;
        if (store is null) return;

        TimeSpan retention;
        DateTimeOffset lastPrune;
        lock (_stateLock)
        {
            retention = _options.Retention;
            lastPrune = _lastPruneUtc;
        }

        if (retention <= TimeSpan.Zero)
            return;
        if (nowUtc - lastPrune < PruneCadence)
            return;

        try
        {
            var cutoff = nowUtc - retention;
            var removed = await store.PruneAsync(cutoff, ct);
            lock (_stateLock) _lastPruneUtc = nowUtc;
            if (removed > 0)
            {
                _logger.LogInformation(
                    "Statistics plugin: pruned {Removed} rows older than {Cutoff:O}",
                    removed,
                    cutoff);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statistics plugin: prune sweep failed");
        }
    }

    private void ReloadOptions()
    {
        var section = _scopedConfig;
        var next = section is null
            ? new StatisticsPluginOptions()
            : StatisticsPluginOptions.FromConfiguration(section);

        lock (_stateLock)
        {
            _options = next;
        }
    }

    private string ResolveDatabasePath()
    {
        StatisticsPluginOptions snapshot;
        lock (_stateLock) snapshot = _options;

        if (!string.IsNullOrWhiteSpace(snapshot.DatabasePath))
            return snapshot.DatabasePath;

        // Co-locate the stats DB with the orchestrator's state.db so a fresh
        // install lands the file in the operator's existing dataroot rather
        // than in CWD. The path is read directly off the orchestrator's
        // options section to avoid taking a DI dependency on the orchestrator
        // assembly from inside this plugin.
        var statePath = _configuration["CodeyBox:StateDatabasePath"];
        if (string.IsNullOrWhiteSpace(statePath))
            statePath = DefaultStateDatabasePath;

        var dir = Path.GetDirectoryName(statePath);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        return Path.Combine(dir, DefaultStatsDatabaseFileName);
    }

    public ValueTask DisposeAsync()
    {
        _configChangeRegistration?.Dispose();
        _configChangeRegistration = null;
        if (_store is { } store)
        {
            _store = null;
            return store.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
