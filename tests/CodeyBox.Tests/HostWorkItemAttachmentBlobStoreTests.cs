using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class HostWorkItemAttachmentBlobStoreTests : IDisposable
{
    private readonly string _rootDir;
    private readonly HostWorkItemAttachmentBlobStore _store;

    public HostWorkItemAttachmentBlobStoreTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"codeybox-blob-store-test-{Guid.NewGuid():N}");
        _store = new HostWorkItemAttachmentBlobStore(() => _rootDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task StageAsync_SavesFileWithHashName()
    {
        var content = "Hello, world!";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _store.StageAsync(stream, maxBytes: 100);

        Assert.False(result.WasDeduplicated);
        Assert.Equal(stream.Length, result.SizeBytes);
        Assert.Equal("315f5bdb76d078c43b8ac0064e4a0164612b1fce77c869345bfc94c75894edd3", result.Sha256);

        var expectedPath = Path.Combine(_rootDir, "31", result.Sha256);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(content, await File.ReadAllTextAsync(expectedPath));
    }

    [Fact]
    public async Task StageAsync_ThrowsWhenBlobTooLarge()
    {
        var content = "Too large content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await Assert.ThrowsAsync<AttachmentBlobTooLargeException>(
            async () => await _store.StageAsync(stream, maxBytes: 5));
    }

    [Fact]
    public async Task StageAsync_DeduplicatesSameContent()
    {
        var content = "Deduplicate me";
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var r1 = await _store.StageAsync(stream1, maxBytes: 100);
        var r2 = await _store.StageAsync(stream2, maxBytes: 100);

        Assert.False(r1.WasDeduplicated);
        Assert.True(r2.WasDeduplicated);
        Assert.Equal(r1.Sha256, r2.Sha256);
        Assert.Equal(r1.SizeBytes, r2.SizeBytes);

        // Verify only one file exists
        var expectedPath = Path.Combine(_rootDir, r1.Sha256[..2], r1.Sha256);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task OpenRead_ReturnsValidStream()
    {
        var content = "Read me back";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var r = await _store.StageAsync(stream, maxBytes: 100);

        using var readStream = _store.OpenRead(r.Sha256);
        Assert.NotNull(readStream);
        using var reader = new StreamReader(readStream);
        var readContent = await reader.ReadToEndAsync();
        Assert.Equal(content, readContent);
    }

    [Fact]
    public async Task Exists_ReturnsCorrectState()
    {
        var content = "Check if exists";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var r = await _store.StageAsync(stream, maxBytes: 100);

        Assert.True(_store.Exists(r.Sha256));
        Assert.False(_store.Exists("0000000000000000000000000000000000000000000000000000000000000000"));
        Assert.False(_store.Exists("invalid_hash"));
    }

    [Fact]
    public async Task TryDelete_RemovesFile()
    {
        var content = "Delete me please";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var r = await _store.StageAsync(stream, maxBytes: 100);

        Assert.True(_store.Exists(r.Sha256));
        var deleted = _store.TryDelete(r.Sha256);

        Assert.True(deleted);
        Assert.False(_store.Exists(r.Sha256));
        Assert.False(File.Exists(Path.Combine(_rootDir, r.Sha256[..2], r.Sha256)));
    }

    [Fact]
    public async Task EnumerateHashes_ReturnsAllHashes()
    {
        using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("first"));
        using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("second"));

        var r1 = await _store.StageAsync(s1, maxBytes: 100);
        var r2 = await _store.StageAsync(s2, maxBytes: 100);

        var hashes = _store.EnumerateHashes();
        Assert.Equal(2, hashes.Count);
        Assert.Contains(r1.Sha256, hashes);
        Assert.Contains(r2.Sha256, hashes);
    }
}
