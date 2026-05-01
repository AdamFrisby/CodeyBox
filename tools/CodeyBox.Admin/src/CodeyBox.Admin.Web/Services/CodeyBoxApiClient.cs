using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Admin.Web.Models;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Typed HTTP client for the CodeyBox orchestrator REST API.
/// Registered with IHttpClientFactory; base address and bearer token
/// are set in Program.cs from configuration and environment variables.
/// </summary>
public sealed class CodeyBoxApiClient : ICodeyBoxApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CodeyBoxApiClient(HttpClient http) => _http = http;

    public async Task<List<WorkItemDto>> GetWorkItemsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<WorkItemDto>>("/workitems", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions, ct);
    }

    public async Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ProjectDto>>("/projects", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<WorkItemDto?> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/workitems", req, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions, ct);
    }

    public async Task<WorkItemDto?> PatchWorkItemAsync(string id, PatchWorkItemRequest req, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(req, options: JsonOptions);
        var resp = await _http.PatchAsync($"/workitems/{Uri.EscapeDataString(id)}", content, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions, ct);
    }

    public async Task<bool> DeleteWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/workitems/{Uri.EscapeDataString(id)}/retry",
            new { From = from },
            JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/workitems/reorder", new ReorderRequest { Ids = ids }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/queue/status", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<QueueStatusDto>(JsonOptions, ct);
    }

    public async Task<QueueStatusDto?> PauseQueueAsync(string reason, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/queue/pause", new { reason }, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Pause failed ({(int)resp.StatusCode}): {body}", null, resp.StatusCode);
        }
        return await resp.Content.ReadFromJsonAsync<QueueStatusDto>(JsonOptions, ct);
    }

    public async Task<QueueStatusDto?> ResumeQueueAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/queue/resume", new { }, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Resume failed ({(int)resp.StatusCode}): {body}", null, resp.StatusCode);
        }
        return await resp.Content.ReadFromJsonAsync<QueueStatusDto>(JsonOptions, ct);
    }

    public async Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/projects/{Uri.EscapeDataString(projectId)}/budget/usage", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BudgetUsageDto>(JsonOptions, ct);
    }

    public async Task<WorkItemTimelineDto?> GetWorkItemTimelineAsync(
        string id, string? kind = null, string? since = null, int? iteration = null,
        CancellationToken ct = default)
    {
        var qs = BuildTimelineQueryString(kind, since, iteration);
        var url = $"/workitems/{Uri.EscapeDataString(id)}/timeline{qs}";
        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemTimelineDto>(JsonOptions, ct);
    }

    private static string BuildTimelineQueryString(string? kind, string? since, int? iteration)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(kind))
            parts.Add($"kind={Uri.EscapeDataString(kind)}");
        if (!string.IsNullOrWhiteSpace(since))
            parts.Add($"since={Uri.EscapeDataString(since)}");
        if (iteration is { } i)
            parts.Add($"iteration={i}");
        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    public async Task<List<SuggestionDto>> GetSuggestionsAsync(
        string? projectId = null, string? category = null, string? severity = null,
        CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectId)) parts.Add($"project={Uri.EscapeDataString(projectId)}");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(severity)) parts.Add($"severity={Uri.EscapeDataString(severity)}");
        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        var page = await _http.GetFromJsonAsync<SuggestionsPage>($"/suggestions{qs}", JsonOptions, ct);
        return page?.Items ?? [];
    }

    public async Task<int> GetSuggestionsCountAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<SuggestionsCountResult>("/suggestions/count", JsonOptions, ct);
        return result?.Count ?? 0;
    }

    private sealed record SuggestionsPage(List<SuggestionDto> Items, int Total, int Offset, int Limit);
    private sealed record SuggestionsCountResult(int Count);

    public async Task<SuggestionDto?> GetSuggestionAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/suggestions/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SuggestionDto>(JsonOptions, ct);
    }

    public async Task<SuggestionDto?> DismissSuggestionAsync(string id, string? reason = null,
        CancellationToken ct = default)
    {
        var body = new { state = "dismissed", dismissReason = reason };
        using var content = JsonContent.Create(body, options: JsonOptions);
        var resp = await _http.PatchAsync($"/suggestions/{Uri.EscapeDataString(id)}", content, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SuggestionDto>(JsonOptions, ct);
    }

    public async Task<bool> PromoteSuggestionAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/suggestions/{Uri.EscapeDataString(id)}/promote",
            new { }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }
}
