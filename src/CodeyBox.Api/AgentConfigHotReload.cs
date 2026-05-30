using System.Text.Json;
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
/// Five blocks are hot-reloadable here:
/// <list type="bullet">
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
    private readonly IOptionsMonitor<CodeyBoxOptions> _monitor;
    private readonly OrchestratorService _orchestrator;
    private readonly AgentClassRouter _router;
    private readonly AgentBurnEstimator _burnEstimator;
    private readonly IAgentBudgetConfigReloadable? _budgetReloader;
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly ClaudeThinkingBlockSanitizerConfig? _sanitizerConfig;
    private readonly AgentCostCalculator? _costCalculator;
    private readonly AgentPricingState? _pricingState;
    private readonly IncrementalRebaseSnapshot? _incrementalRebase;
    private readonly ILogger<AgentConfigHotReload> _log;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;

    // Last-applied serialised forms; used as both the equality check and the
    // value reported back to AuditLog.ConfigReloaded.
    private string _lastConcurrency = "";
    private string _lastBurn = "";
    private string _lastRouter = "";
    private string _lastPricing = "";
    private string _lastBudgets = "";
    private string _lastDefaults = "";
    private string _lastIncrementalRebase = "";
    private string _lastSanitizer = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    internal AgentConfigHotReload(
        IOptionsMonitor<CodeyBoxOptions> monitor,
        OrchestratorService orchestrator,
        AgentClassRouter router,
        AgentBurnEstimator burnEstimator,
        ILogger<AgentConfigHotReload> log,
        AgentDefaultsSnapshot? defaults = null,
        ClaudeThinkingBlockSanitizerConfig? sanitizerConfig = null,
        AgentCostCalculator? costCalculator = null,
        AgentPricingState? pricingState = null,
        IAgentBudgetConfigReloadable? budgetReloader = null,
        IncrementalRebaseSnapshot? incrementalRebase = null)
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
        _costCalculator = costCalculator;
        _pricingState = pricingState;
        _incrementalRebase = incrementalRebase;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Capture initial serialised state so the first OnChange after startup
        // only fires audit entries for fields that actually changed against
        // the snapshot the router / orchestrator were built with.
        var initial = _monitor.CurrentValue;
        _lastConcurrency = SerializeConcurrency(initial.AgentConcurrency);
        _lastBurn = SerializeBurn(initial.AgentBurnEstimator);
        _lastRouter = SerializeRouterInputs(initial.AgentClasses, initial.AgentScoreModifiers);
        _lastPricing = SerializePricing(initial.AgentPricing);
        _lastBudgets = SerializeBudgets(initial.AgentBudgets);
        _lastDefaults = SerializeDefaults(initial.AgentDefaults);
        _lastIncrementalRebase = SerializeIncrementalRebase(initial.IncrementalRebase);
        _lastSanitizer = SerializeSanitizer(initial.ClaudeThinkingBlockSanitizer);

        _subscription = _monitor.OnChange(OnConfigChanged);
        _log.LogInformation(
            "AgentConfigHotReload subscribed to CodeyBoxOptions: classes={ClassesLen} concurrency={ConcurrencyLen} burn={BurnLen} pricing={PricingLen} defaults={DefaultsLen} sanitizer={SanitizerLen}",
            _lastRouter.Length, _lastConcurrency.Length, _lastBurn.Length, _lastPricing.Length, _lastDefaults.Length, _lastSanitizer.Length);
        return Task.CompletedTask;
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
            ApplyConcurrencyIfChanged(opts);
            ApplyRouterIfChanged(opts);
            ApplyBurnIfChanged(opts);
            ApplyPricingIfChanged(opts);
            ApplyBudgetsIfChanged(opts);
            ApplyDefaultsIfChanged(opts);
            ApplyIncrementalRebaseIfChanged(opts);
            ApplySanitizerIfChanged(opts);
        }
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
        var next = SerializeRouterInputs(opts.AgentClasses, opts.AgentScoreModifiers);
        if (string.Equals(_lastRouter, next, StringComparison.Ordinal))
            return;

        var prev = _lastRouter;
        try
        {
            var catalog = AgentClassesConfigBuilder.Build(opts.AgentClasses, _log);
            var todModifiers = AgentClassesConfigBuilder.BuildTodModifiers(opts.AgentScoreModifiers, _log);
            _router.ApplyConfigReload(catalog, todModifiers);
            _lastRouter = next;
            AuditLog.ConfigReloaded("AgentClasses", prev, next);
            _log.LogInformation("Hot-reloaded AgentClasses+AgentScoreModifiers: {OldValue} → {NewValue}", prev, next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Hot-reload of AgentClasses rejected; keeping prior router catalog ({Prev}). " +
                "Fix the configuration error and re-save to retry.",
                prev);
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
        AgentScoreModifiersOptions modifiers) =>
        JsonSerializer.Serialize(
            new
            {
                Classes = classes
                    .Select(c => new
                    {
                        c.Id,
                        c.DisplayName,
                        Members = c.Members
                            .Select(m => new
                            {
                                m.Agent,
                                m.Billing,
                                m.ModelId,
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

    private static string SerializeDefaults(Dictionary<string, string?> defaults) =>
        JsonSerializer.Serialize(
            defaults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            JsonOpts);

    private static string SerializeIncrementalRebase(IncrementalRebaseOptions opts) =>
        JsonSerializer.Serialize(new { opts.Enabled }, JsonOpts);

    private static string SerializeSanitizer(ClaudeThinkingBlockSanitizerOptions opts) =>
        JsonSerializer.Serialize(new { opts.Enabled }, JsonOpts);
}
