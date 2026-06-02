using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class NullBaselineImageProvisionerTests
{
    [Fact]
    public async Task EnsureBaselineImageAsync_ReturnsNullEvenWhenPinnedRefIsSupplied()
    {
        var ensured = await NullBaselineImageProvisioner.Instance.EnsureBaselineImageAsync(
            "work-profile",
            SandboxProfileFlavor.Headless,
            "cb-baseline-existing",
            CancellationToken.None);

        Assert.Null(ensured);
    }
}
