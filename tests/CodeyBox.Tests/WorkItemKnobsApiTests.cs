using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Sandbox;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Patch_KnobsReplacesMap_AndIsRejectedAfterDispatch()
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
    public async Task Patch_WithLengthAndControlViolations_Returns400()
    {
        var created = await CreatePlainQueuedItemAsync("patch caps");
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

        var control = new HttpRequestMessage(HttpMethod.Patch, $"/workitems/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = "ref\u0001actor" },
            }),
        };
        var controlResp = await _client.SendAsync(control);
        Assert.Equal(HttpStatusCode.BadRequest, controlResp.StatusCode);
    }

    [Fact]
    public async Task Create_KnobsMapExceedsEntryCap_Returns400WithCapMessage()
    {
        var knobs = new Dictionary<string, string>();
        for (var i = 0; i < 33; i++)
            knobs[$"k{i}"] = "v";
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "too many",
            prompt = "p",
            knobs,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("at most 32 entries", body);
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
    public async Task Create_KnobKeyExceedsLengthCap_Returns400()
    {
        var longKey = new string('k', 65);
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "key too long",
            prompt = "p",
            knobs = new Dictionary<string, string> { [longKey] = "v" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("exceeds 64 chars", body);
    }

    [Fact]
    public async Task Create_KnobValueExceedsLengthCap_Returns400()
    {
        var longValue = new string('v', 129);
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "value too long",
            prompt = "p",
            knobs = new Dictionary<string, string> { [ChangeScopeKnob.KeyName] = longValue },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("exceeds 128 chars", body);
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
        Assert.Contains("control characters", body);
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
        Assert.Contains("control characters", body);
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
        // Confirms the second UPDATE path (TryUpdateIfStateAsync) actually
        // writes knobs_json — the in-memory response could mask a missing
        // column in the column list.
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

    private sealed class WorkItemWithKnobsResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("knobs")] public IReadOnlyDictionary<string, string>? Knobs { get; init; }
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id => "noop-sandbox";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(1, "", "not found"));
    }
}
