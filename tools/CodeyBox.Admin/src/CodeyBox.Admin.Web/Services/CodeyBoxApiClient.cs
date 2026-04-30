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
}
