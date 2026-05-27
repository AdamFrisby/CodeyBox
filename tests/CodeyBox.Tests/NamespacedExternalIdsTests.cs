using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage of the namespaced <c>externalIds</c> work item field:
/// create with multiple namespaces, PATCH merge/delete/replace semantics,
/// per-(project, namespace, value) uniqueness, dependency resolution by both
/// namespaced and bare values (with ambiguity errors), and webhook-payload
/// dual emission of legacy <c>externalId</c> + new <c>externalIds</c>.
/// </summary>
public sealed class NamespacedExternalIdsTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public NamespacedExternalIdsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed record CreateResp(string Id, string? ExternalId, Dictionary<string, string> ExternalIds);
    private sealed record ErrorResp(string Error);

    [Fact]
    public async Task Create_WithMultipleNamespaces_RoundTrips()
    {
        var body = new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalIds = new Dictionary<string, string>
            {
                ["jobtrack"] = "jt-178",
                ["github"] = "gh-issue:1234",
            },
        };
        var post = await _client.PostAsJsonAsync("/workitems", body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var dto = await post.Content.ReadFromJsonAsync<CreateResp>();
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.ExternalIds.Count);
        Assert.Equal("jt-178", dto.ExternalIds["jobtrack"]);
        Assert.Equal("gh-issue:1234", dto.ExternalIds["github"]);

        var get = await _client.GetAsync($"/workitems/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal(2, fetched!.ExternalIds.Count);
        Assert.Equal("jt-178", fetched.ExternalIds["jobtrack"]);
        Assert.Equal("gh-issue:1234", fetched.ExternalIds["github"]);
    }

    [Fact]
    public async Task Patch_AddsThirdNamespace_MergesByDefault()
    {
        var created = await CreateAsync(new()
        {
            ["jobtrack"] = "jt-200",
            ["github"] = "gh-issue:200",
        });

        var patch = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["linear"] = "LIN-200" },
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var get = await _client.GetAsync($"/workitems/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal(3, fetched!.ExternalIds.Count);
        Assert.Equal("jt-200", fetched.ExternalIds["jobtrack"]);
        Assert.Equal("gh-issue:200", fetched.ExternalIds["github"]);
        Assert.Equal("LIN-200", fetched.ExternalIds["linear"]);
    }

    [Fact]
    public async Task Patch_NullValue_DeletesKey()
    {
        var created = await CreateAsync(new()
        {
            ["jobtrack"] = "jt-301",
            ["github"] = "gh-issue:301",
            ["linear"] = "LIN-301",
        });

        var patch = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["linear"] = null },
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var get = await _client.GetAsync($"/workitems/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal(2, fetched!.ExternalIds.Count);
        Assert.False(fetched.ExternalIds.ContainsKey("linear"));
        Assert.Contains("jobtrack", fetched.ExternalIds.Keys);
        Assert.Contains("github", fetched.ExternalIds.Keys);
    }

    [Fact]
    public async Task Patch_WithReplaceFlag_OverwritesWholeMap()
    {
        var created = await CreateAsync(new()
        {
            ["jobtrack"] = "jt-400",
            ["github"] = "gh-issue:400",
        });

        var patch = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["linear"] = "LIN-400" },
            replaceExternalIds = true,
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var get = await _client.GetAsync($"/workitems/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Single(fetched!.ExternalIds);
        Assert.Equal("LIN-400", fetched.ExternalIds["linear"]);
    }

    [Fact]
    public async Task Conflict_OnNamespaceAndValuePair_Returns409()
    {
        var first = await CreateAsync(new() { ["github"] = "gh-issue:500" });

        // Second item; PATCH attempts to take the same (namespace, value) pair.
        var second = await CreateAsync(new() { ["jobtrack"] = "jt-500" });
        var patch = await _client.PatchAsJsonAsync($"/workitems/{second.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = "gh-issue:500" },
        });
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);

        var body = await patch.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.NotNull(body);
        Assert.Contains("github", body!.Error);
        Assert.Contains("gh-issue:500", body.Error);
        Assert.Contains(first.Id, body.Error);
    }

    [Fact]
    public async Task Conflict_OnCreate_WithSameNamespaceAndValue_Returns400()
    {
        await CreateAsync(new() { ["jobtrack"] = "jt-600" });
        var dup = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalIds = new Dictionary<string, string> { ["jobtrack"] = "jt-600" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        var body = await dup.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.Contains("jobtrack", body!.Error);
        Assert.Contains("jt-600", body.Error);
    }

    [Fact]
    public async Task Dependency_ByNamespacedId_Resolves()
    {
        var a = await CreateAsync(new() { ["jobtrack"] = "jt-700" });
        var b = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "b",
            prompt = "p",
            dependsOn = new[] { "jobtrack:jt-700" },
        });
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);

        var bDto = await b.Content.ReadFromJsonAsync<JsonElement>();
        var bId = bDto.GetProperty("id").GetString();
        var stored = await _factory.Store.GetAsync(new WorkItemId(Guid.Parse(bId!)));
        Assert.NotNull(stored);
        Assert.Single(stored!.DependsOn);
        Assert.Equal(a.Id, stored.DependsOn[0].ToString());
    }

    [Fact]
    public async Task Dependency_ByBareId_Resolves_WhenUnambiguous()
    {
        var a = await CreateAsync(new() { ["github"] = "BARE-800" });
        var b = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "b",
            prompt = "p",
            dependsOn = new[] { "BARE-800" },
        });
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        var bDto = await b.Content.ReadFromJsonAsync<JsonElement>();
        var bId = bDto.GetProperty("id").GetString();
        var stored = await _factory.Store.GetAsync(new WorkItemId(Guid.Parse(bId!)));
        Assert.Equal(a.Id, stored!.DependsOn[0].ToString());
    }

    [Fact]
    public async Task Dependency_ByBareId_Errors_WhenAmbiguous()
    {
        // Same bare value present in two different items via different namespaces.
        await CreateAsync(new() { ["github"] = "AMBIG-900" });
        await CreateAsync(new() { ["linear"] = "AMBIG-900" });

        var b = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "b",
            prompt = "p",
            dependsOn = new[] { "AMBIG-900" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, b.StatusCode);
        var err = await b.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.Contains("ambiguous", err!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListFilter_ByNamespacedExternalId_ReturnsMatchingItem()
    {
        await CreateAsync(new() { ["github"] = "FILTER-1000" });
        await CreateAsync(new() { ["jobtrack"] = "jt-1000" });

        var list = await _client.GetFromJsonAsync<List<CreateResp>>(
            "/workitems?externalId=github:FILTER-1000");
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("FILTER-1000", list![0].ExternalIds["github"]);
    }

    [Fact]
    public async Task Webhook_Payload_IncludesBothLegacyAndNamespaced()
    {
        // Build a work item carrying both a legacy ('legacy' namespace) value
        // and a namespaced value; serialise the payload that the dispatcher
        // would send and assert both fields land in the JSON.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "test",
            Prompt = "p",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = "OLD-1",
                ["github"] = "gh-issue:1",
            },
        };
        var project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/r.git",
        };
        var evt = new WebhookEvent
        {
            Event = "work_item.done",
            WorkItem = item,
            Project = project,
        };

        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);
        var wi = doc.RootElement.GetProperty("workItem");

        Assert.True(wi.TryGetProperty("externalId", out var legacyProp));
        Assert.Equal("OLD-1", legacyProp.GetString());
        Assert.True(wi.TryGetProperty("externalIds", out var dictProp));
        Assert.Equal("OLD-1", dictProp.GetProperty("legacy").GetString());
        Assert.Equal("gh-issue:1", dictProp.GetProperty("github").GetString());
    }

    [Fact]
    public async Task LegacyExternalIdSingular_RoundTripsViaLegacyNamespace()
    {
        // POST with the singular field; GET should expose it both at
        // externalId (legacy projection) and under externalIds['legacy'].
        var post = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalId = "LEGACY-1100",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var dto = await post.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal("LEGACY-1100", dto!.ExternalId);
        Assert.Equal("LEGACY-1100", dto.ExternalIds["legacy"]);
    }

    [Fact]
    public async Task BothFields_OnCreate_MustAgree()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalId = "X",
            externalIds = new Dictionary<string, string> { ["legacy"] = "Y" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.Contains("disagree", err!.Error);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<CreateResp> CreateAsync(Dictionary<string, string> externalIds)
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalIds,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<CreateResp>();
        Assert.NotNull(dto);
        return dto!;
    }
}
