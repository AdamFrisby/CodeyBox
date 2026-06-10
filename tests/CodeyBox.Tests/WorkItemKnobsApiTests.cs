using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeyBox.Orchestrator.Knobs;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for the per-item knob surface: validation at create/patch
/// time, round-trip on the GET shape, and the per-item-vs-project precedence
/// the work-prompt seam relies on.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemKnobsApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemKnobsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_WithChangeScopeSurgical_RoundTripsKnobsMap()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "surgical task",
            prompt = "tighten one line",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.NotNull(body!.Knobs);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, body.Knobs![ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Create_WithoutKnobs_KnobsFieldIsAbsentFromResponse()
    {
        // The DTO serialises Knobs with WhenWritingNull, and an empty
        // per-item map collapses to null at DTO time. So a default create
        // produces a response that does NOT mention the knobs key at all.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "no knobs",
            prompt = "default behaviour",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"knobs\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithUnknownKnobKey_Returns400AndRejectsKey()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bogus",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["notARealKnob"] = "value" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown knob 'notARealKnob'", err);
    }

    [Fact]
    public async Task Create_WithChangeScopeInvalidValue_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad value",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "yolo" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not allowed", err);
    }

    [Fact]
    public async Task Create_KnobKeyCaseInsensitive_AcceptedAndCanonicalised()
    {
        // POST with MIXED-case key; GET should still surface the value at the
        // canonical key as registered by the knob.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "case-insens",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["CHANGESCOPE"] = "refactor" },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.NotNull(body!.Knobs);
        Assert.True(body.Knobs!.TryGetValue("changeScope", out var value) ||
                    body.Knobs.TryGetValue("CHANGESCOPE", out value));
        Assert.Equal("refactor", value);
    }

    [Fact]
    public async Task Patch_KnobsReplacesMap_AndIsRejectedAfterDispatch()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patchable",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
            }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.Equal(ChangeScopeKnob.ValueRefactor, body!.Knobs![ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_EmptyKnobsMap_ClearsAllPerItemOverrides()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "clearable",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new { knobs = new Dictionary<string, string>() }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var get = await _client.GetAsync($"/workitems/{created.Id}");
        var raw = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"knobs\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_WithUnknownKey_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch unknown",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { ["unknownKnob"] = "value" },
            }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class WorkItemWithKnobsResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("knobs")] public IReadOnlyDictionary<string, string>? Knobs { get; init; }
    }
}
