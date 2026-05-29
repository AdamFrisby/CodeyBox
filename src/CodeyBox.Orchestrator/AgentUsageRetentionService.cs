using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hourly background sweep that prunes <c>agent_usage_events</c> rows older than
/// the configured retention window. Without this the table grows unbounded —
/// budget windows only ever read recent rows, so old events are dead weight.
/// The retention day count is read live so operator edits to
/// <c>CodeyBox:AgentBudgets:RetentionDays</c> take effect without a restart.
/// <para>
/// Retention NEVER deletes an event still inside an active budget window. A
/// short <c>RetentionDays</c> (e.g. 7) combined with a Weekly/Monthly window
/// could otherwise prune events from earlier in the current week/month, making
/// the budget SUM undercount spend, overstating percentRemaining, and
/// fail-opening a configured cap. The prune cutoff is clamped to the earliest
/// start of any configured budget window so window-relevant rows always survive.
/// </para>
/// </summary>
public sealed class AgentUsageRetentionService : BackgroundService
{
    private readonly IAgentUsageStore _store;
    private readonly Func<AgentBudgetOptions> _options;
    private readonly ILogger<AgentUsageRetentionService> _log;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;

    public AgentUsageRetentionService(
        IAgentUsageStore store,
        Func<AgentBudgetOptions> options,
        ILogger<AgentUsageRetentionService> log,
        TimeSpan? interval = null,
        TimeProvider? time = null)
    {
        _store = store;
        _options = options;
        _log = log;
        _interval = interval ?? TimeSpan.FromHours(1);
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            var opts = _options();
            var days = opts.RetentionDays;
            if (days <= 0) return; // retention disabled
            var now = _time.GetUtcNow();
            var cutoff = now - TimeSpan.FromDays(days);

            // Never prune events that any active budget window still needs to SUM.
            // If RetentionDays is shorter than an active calendar/rolling span, the
            // retention cutoff would delete in-window rows and fail-open the cap;
            // clamp the cutoff back to the earliest active window start.
            var windowFloor = EarliestActiveWindowStart(opts, now);
            if (windowFloor is { } floor && floor < cutoff)
                cutoff = floor;

            var deleted = await _store.PruneAsync(cutoff, ct);
            if (deleted > 0)
                _log.LogInformation("AgentUsageRetention: pruned {Count} usage events older than {Cutoff:o}", deleted, cutoff);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentUsageRetention: sweep failed");
        }
    }

    /// <summary>
    /// Earliest start instant across every configured budget window, or null when
    /// no windows are configured. Rolling windows start at <c>now - Hours</c>;
    /// Weekly at the current ISO week (Monday 00:00 UTC); Monthly at the 1st of
    /// the current calendar month (00:00 UTC). Mirrors
    /// <see cref="AgentBudgetCalculator"/>'s window bounds.
    /// </summary>
    internal static DateTimeOffset? EarliestActiveWindowStart(AgentBudgetOptions opts, DateTimeOffset now)
    {
        DateTimeOffset? earliest = null;
        foreach (var member in opts.Members.Values)
        {
            foreach (var model in member.Models.Values)
            {
                foreach (var w in model.Windows)
                {
                    DateTimeOffset start;
                    switch (w.Kind)
                    {
                        case BudgetWindowKind.Rolling:
                            // Non-positive Hours is a misconfiguration the calculator
                            // fails closed on; treat it as a wide window here so we
                            // never prune rows it might later need once corrected.
                            var hours = w.Hours is { } h && h > 0 ? h : 0;
                            start = hours > 0 ? now.AddHours(-hours) : now;
                            break;
                        case BudgetWindowKind.Weekly:
                            {
                                var date = now.UtcDateTime.Date;
                                var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
                                start = new DateTimeOffset(date.AddDays(-daysSinceMonday), TimeSpan.Zero);
                                break;
                            }
                        case BudgetWindowKind.Monthly:
                            {
                                var d = now.UtcDateTime;
                                start = new DateTimeOffset(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
                                break;
                            }
                        default:
                            continue;
                    }

                    if (earliest is null || start < earliest)
                        earliest = start;
                }
            }
        }
        return earliest;
    }
}
