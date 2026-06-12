using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentNetworkToleranceSnapshotTests
{
    [Fact]
    public void Constructor_NullInitial_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new AgentNetworkToleranceSnapshot(null!));

        Assert.Equal("initial", ex.ParamName);
    }

    [Fact]
    public void Replace_NullNext_ThrowsArgumentNullException()
    {
        var snapshot = new AgentNetworkToleranceSnapshot(
            new Dictionary<string, AgentNetworkToleranceOptions?>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<ArgumentNullException>(() => snapshot.Replace(null!));

        Assert.Equal("next", ex.ParamName);
    }
}
