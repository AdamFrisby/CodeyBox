using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class WorkItemAuditorProfileApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-profile-api-{Guid.NewGuid():N}.db");
    private WorkItemApiFactory? _factory;
    private HttpClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    [Fact]
    public async Task Create_WithUnknownAuditorProfile_Returns400WithAvailableProfiles()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "profile test",
            prompt = "p",
            auditorProfile = "missing",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("unknown auditorProfile 'missing'", doc.RootElement.GetProperty("error").GetString());
        var available = doc.RootElement
            .GetProperty("availableProfiles")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Equal(["default", "uat"], available);
    }

    [Fact]
    public async Task Create_WithAuditorProfile_RoundTripsAfterApiRestart()
    {
        _factory = NewFactory();
        _client = _factory.CreateClient();

        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "profile test",
            prompt = "p",
            auditorProfile = "uat",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("uat", created!.AuditorProfile);

        _client.Dispose();
        _factory.Dispose();

        _factory = NewFactory();
        _client = _factory.CreateClient();

        var fetched = await _client.GetFromJsonAsync<WorkItemResponse>($"/workitems/{created.Id}");
        Assert.Equal("uat", fetched!.AuditorProfile);
    }

    private WorkItemApiFactory NewFactory() => new(_dbPath, new Project
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://github.com/test/repo",
        Audit = new ProjectAudit
        {
            Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
            {
                ["uat"] = new() { AuditTypes = ["tests"] },
            },
        },
    });

    private sealed record WorkItemResponse(string Id, string? AuditorProfile);
}
