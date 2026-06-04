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

    public async Task<bool> RetryWorkItemAsync(string id, string? from = null, CancellationToken ct = default)
    {
        var path = $"/workitems/{Uri.EscapeDataString(id)}/retry";
        var requestedFrom = string.IsNullOrWhiteSpace(from) ? null : from;
        var resp = requestedFrom is null
            ? await _http.PostAsync(path, content: null, ct)
            : await _http.PostAsJsonAsync(path, new { From = requestedFrom }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ReorderWorkItemsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/workitems/reorder", new ReorderRequest { Ids = ids }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<string?> GetStdoutTailAsync(string workItemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(workItemId)}/stdout-tail", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
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

    public async Task<List<AgentPauseStateDto>> GetPausedAgentsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<AgentPauseStateDto>>("/agents/paused", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<AgentPauseStateDto?> PauseAgentAsync(
        string agent,
        string reason,
        double? durationSeconds = null,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/agents/{Uri.EscapeDataString(agent)}/pause",
            new { reason, durationSeconds },
            JsonOptions,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Pause agent failed ({(int)resp.StatusCode}): {body}", null, resp.StatusCode);
        }
        return await resp.Content.ReadFromJsonAsync<AgentPauseStateDto>(JsonOptions, ct);
    }

    public async Task<bool> ResumeAgentAsync(string agent, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/agents/{Uri.EscapeDataString(agent)}/resume",
            new { },
            JsonOptions,
            ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<BudgetUsageDto?> GetBudgetUsageAsync(string projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/projects/{Uri.EscapeDataString(projectId)}/budget/usage", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BudgetUsageDto>(JsonOptions, ct);
    }

    public async Task<ProjectBudgetDto?> GetProjectBudgetAsync(string projectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/projects/{Uri.EscapeDataString(projectId)}/budget", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectBudgetDto>(JsonOptions, ct);
    }

    public async Task<ProjectQueueStateDto?> PauseProjectQueueAsync(string projectId, string reason, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/projects/{Uri.EscapeDataString(projectId)}/queue/pause",
            new { reason }, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectQueueStateDto>(JsonOptions, ct);
    }

    public async Task<ProjectQueueStateDto?> ResumeProjectQueueAsync(string projectId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/projects/{Uri.EscapeDataString(projectId)}/queue/resume",
            new { }, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectQueueStateDto>(JsonOptions, ct);
    }

    public async Task<WorkItemDto?> ReplayWorkItemAsync(string id, ReplayWorkItemRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/workitems/{Uri.EscapeDataString(id)}/replay", req, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions, ct);
    }

    public async Task<WorkItemReplaysDto?> GetReplaysAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}/replays", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemReplaysDto>(JsonOptions, ct);
    }

    public async Task<AuditReportsDto?> GetAuditReportsAsync(string workItemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"/workitems/{Uri.EscapeDataString(workItemId)}/audit-reports", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AuditReportsDto>(JsonOptions, ct);
    }

    public async Task<string?> GetAuditReportRawOutputAsync(
        string workItemId, int iteration, string auditorName, CancellationToken ct = default)
    {
        var url = $"/workitems/{Uri.EscapeDataString(workItemId)}/audit-reports" +
                  $"/{iteration}/{Uri.EscapeDataString(auditorName)}/raw";
        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
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

    public async Task<List<QuestionDto>> GetQuestionsAsync(string workItemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(workItemId)}/questions", ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<QuestionDto>>(JsonOptions, ct) ?? [];
    }

    public async Task<bool> AnswerQuestionAsync(string workItemId, string questionId, string answer, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/workitems/{Uri.EscapeDataString(workItemId)}/answer",
            new { questionId, answer }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DismissQuestionAsync(string workItemId, string questionId, string reason, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/workitems/{Uri.EscapeDataString(workItemId)}/dismiss-question",
            new { questionId, reason }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<PluginDto>> GetAuditorPluginsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PluginDto>>("/plugins", JsonOptions, ct);
        return result ?? [];
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

    public async Task<string?> PromoteSuggestionAsync(
        string id,
        string? extraInstructions = null,
        string? agent = null,
        string? workBranch = null,
        string? baseBranch = null,
        bool? pushUpstream = null,
        string? agentClassId = null,
        string? externalId = null,
        CancellationToken ct = default)
    {
        var body = new { extraInstructions, agent, workBranch, baseBranch, pushUpstream, agentClassId, externalId };
        var resp = await _http.PostAsJsonAsync(
            $"/suggestions/{Uri.EscapeDataString(id)}/promote",
            body, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<PromoteResponse>(JsonOptions, ct);
        return result?.WorkItemId;
    }

    private sealed record PromoteResponse(string WorkItemId);

    public async Task<WorkItemDiffDto?> GetWorkItemDiffAsync(string id, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{Uri.EscapeDataString(id)}/diff");
        req.Headers.Accept.ParseAdd("application/json");
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemDiffDto>(JsonOptions, ct);
    }

    public async Task<WorkItemTimingsDto?> GetWorkItemTimingsAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}/timings", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemTimingsDto>(JsonOptions, ct);
    }

    public async Task<AggregateTimingsDto?> GetAggregateTimingsAsync(int? n = null, CancellationToken ct = default)
    {
        var url = n.HasValue ? $"/workitems/timings/aggregate?n={n}" : "/workitems/timings/aggregate";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AggregateTimingsDto>(JsonOptions, ct);
    }

    public async Task<AgentStreamAggregateDto?> GetWorkItemAgentStreamAggregateAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}/agent-streams/aggregate", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentStreamAggregateDto>(JsonOptions, ct);
    }

    public async Task<AgentStreamAggregateDto?> GetFleetAgentStreamAggregateAsync(int? n = null, CancellationToken ct = default)
    {
        var url = n.HasValue ? $"/workitems/agent-streams/aggregate?n={n}" : "/workitems/agent-streams/aggregate";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentStreamAggregateDto>(JsonOptions, ct);
    }

    public async Task<WorkItemCostsDto?> GetWorkItemCostsAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}/costs", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkItemCostsDto>(JsonOptions, ct);
    }

    public async Task<List<FleetSummaryDto>> GetFleetSummaryAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<FleetSummaryDto>>("/fleet/summary", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<bool> PauseProjectAsync(string projectId, string? reason = null, CancellationToken ct = default)
    {
        // Per-project pause requires the budget-alerts work item; fall back to global pause.
        var result = await PauseQueueAsync(reason ?? $"Fleet pause: {projectId}", ct);
        return result is not null;
    }

    public async Task<bool> ResumeProjectAsync(string projectId, CancellationToken ct = default)
    {
        // Per-project resume requires the budget-alerts work item; fall back to global resume.
        var result = await ResumeQueueAsync(ct);
        return result is not null;
    }

    public async Task<ProjectCostsDto?> GetProjectCostsAsync(
        string projectId, string? from = null, string? to = null, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(from)) parts.Add($"from={Uri.EscapeDataString(from)}");
        if (!string.IsNullOrEmpty(to)) parts.Add($"to={Uri.EscapeDataString(to)}");
        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        var resp = await _http.GetAsync($"/projects/{Uri.EscapeDataString(projectId)}/costs{qs}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ProjectCostsDto>(JsonOptions, ct);
    }

    // ── Releases ──────────────────────────────────────────────────────────────

    public async Task<int> GetOpenReleasesCountAsync(CancellationToken ct = default)
    {
        var releases = await GetReleasesAsync(state: "Open", ct: ct);
        return releases.Count;
    }

    public async Task<List<ReleaseDto>> GetReleasesAsync(string? projectId = null, string? state = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(projectId)) parts.Add($"projectId={Uri.EscapeDataString(projectId)}");
        if (!string.IsNullOrEmpty(state)) parts.Add($"state={Uri.EscapeDataString(state)}");
        if (limit.HasValue) parts.Add($"limit={limit.Value}");
        if (offset.HasValue) parts.Add($"offset={offset.Value}");
        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        var result = await _http.GetFromJsonAsync<List<ReleaseDto>>($"/releases{qs}", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<List<ReleaseAuditIterationDto>> GetReleaseAuditIterationsAsync(string id, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ReleaseAuditIterationDto>>(
            $"/releases/{Uri.EscapeDataString(id)}/audit-iterations", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<ReleaseDto?> GetReleaseAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/releases/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }

    public async Task<List<object>> GetReleaseWorkItemsAsync(string id, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<object>>($"/releases/{Uri.EscapeDataString(id)}/workitems", JsonOptions, ct);
        return result ?? [];
    }

    public async Task<ReleaseDto?> CreateReleaseAsync(CreateReleaseRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/releases", req, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }

    public async Task<ReleaseDto?> CloseReleaseAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/releases/{Uri.EscapeDataString(id)}/close", null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }

    public async Task<ReleaseDto?> ReopenReleaseAsync(string id, string reason, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/releases/{Uri.EscapeDataString(id)}/reopen",
            new ReopenReleaseRequest(reason), JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }

    public async Task<ReleaseDto?> AbandonReleaseAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/releases/{Uri.EscapeDataString(id)}/abandon", null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }

    public async Task<ReleaseDto?> TriggerReleaseAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/releases/{Uri.EscapeDataString(id)}/release",
            new { confirmation = "yes-i-know-the-risk" },
            JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReleaseDto>(JsonOptions, ct);
    }
}
