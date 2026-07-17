using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the bounded, backed-off retry that rides out the fork→exec
/// file-descriptor-inheritance window on exclusive lease acquisition without
/// weakening mutual exclusion. Exercises the pure retry core directly so the
/// behaviour is asserted without real flock calls or wall-clock time.
/// </summary>
public sealed class IncusExclusiveLeaseRetryTests
{
    [Fact]
    public void RidesOutTransientConflictThenSucceeds()
    {
        var attempts = 0;
        var sleeps = new List<TimeSpan>();

        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithRetry(
            () => ++attempts >= 3,
            maxAttempts: 12,
            retryDelay: TimeSpan.FromMilliseconds(15),
            sleep: sleeps.Add);

        Assert.True(acquired);
        Assert.Equal(3, attempts);
        // One sleep before each of the two retries; none after the success.
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(15) }, sleeps);
    }

    [Fact]
    public void SucceedsOnFirstAttemptWithoutSleeping()
    {
        var sleepCalls = 0;

        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithRetry(
            () => true,
            maxAttempts: 12,
            retryDelay: TimeSpan.FromMilliseconds(15),
            sleep: _ => sleepCalls++);

        Assert.True(acquired);
        Assert.Equal(0, sleepCalls);
    }

    [Fact]
    public void GivesUpAfterBudget_PreservingMutualExclusion()
    {
        var attempts = 0;
        var sleeps = 0;

        // A genuinely-held lease never frees: every attempt reports would-block.
        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithRetry(
            () => { attempts++; return false; },
            maxAttempts: 5,
            retryDelay: TimeSpan.FromMilliseconds(1),
            sleep: _ => sleeps++);

        Assert.False(acquired);
        Assert.Equal(5, attempts);
        // No sleep after the final, budget-exhausting attempt.
        Assert.Equal(4, sleeps);
    }

    [Fact]
    public void RejectsNonPositiveAttemptBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncusSafeFile.TryAcquireExclusiveLeaseWithRetry(
                () => true,
                maxAttempts: 0,
                retryDelay: TimeSpan.Zero,
                sleep: _ => { }));
    }
}
