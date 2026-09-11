using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Validates the audit-phase budget ordering at configuration load:
/// auditor idle timeout &lt; per-iteration audit timeout &lt; item-stale
/// timeout &lt; sandbox wall clock.
/// </summary>
/// <remarks>
/// <para>
/// Each leg exists so a tighter budget cannot silently kill work a wider
/// budget legitimately allows: an idle window at or above the per-iteration
/// budget would never trip before the iteration died; a per-iteration budget
/// at or above the item-stale window lets the stale watchdog park a healthy
/// long iteration; a stale window at or above the wall clock lets wedged
/// items outlive their sandbox. The measured incident behind this check is a
/// <c>csharp:test-pass</c> run producing no stdout for longer than the idle
/// guard while demonstrably progressing, recorded <c>incomplete</c> because
/// the budgets disagreed about what "stuck" means.
/// </para>
/// <para>
/// Disabled legs (zero idle/item-stale timeouts, null wall clock) are skipped
/// so the "disable this detector" sentinels keep working. Per-agent
/// item-stale overrides are exempt from the wall-clock leg, matching
/// <see cref="WorkerProgressWatchdogOptions.ValidateTimeoutOrdering"/>: a
/// batch-latency agent (e.g. <c>crock</c>) may legitimately outlive the
/// default sandbox backstop.
/// </para>
/// </remarks>
public static class AuditBudgetOrdering
{
    /// <summary>Config path of the auditor idle timeout.</summary>
    public const string AuditorIdleTimeoutPath = "CodeyBox:PipelineTuning:AuditorIdleTimeout";

    /// <summary>Config path of the per-iteration audit timeout.</summary>
    public const string PerIterationTimeoutPath = "CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes";

    /// <summary>Config path of the item-stale timeout.</summary>
    public const string ItemStaleTimeoutPath = "CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout";

    /// <summary>Config path of the sandbox wall-clock backstop.</summary>
    public const string SandboxWallClockPath = "Sandbox:Limits:WallClock";

    /// <summary>
    /// Enforces auditor-idle &lt; per-iteration &lt; item-stale &lt; wall clock.
    /// Throws <see cref="InvalidOperationException"/> naming the offending
    /// config paths when the ordering is violated.
    /// </summary>
    public static void Validate(
        TimeSpan auditorIdleTimeout,
        TimeSpan perIterationTimeout,
        TimeSpan itemStaleTimeout,
        TimeSpan? wallClock,
        string auditorIdlePath = AuditorIdleTimeoutPath,
        string perIterationPath = PerIterationTimeoutPath,
        string itemStalePath = ItemStaleTimeoutPath,
        string wallClockPath = SandboxWallClockPath)
    {
        if (auditorIdleTimeout > TimeSpan.Zero
            && perIterationTimeout > TimeSpan.Zero
            && auditorIdleTimeout >= perIterationTimeout)
        {
            throw new InvalidOperationException(
                $"{auditorIdlePath} ({auditorIdleTimeout}) must be < {perIterationPath} ({perIterationTimeout}) " +
                "so the idle guard trips before the iteration budget expires.");
        }

        if (perIterationTimeout > TimeSpan.Zero
            && itemStaleTimeout > TimeSpan.Zero
            && perIterationTimeout >= itemStaleTimeout)
        {
            throw new InvalidOperationException(
                $"{perIterationPath} ({perIterationTimeout}) must be < {itemStalePath} ({itemStaleTimeout}) " +
                "so a healthy long audit iteration is not parked as stale before its own budget expires.");
        }

        if (itemStaleTimeout > TimeSpan.Zero
            && wallClock is { } wall
            && wall > TimeSpan.Zero
            && itemStaleTimeout >= wall)
        {
            throw new InvalidOperationException(
                $"{itemStalePath} ({itemStaleTimeout}) must be < {wallClockPath} ({wall}) " +
                "so stale recovery fires before the wall-clock backstop destroys the sandbox.");
        }
    }

    /// <summary>
    /// Resolves the effective auditor idle timeout for ordering checks: the
    /// largest enabled idle window wins, because the widest idle guard is the
    /// one that must still fit inside the per-iteration budget.
    /// </summary>
    public static TimeSpan EffectiveIdleTimeout(
        TimeSpan auditorIdleTimeout,
        TimeSpan? cSharpTestPassAuditorIdleTimeout)
    {
        var effective = auditorIdleTimeout;
        if (cSharpTestPassAuditorIdleTimeout is { } testIdle
            && testIdle > effective)
        {
            effective = testIdle;
        }

        return effective;
    }

    /// <summary>
    /// Formats an incomplete-auditor label that names the budget and its
    /// configured value, so a budget-exceeded termination is recorded
    /// distinctly from an auditor that ran and produced findings and never
    /// surfaces as an ordinary infrastructure failure without its cause.
    /// </summary>
    public static string FormatBudgetedAuditorLabel(
        string auditorName,
        string agentKind,
        string budgetPath,
        TimeSpan budgetValue)
        => $"{auditorName} ({agentKind}) [budget {budgetPath}={budgetValue}]";
}
