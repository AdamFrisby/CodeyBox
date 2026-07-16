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
        using var temp = TestTempDirectory.Create("codeybox-lease-backoff-");
        using var lease = File.Create(Path.Combine(temp.Root, "lease"));

        // Model a transient fork-duplicated descriptor by reporting the lease as
        // held for the first few non-blocking flock attempts and free thereafter.
        // Injecting the acquire step keeps the retry-until-success behaviour under
        // test deterministic: the real-flock variant here raced a concurrent
        // fork/exec elsewhere in the loaded parallel suite, which duplicated the
        // holder's descriptor and kept the lock alive past Dispose until the child
        // reached exec — long after a no-op spin exhausted its attempt budget.
        const int heldForAttempts = 3;
        var attempts = 0;
        var sleeps = 0;
        var acquired = IncusSafeFile.TryAcquireExclusiveLeaseWithBackoff(
            lease,
            maxAttempts: 20,
            retryDelay: TimeSpan.Zero,
            sleep: _ => sleeps++,
            tryAcquire: _ => ++attempts > heldForAttempts);

        Assert.True(acquired);
        Assert.Equal(heldForAttempts + 1, attempts); // wins on the attempt after release
        Assert.Equal(heldForAttempts, sleeps);       // one inter-attempt sleep per contended attempt
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
