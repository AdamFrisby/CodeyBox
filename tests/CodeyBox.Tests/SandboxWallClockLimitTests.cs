using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Guards the sandbox wall-clock backstop against being set below the time a single unit of agent work
/// is legitimately allowed to take.
/// </summary>
public sealed class SandboxWallClockLimitTests
{
    /// <summary>
    /// The shipped default for <c>Defaults.Audit.PerIterationTimeoutMinutes</c>. One audit iteration is
    /// permitted this long, and it runs inside the sandbox — so the sandbox must outlive it.
    /// </summary>
    private static readonly TimeSpan AuditPerIterationBudget = TimeSpan.FromMinutes(90);

    [Fact]
    public void DefaultWallClock_OutlivesASingleAuditIteration()
    {
        // Regression: this was 60 minutes, i.e. SHORTER than one audit iteration's own budget.
        // IncusSandbox clamps its 6h ExecTimeout down to the wall clock, so the sandbox was destroyed
        // 30 minutes before the work inside it was even allowed to finish. Agent runs that were
        // demonstrably progressing died as "Incus CLI operation [exec] exceeded its 3600-second
        // deadline", which reads like a hang and is not one.
        var wallClock = SandboxResourceLimits.Default.WallClock;

        Assert.NotNull(wallClock);
        Assert.True(
            wallClock >= AuditPerIterationBudget,
            $"Sandbox wall clock ({wallClock}) must outlive one audit iteration ({AuditPerIterationBudget}); "
            + "otherwise long-but-healthy agent work is killed mid-flight and reported as a timeout.");
    }

    [Fact]
    public void DefaultWallClock_IsABackstopNotAStallDetector()
    {
        // A wall clock cannot tell a stalled run from a slow one — that is WorkerProgressWatchdog's job,
        // which measures absence of progress. So this value should be generous enough that hitting it
        // means something is genuinely wrong, not merely that the work was large.
        Assert.True(
            SandboxResourceLimits.Default.WallClock >= TimeSpan.FromHours(2),
            "Wall clock is a backstop; tighten stall detection via the progress watchdog instead.");
    }
}
