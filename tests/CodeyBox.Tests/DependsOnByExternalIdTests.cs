using System.Net;
using System.Net.Http.Json;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that dependsOn entries that are externalIds (not UUIDs) are resolved
/// to the internal UUID at create time and stored correctly.
/// </summary>
public sealed class DependsOnByExternalIdTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public DependsOnByExternalIdTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task DependsOn_ByExternalId_ResolvesToInternalUuid()
    {
        // Create A with externalId "JIRA-1"
        var rA = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "A",
            prompt = "p",
            externalId = "JIRA-1",
        });
        Assert.Equal(HttpStatusCode.Created, rA.StatusCode);
        var aDto = await rA.Content.ReadFromJsonAsync<WorkItemIdResponse>();
        Assert.NotNull(aDto);

        // Create B with dependsOn = ["JIRA-1"] (externalId, not UUID)
        var rB = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "B",
            prompt = "p",
            dependsOn = new[] { "JIRA-1" },
        });
        Assert.Equal(HttpStatusCode.Created, rB.StatusCode);
        var bDto = await rB.Content.ReadFromJsonAsync<WorkItemIdResponse>();
        Assert.NotNull(bDto);

        // Verify B's stored dependsOn contains A's UUID, not the externalId string
        var bStored = await _factory.Store.GetAsync(new CodeyBox.Core.WorkItemId(Guid.Parse(bDto!.Id)));
        Assert.NotNull(bStored);
        Assert.Single(bStored!.DependsOn);
        Assert.Equal(aDto!.Id, bStored.DependsOn[0].ToString());
    }

    [Fact]
    public async Task DependsOn_ByExternalId_SameLinkage_AsUuid()
    {
        // Create A with externalId
        var rA = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "A",
            prompt = "p",
            externalId = "EXT-100",
        });
        Assert.Equal(HttpStatusCode.Created, rA.StatusCode);
        var aDto = await rA.Content.ReadFromJsonAsync<WorkItemIdResponse>();

        // B depends on A via externalId
        var rByExt = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "B-ext",
            prompt = "p",
            dependsOn = new[] { "EXT-100" },
        });
        Assert.Equal(HttpStatusCode.Created, rByExt.StatusCode);

        // C depends on A via UUID
        var rByUuid = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "C-uuid",
            prompt = "p",
            dependsOn = new[] { aDto!.Id },
        });
        Assert.Equal(HttpStatusCode.Created, rByUuid.StatusCode);

        var bDto = await rByExt.Content.ReadFromJsonAsync<WorkItemIdResponse>();
        var cDto = await rByUuid.Content.ReadFromJsonAsync<WorkItemIdResponse>();

        var bStored = await _factory.Store.GetAsync(new CodeyBox.Core.WorkItemId(Guid.Parse(bDto!.Id)));
        var cStored = await _factory.Store.GetAsync(new CodeyBox.Core.WorkItemId(Guid.Parse(cDto!.Id)));

        // Both B and C should depend on A's UUID
        Assert.Equal(aDto.Id, bStored!.DependsOn[0].ToString());
        Assert.Equal(aDto.Id, cStored!.DependsOn[0].ToString());
    }

    private sealed record WorkItemIdResponse(string Id);
}
