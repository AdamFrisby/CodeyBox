using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="SqliteIdempotencyStore"/>: the per-key cache used by
/// the API <c>Idempotency-Key</c> middleware. Hit must match body hash exactly;
/// hash mismatch is the 409-conflict signal; expired rows surface as misses.
/// </summary>
public sealed class SqliteIdempotencyStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-idem-{Guid.NewGuid():N}.db");
    private readonly SqliteIdempotencyStore _store;

    public SqliteIdempotencyStoreTests() => _store = new SqliteIdempotencyStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static IdempotencyEntry Entry(
        string key,
        string bodyHash,
        int status = 200,
        string body = "{}",
        DateTimeOffset? expires = null) => new(
            key,
            bodyHash,
            status,
            System.Text.Encoding.UTF8.GetBytes(body),
            "application/json",
            expires ?? DateTimeOffset.UtcNow.AddHours(24));

    [Fact]
    public async Task LookupAsync_ReturnsMiss_WhenKeyAbsent()
    {
        var now = DateTimeOffset.UtcNow;
        var result = await _store.LookupAsync("absent-key", "any-hash", now);
        Assert.Equal(IdempotencyLookupOutcome.Miss, result.Outcome);
        Assert.Null(result.Entry);
    }

    [Fact]
    public async Task LookupAsync_ReturnsHit_OnSameBodyHash()
    {
        var key = Guid.NewGuid().ToString();
        await _store.PutAsync(Entry(key, "hashA", status: 201, body: "{\"id\":\"x\"}"));

        var result = await _store.LookupAsync(key, "hashA", DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Hit, result.Outcome);
        Assert.NotNull(result.Entry);
        Assert.Equal(201, result.Entry!.ResponseStatus);
        Assert.Equal("{\"id\":\"x\"}", System.Text.Encoding.UTF8.GetString(result.Entry.ResponseBody));
    }

    [Fact]
    public async Task LookupAsync_ReturnsConflict_OnDifferentBodyHash()
    {
        var key = Guid.NewGuid().ToString();
        await _store.PutAsync(Entry(key, "hashA"));

        var result = await _store.LookupAsync(key, "hashB", DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task LookupAsync_TreatsExpiredRowsAsMiss()
    {
        var key = Guid.NewGuid().ToString();
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store.PutAsync(Entry(key, "hashA", expires: expired));

        var result = await _store.LookupAsync(key, "hashA", DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Miss, result.Outcome);
    }

    [Fact]
    public async Task PutAsync_IsIdempotent_DoesNotOverwriteExistingRow()
    {
        // ON CONFLICT DO NOTHING — the first writer wins. This protects against
        // a successful response getting clobbered by a slower in-flight retry.
        var key = Guid.NewGuid().ToString();
        await _store.PutAsync(Entry(key, "hashA", status: 201, body: "first"));
        await _store.PutAsync(Entry(key, "hashA", status: 202, body: "second"));

        var result = await _store.LookupAsync(key, "hashA", DateTimeOffset.UtcNow);
        Assert.Equal(201, result.Entry!.ResponseStatus);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(result.Entry.ResponseBody));
    }

    [Fact]
    public async Task DeleteExpiredAsync_RemovesOnlyRowsAtOrBeforeCutoff()
    {
        await _store.PutAsync(Entry("a", "h1", expires: DateTimeOffset.UtcNow.AddMinutes(-10)));
        await _store.PutAsync(Entry("b", "h2", expires: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await _store.PutAsync(Entry("c", "h3", expires: DateTimeOffset.UtcNow.AddHours(2)));

        var deleted = await _store.DeleteExpiredAsync(DateTimeOffset.UtcNow);
        Assert.Equal(2, deleted);

        var stillThere = await _store.LookupAsync("c", "h3", DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Hit, stillThere.Outcome);
    }
}
