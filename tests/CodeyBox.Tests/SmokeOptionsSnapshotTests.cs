using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SmokeOptionsSnapshotTests
{
    [Fact]
    public void Constructor_NullInitial_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new SmokeOptionsSnapshot(null!));

        Assert.Equal("initial", ex.ParamName);
    }

    [Fact]
    public void Replace_NullNext_ThrowsArgumentNullException()
    {
        var snapshot = new SmokeOptionsSnapshot(new SmokeOptions());

        var ex = Assert.Throws<ArgumentNullException>(() => snapshot.Replace(null!));

        Assert.Equal("next", ex.ParamName);
    }
}
