using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the per-project uniqueness constraint on externalId:
/// - same externalId in same project → 400
/// - same externalId in different projects → allowed
/// </summary>
public sealed class ExternalIdUniquenessTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ExternalIdUniquenessTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object Body(string extId) => new
    {
        projectId = "test-project",
        title = "t",
        prompt = "p",
        externalId = extId,
    };

    [Fact]
    public async Task SameExternalId_SameProject_Rejected()
    {
        var r1 = await _client.PostAsJsonAsync("/workitems", Body("JIRA-99"));
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var r2 = await _client.PostAsJsonAsync("/workitems", Body("JIRA-99"));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);

        var err = await r2.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(err);
        Assert.Contains("JIRA-99", err.Error);
        Assert.Contains("already exists", err.Error);
    }

    [Fact]
    public async Task SameExternalId_DifferentProjects_Allowed()
    {
        // Both "test-project" and "second-project" are seeded in the factory's
        // InMemoryProjectRepository. The unique constraint is per-project, so the
        // same externalId in two different projects must both succeed.
        var r1 = await _client.PostAsJsonAsync("/workitems", Body("XT-1"));
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var r2 = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "second-project",
            title = "t",
            prompt = "p",
            externalId = "XT-1",
        });
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        var item1 = await _factory.Store.GetByExternalIdAsync(new ProjectId("test-project"), "XT-1");
        var item2 = await _factory.Store.GetByExternalIdAsync(new ProjectId("second-project"), "XT-1");
        Assert.NotNull(item1);
        Assert.NotNull(item2);
        Assert.NotEqual(item1!.Id, item2!.Id);
    }

    [Fact]
    public async Task NoExternalId_MultipleItems_AllSucceed()
    {
        // Items without externalId must coexist freely (nulls are not unique-constrained).
        var r1 = await _client.PostAsJsonAsync("/workitems", new { projectId = "test-project", title = "a", prompt = "p" });
        var r2 = await _client.PostAsJsonAsync("/workitems", new { projectId = "test-project", title = "b", prompt = "p" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
    }

    [Fact]
    public async Task ExternalId_RoundTrip_PersistedAndReturned()
    {
        var r = await _client.PostAsJsonAsync("/workitems", Body("ROUND-1"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var dto = await r.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(dto);
        Assert.Equal("ROUND-1", dto!.ExternalId);
    }

    private sealed record ErrorResponse(string Error);
    private sealed record WorkItemResponse(string Id, string? ExternalId);
}
