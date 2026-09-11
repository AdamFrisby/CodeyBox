using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers <see cref="AuditorIdlePolicy.Decide"/> — the pure core behind the
/// liveness-aware auditor idle guard. A quiet run is idle only when there is
/// no output AND no process activity; a run that still holds live sandbox
/// work keeps waiting, and the absolute bound always wins.
/// </summary>
public sealed class AuditorIdlePolicyTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Absolute = TimeSpan.FromMinutes(30);

    [Fact]
    public void QuietBeyondIdle_WithActiveExecs_KeepsWaiting()
    {
        // The incident shape: a test suite emitting nothing for longer than
        // the idle window while its process is demonstrably still running
        // must not be classified as idle.
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromMinutes(6),
            idleTimeout: Idle,
            totalElapsed: TimeSpan.FromMinutes(6),
            absoluteTimeout: Absolute,
            auditorTaskCompleted: false,
            hasActiveExecs: true);

        Assert.Equal(AuditorIdleDecision.KeepWaiting, decision);
    }

    [Fact]
    public void QuietBeyondIdle_WithoutActiveExecs_DeclaresIdle()
    {
        // No output and no live work: still terminated.
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromMinutes(6),
            idleTimeout: Idle,
            totalElapsed: TimeSpan.FromMinutes(6),
            absoluteTimeout: Absolute,
            auditorTaskCompleted: false,
            hasActiveExecs: false);

        Assert.Equal(AuditorIdleDecision.DeclareIdle, decision);
    }

    [Fact]
    public void CompletedTask_NeverDeclaresIdle()
    {
        // The verdict is ready; the quiet window is moot even past the
        // absolute bound.
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromHours(2),
            idleTimeout: Idle,
            totalElapsed: TimeSpan.FromHours(2),
            absoluteTimeout: Absolute,
            auditorTaskCompleted: true,
            hasActiveExecs: false);

        Assert.Equal(AuditorIdleDecision.KeepWaiting, decision);
    }

    [Fact]
    public void AbsoluteExceeded_FiresRegardlessOfLiveness()
    {
        // The backstop for a genuinely hung run: busy-looking but never
        // finishing still terminates.
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromSeconds(1),
            idleTimeout: Idle,
            totalElapsed: TimeSpan.FromMinutes(31),
            absoluteTimeout: Absolute,
            auditorTaskCompleted: false,
            hasActiveExecs: true);

        Assert.Equal(AuditorIdleDecision.AbsoluteExceeded, decision);
    }

    [Fact]
    public void OutputFlowing_KeepsWaiting()
    {
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromSeconds(10),
            idleTimeout: Idle,
            totalElapsed: TimeSpan.FromMinutes(1),
            absoluteTimeout: Absolute,
            auditorTaskCompleted: false,
            hasActiveExecs: false);

        Assert.Equal(AuditorIdleDecision.KeepWaiting, decision);
    }

    [Fact]
    public void QuietExactlyAtIdleWindow_DeclaresIdle()
    {
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: Idle,
            idleTimeout: Idle,
            totalElapsed: Idle,
            absoluteTimeout: Absolute,
            auditorTaskCompleted: false,
            hasActiveExecs: false);

        Assert.Equal(AuditorIdleDecision.DeclareIdle, decision);
    }

    [Fact]
    public void DisabledIdleLeg_NeverDeclaresIdle()
    {
        var decision = AuditorIdlePolicy.Decide(
            quietElapsed: TimeSpan.FromHours(5),
            idleTimeout: TimeSpan.Zero,
            totalElapsed: TimeSpan.FromHours(5),
            absoluteTimeout: TimeSpan.Zero,
            auditorTaskCompleted: false,
            hasActiveExecs: false);

        Assert.Equal(AuditorIdleDecision.KeepWaiting, decision);
    }
}
