using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Direct in-host tests for <see cref="IdempotencyMiddleware"/> that exercise
/// branches the API-level <see cref="PutPromptEndpointTests"/> can't reach with
/// a real endpoint: the 256 KB cache cap, 5xx pass-through (no caching of
/// server errors), and the "store not registered" pass-through fall-back.
/// </summary>
public sealed class IdempotencyMiddlewareTests
{
    /// <summary>
    /// Builds a one-shot test host with the middleware in front of a
    /// configurable endpoint that returns <paramref name="status"/> and
    /// <paramref name="responseBody"/> bytes. <paramref name="store"/> is
    /// registered in DI when non-null; passing null exercises the
    /// passthrough branch.
    /// </summary>
    private static async Task<TestServer> BuildHostAsync(
        int status,
        byte[] responseBody,
        IIdempotencyStore? store)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    if (store is not null)
                        services.AddSingleton(store);
                });
                web.Configure(app =>
                {
                    IdempotencyMiddleware.Use(app);
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/octet-stream";
                        await ctx.Response.Body.WriteAsync(responseBody);
                    });
                });
            });
        var host = await builder.StartAsync();
        return host.GetTestServer();
    }

    private sealed class CountingIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyEntry> _entries = new();
        public int LookupCount { get; private set; }
        public int PutCount { get; private set; }

        public Task<IdempotencyLookupResult> LookupAsync(string key, string bodyHash, DateTimeOffset now, CancellationToken ct = default)
        {
            LookupCount++;
            if (!_entries.TryGetValue(key, out var entry))
                return Task.FromResult(new IdempotencyLookupResult(IdempotencyLookupOutcome.Miss, null));
            if (entry.ExpiresAt <= now)
                return Task.FromResult(new IdempotencyLookupResult(IdempotencyLookupOutcome.Miss, null));
            return Task.FromResult(string.Equals(entry.BodyHash, bodyHash, StringComparison.Ordinal)
                ? new IdempotencyLookupResult(IdempotencyLookupOutcome.Hit, entry)
                : new IdempotencyLookupResult(IdempotencyLookupOutcome.Conflict, entry));
        }

        public Task PutAsync(IdempotencyEntry entry, CancellationToken ct = default)
        {
            PutCount++;
            _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static async Task<HttpResponseMessage> PostAsync(TestServer server, string body, string key)
    {
        using var client = server.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task OversizedResponse_ExceedsMaxCachedResponseBytes_IsNotCached()
    {
        // Responses larger than IdempotencyMiddleware.MaxCachedResponseBytes
        // (256 KB) must NOT be persisted — otherwise a single key can pin
        // 30 MB of state per cached row (Kestrel's default body cap).
        // Regression coverage: a refactor that drops the size guard, applies
        // it to a different counter, or off-by-ones the boundary would
        // silently re-introduce the storage-exhaustion vector.
        var payload = new byte[IdempotencyMiddleware.MaxCachedResponseBytes + 1];
        Array.Fill(payload, (byte)'x');
        var store = new CountingIdempotencyStore();
        using var server = await BuildHostAsync(status: 200, responseBody: payload, store);

        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // Caller receives the full oversized body — only the CACHE write is skipped.
        Assert.Equal(payload.Length, (await first.Content.ReadAsByteArrayAsync()).Length);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        // No row was persisted: PutAsync was never called and the second
        // call must reach the downstream handler again (no replay header).
        Assert.Equal(0, store.PutCount);
        var second = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task AtMaxCachedResponseBytes_IsCached_BoundaryInclusive()
    {
        // Exact-cap responses must still be cached — the guard is "<=", not
        // "<", and pinning that here catches an off-by-one that would skip
        // caching every response sized exactly at the cap.
        var payload = new byte[IdempotencyMiddleware.MaxCachedResponseBytes];
        Array.Fill(payload, (byte)'y');
        var store = new CountingIdempotencyStore();
        using var server = await BuildHostAsync(status: 200, responseBody: payload, store);

        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, store.PutCount);

        var second = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task ServerError_5xx_IsNotCached()
    {
        // 5xx replies must NOT be cached: the client should be allowed to
        // retry against a recovered backend. A regression that flipped the
        // bound to "< 500" (instead of "< 300") would silently cache
        // server errors for 24 h, locking every retry into the failure.
        var store = new CountingIdempotencyStore();
        using var server = await BuildHostAsync(
            status: 500, responseBody: Encoding.UTF8.GetBytes("boom"), store);

        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        // Nothing was persisted, so the retry must re-hit the handler — not
        // replay from the cache. Locking this means a refactor that started
        // caching 5xx outcomes would surface as a test failure here rather
        // than as a silent 24-hour lockout in production.
        Assert.Equal(0, store.PutCount);
        var second = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task ServiceUnavailable_503_IsNotCached()
    {
        // Same contract as 500 but with a different 5xx code — guards the
        // upper-bound side of the "200..299" inclusive range without making
        // the test fragile to the specific code chosen by the handler.
        var store = new CountingIdempotencyStore();
        using var server = await BuildHostAsync(
            status: 503, responseBody: Encoding.UTF8.GetBytes("retry"), store);

        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(0, store.PutCount);

        var second = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task StoreNotRegistered_FallsThroughToHandler_NoSilentDrop()
    {
        // If IIdempotencyStore is missing from DI the middleware must
        // degrade to passthrough — a misconfigured deployment never
        // silently swallows a mutation. Regression: a refactor that
        // returns 5xx (or short-circuits with a cached 200) on the
        // missing-store branch would make every mutating endpoint
        // dependent on the optional service.
        using var server = await BuildHostAsync(
            status: 201, responseBody: Encoding.UTF8.GetBytes("ok"), store: null);

        var key = Guid.NewGuid().ToString();
        var first = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        // Second call also passes through — no row exists, no replay.
        var second = await PostAsync(server, "{}", key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }
}
