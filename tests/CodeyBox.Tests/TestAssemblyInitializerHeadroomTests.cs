using System.IO;

namespace CodeyBox.Tests;

public sealed class TestAssemblyInitializerHeadroomTests
{
    [Fact]
    public void DescribeLowTempDiskHeadroom_BelowThreshold_WarnsWithFreeMiB()
    {
        var message = TestAssemblyInitializer.DescribeLowTempDiskHeadroom(
            "/tmp/",
            availableFreeBytes: 256L * 1024 * 1024,
            recommendedFreeBytes: TestAssemblyInitializer.RecommendedTempFreeBytes);

        Assert.NotNull(message);
        Assert.Contains("256 MiB free", message);
        Assert.Contains("'/tmp/'", message);
        Assert.Contains("no such table", message);
    }

    [Fact]
    public void DescribeLowTempDiskHeadroom_AtThreshold_DoesNotWarn()
    {
        var message = TestAssemblyInitializer.DescribeLowTempDiskHeadroom(
            "/tmp/",
            availableFreeBytes: TestAssemblyInitializer.RecommendedTempFreeBytes,
            recommendedFreeBytes: TestAssemblyInitializer.RecommendedTempFreeBytes);

        Assert.Null(message);
    }

    [Fact]
    public void DescribeLowTempDiskHeadroom_AboveThreshold_DoesNotWarn()
    {
        var message = TestAssemblyInitializer.DescribeLowTempDiskHeadroom(
            "/tmp/",
            availableFreeBytes: TestAssemblyInitializer.RecommendedTempFreeBytes + 1,
            recommendedFreeBytes: TestAssemblyInitializer.RecommendedTempFreeBytes);

        Assert.Null(message);
    }

    [Fact]
    public void WarnOnLowTempDiskHeadroom_RealDrive_DoesNotThrow()
    {
        // Exercises the DriveInfo-gathering wrapper against the real temp drive;
        // it must never throw or fail a run regardless of available space.
        using var writer = new StringWriter();
        TestAssemblyInitializer.WarnOnLowTempDiskHeadroom(writer);
    }
}
