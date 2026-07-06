using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AttachmentsOptionsTests
{
    [Fact]
    public void ResolveRoot_ThrowsOnNullOrWhitespace()
    {
        Assert.Throws<InvalidOperationException>(() => AttachmentsOptions.ResolveRoot(""));
        Assert.Throws<InvalidOperationException>(() => AttachmentsOptions.ResolveRoot("   "));
        Assert.Throws<InvalidOperationException>(() => AttachmentsOptions.ResolveRoot(null!));
    }

    [Fact]
    public void ResolveRoot_ExpandsTildeSlashToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = AttachmentsOptions.ResolveRoot("~/foo/attachments");
        Assert.Equal(Path.Combine(home, "foo/attachments"), result);
    }

    [Fact]
    public void ResolveRoot_ExpandsBareTildeToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = AttachmentsOptions.ResolveRoot("~");
        Assert.Equal(home, result);
    }

    [Fact]
    public void ResolveRoot_PassesThroughAbsolutePath()
    {
        var result = AttachmentsOptions.ResolveRoot("/var/lib/codeybox/attachments");
        Assert.Equal("/var/lib/codeybox/attachments", result);
    }
}
