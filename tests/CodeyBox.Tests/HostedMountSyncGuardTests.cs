using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the shared hosted-mount sync-back guard used by every
/// hosted-sandbox plugin (E2B, Runloop): untrusted guest listings resolve to
/// host paths through one implementation, symlink components are refused both
/// at resolve time and immediately before the write, and the guarded write
/// lands bytes for the benign case.
/// </summary>
public sealed class HostedMountSyncGuardTests : IDisposable
{
    private readonly string _hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public HostedMountSyncGuardTests() => Directory.CreateDirectory(_hostDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hostDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task ResolveAndWrite_RegularFile_WritesBytes()
    {
        Assert.True(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data/notes.txt", out var hostFile, out _));
        Assert.Equal(Path.Combine(_hostDir, "notes.txt"), hostFile);

        var writeError = await HostedMountSyncGuard.WriteFileGuardedAsync(
            _hostDir, hostFile, "/data/notes.txt", "guest-bytes"u8.ToArray(), CancellationToken.None);
        Assert.Null(writeError);
        Assert.Equal("guest-bytes", await File.ReadAllTextAsync(hostFile));
    }

    [Fact]
    public void Resolve_GuestPathOutsideMount_Refused()
    {
        Assert.False(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/other/evil.txt", out _, out var refusal));
        Assert.Contains("escapes", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MountRootItself_Refused()
    {
        Assert.False(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data", out _, out _));
    }

    [Fact]
    public void Resolve_LiveHostSymlink_RefusedWithoutOverwritingTarget()
    {
        var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(outside, "original-outside");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_hostDir, "evil.txt"), outside);
            Assert.False(HostedMountSyncGuard.TryResolveHostFile(
                _hostDir, "/data", "/data/evil.txt", out _, out var refusal));
            Assert.Contains("symlink", refusal, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original-outside", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Resolve_DanglingHostSymlink_Refused()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.CreateSymbolicLink(Path.Combine(_hostDir, "dangling.txt"), missing);
        Assert.False(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data/dangling.txt", out _, out var refusal));
        Assert.Contains("symlink", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Resolve_SymlinkedDirectoryComponent_Refused()
    {
        var real = Path.Combine(_hostDir, "real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.Combine(_hostDir, "linked"), real);
        Assert.False(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data/linked/notes.txt", out _, out var refusal));
        Assert.Contains("symlink", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_SymlinkSwappedInAfterResolve_RefusedWithoutOverwritingTarget()
    {
        Assert.True(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data/evil.txt", out var hostFile, out _));

        var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(outside, "original-outside");
        try
        {
            File.CreateSymbolicLink(hostFile, outside);
            var writeError = await HostedMountSyncGuard.WriteFileGuardedAsync(
                _hostDir, hostFile, "/data/evil.txt", "guest-bytes"u8.ToArray(), CancellationToken.None);
            Assert.NotNull(writeError);
            Assert.Contains("symlink", writeError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original-outside", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Write_CancelledToken_ThrowsInsteadOfSwallowing()
    {
        Assert.True(HostedMountSyncGuard.TryResolveHostFile(
            _hostDir, "/data", "/data/notes.txt", out var hostFile, out _));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HostedMountSyncGuard.WriteFileGuardedAsync(
                _hostDir, hostFile, "/data/notes.txt", "guest-bytes"u8.ToArray(), cts.Token));
    }
}
