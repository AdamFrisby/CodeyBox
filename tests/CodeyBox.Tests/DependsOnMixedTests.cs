using System.Net;
using System.Net.Http.Json;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a dependsOn array containing a mix of UUIDs and externalIds
/// resolves all entries correctly to internal UUIDs.
/// </summary>
public sealed class DependsOnMixedTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public DependsOnMixedTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task MixedDependsOn_ResolvesAll()
    {
        // A: referenced later by UUID
        var rA = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "A",
            prompt = "p",
        });
        Assert.Equal(HttpStatusCode.Created, rA.StatusCode);
        var aDto = await rA.Content.ReadFromJsonAsync<WorkItemIdResponse>();

        // B: referenced later by externalId
        var rB = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "B",
            prompt = "p",
            externalId = "DEP-B",
        });
        Assert.Equal(HttpStatusCode.Created, rB.StatusCode);
        var bDto = await rB.Content.ReadFromJsonAsync<WorkItemIdResponse>();

        // C depends on A (by UUID) and B (by externalId)
        var rC = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "C",
            prompt = "p",
            dependsOn = new[] { aDto!.Id, "DEP-B" },
        });
        Assert.Equal(HttpStatusCode.Created, rC.StatusCode);
        var cDto = await rC.Content.ReadFromJsonAsync<WorkItemIdResponse>();

        var cStored = await _factory.Store.GetAsync(new CodeyBox.Core.WorkItemId(Guid.Parse(cDto!.Id)));
        Assert.NotNull(cStored);
        Assert.Equal(2, cStored!.DependsOn.Count);
        Assert.Contains(cStored.DependsOn, d => d.ToString() == aDto.Id);
        Assert.Contains(cStored.DependsOn, d => d.ToString() == bDto!.Id);
    }

    private sealed record WorkItemIdResponse(string Id);
}
