using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for <c>PUT /workitems/{id}/prompt</c> and the idempotency
/// middleware. The PUT endpoint must work mid-flight (item is past Queued) and
/// echo the new revision; replays with a stable Idempotency-Key must return
/// the cached response without re-mutating state.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PutPromptEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public PutPromptEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem WorkingItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "T",
        Prompt = "original",
        State = WorkItemState.Working,
    };

    [Fact]
    public async Task PutPrompt_ReplacesPromptAndIncrementsRevision()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PutAsJsonAsync(
            $"/workitems/{item.Id}/prompt",
            new { prompt = "updated mid-flight" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, payload.GetProperty("promptRevision").GetInt32());

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("updated mid-flight", read!.Prompt);
        Assert.Equal(2, read.PromptRevision);
    }

    [Fact]
    public async Task PatchPrompt_BumpsPromptRevision_EndToEnd()
    {
        // The PATCH /workitems/{id} endpoint must increment PromptRevision when
        // the prompt field is patched. Regression guard: a refactor that
        // removed the bump from the PATCH path (or routed the prompt write
        // through the full-row UPDATE which no longer touches prompt columns)
        // would silently lose the revision contract for Queued items.
        var item = WorkingItem() with { State = WorkItemState.Queued };
        await _factory.Store.CreateAsync(item);

        var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"/workitems/{item.Id}")
        {
            Content = JsonContent.Create(new { prompt = "patched prompt" }),
        };
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("patched prompt", read!.Prompt);
        Assert.Equal(item.PromptRevision + 1, read.PromptRevision);
    }

    [Fact]
    public async Task PutPrompt_OnTerminalItem_Returns409()
    {
        var item = WorkingItem() with { State = WorkItemState.Done };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PutAsJsonAsync(
            $"/workitems/{item.Id}/prompt",
            new { prompt = "too late" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("original", read!.Prompt);
        Assert.Equal(1, read.PromptRevision);
    }

    [Fact]
    public async Task PutPrompt_RejectsEmptyPrompt()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PutAsJsonAsync($"/workitems/{item.Id}/prompt", new { prompt = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_SameBodyReplay_ReturnsCachedResponse()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var key = Guid.NewGuid().ToString();
        var first = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"v2\"}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadAsStringAsync();

        // Second call: same key + body. The middleware must short-circuit and
        // return the cached payload; the work item's revision must NOT bump.
        var second = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"v2\"}", key);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        var secondPayload = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstPayload, secondPayload);

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(2, read!.PromptRevision); // still 2, not 3
    }

    [Fact]
    public async Task IdempotencyKey_DifferentBodyReuse_Returns409()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var key = Guid.NewGuid().ToString();
        var first = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"alpha\"}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var conflict = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"beta\"}", key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("alpha", read!.Prompt); // beta was rejected
    }

    [Fact]
    public async Task IdempotencyKey_CrossEndpointReplay_IsTreatedAsConflict()
    {
        // The dedupe digest mixes (method, path, body), so a client that
        // accidentally reuses the same Idempotency-Key across two unrelated
        // endpoints (e.g. two empty-body mutations like DELETE /workitems/{id}
        // and POST /workitems/{id}/retry — both hash to SHA-256-of-empty
        // without scoping) must NOT receive the first endpoint's cached
        // response. We exercise the scoping with two DIFFERENT work items here
        // because the test factory does not run the orchestrator (so POST
        // /retry has no scheduler). The middleware behaviour is the same:
        // same key + different path = same-body-but-different-digest = 409.
        var item1 = WorkingItem();
        var item2 = WorkingItem();
        await _factory.Store.CreateAsync(item1);
        await _factory.Store.CreateAsync(item2);

        var key = Guid.NewGuid().ToString();
        var first = await PutWithKeyAsync($"/workitems/{item1.Id}/prompt", "{\"prompt\":\"shared body\"}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same key + same JSON body but different path → must NOT replay item1's
        // cached response and must NOT silently skip item2's mutation. The
        // middleware sees a different digest under the same key and returns 409.
        var crossEndpoint = await PutWithKeyAsync($"/workitems/{item2.Id}/prompt", "{\"prompt\":\"shared body\"}", key);
        Assert.Equal(HttpStatusCode.Conflict, crossEndpoint.StatusCode);

        // The second item must be untouched — no silent skip, no silent apply.
        var read2 = await _factory.Store.GetAsync(item2.Id);
        Assert.Equal("original", read2!.Prompt);
        Assert.Equal(1, read2.PromptRevision);
    }

    [Fact]
    public async Task IdempotencyKey_OversizedKey_Returns400()
    {
        // The middleware rejects keys longer than IdempotencyMiddleware.MaxKeyLength
        // BEFORE attempting any cache lookup or downstream call. Guards against an
        // attacker stuffing the cache table with arbitrarily-long composite keys.
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var oversized = new string('a', 201); // MaxKeyLength is 200
        var resp = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"x\"}", oversized);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Body must not have been touched by the rejected request.
        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("original", read!.Prompt);
        Assert.Equal(1, read.PromptRevision);
    }

    [Fact]
    public async Task IdempotencyKey_NonSuccessResponse_IsNotCached()
    {
        // A 409 from the handler (e.g. terminal-state guard) must NOT be cached
        // — caching the failure would lock subsequent retries into the 409 even
        // after the state changes. The middleware only caches 2xx outcomes.
        var item = WorkingItem() with { State = WorkItemState.Done };
        await _factory.Store.CreateAsync(item);

        var key = Guid.NewGuid().ToString();
        var first = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"x\"}", key);
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        // The replay header signals a cache hit; the first call must NOT carry it.
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        // Second call with the same key + same body must STILL reach the handler
        // (cache miss because non-2xx was not stored) — it returns the SAME 409
        // because the underlying state still rejects, but it is NOT a cached
        // replay. Locking the contract: the response must not carry the
        // Idempotent-Replayed header.
        var second = await PutWithKeyAsync($"/workitems/{item.Id}/prompt", "{\"prompt\":\"x\"}", key);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task IdempotencyKey_GetRequest_BypassesMiddleware()
    {
        // The middleware only intercepts mutating methods (POST, PUT, PATCH,
        // DELETE). GETs pass through unchanged regardless of the header so a
        // client that defensively stamps every request never sees a phantom
        // 409 because of accidental key reuse on a read.
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var key = Guid.NewGuid().ToString();
        var req1 = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}");
        req1.Headers.Add("Idempotency-Key", key);
        var resp1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var req2 = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}");
        req2.Headers.Add("Idempotency-Key", key);
        var resp2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        Assert.False(resp2.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task IdempotencyKey_OmittedHeader_BehavesLikeBefore_NoCaching()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var first = await _client.PutAsJsonAsync($"/workitems/{item.Id}/prompt", new { prompt = "a" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await _client.PutAsJsonAsync($"/workitems/{item.Id}/prompt", new { prompt = "b" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var read = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("b", read!.Prompt);
        Assert.Equal(3, read.PromptRevision); // 1 → 2 → 3 (no caching)
    }

    private async Task<HttpResponseMessage> PutWithKeyAsync(string path, string json, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(req);
    }
}
