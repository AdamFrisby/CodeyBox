using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that subscribes to <see cref="IOptionsMonitor{TOptions}"/>
/// for <see cref="CodeyBoxOptions"/> and pushes per-block reloads into the
/// router, orchestrator, burn estimator, cost calculator, and agent default
/// model IDs without a process restart.
///
/// <para>
/// Several blocks are hot-reloadable here:
/// <list type="bullet">
/// <item><c>CodeyBox:WorkerPool:MaxConcurrentWorkers</c> →
///   <see cref="OrchestratorService.ApplyWorkerPoolReload"/>. The other
///   <c>WorkerPool</c> fields (<c>MaxConcurrentSandboxes</c>, <c>MinSpawnInterval</c>)
///   are captured at startup and not re-bound here.</item>
/// <item><c>CodeyBox:AgentConcurrency</c> → <see cref="OrchestratorService.ApplyAgentConcurrencyReload"/>.</item>
/// <item><c>CodeyBox:AgentClasses</c> + <c>CodeyBox:AgentScoreModifiers</c> →
///   <see cref="AgentClassRouter.ApplyConfigReload"/>. Both are bundled because
///   the router stores them as a single coherent snapshot, and TOD modifiers
///   only have meaning relative to the class catalog they tag.</item>
/// <item><c>CodeyBox:AgentBurnEstimator</c> → <see cref="AgentBurnEstimator.ApplyConfigReload"/>.</item>
/// <item><c>CodeyBox:AgentPricing</c> → re-merge with bundled defaults, then
///   <see cref="AgentPricingState.ApplySuccessfulMerge"/>.</item>
/// <item><c>CodeyBox:AgentBudgets</c> → <see cref="IAgentBudgetConfigReloadable.ApplyConfigReload"/>
///   (atomically swaps the budget windows/limits; the calculator holds no
///   snapshot cache — it recomputes from the live usage store on every call —
///   so the new windows take effect on the next gate/visibility read).</item>
/// <item><c>CodeyBox:AgentDefaults</c> → <see cref="AgentDefaultsSnapshot.Replace"/>.</item>
/// <item><c>CodeyBox:AgentNetworkTolerance</c> → <see cref="AgentNetworkToleranceSnapshot.Replace"/>.</item>
/// <item><c>CodeyBox:AgentPauses</c> → <see cref="IAgentPauseController"/> config-owned
///   pause/resume reconciliation.</item>
/// <item><c>CodeyBox:Smoke</c> → <see cref="SmokeOptionsSnapshot.Replace"/>
///   so the master smoke switch applies to pickup, router, and in-VM gates
///   without restart.</item>
/// </list>
/// </para>
///
/// <para>
/// Each block emits an <c>AuditLog.ConfigReloaded</c> entry only when the
/// JSON-serialised value actually changed against the last-applied snapshot —
/// an unrelated edit in <c>~/codeybox-extra.json</c> fires
/// <see cref="IOptionsMonitor{TOptions}.OnChange"/> but produces no audit
/// noise for blocks that did not move.
/// </para>
///
/// <para>
/// In-flight items are not retroactively re-evaluated. The orchestrator
/// consults the per-agent cap dictionary only at dispatch time, and the
/// router snapshots its catalog at the entry of each public method, so a
/// running iteration finishes against the config snapshot it started with.
/// </para>
/// </summary>
public sealed class AgentConfigHotReload : IHostedService, IDisposable
{
    private const string ConfigPausedBy = "config";

    private readonly IOptionsMonitor<CodeyBoxOptions> _monitor;
    private readonly OrchestratorService _orchestrator;
    private readonly AgentClassRouter _router;
    private readonly AgentBurnEstimator _burnEstimator;
    private readonly IAgentBudgetConfigReloadable? _budgetReloader;
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly ClaudeThinkingBlockSanitizerConfig? _sanitizerConfig;
    private readonly AgentNetworkToleranceSnapshot? _networkTolerance;
    private readonly AgentCostCalculator? _costCalculator;
    private readonly AgentPricingState? _pricingState;
    private readonly IncrementalRebaseSnapshot? _incrementalRebase;
    private readonly PipelineTuningSnapshot? _pipelineTuning;
    private readonly BudgetDeferralRecheckSnapshot? _budgetDeferralRecheck;
    private readonly QuotaRouterOptions? _quotaRouterOptions;
    private readonly IInVmSmokeCoveragePolicy? _coverage;
    private readonly SmokeOptionsSnapshot? _smokeOptions;
    private readonly TransitionHealthOptionsSnapshot? _transitionHealth;
    private readonly IAgentPauseController? _pauses;
    private readonly IAgentRegistry? _agents;
    private readonly ILogger<AgentConfigHotReload> _log;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;

    // Last-applied serialised forms; used as both the equality check and the
    // value reported back to AuditLog.ConfigReloaded.
    private string _lastWorkerPool = "";
    private string _lastConcurrency = "";
    private string _lastBurn = "";
    private string _lastRouter = "";
    private string _lastPricing = "";
    private string _lastBudgets = "";
    private string _lastDefaults = "";
    private string _lastIncrementalRebase = "";
    private string _lastSanitizer = "";
    private string _lastQuotaRouter = "";
    private string _lastPipelineTuning = "";
    private string _lastBudgetDeferralRecheck = "";
    private string _lastSmoke = "";
    private string _lastTransitionHealth = "";
    private string _lastAgentPauses = "";
    private string _lastNetworkTolerance = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    internal AgentConfigHotReload(
        IOptionsMonitor<CodeyBoxOptions> monitor,
        OrchestratorService orchestrator,
        AgentClassRouter router,
        AgentBurnEstimator burnEstimator,
        ILogger<AgentConfigHotReload> log,
        AgentDefaultsSnapshot? defaults = null,
        ClaudeThinkingBlockSanitizerConfig? sanitizerConfig = null,
        AgentNetworkToleranceSnapshot? networkTolerance = null,
        AgentCostCalculator? costCalculator = null,
        AgentPricingState? pricingState = null,
        IAgentBudgetConfigReloadable? budgetReloader = null,
        IncrementalRebaseSnapshot? incrementalRebase = null,
        PipelineTuningSnapshot? pipelineTuning = null,
        BudgetDeferralRecheckSnapshot? budgetDeferralRecheck = null,
        QuotaRouterOptions? quotaRouterOptions = null,
        IInVmSmokeCoveragePolicy? coverage = null,
        SmokeOptionsSnapshot? smokeOptions = null,
        IAgentPauseController? pauses = null,
        IAgentRegistry? agents = null,
        TransitionHealthOptionsSnapshot? transitionHealth = null)
    {
        if (costCalculator is not null && pricingState is null)
        {
            throw new ArgumentException(
                "AgentPricingState is required when AgentCostCalculator is registered for hot-reload.",
                nameof(pricingState));
        }

        _monitor = monitor;
        _orchestrator = orchestrator;
        _router = router;
        _burnEstimator = burnEstimator;
        _budgetReloader = budgetReloader;
        _defaults = defaults;
        _sanitizerConfig = sanitizerConfig;
        _networkTolerance = networkTolerance;
        _costCalculator = costCalculator;
        _pricingState = pricingState;
        _incrementalRebase = incrementalRebase;
        _pipelineTuning = pipelineTuning;
        _budgetDeferralRecheck = budgetDeferralRecheck;
        _quotaRouterOptions = quotaRouterOptions;
        _coverage = coverage;
        _smokeOptions = smokeOptions;
        _transitionHealth = transitionHealth;
        _pauses = pauses;
        _agents = agents;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Capture initial serialised state so the first OnChange after startup
        // only fires audit entries for fields that actually changed against
        // the snapshot the router / orchestrator were built with.
        var initial = _monitor.CurrentValue;
        _lastWorkerPool = SerializeWorkerPool(initial.WorkerPool, initial.Concurrency);
        _lastConcurrency = SerializeConcurrency(initial.AgentConcurrency);
        _lastBurn = SerializeBurn(initial.AgentBurnEstimator);
        _lastRouter = SerializeRouterInputs(initial.AgentClasses, initial.AgentInstances, initial.AgentScoreModifiers);
        _lastPricing = SerializePricing(initial.AgentPricing);
        _lastBudgets = SerializeBudgets(initial.AgentBudgets);
        _lastDefaults = SerializeDefaults(initial.AgentDefaults);
        _lastNetworkTolerance = SerializeNetworkTolerance(initial.AgentNetworkTolerance);
        _lastIncrementalRebase = SerializeIncrementalRebase(initial.IncrementalRebase);
        _lastSanitizer = SerializeSanitizer(initial.ClaudeThinkingBlockSanitizer);
        _lastQuotaRouter = SerializeQuotaRouter(initial.QuotaRouter);
        _lastPipelineTuning = SerializePipelineTuning(initial.PipelineTuning);
        _lastBudgetDeferralRecheck = SerializeBudgetDeferralRecheck(initial.BudgetDeferralRecheck);
        _lastSmoke = SerializeSmoke(initial.Smoke);
        _lastTransitionHealth = SerializeTransitionHealth(initial.TransitionHealth);
        _lastAgentPauses = SerializeAgentPauses(initial.AgentPauses);

        AgentSuspendResilience.SetMaxRetries(initial.PipelineTuning.AgentSuspendMaxRetries);
        SessionResumeOptions.SetMaxResumeAttempts(initial.PipelineTuning.AgentSessionResumeMaxAttempts);
        await ApplyConfiguredAgentPausesAtStartupAsync(initial, cancellationToken);

        _subscription = _monitor.OnChange(OnConfigChanged);
        _log.LogInformation(
            "AgentConfigHotReload subscribed to CodeyBoxOptions: classes={ClassesLen} concurrency={ConcurrencyLen} burn={BurnLen} pricing={PricingLen} defaults={DefaultsLen} sanitizer={SanitizerLen} tolerance={ToleranceLen}",
            _lastRouter.Length, _lastConcurrency.Length, _lastBurn.Length, _lastPricing.Length, _lastDefaults.Length, _lastSanitizer.Length, _lastNetworkTolerance.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnConfigChanged(CodeyBoxOptions opts)
    {
        lock (_gate)
        {
            ApplyWorkerPoolIfChanged(opts);
            ApplyConcurrencyIfChanged(opts);
            ApplySmokeIfChanged(opts);
            ApplyTransitionHealthIfChanged(opts);
            ApplyRouterIfChanged(opts);
            ApplyBurnIfChanged(opts);
            ApplyPricingIfChanged(opts);
            ApplyBudgetsIfChanged(opts);
            ApplyDefaultsIfChanged(opts);
            ApplyNetworkToleranceIfChanged(opts);
            ApplyAgentPausesIfChanged(opts);
            ApplyIncrementalRebaseIfChanged(opts);
            ApplySanitizerIfChanged(opts);
            ApplyQuotaRouterIfChanged(opts);
            ApplyPipelineTuningIfChanged(opts);
            ApplyBudgetDeferralRecheckIfChanged(opts);
        }
    }

    private async Task ApplyConfiguredAgentPausesAtStartupAsync(
        CodeyBoxOptions opts,
        CancellationToken ct)
    {
        if (_pauses is null)
            return;

        try
        {
            await ReconcileConfiguredAgentPausesAsync(opts.AgentPauses, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Could not apply startup AgentPauses config; runtime pause API remains available");
        }
    }

    private void ApplyAgentPausesIfChanged(CodeyBoxOptions opts)
    {
        if (_pauses is null) return;

        var next = SerializeAgentPauses(opts.AgentPauses);
        if (string.Equals(_lastAgentPauses, next, StringComparison.Ordinal))
            return;

        var prev = _lastAgentPauses;
        try
        {
            ReconcileConfiguredAgentPausesAsync(opts.AgentPauses, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            _lastAgentPauses = next;
            AuditLog.ConfigReloaded("AgentPauses", prev, next);
            _log.LogInformation("Hot-reloaded AgentPauses: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Hot-reload of AgentPauses rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private async Task ReconcileConfiguredAgentPausesAsync(
        IReadOnlyDictionary<string, AgentPauseConfig> configured,
        CancellationToken ct)
    {
        var desired = new Dictionary<AgentKind, (string Reason, DateTimeOffset? ExpiresAt)>();
        foreach (var (rawAgent, pause) in configured)
        {
            var agent = new AgentKind(rawAgent.Trim().ToLowerInvariant());
            if (_agents is not null && !_agents.Available.Contains(agent))
            {
                _log.LogWarning(
                    "Ignoring configured pause for unknown agent '{Agent}'",
                    rawAgent);
                continue;
            }

            if (!pause.Paused)
                continue;

            if (AgentPauseValidation.ValidateRequiredReason(
                    pause.Reason,
                    $"CodeyBox:AgentPauses:{rawAgent}:Reason") is { } reasonError)
                throw new InvalidOperationException(reasonError);

            var expiresAt = ResolveConfiguredPauseExpiresAt(rawAgent, pause);
            if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                continue;

            desired[agent] = (pause.Reason!.Trim(), expiresAt);
        }

        var current = await _pauses!.ListPausedAsync(ct).ConfigureAwait(false);
        foreach (var state in current)
        {
            if (!string.Equals(state.PausedBy, ConfigPausedBy, StringComparison.OrdinalIgnoreCase))
                continue;
            if (desired.ContainsKey(state.Agent))
                continue;

            await _pauses.ResumeAsync(
                state.Agent,
                ConfigPausedBy,
                "removed from config",
                ct).ConfigureAwait(false);
        }

        var ownership = current.ToDictionary(s => s.Agent, s => s.PausedBy);
        foreach (var (agent, (reason, expiresAt)) in desired)
        {
            if (ownership.TryGetValue(agent, out var pausedBy)
                && !string.Equals(pausedBy, ConfigPausedBy, StringComparison.OrdinalIgnoreCase))
            {
                // A runtime owner (API / work-item / operator CLI) already holds
                // this agent's pause row. The config block is "config-owned-only"
                // and must NOT take over the row — otherwise removing the config
                // entry later would resume an agent the runtime never asked to
                // unpause. The runtime pause remains authoritative; the config
                // intent is ignored for as long as the runtime pause stands.
                _log.LogInformation(
                    "Skipping configured pause for {Agent}: runtime pause already owned by '{Owner}'",
                    agent.Value, pausedBy ?? "(unknown)");
                continue;
            }

            await _pauses.PauseAsync(
                agent,
                reason,
                ConfigPausedBy,
                expiresAt,
                ct).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? ResolveConfiguredPauseExpiresAt(
        string agent,
        AgentPauseConfig pause)
    {
        if (pause.DurationSeconds is { } seconds && seconds <= 0)
            throw new InvalidOperationException($"CodeyBox:AgentPauses:{agent}:DurationSeconds must be positive");
        if (pause.DurationSeconds is not null && pause.ExpiresAt is not null)
            throw new InvalidOperationException(
                $"CodeyBox:AgentPauses:{agent} must provide either DurationSeconds or ExpiresAt, not both");

        if (pause.ExpiresAt is { } expiresAt)
            return expiresAt;

        return pause.DurationSeconds is { } durationSeconds
            ? DateTimeOffset.UtcNow.AddSeconds(durationSeconds)
            : null;
    }

    private void ApplyQuotaRouterIfChanged(CodeyBoxOptions opts)
    {
        if (_quotaRouterOptions is null) return;

        var next = SerializeQuotaRouter(opts.QuotaRouter);
        if (string.Equals(_lastQuotaRouter, next, StringComparison.Ordinal))
            return;

        var prev = _lastQuotaRouter;
        try
        {
            // Mutate the shared singleton in place. The router holds the same
            // QuotaRouterOptions reference and reads its properties on every
            // gate decision, so the new values take effect on the next pickup
            // attempt without a process restart. Probe-side knobs (cache TTL
            // bound at construction) are NOT reloaded here — they have their
            // own IOptionsMonitor-driven resilience-provider delegate.
            QuotaRouterConfigMapper.ApplyHotReload(_quotaRouterOptions, opts.QuotaRouter);

            _lastQuotaRouter = next;
            AuditLog.ConfigReloaded("QuotaRouter", prev, next);
            _log.LogInformation("Hot-reloaded QuotaRouter: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of QuotaRouter rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplySmokeIfChanged(CodeyBoxOptions opts)
    {
        if (_smokeOptions is null) return;

        var next = SerializeSmoke(opts.Smoke);
        if (string.Equals(_lastSmoke, next, StringComparison.Ordinal))
            return;

        var prev = _lastSmoke;
        var enforceProbeCoverage = false;
        try
        {
            var previousOptions = _smokeOptions.Current;
            var nextOptions = ToSmokeOptions(opts.Smoke, previousOptions.CacheTtlMinutes);
            _smokeOptions.Replace(nextOptions);
            _lastSmoke = next;
            AuditLog.ConfigReloaded("Smoke", prev, next);
            _log.LogInformation("Hot-reloaded Smoke: {OldValue} → {NewValue}", prev, next);
            enforceProbeCoverage = !previousOptions.Enabled && nextOptions.Enabled;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of Smoke rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }

        if (enforceProbeCoverage)
            EnforceProbeCoverage(opts);
    }

    private void ApplyIncrementalRebaseIfChanged(CodeyBoxOptions opts)
    {
        if (_incrementalRebase is null) return;

        var next = SerializeIncrementalRebase(opts.IncrementalRebase);
        if (string.Equals(_lastIncrementalRebase, next, StringComparison.Ordinal))
            return;

        var prev = _lastIncrementalRebase;
        try
        {
            _incrementalRebase.Replace(new IncrementalRebaseOptions
            {
                Enabled = opts.IncrementalRebase.Enabled,
            });
            _lastIncrementalRebase = next;
            AuditLog.ConfigReloaded("IncrementalRebase", prev, next);
            _log.LogInformation("Hot-reloaded IncrementalRebase: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of IncrementalRebase rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyBudgetsIfChanged(CodeyBoxOptions opts)
    {
        if (_budgetReloader is null) return;

        var next = SerializeBudgets(opts.AgentBudgets);
        if (string.Equals(_lastBudgets, next, StringComparison.Ordinal))
            return;

        var prev = _lastBudgets;
        try
        {
            _budgetReloader.ApplyConfigReload(opts.AgentBudgets);
            _lastBudgets = next;
            AuditLog.ConfigReloaded("AgentBudgets", prev, next);
            _log.LogInformation("Hot-reloaded AgentBudgets: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentBudgets rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyWorkerPoolIfChanged(CodeyBoxOptions opts)
    {
        var next = SerializeWorkerPool(opts.WorkerPool, opts.Concurrency);
        if (string.Equals(_lastWorkerPool, next, StringComparison.Ordinal))
            return;

        var prev = _lastWorkerPool;
        try
        {
            var resolved = ResolveEffectiveMaxConcurrentWorkers(opts.WorkerPool, opts.Concurrency);
            _orchestrator.ApplyWorkerPoolReload(resolved);
            _lastWorkerPool = next;
            AuditLog.ConfigReloaded("WorkerPool", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of WorkerPool rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    /// <summary>
    /// Resolves the same effective <c>MaxConcurrentWorkers</c> value that the
    /// startup factory in <see cref="OrchestratorOptionsFactory.Build(int?, WorkerPoolOptions, ILogger)"/>
    /// produces: <c>WorkerPool.MaxConcurrentWorkers</c> wins when set; the
    /// deprecated top-level <c>Concurrency</c> is the fallback; default is 1.
    /// </summary>
    private static int ResolveEffectiveMaxConcurrentWorkers(
        WorkerPoolOptions workerPool,
        int? legacyConcurrency)
    {
        if (workerPool.MaxConcurrentWorkers is { } explicitValue)
            return explicitValue;
        if (legacyConcurrency is { } legacyValue)
            return legacyValue;
        return 1;
    }

    private void ApplyConcurrencyIfChanged(CodeyBoxOptions opts)
    {
        var next = SerializeConcurrency(opts.AgentConcurrency);
        if (string.Equals(_lastConcurrency, next, StringComparison.Ordinal))
            return;

        var prev = _lastConcurrency;
        try
        {
            _orchestrator.ApplyAgentConcurrencyReload(opts.AgentConcurrency);
            _lastConcurrency = next;
            AuditLog.ConfigReloaded("AgentConcurrency", prev, next);
            _log.LogInformation("Hot-reloaded AgentConcurrency: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentConcurrency rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyRouterIfChanged(CodeyBoxOptions opts)
    {
        var next = SerializeRouterInputs(opts.AgentClasses, opts.AgentInstances, opts.AgentScoreModifiers);
        if (string.Equals(_lastRouter, next, StringComparison.Ordinal))
            return;

        var prev = _lastRouter;
        try
        {
            var catalog = AgentClassesConfigBuilder.Build(opts.AgentClasses, opts.AgentInstances, _log);
            var todModifiers = AgentClassesConfigBuilder.BuildTodModifiers(opts.AgentScoreModifiers, _log);
            _router.ApplyConfigReload(catalog, todModifiers);
            _lastRouter = next;
            AuditLog.ConfigReloaded("AgentClasses", prev, next);
            _log.LogInformation("Hot-reloaded AgentClasses+AgentScoreModifiers: {OldValue} → {NewValue}", prev, next);

            // AC#1 must hold across hot-reloads too: a member added at runtime
            // with no registered in-VM probe would otherwise stay default-Available
            // and fail on first dispatch (the startup coverage validator only runs
            // once). Re-run coverage enforcement through the gate so newly-added
            // uncovered members are benched immediately. Idempotent for members
            // already covered or already benched.
            EnforceProbeCoverage(opts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentClasses rejected; keeping prior router catalog ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void EnforceProbeCoverage(CodeyBoxOptions opts)
    {
        if (_coverage is null) return;
        var coverage = InVmSmokeCoverageRequest.FromAgentClasses(opts.AgentClasses);
        foreach (var outcome in _coverage.EnforceMissingProbeCoverage(coverage))
        {
            if (outcome.Action == InVmSmokeCoverageAction.Benched)
                _log.LogWarning(
                    "Hot-reload added AgentClass member '{Agent}' (class(es): {ClassIds}) with no registered " +
                    "IInVmSmokeProbe; BENCHED so work routes past it instead of failing on first dispatch (AC#1).",
                    outcome.Agent, string.Join(", ", outcome.ClassIds));
        }
    }

    private void ApplyBurnIfChanged(CodeyBoxOptions opts)
    {
        var next = SerializeBurn(opts.AgentBurnEstimator);
        if (string.Equals(_lastBurn, next, StringComparison.Ordinal))
            return;

        var prev = _lastBurn;
        try
        {
            _burnEstimator.ApplyConfigReload(opts.AgentBurnEstimator);
            _lastBurn = next;
            AuditLog.ConfigReloaded("AgentBurnEstimator", prev, next);
            _log.LogInformation("Hot-reloaded AgentBurnEstimator: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentBurnEstimator rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyPricingIfChanged(CodeyBoxOptions opts)
    {
        if (_costCalculator is null || _pricingState is null) return;

        var next = SerializePricing(opts.AgentPricing);
        if (string.Equals(_lastPricing, next, StringComparison.Ordinal))
            return;

        var prev = _lastPricing;
        try
        {
            // Re-merge bundled baseline with the new operator snapshot so a
            // hot-reload of CodeyBox:AgentPricing keeps bundled rates for keys
            // the operator didn't override. The bundled file is static between
            // deploys, so there is no need to reread from disk here.
            var merged = AgentPricingMerge.Merge(_pricingState.Defaults.Baseline, opts.AgentPricing);
            _pricingState.ApplySuccessfulMerge(merged, _costCalculator);
            _lastPricing = next;
            AuditLog.ConfigReloaded("AgentPricing", prev, next);
            _log.LogInformation("Hot-reloaded AgentPricing: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentPricing rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyDefaultsIfChanged(CodeyBoxOptions opts)
    {
        if (_defaults is null) return;

        var next = SerializeDefaults(opts.AgentDefaults);
        if (string.Equals(_lastDefaults, next, StringComparison.Ordinal))
            return;

        var prev = _lastDefaults;
        try
        {
            var dict = new Dictionary<string, string?>(opts.AgentDefaults, opts.AgentDefaults.Comparer);
            _defaults.Replace(dict);
            _lastDefaults = next;
            AuditLog.ConfigReloaded("AgentDefaults", prev, next);
            _log.LogInformation("Hot-reloaded AgentDefaults: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentDefaults rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyNetworkToleranceIfChanged(CodeyBoxOptions opts)
    {
        if (_networkTolerance is null) return;

        var next = SerializeNetworkTolerance(opts.AgentNetworkTolerance);
        if (string.Equals(_lastNetworkTolerance, next, StringComparison.Ordinal))
            return;

        var prev = _lastNetworkTolerance;
        try
        {
            _networkTolerance.Replace(opts.AgentNetworkTolerance);
            _lastNetworkTolerance = next;
            AuditLog.ConfigReloaded("AgentNetworkTolerance", prev, next);
            _log.LogInformation("Hot-reloaded AgentNetworkTolerance: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentNetworkTolerance rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplySanitizerIfChanged(CodeyBoxOptions opts)
    {
        if (_sanitizerConfig is null) return;

        var next = SerializeSanitizer(opts.ClaudeThinkingBlockSanitizer);
        if (string.Equals(_lastSanitizer, next, StringComparison.Ordinal))
            return;

        var prev = _lastSanitizer;
        try
        {
            _sanitizerConfig.Enabled = opts.ClaudeThinkingBlockSanitizer.Enabled;
            _lastSanitizer = next;
            AuditLog.ConfigReloaded("ClaudeThinkingBlockSanitizer", prev, next);
            _log.LogInformation("Hot-reloaded ClaudeThinkingBlockSanitizer: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of ClaudeThinkingBlockSanitizer rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    /// <summary>
    /// Hot-reload fingerprint for the worker-pool block. Only includes the
    /// hot-reloadable fields — <c>MaxConcurrentSandboxes</c> and
    /// <c>MinSpawnInterval</c> are captured at startup and are explicitly out
    /// of scope here, so an unrelated edit to them does not trigger a
    /// no-op resize call.
    /// </summary>
    private static string SerializeWorkerPool(WorkerPoolOptions opts, int? legacyConcurrency) =>
        JsonSerializer.Serialize(
            new
            {
                opts.MaxConcurrentWorkers,
                LegacyConcurrency = legacyConcurrency,
            },
            JsonOpts);

    private static string SerializeConcurrency(AgentConcurrencyOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                Members = opts.Members
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.MaxConcurrent),
            },
            JsonOpts);

    private static string SerializeBurn(AgentBurnEstimatorOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                DefaultBurnPercentPerItem = opts.DefaultBurnPercentPerItem
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                WindowTokenBudget = opts.WindowTokenBudget
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                opts.RollingSampleSize,
                CacheTtlSeconds = opts.CacheTtl.TotalSeconds,
            },
            JsonOpts);

    private static string SerializeBudgets(AgentBudgetOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.RetentionDays,
                Members = opts.Members
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Models
                            .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                m => m.Key,
                                m => m.Value.Windows
                                    .Select(w => new { w.Kind, w.Hours, w.LimitCents })
                                    .ToArray())),
            },
            JsonOpts);

    private static string SerializePricing(AgentPricingOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                Rates = opts.Rates
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value
                            .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(m => m.Key, m => m.Value)),
                DefaultRates = opts.DefaultRates
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            JsonOpts);

    private static string SerializeRouterInputs(
        List<AgentClassOptions> classes,
        List<AgentInstanceOptions> instances,
        AgentScoreModifiersOptions modifiers) =>
        JsonSerializer.Serialize(
            new
            {
                Instances = instances
                    .Select(i => new
                    {
                        i.Id,
                        i.Agent,
                        i.CredentialFilePath,
                        i.TokenEnvironmentVariable,
                        i.AuthJsonEnvironmentVariable,
                        i.SettingsFilePath,
                        i.DestinationPath,
                        i.SandboxEnvironmentVariable,
                    })
                    .OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Agent, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Classes = classes
                    .Select(c => new
                    {
                        c.Id,
                        c.DisplayName,
                        Members = c.Members
                            .Select(m => new
                            {
                                m.Agent,
                                m.InstanceId,
                                m.Billing,
                                m.ModelId,
                                m.CredentialFilePath,
                                m.TokenEnvironmentVariable,
                                m.AuthJsonEnvironmentVariable,
                                m.SettingsFilePath,
                                m.DestinationPath,
                                m.SandboxEnvironmentVariable,
                                m.QualityScore,
                                m.ReasoningMode,
                                Capabilities = m.Capabilities
                                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                    .ToArray(),
                            })
                            .ToArray(),
                    })
                    .ToArray(),
                ScoreModifiers = modifiers.ByTimeOfDay
                    .Select(t => new
                    {
                        t.Agent,
                        t.Modifier,
                        Windows = t.Windows
                            .Select(w => new { Days = w.Days.ToArray(), w.StartUtc, w.EndUtc })
                            .ToArray(),
                    })
                    .ToArray(),
            },
            JsonOpts);

    private static string SerializeNetworkTolerance(Dictionary<string, AgentNetworkToleranceOptions?> tolerance) =>
        JsonSerializer.Serialize(
            tolerance.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    kv => kv.Key,
                    kv => SerializeNetworkToleranceValue(kv.Value),
                    StringComparer.OrdinalIgnoreCase),
            JsonOpts);

    private static object? SerializeNetworkToleranceValue(AgentNetworkToleranceOptions? value) =>
        value is null
            ? null
            : new
            {
                value.RequestMaxRetries,
                value.StreamMaxRetries,
                value.StreamIdleTimeoutMs,
                value.Provider,
                value.ApiTimeoutMs,
            };

    private static string SerializeDefaults(Dictionary<string, string?> defaults) =>
        JsonSerializer.Serialize(
            defaults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            JsonOpts);

    private static string SerializeAgentPauses(Dictionary<string, AgentPauseConfig> pauses) =>
        JsonSerializer.Serialize(
            pauses.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        kv.Value.Paused,
                        Reason = kv.Value.Reason,
                        kv.Value.ExpiresAt,
                        kv.Value.DurationSeconds,
                    },
                    StringComparer.OrdinalIgnoreCase),
            JsonOpts);

    private static string SerializeIncrementalRebase(IncrementalRebaseOptions opts) =>
        JsonSerializer.Serialize(new { opts.Enabled }, JsonOpts);

    private static string SerializeSanitizer(ClaudeThinkingBlockSanitizerOptions opts) =>
        JsonSerializer.Serialize(new { opts.Enabled }, JsonOpts);

    private static string SerializeQuotaRouter(QuotaRouterConfig opts)
    {
        var mapped = QuotaRouterConfigMapper.ToOptions(opts);
        return JsonSerializer.Serialize(
            new
            {
                opts.MinQuotaPct,
                MinQuotaPctByWindow = mapped.MinQuotaPctByWindow
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                opts.StartFloorPct,
                opts.EndFloorPct,
                opts.RampWindowSeconds,
                RampWindowByAgentSeconds = mapped.RampWindowByAgent
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => (int)kv.Value.TotalSeconds),
                FloorByAgent = mapped.FloorByAgent
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => new
                    {
                        kv.Value.MinQuotaPct,
                        kv.Value.StartFloorPct,
                        kv.Value.EndFloorPct,
                        RampWindowSeconds = kv.Value.RampWindow is { } rampWindow
                            ? checked((int)rampWindow.TotalSeconds)
                            : (int?)null,
                    }),
                opts.QuotaRecheckIntervalSeconds,
                opts.QuotaRecoveryProbeIntervalSeconds,
                opts.MaxQuotaRecoveryProbeEligibilityScan,
                UnknownPolicy = opts.UnknownPolicy.ToString(),
                opts.ObservedFailureWindowMinutes,
                opts.ObservedFailureRetentionMinutes,
                opts.CapRetryIntervalSeconds,
                opts.ColdStartFitInWindow,
                opts.DrainAggressiveness,
                ExpectedResets = mapped.ExpectedResets
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => new
                    {
                        Timestamps = kv.Value.Timestamps
                            .OrderBy(t => t)
                            .ToArray(),
                        CadenceSeconds = kv.Value.Cadence is { } cadence
                            ? checked((int)cadence.TotalSeconds)
                            : (int?)null,
                        kv.Value.CadenceAnchor,
                    }),
                IntraKindRoutingPolicy = opts.IntraKindRoutingPolicy.ToString(),
            },
            JsonOpts);
    }

    private void ApplyPipelineTuningIfChanged(CodeyBoxOptions opts)
    {
        if (_pipelineTuning is null) return;

        var next = SerializePipelineTuning(opts.PipelineTuning);
        if (string.Equals(_lastPipelineTuning, next, StringComparison.Ordinal))
            return;

        var prev = _lastPipelineTuning;
        try
        {
            _pipelineTuning.Replace(opts.PipelineTuning);
            AgentSuspendResilience.SetMaxRetries(opts.PipelineTuning.AgentSuspendMaxRetries);
            SessionResumeOptions.SetMaxResumeAttempts(opts.PipelineTuning.AgentSessionResumeMaxAttempts);
            _lastPipelineTuning = next;
            AuditLog.ConfigReloaded("PipelineTuning", prev, next);
            _log.LogInformation("Hot-reloaded PipelineTuning: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of PipelineTuning rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private void ApplyBudgetDeferralRecheckIfChanged(CodeyBoxOptions opts)
    {
        if (_budgetDeferralRecheck is null) return;

        var next = SerializeBudgetDeferralRecheck(opts.BudgetDeferralRecheck);
        if (string.Equals(_lastBudgetDeferralRecheck, next, StringComparison.Ordinal))
            return;

        var prev = _lastBudgetDeferralRecheck;
        try
        {
            _budgetDeferralRecheck.Replace(opts.BudgetDeferralRecheck);
            _lastBudgetDeferralRecheck = next;
            AuditLog.ConfigReloaded("BudgetDeferralRecheck", prev, next);
            _log.LogInformation("Hot-reloaded BudgetDeferralRecheck: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of BudgetDeferralRecheck rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private static string SerializePipelineTuning(PipelineTuningOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.MaxPlanReviewIterations,
                DefaultQuotaFailurePauseSeconds = opts.DefaultQuotaFailurePause.TotalSeconds,
                QuotaExhaustionFallbackTtlSeconds = opts.QuotaExhaustionFallbackTtl.TotalSeconds,
                MaxParsedQuotaResetWindowSeconds = opts.MaxParsedQuotaResetWindow.TotalSeconds,
                opts.MergeSandboxStagingRestoreAttempts,
                opts.MaxQuestionsPerWorkItem,
                opts.AgentSuspendMaxRetries,
                opts.AgentSessionResumeMaxAttempts,
                opts.AutoMergeRaceRecoveryMaxAttempts,
                opts.EnableSandboxReuse,
                opts.MaxSandboxReuses,
                MaxSandboxLifetimeSeconds = opts.MaxSandboxLifetime.TotalSeconds,
                opts.SandboxPressureThreshold,
                opts.AuditShortCircuitEnabled,
                AuditorIdleTimeoutSeconds = opts.AuditorIdleTimeout.TotalSeconds,
                opts.BlockRedundantDotnetBuildTestInAuditSandbox,
                CSharpTestPassAuditorIdleTimeoutSeconds = opts.CSharpTestPassAuditorIdleTimeout?.TotalSeconds,
                CSharpTestPassBlameHangTimeoutSeconds = opts.CSharpTestPassBlameHangTimeout?.TotalSeconds,
            },
            JsonOpts);

    private static SmokeOptions ToSmokeOptions(SmokeConfig opts, int cacheTtlMinutes) => new()
    {
        Enabled = opts.Enabled,
        CacheTtlMinutes = cacheTtlMinutes,
        StartupTimeoutSeconds = opts.StartupTimeoutSeconds,
    };

    private void ApplyTransitionHealthIfChanged(CodeyBoxOptions opts)
    {
        if (_transitionHealth is null) return;

        var next = SerializeTransitionHealth(opts.TransitionHealth);
        if (string.Equals(_lastTransitionHealth, next, StringComparison.Ordinal))
            return;

        var prev = _lastTransitionHealth;
        try
        {
            var nextOptions = TransitionHealthConfigMapper.ToOptions(
                opts.TransitionHealth.Enabled,
                opts.TransitionHealth.WindowHours,
                opts.TransitionHealth.MaxTransitions);
            _transitionHealth.Replace(nextOptions);
            _lastTransitionHealth = next;
            AuditLog.ConfigReloaded("TransitionHealth", prev, next);
            _log.LogInformation("Hot-reloaded TransitionHealth: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of TransitionHealth rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private static string SerializeTransitionHealth(TransitionHealthConfig opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.Enabled,
                opts.WindowHours,
                opts.MaxTransitions,
            },
            JsonOpts);

    private static string SerializeSmoke(SmokeConfig opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.Enabled,
                opts.StartupTimeoutSeconds,
            },
            JsonOpts);

    private static string SerializeBudgetDeferralRecheck(BudgetDeferralRecheckOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                PausedProjectRecheckSeconds = opts.PausedProjectRecheck.TotalSeconds,
                HourlyLimitRecheckSeconds = opts.HourlyLimitRecheck.TotalSeconds,
                DailyLimitRecheckSeconds = opts.DailyLimitRecheck.TotalSeconds,
                ConcurrentLimitRecheckSeconds = opts.ConcurrentLimitRecheck.TotalSeconds,
                RefactorExclusivityRecheckSeconds = opts.RefactorExclusivityRecheck.TotalSeconds,
            },
            JsonOpts);
}
