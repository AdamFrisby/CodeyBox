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
    public async Task Create_WithoutMinModelScore_DefaultsTo95()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "default task",
            prompt = "do something",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithScoreResponse>();
        Assert.Equal(95, body!.MinModelScore);
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

    // ── Local response shape ──────────────────────────────────────────────────

    private sealed record WorkItemWithScoreResponse(string Id, string State, int MinModelScore = 95);
}
