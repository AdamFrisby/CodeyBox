using System.Net;
using System.Net.Http.Json;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests verifying that <c>minModelScore</c> round-trips through
/// <c>POST /workitems</c> and appears in the <c>GET /workitems/{id}</c>
/// response body.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemMinScoreApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemMinScoreApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── POST /workitems — minModelScore field ─────────────────────────────────

    [Fact]
    public async Task Create_WithMinModelScore70_RoundTripsInResponse()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "low-stakes task",
            prompt = "do something simple",
            minModelScore = 70,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(70, body!.MinModelScore);
    }

    [Fact]
    public async Task Create_WithMinModelScore100_RoundTripsInResponse()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "high-quality task",
            prompt = "do something important",
            minModelScore = 100,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(100, body!.MinModelScore);
    }

    [Fact]
    public async Task Create_WithoutMinModelScore_DefaultsToOpen()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "default task",
            prompt = "do something",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(0, body!.MinModelScore);
    }

    // ── GET /workitems/{id} — minModelScore present ───────────────────────────

    [Fact]
    public async Task Get_ById_ReturnsMinModelScore()
    {
        // Create with explicit score.
        var createResp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "score test",
            prompt = "prompt",
            minModelScore = 80,
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();

        // GET by UUID should surface the same score.
        var getResp = await _client.GetAsync($"/workitems/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(80, fetched!.MinModelScore);
    }

    // ── Clamping: out-of-range values are clamped to [0, 200] ────────────────

    [Fact]
    public async Task Create_MinModelScoreAbove200_ClampedTo200()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "clamped task",
            prompt = "p",
            minModelScore = 999,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(200, body!.MinModelScore);
    }

    [Fact]
    public async Task Create_MinModelScoreNegative_ClampedToZero()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "clamped zero task",
            prompt = "p",
            minModelScore = -10,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(0, body!.MinModelScore);
    }

    // ── requiredCapabilities round-trip + PATCH ──────────────────────────────

    [Fact]
    public async Task Create_WithRequiredCapabilities_RoundTripsInResponse()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "sensitive task",
            prompt = "do something restricted",
            requiredCapabilities = new[] { "sensitive", "architectural" },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.NotNull(body!.RequiredCapabilities);
        Assert.Equal(new[] { "sensitive", "architectural" }, body.RequiredCapabilities);
    }

    [Fact]
    public async Task Create_DuplicateCapabilities_DedupedCaseInsensitive()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "dup caps",
            prompt = "p",
            requiredCapabilities = new[] { "sensitive", "Sensitive", "SENSITIVE" },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.Single(body!.RequiredCapabilities!);
        Assert.Equal("sensitive", body.RequiredCapabilities![0]);
    }

    [Fact]
    public async Task Create_WithoutRequiredCapabilities_DefaultsEmpty()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "default-open",
            prompt = "p",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.NotNull(body!.RequiredCapabilities);
        Assert.Empty(body.RequiredCapabilities!);
    }

    [Fact]
    public async Task Patch_UpdatesRequiredCapabilities()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "to-patch",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                requiredCapabilities = new[] { "sensitive" },
            }),
        };
        var patchResp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        var afterGet = await _client.GetAsync($"/workitems/{created.Id}");
        var fetched = await afterGet.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.Equal(new[] { "sensitive" }, fetched!.RequiredCapabilities);
    }

    // ── requiredCapabilities trim/validation on create + PATCH ──────────────

    [Fact]
    public async Task Create_RequiredCapabilities_TrimsWhitespace()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "trim caps",
            prompt = "p",
            requiredCapabilities = new[] { "  sensitive  ", "architectural\t" },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.Equal(new[] { "sensitive", "architectural" }, body!.RequiredCapabilities);
    }

    [Fact]
    public async Task Patch_RequiredCapabilities_TrimsWhitespace()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "to-patch-trim",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                requiredCapabilities = new[] { "  sensitive  " },
            }),
        };
        var patchResp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var body = await patchResp.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        Assert.Equal(new[] { "sensitive" }, body!.RequiredCapabilities);
    }

    [Fact]
    public async Task Create_RequiredCapabilities_TooManyEntries_Returns400()
    {
        // NormaliseRequiredCapabilities caps at 16 entries.
        var tags = Enumerable.Range(0, 17).Select(i => $"tag-{i}").ToArray();
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "too many",
            prompt = "p",
            requiredCapabilities = tags,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RequiredCapabilities_TagTooLong_Returns400()
    {
        // 65 chars exceeds the 64-char per-tag cap.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "long tag",
            prompt = "p",
            requiredCapabilities = new[] { new string('x', 65) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RequiredCapabilities_ControlChar_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "ctrl char",
            prompt = "p",
            requiredCapabilities = new[] { "sensitive" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_RequiredCapabilities_TooManyEntries_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch too many",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        var tags = Enumerable.Range(0, 17).Select(i => $"tag-{i}").ToArray();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new { requiredCapabilities = tags }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_RequiredCapabilities_TagTooLong_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch long",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                requiredCapabilities = new[] { new string('x', 65) },
            }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_RequiredCapabilities_ControlChar_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch ctrl",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithCapabilitiesResponse>();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new
            {
                requiredCapabilities = new[] { "sensitive" },
            }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Local response shape ──────────────────────────────────────────────────

    private sealed record WorkItemWithScoreResponse(string Id, string State, int MinModelScore = 95);

    private sealed record WorkItemWithCapabilitiesResponse(
        string Id,
        string State,
        string[]? RequiredCapabilities = null);
}
