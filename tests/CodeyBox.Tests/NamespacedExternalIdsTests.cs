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
    public async Task ListFilter_ByBareExternalId_MatchesValueAcrossAnyNamespace()
    {
        // Two items carry the same bare value under different namespaces; a
        // bare ?externalId= filter (no colon) must return BOTH. This pins the
        // bare-value branch which scans i.ExternalIds.Values for exact match.
        var first = await CreateAsync(new() { ["github"] = "BARE-FILTER-1" });
        var second = await CreateAsync(new() { ["linear"] = "BARE-FILTER-1" });
        await CreateAsync(new() { ["github"] = "OTHER-BARE-FILTER" });

        var list = await _client.GetFromJsonAsync<List<CreateResp>>(
            "/workitems?externalId=BARE-FILTER-1");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        var ids = list.Select(i => i.Id).ToHashSet();
        Assert.Contains(first.Id, ids);
        Assert.Contains(second.Id, ids);
    }

    [Fact]
    public async Task ListFilter_ByBareExternalId_NoMatch_ReturnsEmpty()
    {
        // Confirms the bare-value branch checks Values (not Keys): looking up
        // a string that only appears as a namespace key must not match.
        await CreateAsync(new() { ["github"] = "VALUE-ONLY" });

        var list = await _client.GetFromJsonAsync<List<CreateResp>>(
            "/workitems?externalId=github");
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task ListFilter_ByProjectId_NarrowsToMatchingProject()
    {
        // The /workitems list endpoint accepts an optional ?projectId= filter.
        // Items created in two projects must be filterable via this parameter.
        var inFirst = await CreateAsync(new() { ["github"] = "PROJFILTER-1" });
        var second = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "second-project",
            title = "t",
            prompt = "p",
            externalIds = new Dictionary<string, string> { ["github"] = "PROJFILTER-2" },
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var inSecondDto = await second.Content.ReadFromJsonAsync<CreateResp>();

        // Filter to the first project only.
        var firstOnly = await _client.GetFromJsonAsync<List<CreateResp>>(
            "/workitems?projectId=test-project");
        Assert.NotNull(firstOnly);
        Assert.Contains(firstOnly!, i => i.Id == inFirst.Id);
        Assert.DoesNotContain(firstOnly, i => i.Id == inSecondDto!.Id);

        // Filter to the second project only.
        var secondOnly = await _client.GetFromJsonAsync<List<CreateResp>>(
            "/workitems?projectId=second-project");
        Assert.NotNull(secondOnly);
        Assert.Contains(secondOnly!, i => i.Id == inSecondDto!.Id);
        Assert.DoesNotContain(secondOnly, i => i.Id == inFirst.Id);
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

    // ── Route resolver: composite forms ───────────────────────────────────

    [Fact]
    public async Task RouteResolver_ProjectIdNamespaceValue_ResolvesItem()
    {
        // GET /workitems/{projectId}:{namespace}:{value} must hit the
        // namespaced lookup branch in ResolveWorkItemAsync.
        var created = await CreateAsync(new() { ["github"] = "gh-issue:1234" });

        var get = await _client.GetAsync("/workitems/test-project:github:gh-issue:1234");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("gh-issue:1234", fetched.ExternalIds["github"]);
    }

    [Fact]
    public async Task RouteResolver_ProjectIdBareValue_FallsBackToBareLookup()
    {
        // Without a recognised namespace prefix, the second segment is treated
        // as a bare value and resolves via the bare lookup. Same value lives
        // under a single namespace, so the lookup is unambiguous.
        var created = await CreateAsync(new() { ["github"] = "BARE-ROUTE-1" });

        var get = await _client.GetAsync("/workitems/test-project:BARE-ROUTE-1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<CreateResp>();
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task RouteResolver_BareLookup_AmbiguousAcrossItems_Returns400()
    {
        // Two distinct items share the same bare value via different namespaces;
        // the resolver must translate AmbiguousExternalIdException into a 400
        // with a disambiguation hint (not propagate as 500).
        await CreateAsync(new() { ["github"] = "AMBIG-ROUTE-1" });
        await CreateAsync(new() { ["linear"] = "AMBIG-ROUTE-1" });

        var get = await _client.GetAsync("/workitems/test-project:AMBIG-ROUTE-1");
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        var err = await get.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.Contains("ambiguous", err!.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("github", err.Error);
        Assert.Contains("linear", err.Error);
    }

    [Fact]
    public async Task RouteResolver_NamespacedForm_NotFound_Returns404()
    {
        // Sanity: a well-formed namespaced lookup for an unknown value is 404,
        // not 400 — the format is valid, the row just doesn't exist.
        var get = await _client.GetAsync("/workitems/test-project:github:does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ── PATCH /external-ids: error branches ───────────────────────────────

    [Fact]
    public async Task Patch_NotFound_When_WorkItemDoesNotExist()
    {
        // Resolver returns 404 when the work item id does not exist.
        var missing = Guid.NewGuid();
        var patch = await _client.PatchAsJsonAsync($"/workitems/{missing}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = "gh-issue:99" },
        });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    [Fact]
    public async Task Patch_MissingExternalIdsField_Returns400()
    {
        // Body present but ExternalIds field absent → 400.
        var created = await CreateAsync(new() { ["github"] = "PATCH-FIELD-1" });
        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResp>();
        Assert.Contains("externalIds", err!.Error);
    }

    [Fact]
    public async Task Patch_InvalidValue_ControlChar_Returns400()
    {
        var created = await CreateAsync(new() { ["github"] = "PATCH-VAL-1" });
        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = "hasctrl" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_InvalidValue_WhitespaceOrSlash_Returns400()
    {
        var created = await CreateAsync(new() { ["github"] = "PATCH-VAL-2" });
        var withSpace = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = "has space" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withSpace.StatusCode);

        var withSlash = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = "has/slash" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withSlash.StatusCode);
    }

    [Fact]
    public async Task Patch_InvalidValue_TooLong_Returns400()
    {
        var created = await CreateAsync(new() { ["github"] = "PATCH-VAL-3" });
        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["github"] = new string('a', 257) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_InvalidNamespaceKey_Returns400()
    {
        var created = await CreateAsync(new() { ["github"] = "PATCH-NS-1" });
        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}/external-ids", new
        {
            externalIds = new Dictionary<string, string?> { ["UpperCase"] = "ok-value" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_InvalidValueInDict_Returns400()
    {
        // POST must run ValidateExternalId on every value in the externalIds
        // dict, not just the singular legacy field.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalIds = new Dictionary<string, string> { ["github"] = "has space" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_InvalidNamespaceKey_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "t",
            prompt = "p",
            externalIds = new Dictionary<string, string> { ["UpperCase"] = "ok" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
