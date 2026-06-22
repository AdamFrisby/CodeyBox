using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

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
    public async Task Create_WithManyDescriptorRegisteredKnobs_RoundTripsThroughApi()
    {
        using var factory = new WorkItemApiFactory();
        var knobs = Enumerable.Range(0, 40)
            .ToDictionary(i => $"freeForm{i}", i => $"value-{i}");
        factory.AdditionalKnobs.AddRange(
            knobs.Keys.Select(k => new DescriptorLocalStringKnob(k)));
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "many knobs",
            prompt = "p",
            knobs,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.Equal(40, body!.Knobs!.Count);

        var stored = await factory.Store.GetAsync(WorkItemId.Parse(body.Id));
        Assert.NotNull(stored);
        Assert.Equal(40, stored!.Knobs.Count);
        Assert.Equal("value-39", stored.Knobs["freeForm39"]);
    }

    [Fact]
    public async Task Create_KnobKeyCaseInsensitive_AcceptedAndLookupCaseInsensitive()
    {
        // POST with MIXED-case key. The registry normalises storage to the
        // descriptor's canonical key/value casing.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "case-insens",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["CHANGESCOPE"] = "refactor" },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"changeScope\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CHANGESCOPE\"", raw, StringComparison.Ordinal);
        var body = System.Text.Json.JsonSerializer.Deserialize<WorkItemWithKnobsResponse>(raw);
        Assert.NotNull(body!.Knobs);
        Assert.Equal("refactor", body.Knobs![ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsReplacesMap()
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
    public async Task Patch_KnobsCombinedWithTitle_PersistsBothInOneRequest()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "combined title",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var resp = await _client.PatchAsJsonAsync($"/workitems/{created!.Id}", new
        {
            title = "patched title",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal("patched title", stored!.Title);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, stored.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithPrompt_PersistsBothAndBumpsRevision()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "combined prompt",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var resp = await _client.PatchAsJsonAsync($"/workitems/{created!.Id}", new
        {
            prompt = "patched prompt",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal("patched prompt", stored!.Prompt);
        Assert.Equal(2, stored.PromptRevision);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, stored.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithOtherQueuedFields_PersistsAllInOneRequest()
    {
        var created = await CreatePlainQueuedItemAsync("combined queued fields");

        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}", new
        {
            agent = "codex",
            workTimeoutMinutes = 123,
            mergeTimeoutMinutes = 45,
            minModelScore = 88,
            requiredCapabilities = new[] { " sensitive ", "architecture" },
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal(AgentKind.Codex, stored!.Agent);
        Assert.Equal(TimeSpan.FromMinutes(123), stored.WorkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(45), stored.MergeTimeout);
        Assert.Equal(88, stored.MinModelScore);
        Assert.Equal(new[] { "sensitive", "architecture" }, stored.RequiredCapabilities);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, stored.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithDependsOn_PersistsBothInOneRequest()
    {
        var dep = await CreatePlainQueuedItemAsync("dep");
        var target = await CreatePlainQueuedItemAsync("target");

        var resp = await _client.PatchAsJsonAsync($"/workitems/{target.Id}", new
        {
            dependsOn = new[] { dep.Id },
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(target.Id));
        Assert.NotNull(stored);
        Assert.Single(stored!.DependsOn);
        Assert.Equal(WorkItemId.Parse(dep.Id), stored.DependsOn[0]);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, stored.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithAuditBudget_PersistsBothAndAuditsKnobsChanged()
    {
        var created = await CreatePlainQueuedItemAsync("knob audit budget");
        var sink = new TestSink();
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var resp = await _client.PatchAsJsonAsync($"/workitems/{created.Id}", new
        {
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
            auditMaxIterations = 7,
            auditComplexity = "hard",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, stored!.Knobs[ChangeScopeKnob.KeyName]);
        Assert.Equal(7, stored.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);

        var auditEvent = Assert.Single(sink.Events, e =>
            GetScalar<string>(e, "EventName") == "work_item.patched"
            && GetScalar<string>(e, "WorkItemId") == created.Id);
        Assert.True(GetScalar<bool>(auditEvent, "KnobsChanged"));
        Assert.True(GetScalar<bool>(auditEvent, "AuditBudgetChanged"));
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithAuditBudget_UsesSingleGuardedWrite()
    {
        using var factory = new WorkItemApiFactory
        {
            WorkItemStoreDecorator = inner => new RejectSeparateAuditBudgetStore(inner),
        };
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "single guarded audit budget",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var resp = await client.PatchAsJsonAsync($"/workitems/{created!.Id}", new
        {
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
            auditMaxIterations = 7,
            auditComplexity = "hard",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, stored!.Knobs[ChangeScopeKnob.KeyName]);
        Assert.Equal(7, stored.AuditMaxIterations);
        Assert.Equal("hard", stored.AuditComplexity);
    }

    [Fact]
    public async Task Patch_KnobsCombinedWithTitleGuardConflict_Returns409WithoutPersistingRequestFields()
    {
        using var factory = new WorkItemApiFactory
        {
            WorkItemStoreDecorator = inner => new KnobWriteConflictStore(inner),
        };
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "mixed race target",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var resp = await client.PatchAsJsonAsync($"/workitems/{created!.Id}", new
        {
            title = "request title",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var stored = await factory.Store.GetAsync(WorkItemId.Parse(created!.Id));
        Assert.NotNull(stored);
        Assert.Equal("concurrent title", stored!.Title);
        Assert.Empty(stored.Knobs);
    }

    [Fact]
    public async Task Patch_KnobsGuardedWriteConflict_Returns409AndDoesNotReplaceKnobs()
    {
        using var factory = new WorkItemApiFactory
        {
            WorkItemStoreDecorator = inner => new KnobWriteConflictStore(inner),
        };
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "race target",
            prompt = "p",
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var resp = await client.PatchAsJsonAsync($"/workitems/{created!.Id}", new
        {
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var stored = await factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal("concurrent title", stored!.Title);
        Assert.Empty(stored.Knobs);
    }

    [Fact]
    public async Task Patch_Knobs_OnNonQueuedItem_Returns409Conflict()
    {
        // Knobs is in the queuedOnlyPatch gate: once an item leaves Queued, a
        // knob edit must surface 409 so the running pipeline isn't perturbed.
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "running",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created!.Id))
            ?? throw new InvalidOperationException("created item missing from store");
        await _factory.Store.UpdateAsync(stored with { State = WorkItemState.Working });

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor },
            }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
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

    [Fact]
    public async Task Patch_WithInvalidChangeScopeValue_Returns400()
    {
        var created = await CreatePlainQueuedItemAsync("patch invalid");

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "yolo" },
            }),
        };
        var resp = await _client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not allowed", body);
    }

    [Fact]
    public async Task Patch_WithNullKnobValue_Returns400()
    {
        var created = await CreatePlainQueuedItemAsync("patch null");
        var rawJson = """
            {
              "knobs": { "changeScope": null }
            }
            """;
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };

        var resp = await _client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("must not be null", body);
    }

    [Fact]
    public async Task Patch_ChangeScopeValuesRejectedByDescriptor_Returns400()
    {
        var created = await CreatePlainQueuedItemAsync("patch descriptor rejection");
        var longValue = new string('v', 129);
        var tooLong = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = longValue },
            }),
        };
        var tooLongResp = await _client.SendAsync(tooLong);
        Assert.Equal(HttpStatusCode.BadRequest, tooLongResp.StatusCode);
        Assert.Contains("not allowed", await tooLongResp.Content.ReadAsStringAsync());

        var control = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "ref\u0001actor" },
            }),
        };
        var controlResp = await _client.SendAsync(control);
        Assert.Equal(HttpStatusCode.BadRequest, controlResp.StatusCode);
        Assert.Contains("not allowed", await controlResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public void NormaliseKnobs_AllowsMapSizeAcceptedByDescriptors()
    {
        var descriptors = Enumerable.Range(0, 40)
            .Select(i => new DescriptorLocalStringKnob($"freeForm{i}"))
            .Cast<IKnob>()
            .ToArray();
        var registry = new KnobRegistry(descriptors);
        var knobs = new Dictionary<string, string>();
        for (var i = 0; i < 40; i++)
            knobs[$"freeForm{i}"] = $"value-{i}";

        var (normalised, error) = WorkItemCreationService.NormaliseKnobs(knobs, registry);

        Assert.Null(error);
        Assert.NotNull(normalised);
        Assert.Equal(40, normalised!.Count);
        Assert.Equal("value-39", normalised["freeForm39"]);
    }

    [Fact]
    public async Task Create_KnobsWithEmptyKey_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "empty key",
            prompt = "p",
            knobs = new Dictionary<string, string> { [" "] = "value" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("knob key must not be empty", body);
    }

    [Fact]
    public async Task Create_UnknownLongKnobKey_ReturnsRegistryUnknownKeyError()
    {
        var longKey = new string('k', 65);
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "unknown long key",
            prompt = "p",
            knobs = new Dictionary<string, string> { [longKey] = "v" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown knob", body);
    }

    [Fact]
    public async Task Create_DescriptorLocalLongValue_RoundTrips()
    {
        using var factory = new WorkItemApiFactory();
        factory.AdditionalKnobs.Add(new DescriptorLocalStringKnob("longText"));
        using var client = factory.CreateClient();
        var longValue = new string('v', 129);
        var resp = await client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "descriptor long value",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["longText"] = longValue },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.Equal(longValue, body!.Knobs!["longText"]);
    }

    [Fact]
    public async Task Create_KnobValueNull_Returns400()
    {
        // Send raw JSON with an explicit null value to reach the null-value
        // branch — Dictionary<string, string> cannot express null in C# code.
        var rawJson = """
            {
              "projectId": "test-project",
              "title": "null value",
              "prompt": "p",
              "knobs": { "changeScope": null }
            }
            """;
        var content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/workitems", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("must not be null", body);
    }

    [Fact]
    public async Task Create_KnobKeyWithControlChar_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "control in key",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["badkey"] = "v" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown knob", body);
    }

    [Fact]
    public async Task Create_KnobValueWithControlChar_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "control in value",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "refactor" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not allowed", body);
    }

    [Fact]
    public async Task Patch_PromptWithInvalidKnobs_Returns400WithoutPersistingPrompt()
    {
        var created = await CreatePlainQueuedItemAsync("atomic patch");

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                prompt = "mutated prompt",
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "yolo" },
            }),
        };
        var resp = await _client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal("p", stored!.Prompt);
        Assert.Equal(1, stored.PromptRevision);
    }

    [Fact]
    public async Task Create_KnobsRoundTripsThroughStore()
    {
        // Round-trip via a fresh store read (not the in-memory snapshot
        // returned by POST). Pins SerialiseKnobs / ReadKnobs / the
        // CreateAsync $knobs binding.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "store round-trip",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var get = await _client.GetAsync($"/workitems/{created!.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        Assert.NotNull(fetched!.Knobs);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, fetched.Knobs![ChangeScopeKnob.KeyName]);

        // Also verify direct store read sees the persisted map.
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Single(stored!.Knobs);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, stored.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_KnobsReplacesMap_PersistsToStore()
    {
        // Confirms the queued-field UPDATE path actually writes knobs_json —
        // the in-memory response could mask a missing column in the column list.
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch store",
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

        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, stored!.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Patch_TitleOnly_PreservesExistingKnobs()
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "patch title only",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical },
        });
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created!.Id}")
        {
            Content = JsonContent.Create(new { title = "patched title" }),
        };
        var resp = await _client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var stored = await _factory.Store.GetAsync(WorkItemId.Parse(created.Id));
        Assert.NotNull(stored);
        Assert.Equal(ChangeScopeKnob.ValueSurgical, stored!.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task Create_MixedCaseKnobKeyAndValue_RespondsWithCanonicalCasing()
    {
        // Sends both a non-canonical key casing AND a non-canonical value
        // casing; asserts the raw JSON response carries the registered
        // canonical strings so dashboards rendering off the GET body don't
        // see operator's mixed casing.
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "canon casing",
            prompt = "p",
            knobs = new Dictionary<string, string> { ["ChangeScope"] = "Refactor" },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();

        var get = await _client.GetAsync($"/workitems/{created!.Id}");
        var raw = await get.Content.ReadAsStringAsync();
        Assert.Contains("\"changeScope\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ChangeScope\"", raw, StringComparison.Ordinal);

        var fetched = System.Text.Json.JsonSerializer.Deserialize<WorkItemWithKnobsResponse>(raw);
        Assert.NotNull(fetched!.Knobs);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, fetched.Knobs![ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task AppPromptPreprocessorChain_IncludesChangeScopeFragmentFromRegisteredKnob()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "prompt chain",
            Prompt = "p",
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueSurgical,
            },
        };
        await _factory.Store.CreateAsync(item);

        var chain = _factory.Services.GetRequiredService<AgentPromptPreprocessorChain>();
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "https://github.com/test/repo",
        };
        var result = await chain.ProcessAsync(
            new PromptContext(
                item.Id,
                AgentKind.Claude,
                AgentPromptPhase.Work,
                Iteration: 1,
                project,
                new NoopSandbox(),
                "/work"),
            "original prompt");

        Assert.Contains("Per-item directives (knobs)", result);
        Assert.Contains("changeScope=surgical", result);
        Assert.Contains("SURGICAL", result);
    }

    private async Task<WorkItemWithKnobsResponse> CreatePlainQueuedItemAsync(string title)
    {
        var create = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title,
            prompt = "p",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<WorkItemWithKnobsResponse>();
        return created!;
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }

    private sealed class WorkItemWithKnobsResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("knobs")] public IReadOnlyDictionary<string, string>? Knobs { get; init; }
    }

    private sealed class DescriptorLocalStringKnob(string key) : IKnob
    {
        public string Key { get; } = key;
        public string Description => $"descriptor-local string knob {Key}";
        public IReadOnlyList<string> AllowedValues => [];
        public string DefaultValue => "default";
        public string? GetWorkPromptFragment(string value) => null;
    }

    private sealed class KnobWriteConflictStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        public override async Task<bool> TryReplaceKnobsIfStateAndUpdatedAtAsync(
            WorkItemId id,
            IReadOnlyDictionary<string, string> knobs,
            DateTimeOffset updatedAt,
            WorkItemState onlyIfState,
            DateTimeOffset onlyIfUpdatedAt,
            CancellationToken ct = default)
        {
            await PersistConcurrentTitleAsync(id, updatedAt, onlyIfState, onlyIfUpdatedAt, ct);

            return false;
        }

        public override async Task<bool> TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
            WorkItem item,
            WorkItemState onlyIfState,
            DateTimeOffset onlyIfUpdatedAt,
            CancellationToken ct = default)
        {
            await PersistConcurrentTitleAsync(item.Id, item.UpdatedAt, onlyIfState, onlyIfUpdatedAt, ct);

            return false;
        }

        private async Task PersistConcurrentTitleAsync(
            WorkItemId id,
            DateTimeOffset updatedAt,
            WorkItemState onlyIfState,
            DateTimeOffset onlyIfUpdatedAt,
            CancellationToken ct)
        {
            var current = await Inner.GetAsync(id, ct);
            if (current is null) return;

            var concurrent = current with
            {
                Title = "concurrent title",
                UpdatedAt = updatedAt.AddTicks(1),
            };
            await Inner.TryUpdateIfStateAndUpdatedAtAsync(
                concurrent,
                onlyIfState,
                onlyIfUpdatedAt,
                ct);
        }
    }

    private sealed class RejectSeparateAuditBudgetStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        public override Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(
            WorkItemId id,
            int? auditMaxIterations,
            string? auditComplexity,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("knob+audit PATCH must use the combined guarded write");
    }

    private abstract class ForwardingWorkItemStore(SqliteWorkItemStore inner) : IWorkItemStore
    {
        protected SqliteWorkItemStore Inner { get; } = inner;

        public virtual Task CreateAsync(WorkItem item, CancellationToken ct = default) => Inner.CreateAsync(item, ct);
        public virtual Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Inner.UpdateAsync(item, ct);
        public virtual Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            Inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public virtual Task<bool> TryUpdateIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
            Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
        public virtual Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public virtual Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, WorkItemState onlyIfState, CancellationToken ct = default) =>
            Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
        public virtual Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public virtual Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public virtual Task<bool> TryReplaceKnobsIfStateAndUpdatedAtAsync(WorkItemId id, IReadOnlyDictionary<string, string> knobs, DateTimeOffset updatedAt, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
            Inner.TryReplaceKnobsIfStateAndUpdatedAtAsync(id, knobs, updatedAt, onlyIfState, onlyIfUpdatedAt, ct);
        public virtual Task<bool> TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
            Inner.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
        public virtual Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Inner.GetAsync(id, ct);
        public virtual IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);
        public virtual IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.ListByStateAsync(state, ct);
        public virtual Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.CountByStateAsync(state, ct);
        public virtual Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Inner.ReorderAsync(orderedIds, ct);
        public virtual IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
            Inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
        public virtual Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            Inner.CountStartedInWindowAsync(projectId, since, ct);
        public virtual Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Inner.CountInFlightAsync(projectId, ct);
        public virtual Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(ProjectId projectId, CancellationToken ct = default, WorkItemId? excludeId = null) =>
            Inner.CountInFlightSplitByRefactorAsync(projectId, ct, excludeId);
        public virtual Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            Inner.GetByExternalIdAsync(projectId, externalId, ct);
        public virtual Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            Inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public virtual Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public virtual Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            Inner.GetFleetStateCountsAsync(ct);
        public virtual Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            Inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public virtual Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
            Inner.GetFleetPauseStatesAsync(ct);
        public virtual IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            Inner.ListByReplaySourceAsync(sourceId, ct);
        public virtual IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Inner.ListSuspendedAsync(ct);
        public virtual Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
            Inner.GetActiveBaselineImageRefsAsync(ct);
        public virtual Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
            Inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public virtual Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Inner.OrphanReplaysAsync(sourceId, ct);
        public virtual IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Inner.ListByReleaseAsync(releaseId, ct);
        public virtual Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            Inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public virtual Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            Inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public virtual Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            Inner.GetIterationsAsync(workItemId, ct);
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id => "noop-sandbox";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(1, "", "not found"));
    }
}
