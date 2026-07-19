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
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
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
    public async Task PutAsync_IsIdempotent_DoesNotOverwriteLiveRow()
    {
        // The first writer wins within the TTL window — a concurrent retry that
        // wins the race after the response was already cached must not clobber
        // a live entry with potentially-different bytes. This is what makes the
        // "same key + same body" replay return the SAME bytes the first caller
        // received.
        var key = Guid.NewGuid().ToString();
        await _store.PutAsync(Entry(key, "hashA", status: 201, body: "first"));
        await _store.PutAsync(Entry(key, "hashA", status: 202, body: "second"));

        var result = await _store.LookupAsync(key, "hashA", DateTimeOffset.UtcNow);
        Assert.Equal(201, result.Entry!.ResponseStatus);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(result.Entry.ResponseBody));
    }

    [Fact]
    public async Task PutAsync_OverwritesRow_AfterExpirationWindow()
    {
        // Once the existing row has expired the next PutAsync MUST overwrite it
        // — otherwise the stale tombstone keeps every same-key replay falling
        // through to the downstream endpoint as a "Miss", which breaks the
        // spec's "same key + same body returns cached response" guarantee for
        // any key whose first use was >24h ago. The expired row is also a slot
        // that the sweep service may not have reclaimed yet.
        var key = Guid.NewGuid().ToString();
        await _store.PutAsync(Entry(key, "hashA", status: 201, body: "stale",
            expires: DateTimeOffset.UtcNow.AddMinutes(-1)));

        // Fresh entry — different hash, different body, future expiry.
        await _store.PutAsync(Entry(key, "hashB", status: 200, body: "fresh",
            expires: DateTimeOffset.UtcNow.AddHours(24)));

        var result = await _store.LookupAsync(key, "hashB", DateTimeOffset.UtcNow);
        Assert.Equal(IdempotencyLookupOutcome.Hit, result.Outcome);
        Assert.Equal(200, result.Entry!.ResponseStatus);
        Assert.Equal("fresh", System.Text.Encoding.UTF8.GetString(result.Entry.ResponseBody));
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

    [Fact]
    public async Task DeleteExpiredAsync_ContinuesPastFirstBatch()
    {
        const int expiredCount = 501;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < expiredCount; i++)
            await _store.PutAsync(Entry($"expired-{i}", "h", expires: now.AddMinutes(-10)));
        await _store.PutAsync(Entry("fresh", "h", expires: now.AddHours(1)));

        var deleted = await _store.DeleteExpiredAsync(now);

        Assert.Equal(expiredCount, deleted);
        Assert.Equal(IdempotencyLookupOutcome.Miss,
            (await _store.LookupAsync("expired-500", "h", now)).Outcome);
        Assert.Equal(IdempotencyLookupOutcome.Hit,
            (await _store.LookupAsync("fresh", "h", now)).Outcome);
    }
}
