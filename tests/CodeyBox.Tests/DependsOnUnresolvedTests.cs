using System.Net;
using System.Net.Http.Json;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that referencing an externalId that does not exist returns 400
/// with a helpful error message.
/// </summary>
public sealed class DependsOnUnresolvedTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public DependsOnUnresolvedTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task DependsOn_UnknownExternalId_Returns400()
    {
        var r = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            dependsOn = new[] { "DOES-NOT-EXIST" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(err);
        Assert.Contains("DOES-NOT-EXIST", err!.Error);
    }

    [Fact]
    public async Task DependsOn_UnknownUuid_Returns400()
    {
        var unknownUuid = Guid.NewGuid().ToString();
        var r = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            dependsOn = new[] { unknownUuid },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task DependsOn_MixedKnownAndUnknown_Returns400()
    {
        // Create one valid item
        var rA = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "A",
            prompt = "p",
            externalId = "KNOWN-1",
        });
        Assert.Equal(HttpStatusCode.Created, rA.StatusCode);

        // Depend on both the known item and an unknown externalId
        var r = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            dependsOn = new[] { "KNOWN-1", "UNKNOWN-99" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        var err = await r.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("UNKNOWN-99", err!.Error);
    }

    private sealed record ErrorResponse(string Error);
}
