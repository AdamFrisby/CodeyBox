using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class NuGetPackageCacheGuestPathsTests
{
    [Theory]
    [InlineData("/home/ubuntu/.nuget/packages", "/home/ubuntu", "/home/ubuntu/.nuget")]
    [InlineData("/home/ubuntu/.nuget/packages/", "/home/ubuntu/", "/home/ubuntu/.nuget")]
    [InlineData("/home/ubuntu/.nuget", "/home/ubuntu", "/home/ubuntu/.nuget")]
    [InlineData("/home/ubuntu/.nuget/packages/foo", "/home/ubuntu", "/home/ubuntu/.nuget")]
    public void TryGetNuGetHomeDirectory_ReturnsNuGetHome_WhenSeedIsUnderGuestHome(
        string vmDestPath,
        string guestHome,
        string expected)
        => Assert.Equal(
            expected,
            NuGetPackageCacheGuestPaths.TryGetNuGetHomeDirectory(vmDestPath, guestHome));

    [Theory]
    [InlineData("/var/cache/codeybox/nuget", "/home/ubuntu")]
    [InlineData("/home/ubuntu/.local/share/nuget", "/home/ubuntu")]
    [InlineData("/home/other/.nuget/packages", "/home/ubuntu")]
    [InlineData("", "/home/ubuntu")]
    [InlineData("/home/ubuntu/.nuget/packages", "")]
    [InlineData(null, "/home/ubuntu")]
    [InlineData("/home/ubuntu/.nuget/packages", null)]
    public void TryGetNuGetHomeDirectory_ReturnsNull_WhenSeedIsOutsideGuestNuGetHome(
        string? vmDestPath,
        string? guestHome)
        => Assert.Null(NuGetPackageCacheGuestPaths.TryGetNuGetHomeDirectory(vmDestPath!, guestHome!));
}
