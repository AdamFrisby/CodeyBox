using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
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
/// <item><c>CodeyBox:WorkerPool</c> hot-reloadable trio
///   (<c>MaxConcurrentWorkers</c> →
///   <see cref="OrchestratorService.ApplyWorkerPoolReload"/>,
///   <c>MaxConcurrentSandboxes</c> →
///   <see cref="SandboxAdmissionControlledProvider.ApplyMaxConcurrentSandboxesReload"/>,
///   <c>MinSpawnInterval</c> →
///   <see cref="OrchestratorService.ApplyMinSpawnIntervalReload"/>).
///   The remaining <c>WorkerPool</c> fields are captured at startup — see
///   <see cref="WorkerPoolHotReloadPolicy"/> for the exact split.</item>
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
/// <item><c>CodeyBox:ToolchainFaults</c> → <see cref="ToolchainFaultSnapshot.Replace"/>
/// so a new language signature takes effect without restart or code change.</item>
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
///
/// <para>
/// Silence after an edit means both "applied" and "ignored", so every reload
/// also reports the two ignored shapes: keys that changed but require a
/// restart (<see cref="ConfigReloadClassification.DiffRestartRequiredKeys"/>,
/// emitted as <c>config_requires_restart</c> naming the keys) and keys that
/// bind to no option at all (re-inspected against the live raw configuration
/// and emitted as <c>config_unbound_keys</c>). Per-key effectiveness is
/// queryable without reading source through
/// <see cref="ConfigReloadClassification.TryGetEffect"/> and the
/// <c>GET /config/reload-effects</c> endpoint.
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
    private readonly NonDeterministicTestEscalationSnapshot? _flakeEscalation;
    private readonly BudgetDeferralRecheckSnapshot? _budgetDeferralRecheck;
    private readonly AgentCircuitBreakerSnapshot? _circuitBreaker;
    private readonly QuotaRouterOptions? _quotaRouterOptions;
    private readonly IInVmSmokeCoveragePolicy? _coverage;
    private readonly SmokeOptionsSnapshot? _smokeOptions;
    private readonly TestFailureAttributionOptionsSnapshot? _testFailureAttribution;
    private readonly ToolchainFaultSnapshot? _toolchainFaults;
    private readonly TransitionHealthOptionsSnapshot? _transitionHealth;
    private readonly IAgentPauseController? _pauses;
    private readonly IAgentRegistry? _agents;
    private readonly ISandboxHostPoolSnapshot? _hostPoolSnapshot;
    private readonly SandboxAdmissionControlledProvider? _sandboxAdmission;
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
    private string _lastFlakeEscalation = "";
    private string _lastSanitizer = "";
    private string _lastQuotaRouter = "";
    private string _lastPipelineTuning = "";
    private string _lastBudgetDeferralRecheck = "";
    private string _lastCircuitBreaker = "";
    private string _lastSmoke = "";
    private string _lastTestFailureAttribution = "";
    private string _lastToolchainFaults = "";
    private string _lastTransitionHealth = "";
    private string _lastAgentPauses = "";
    private string _lastNetworkTolerance = "";
    private string _lastHostPoolCapacity = "";

    // Last-reported restart-required values (partial copy of the compared
    // fields only — all scalars/strings, so later mutation of the monitored
    // instance cannot corrupt the baseline). A change here is observed but
    // has no effect until restart; it is reported, not applied.
    private CodeyBoxOptions _restartBaseline = new();

    // Raw-configuration handle for the reload-time unbound-key check. Null in
    // tests that only exercise the typed-options path, which skips the check.
    private readonly IConfiguration? _configuration;

    // Unbound keys already reported, so each new key is named once.
    private HashSet<string> _lastUnboundKeys = new(StringComparer.OrdinalIgnoreCase);

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
        NonDeterministicTestEscalationSnapshot? flakeEscalation = null,
        BudgetDeferralRecheckSnapshot? budgetDeferralRecheck = null,
        AgentCircuitBreakerSnapshot? circuitBreaker = null,
        QuotaRouterOptions? quotaRouterOptions = null,
        IInVmSmokeCoveragePolicy? coverage = null,
        SmokeOptionsSnapshot? smokeOptions = null,
        TestFailureAttributionOptionsSnapshot? testFailureAttribution = null,
        ToolchainFaultSnapshot? toolchainFaults = null,
        IAgentPauseController? pauses = null,
        IAgentRegistry? agents = null,
        TransitionHealthOptionsSnapshot? transitionHealth = null,
        ISandboxHostPoolSnapshot? hostPoolSnapshot = null,
        SandboxAdmissionControlledProvider? sandboxAdmission = null,
        IConfiguration? configuration = null)
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
        _flakeEscalation = flakeEscalation;
        _pipelineTuning = pipelineTuning;
        _budgetDeferralRecheck = budgetDeferralRecheck;
        _circuitBreaker = circuitBreaker;
        _quotaRouterOptions = quotaRouterOptions;
        _coverage = coverage;
        _smokeOptions = smokeOptions;
        _testFailureAttribution = testFailureAttribution;
        _toolchainFaults = toolchainFaults;
        _transitionHealth = transitionHealth;
        _pauses = pauses;
        _agents = agents;
        _hostPoolSnapshot = hostPoolSnapshot;
        _sandboxAdmission = sandboxAdmission;
        _configuration = configuration;
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
        _lastFlakeEscalation = SerializeFlakeEscalation(initial.NonDeterministicTestEscalation);
        _lastSanitizer = SerializeSanitizer(initial.ClaudeThinkingBlockSanitizer);
        _lastQuotaRouter = SerializeQuotaRouter(initial.QuotaRouter);
        _lastPipelineTuning = SerializePipelineTuning(initial.PipelineTuning);
        _lastBudgetDeferralRecheck = SerializeBudgetDeferralRecheck(initial.BudgetDeferralRecheck);
        _lastCircuitBreaker = SerializeCircuitBreaker(initial.AgentCircuitBreaker);
        _lastSmoke = SerializeSmoke(initial.Smoke);
        _lastTestFailureAttribution = SerializeTestFailureAttribution(initial.TestFailureAttribution);
        _lastToolchainFaults = SerializeToolchainFaults(initial.ToolchainFaults);
        _lastTransitionHealth = SerializeTransitionHealth(initial.TransitionHealth);
        _lastAgentPauses = SerializeAgentPauses(initial.AgentPauses);
        _lastHostPoolCapacity = SerializeHostPoolCapacity(initial);
        _restartBaseline = CaptureRestartBaseline(initial);
        _lastUnboundKeys = InspectUnboundKeys();

        AgentSuspendResilience.SetMaxRetries(initial.PipelineTuning.AgentSuspendMaxRetries);
        SessionResumeOptions.SetMaxResumeAttempts(initial.PipelineTuning.AgentSessionResumeMaxAttempts);
        OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(initial.QuotaRouter.OauthRefreshMaxBodyBytes);
        OauthCredentialRefresherBounds.SetMaxCliOutputChars(initial.QuotaRouter.OauthRefreshMaxCliOutputChars);
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
            ReportRestartRequiredIfChanged(opts);
            ReportUnboundIfChanged();
            LogRemoteHostCapacityIfChanged(opts);
            ApplyConcurrencyIfChanged(opts);
            ApplySmokeIfChanged(opts);
            ApplyTestFailureAttributionIfChanged(opts);
            ApplyToolchainFaultsIfChanged(opts);
            ApplyTransitionHealthIfChanged(opts);
            ApplyRouterIfChanged(opts);
            ApplyBurnIfChanged(opts);
            ApplyPricingIfChanged(opts);
            ApplyBudgetsIfChanged(opts);
            ApplyDefaultsIfChanged(opts);
            ApplyNetworkToleranceIfChanged(opts);
            ApplyAgentPausesIfChanged(opts);
            ApplyIncrementalRebaseIfChanged(opts);
            ApplyFlakeEscalationIfChanged(opts);
            ApplySanitizerIfChanged(opts);
            ApplyQuotaRouterIfChanged(opts);
            ApplyPipelineTuningIfChanged(opts);
            ApplyBudgetDeferralRecheckIfChanged(opts);
            ApplyCircuitBreakerIfChanged(opts);
        }
    }

    /// <summary>
    /// Names the restart-required keys whose effective value changed against
    /// the last-reported baseline. Silence after an edit currently means both
    /// "applied" and "ignored" — this report is the difference: the keys are
    /// observed but the running process keeps the prior values until restart.
    /// </summary>
    private void ReportRestartRequiredIfChanged(CodeyBoxOptions opts)
    {
        try
        {
            var changed = ConfigReloadClassification.DiffRestartRequiredKeys(_restartBaseline, opts);
            _restartBaseline = CaptureRestartBaseline(opts);
            if (changed.Count == 0)
                return;

            AuditLog.ConfigRequiresRestart(changed);
            _log.LogWarning(
                "Configuration change requires a restart to take effect and was NOT applied: {Keys}. " +
                "The running process keeps the prior values until restarted.",
                string.Join(", ", changed));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not evaluate restart-required configuration changes; keeping prior baseline.");
        }
    }

    /// <summary>
    /// Re-runs the unbound-key inspection against the live raw configuration
    /// and names keys that appeared since the last check. The startup
    /// validator refuses to start on such keys; surfacing them here gives the
    /// same signal at reload time, where an operator editing a live system
    /// will actually see it.
    /// </summary>
    private void ReportUnboundIfChanged()
    {
        if (_configuration is null)
            return;

        try
        {
            var current = InspectUnboundKeys();
            var added = current
                .Where(path => !_lastUnboundKeys.Contains(path))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            _lastUnboundKeys = current;
            if (added.Length == 0)
                return;

            AuditLog.ConfigUnboundKeys(added);
            _log.LogWarning(
                "Configuration reload contains keys that bind to no option and will be ignored: {Keys}. " +
                "Fix or remove these keys; a restart with strict validation would refuse to start.",
                string.Join(", ", added));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not evaluate unbound configuration keys; keeping prior baseline.");
        }
    }

    private HashSet<string> InspectUnboundKeys()
    {
        if (_configuration is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return UnboundConfigKeyHostedValidator.Inspect(_configuration)
            .Select(static report => report.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Partial copy of the restart-required surface only. Every copied member
    /// is a value type or an immutable string (nested options are re-created,
    /// never aliased), so later mutation of the monitored instance cannot
    /// corrupt the baseline the next reload diffs against.
    /// </summary>
    private static CodeyBoxOptions CaptureRestartBaseline(CodeyBoxOptions opts) => new()
    {
        SandboxProvider = opts.SandboxProvider,
        StateDatabasePath = opts.StateDatabasePath,
        GitRootDirectory = opts.GitRootDirectory,
        GitCommandMaxOutputBytes = opts.GitCommandMaxOutputBytes,
        AgentStreams = new AgentStreamsOptions { Path = opts.AgentStreams.Path },
        EnableSharedUpstreamMirror = opts.EnableSharedUpstreamMirror,
        SharedUpstreamMirrorDirectory = opts.SharedUpstreamMirrorDirectory,
        Incus = new IncusSandboxConfig
        {
            ProjectName = opts.Incus.ProjectName,
            StagingDirectory = opts.Incus.StagingDirectory,
        },
        WorkerPool = new WorkerPoolOptions
        {
            DispatchGateAcquisitionBackoff = opts.WorkerPool.DispatchGateAcquisitionBackoff,
            MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = opts.WorkerPool.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation,
            NoProgressBackoffBase = opts.WorkerPool.NoProgressBackoffBase,
            NoProgressBackoffMax = opts.WorkerPool.NoProgressBackoffMax,
            MaxNoProgressRedispatches = opts.WorkerPool.MaxNoProgressRedispatches,
        },
    };

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

        var prev = _lastQuotaRouter;
        try
        {
            var next = SerializeQuotaRouter(opts.QuotaRouter);
            if (string.Equals(_lastQuotaRouter, next, StringComparison.Ordinal))
                return;

            // Mutate the shared singleton in place. The router holds the same
            // QuotaRouterOptions reference and reads its properties on every
            // gate decision, so the new values take effect on the next pickup
            // attempt without a process restart. Active provider cache TTL is
            // constructor-bound; paused-probe cadence and resilience knobs are
            // read through live delegates and update here.
            QuotaRouterConfigMapper.ApplyHotReload(_quotaRouterOptions, opts.QuotaRouter);
            OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(opts.QuotaRouter.OauthRefreshMaxBodyBytes);
            OauthCredentialRefresherBounds.SetMaxCliOutputChars(opts.QuotaRouter.OauthRefreshMaxCliOutputChars);

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

    private void ApplyFlakeEscalationIfChanged(CodeyBoxOptions opts)
    {
        if (_flakeEscalation is null) return;

        var next = SerializeFlakeEscalation(opts.NonDeterministicTestEscalation);
        if (string.Equals(_lastFlakeEscalation, next, StringComparison.Ordinal))
            return;

        var prev = _lastFlakeEscalation;
        try
        {
            _flakeEscalation.Replace(new NonDeterministicTestEscalationOptions
            {
                Enabled = opts.NonDeterministicTestEscalation.Enabled,
                MaxTestsPerChild = opts.NonDeterministicTestEscalation.MaxTestsPerChild,
            });
            _lastFlakeEscalation = next;
            AuditLog.ConfigReloaded("NonDeterministicTestEscalation", prev, next);
            _log.LogInformation("Hot-reloaded NonDeterministicTestEscalation: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of NonDeterministicTestEscalation rejected; keeping prior view ({Prev}). " +
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
            var resolvedWorkers = ResolveEffectiveMaxConcurrentWorkers(opts.WorkerPool, opts.Concurrency);
            var resolvedSandboxes = ResolveEffectiveMaxConcurrentSandboxes(opts.WorkerPool, resolvedWorkers);
            var resolvedInterval = opts.WorkerPool.MinSpawnInterval;
            // Validate the whole trio before touching any live gate so a
            // rejected candidate keeps every prior value in effect instead of
            // committing workers while rejecting sandboxes or pacing.
            OrchestratorOptionsFactory.ValidateWorkerPoolReload(
                resolvedWorkers,
                resolvedSandboxes,
                resolvedInterval);
            _orchestrator.ApplyWorkerPoolReload(resolvedWorkers);
            _sandboxAdmission?.ApplyMaxConcurrentSandboxesReload(resolvedSandboxes);
            _orchestrator.ApplyMinSpawnIntervalReload(resolvedInterval);
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

    private static int ResolveEffectiveMaxConcurrentSandboxes(WorkerPoolOptions workerPool, int maxConcurrentWorkers) =>
        workerPool.MaxConcurrentSandboxes
        ?? OrchestratorOptionsFactory.DeriveDefaultMaxConcurrentSandboxes(maxConcurrentWorkers);

    private void LogRemoteHostCapacityIfChanged(CodeyBoxOptions opts)
    {
        if (_hostPoolSnapshot is null) return;

        string next;
        try
        {
            next = SerializeHostPoolCapacity(opts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload remote host capacity check failed before logging fan-out cap state. " +
                "Fix the configuration error and re-save to retry.");
            return;
        }

        if (string.Equals(_lastHostPoolCapacity, next, StringComparison.Ordinal))
            return;

        var prev = _lastHostPoolCapacity;
        try
        {
            var maxWorkers = ResolveEffectiveMaxConcurrentWorkers(opts.WorkerPool, opts.Concurrency);
            var maxSandboxes = ResolveEffectiveMaxConcurrentSandboxes(opts.WorkerPool, maxWorkers);
            RemoteHostPoolCapacityLogger.Log(
                _hostPoolSnapshot,
                new OrchestratorOptions
                {
                    MaxConcurrentWorkers = maxWorkers,
                    MaxConcurrentSandboxes = maxSandboxes,
                },
                _log);
            _lastHostPoolCapacity = next;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload remote host capacity check rejected; keeping prior fan-out capacity snapshot ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
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
            var catalog = AgentClassesConfigBuilder.Build(opts.AgentClasses, opts.AgentInstances, _log, opts.Copilot.Providers);
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
    /// Hot-reload fingerprint for the worker-pool block. Covers exactly the
    /// <see cref="WorkerPoolHotReloadPolicy.HotReloadableFields"/> set — an
    /// edit to any other <c>WorkerPool</c> field does not trigger a reload
    /// (those fields are startup-captured and require a restart).
    /// Internal for the fingerprint-coverage test, which proves every
    /// hot-reloadable field is observed here.
    /// </summary>
    internal static string SerializeWorkerPool(WorkerPoolOptions opts, int? legacyConcurrency) =>
        JsonSerializer.Serialize(
            new
            {
                opts.MaxConcurrentWorkers,
                opts.MaxConcurrentSandboxes,
                opts.MinSpawnInterval,
                LegacyConcurrency = legacyConcurrency,
            },
            JsonOpts);

    private string SerializeHostPoolCapacity(CodeyBoxOptions opts)
    {
        if (_hostPoolSnapshot is null)
            return "";

        var maxWorkers = ResolveEffectiveMaxConcurrentWorkers(opts.WorkerPool, opts.Concurrency);
        var maxSandboxes = ResolveEffectiveMaxConcurrentSandboxes(opts.WorkerPool, maxWorkers);
        var rows = _hostPoolSnapshot.SnapshotHostPool()
            .OrderBy(static row => row.HostId, StringComparer.Ordinal)
            .Select(static row => new
            {
                row.HostId,
                row.Capacity,
                row.Cordoned,
                row.ConfiguredHealthy,
                AllowedNetworkProfiles = row.AllowedNetworkProfiles
                    .OrderBy(static profile => profile, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                MaxConcurrentWorkers = maxWorkers,
                MaxConcurrentSandboxes = maxSandboxes,
                Hosts = rows,
            },
            JsonOpts);
    }

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

    private static string SerializeFlakeEscalation(NonDeterministicTestEscalationOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.Enabled,
                opts.MaxTestsPerChild,
            },
            JsonOpts);

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
                Pools = mapped.Pools
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => new
                    {
                        Kind = kv.Value.Kind.ToString(),
                        kv.Value.BalanceUnit,
                        kv.Value.ReservationEstimate,
                    }),
                FloorByPool = mapped.FloorByPool
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => new
                    {
                        kv.Value.MinQuotaPct,
                        kv.Value.StartFloorPct,
                        kv.Value.EndFloorPct,
                        RampWindowSeconds = kv.Value.RampWindow is { } poolRamp
                            ? checked((int)poolRamp.TotalSeconds)
                            : (int?)null,
                        kv.Value.MinBalance,
                    }),
                opts.QuotaRecheckIntervalSeconds,
                opts.QuotaRecoveryProbeIntervalSeconds,
                opts.MaxQuotaRecoveryProbeEligibilityScan,
                opts.PausedQuotaCacheTtlSeconds,
                opts.PausedProbeMaxStalenessSeconds,
                opts.PausedQuotaMaxCacheEntries,
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
                opts.OauthRefreshMaxBodyBytes,
                opts.OauthRefreshMaxCliOutputChars,
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

    private void ApplyCircuitBreakerIfChanged(CodeyBoxOptions opts)
    {
        if (_circuitBreaker is null) return;

        var next = SerializeCircuitBreaker(opts.AgentCircuitBreaker);
        if (string.Equals(_lastCircuitBreaker, next, StringComparison.Ordinal))
            return;

        var prev = _lastCircuitBreaker;
        try
        {
            _circuitBreaker.Replace(opts.AgentCircuitBreaker);
            _lastCircuitBreaker = next;
            AuditLog.ConfigReloaded("AgentCircuitBreaker", prev, next);
            _log.LogInformation("Hot-reloaded AgentCircuitBreaker: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentCircuitBreaker rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private static string SerializeCircuitBreaker(AgentCircuitBreakerOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.Enabled,
                opts.FailureThreshold,
                WindowSeconds = opts.Window.TotalSeconds,
                CooldownSeconds = opts.Cooldown.TotalSeconds,
                opts.HalfOpenTrials,
                PerAgent = opts.PerAgent
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new
                    {
                        Agent = kv.Key,
                        kv.Value.FailureThreshold,
                        WindowSeconds = kv.Value.Window?.TotalSeconds,
                        CooldownSeconds = kv.Value.Cooldown?.TotalSeconds,
                        kv.Value.HalfOpenTrials,
                    }),
            },
            JsonOpts);

    /// <summary>
    /// Hot-reload fingerprint for the pipeline-tuning block. Covers every
    /// <see cref="PipelineTuningHotReloadPolicy.HotReloadableFields"/> entry:
    /// a field missing here would silently behave as restart-required however
    /// the policy classifies it, so a test mutates each property solo and
    /// requires the fingerprint to move.
    /// </summary>
    internal static string SerializePipelineTuning(PipelineTuningOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.MaxPlanReviewIterations,
                opts.PlanTaskBindingCoverageRatio,
                DefaultQuotaFailurePauseSeconds = opts.DefaultQuotaFailurePause.TotalSeconds,
                DefaultRateLimitPauseSeconds = opts.DefaultRateLimitPause.TotalSeconds,
                QuotaExhaustionFallbackTtlSeconds = opts.QuotaExhaustionFallbackTtl.TotalSeconds,
                MaxParsedQuotaResetWindowSeconds = opts.MaxParsedQuotaResetWindow.TotalSeconds,
                opts.MergeSandboxStagingRestoreAttempts,
                opts.MaxQuestionsPerWorkItem,
                opts.AgentSuspendMaxRetries,
                opts.AgentSessionResumeMaxAttempts,
                opts.MaxRetainedAgentTurnSandboxes,
                opts.AutoMergeRaceRecoveryMaxAttempts,
                opts.EnableSandboxReuse,
                opts.MaxSandboxReuses,
                MaxSandboxLifetimeSeconds = opts.MaxSandboxLifetime.TotalSeconds,
                opts.SandboxPressureThreshold,
                SandboxPermitWaitWarningThresholdSeconds = opts.SandboxPermitWaitWarningThreshold.TotalSeconds,
                opts.AuditShortCircuitEnabled,
                opts.EmptyReworkEscalationRetries,
                AuditorIdleTimeoutSeconds = opts.AuditorIdleTimeout.TotalSeconds,
                AuditorAbsoluteTimeoutSeconds = opts.AuditorAbsoluteTimeout.TotalSeconds,
                opts.BlockRedundantDotnetBuildTestInAuditSandbox,
                CSharpTestPassAuditorIdleTimeoutSeconds = opts.CSharpTestPassAuditorIdleTimeout?.TotalSeconds,
                CSharpTestPassBlameHangTimeoutSeconds = opts.CSharpTestPassBlameHangTimeout?.TotalSeconds,
                opts.EnableHandoffSeeding,
                opts.SelfReviewChecklistEnabled,
                opts.PlannedItemAuditRebalanceEnabled,
                PlannedItemAdvisoryAuditors = opts.PlannedItemAdvisoryAuditors.ToArray(),
            },
            JsonOpts);

    private static SmokeOptions ToSmokeOptions(SmokeConfig opts, int cacheTtlMinutes) => new()
    {
        Enabled = opts.Enabled,
        CacheTtlMinutes = cacheTtlMinutes,
        StartupTimeoutSeconds = opts.StartupTimeoutSeconds,
    };

    private void ApplyTestFailureAttributionIfChanged(CodeyBoxOptions opts)
    {
        if (_testFailureAttribution is null) return;

        var next = SerializeTestFailureAttribution(opts.TestFailureAttribution);
        if (string.Equals(_lastTestFailureAttribution, next, StringComparison.Ordinal))
            return;

        var prev = _lastTestFailureAttribution;
        try
        {
            _testFailureAttribution.Replace(opts.TestFailureAttribution);
            _lastTestFailureAttribution = next;
            AuditLog.ConfigReloaded("TestFailureAttribution", prev, next);
            _log.LogInformation("Hot-reloaded TestFailureAttribution: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of TestFailureAttribution rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

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

    private static string SerializeTestFailureAttribution(TestFailureAttributionOptions opts) =>
        JsonSerializer.Serialize(
            new
            {
                opts.Enabled,
            },
            JsonOpts);

    private void ApplyToolchainFaultsIfChanged(CodeyBoxOptions opts)
    {
        if (_toolchainFaults is null) return;

        var next = SerializeToolchainFaults(opts.ToolchainFaults);
        if (string.Equals(_lastToolchainFaults, next, StringComparison.Ordinal))
            return;

        var prev = _lastToolchainFaults;
        try
        {
            _toolchainFaults.Replace(opts.ToolchainFaults);
            _lastToolchainFaults = next;
            AuditLog.ConfigReloaded("ToolchainFaults", prev, next);
            _log.LogInformation("Hot-reloaded ToolchainFaults: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of ToolchainFaults rejected; keeping prior view ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
        }
    }

    private static string SerializeToolchainFaults(Dictionary<string, ToolchainFaultSignatureOptions?> signatures) =>
        JsonSerializer.Serialize(
            (signatures ?? new Dictionary<string, ToolchainFaultSignatureOptions?>())
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    kv => kv.Key,
                    kv => SerializeToolchainFaultSignature(kv.Value),
                    StringComparer.OrdinalIgnoreCase),
            JsonOpts);

    private static object? SerializeToolchainFaultSignature(ToolchainFaultSignatureOptions? value) =>
        value is null
            ? null
            : new
            {
                value.FaultClass,
                Disposition = value.Disposition.ToString(),
                value.ExitCodes,
                value.ExitCodeAbove,
                value.StdoutContains,
                value.StderrContains,
                value.OutputContains,
                value.StdoutRegex,
                value.StderrRegex,
                value.OutputRegex,
            };

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
