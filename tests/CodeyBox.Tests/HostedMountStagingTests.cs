using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the shared hosted-mount staging guard: the staged tree
/// carries untrusted workspace content, so any symlink inside it must refuse
/// the mount before a byte reaches third-party storage, while ordinary trees
/// still stage. Uses real temporary directories (no mocks): a broken guard
/// uploads or accepts the link target and flips these tests red.
/// </summary>
public sealed class HostedMountStagingTests : IDisposable
{
    private readonly string _hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _outsideDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public HostedMountStagingTests()
    {
        Directory.CreateDirectory(_hostDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_hostDir, recursive: true); } catch { }
        try { Directory.Delete(_outsideDir, recursive: true); } catch { }
    }

    [Fact]
    public void CollectStageFiles_RegularTree_ListsNestedFiles()
    {
        File.WriteAllText(Path.Combine(_hostDir, "input.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_hostDir, "sub"));
        File.WriteAllText(Path.Combine(_hostDir, "sub", "nested.txt"), "nested");

        var files = HostedMountStaging.CollectStageFiles(_hostDir, "E2B", 5000, CancellationToken.None);

        Assert.Equal(2, files.Count);
        var relatives = files.Select(static f => f.Relative).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("input.txt", relatives);
        Assert.Contains(Path.Combine("sub", "nested.txt"), relatives);
        Assert.All(files, f => Assert.StartsWith(_hostDir, f.HostFull, StringComparison.Ordinal));
    }

    [Fact]
    public void CollectStageFiles_SymlinkedSubdir_RefusesMount()
    {
        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "outside-secret");
        Directory.CreateSymbolicLink(Path.Combine(_hostDir, "linkdir"), _outsideDir);

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostedMountStaging.CollectStageFiles(_hostDir, "E2B", 5000, CancellationToken.None));
        Assert.Contains("symlink", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectStageFiles_SymlinkedFile_RefusesMount()
    {
        var outside = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(outside, "outside-secret");
        File.CreateSymbolicLink(Path.Combine(_hostDir, "evil.txt"), outside);

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostedMountStaging.CollectStageFiles(_hostDir, "Runloop", 5000, CancellationToken.None));
        Assert.Contains("symlink", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectStageFiles_DanglingSymlink_RefusesMount()
    {
        var missing = Path.Combine(_outsideDir, "no-such-file.txt");
        File.CreateSymbolicLink(Path.Combine(_hostDir, "dangling.txt"), missing);

        Assert.Throws<InvalidOperationException>(
            () => HostedMountStaging.CollectStageFiles(_hostDir, "E2B", 5000, CancellationToken.None));
    }

    [Fact]
    public void ThrowIfSymlinked_LinkSwappedInAfterListing_RefusesOpen()
    {
        var victim = Path.Combine(_hostDir, "input.txt");
        File.WriteAllText(victim, "hello");

        var files = HostedMountStaging.CollectStageFiles(_hostDir, "E2B", 5000, CancellationToken.None);
        Assert.Single(files);

        File.Delete(victim);
        var outside = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(outside, "outside-secret");
        File.CreateSymbolicLink(victim, outside);

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostedMountStaging.ThrowIfSymlinked(_hostDir, victim, "input.txt", "E2B"));
        Assert.Contains("symlink", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowIfSymlinked_RegularFile_DoesNotThrow()
    {
        var victim = Path.Combine(_hostDir, "input.txt");
        File.WriteAllText(victim, "hello");

        var ex = Record.Exception(
            () => HostedMountStaging.ThrowIfSymlinked(_hostDir, victim, "input.txt", "E2B"));
        Assert.Null(ex);
    }
}
