using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for <see cref="IncusSafeFile.TryAcquireExclusiveLeaseWithBackoff"/>.
/// A concurrent fork/exec anywhere in the process momentarily duplicates an open
/// advisory-lease descriptor into the forked child, keeping the flock alive until
/// the child reaches exec. The recovery-manifest and provisioning-coordination
/// acquisitions used to reject that transient contention immediately, causing
/// spurious "already owned by another process" failures under the parallel test
/// suite; the backoff retries the non-blocking flock until such a window clears.
/// </summary>
public sealed class IncusSafeFileLeaseBackoffTests
{
    [Fact]
    public void Backoff_RetriesUntilTransientHolderReleases()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = TestTempDirectory.Create("codeybox-lease-backoff-");
        var leasePath = Path.Combine(temp.Root, "lease");

        // A second open of the same path is a distinct open file description, so its
        // flock genuinely conflicts — this is exactly what a fork-duplicated
        // descriptor looks like to a fresh acquirer.
        using var transientHolder = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(leasePath);
        Assert.True(IncusSafeFile.TryAcquireExclusiveLease(transientHolder));

        using var contender = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(leasePath);
        var sleeps = 0;
        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithBackoff(
            contender,
            maxAttempts: 20,
            retryDelay: TimeSpan.Zero,
            sleep: _ =>
            {
                sleeps++;
                if (sleeps == 3)
                    transientHolder.Dispose(); // the transient descriptor reaches exec and closes
            });

        Assert.True(acquired);
        Assert.True(sleeps >= 3, $"expected at least 3 retries before release, saw {sleeps}");
    }

    [Fact]
    public void Backoff_ReturnsFalseWhenGenuinelyHeldForWholeBudget()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = TestTempDirectory.Create("codeybox-lease-backoff-");
        var leasePath = Path.Combine(temp.Root, "lease");

        using var genuineOwner = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(leasePath);
        Assert.True(IncusSafeFile.TryAcquireExclusiveLease(genuineOwner));

        using var contender = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(leasePath);
        var sleeps = 0;
        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithBackoff(
            contender,
            maxAttempts: 5,
            retryDelay: TimeSpan.Zero,
            sleep: _ => sleeps++);

        Assert.False(acquired);
        Assert.Equal(4, sleeps); // maxAttempts attempts => maxAttempts-1 inter-attempt sleeps
    }

    [Fact]
    public void Backoff_RejectsNonPositiveAttemptBudget()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = TestTempDirectory.Create("codeybox-lease-backoff-");
        var leasePath = Path.Combine(temp.Root, "lease");
        using var lease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(leasePath);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncusSafeFile.TryAcquireExclusiveLeaseWithBackoff(lease, maxAttempts: 0));
    }
}
