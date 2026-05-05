using System.Net.Http.Json;
using CodeyBox.Cli.Models;

namespace CodeyBox.Cli.Services;

internal sealed class CodeyBoxApiException(int statusCode, string body)
    : Exception($"HTTP {statusCode}: {body}")
{
    internal int StatusCode { get; } = statusCode;
}

internal sealed class CodeyBoxClient
{
    private readonly HttpClient _http;

    internal CodeyBoxClient(HttpClient http) => _http = http;

    internal static CodeyBoxClient Create(ResolvedConfig config)
    {
        var http = new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) };
        if (!string.IsNullOrEmpty(config.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        return new CodeyBoxClient(http);
    }

    internal async Task<List<WorkItemDto>> GetWorkItemsAsync(
        string? project = null, string? state = null, int? limit = null,
        CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (project is not null) parts.Add($"project={Uri.EscapeDataString(project)}");
        if (state is not null) parts.Add($"state={Uri.EscapeDataString(state)}");
        if (limit is not null) parts.Add($"limit={limit}");
        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : "";

        var resp = await _http.GetAsync($"/workitems{qs}", ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.ListWorkItemDto, ct) ?? [];
    }

    internal async Task<WorkItemDto?> GetWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct);
    }

    internal async Task<WorkItemDto> CreateWorkItemAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/workitems", req, CliJsonContext.Default.CreateWorkItemRequest, ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    internal async Task DeleteWorkItemAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/workitems/{Uri.EscapeDataString(id)}", ct);
        await EnsureSuccessAsync(resp, ct);
    }

    internal async Task<WorkItemDto> RetryWorkItemAsync(string id, string from = "work", CancellationToken ct = default)
    {
        var req = new RetryRequest { From = from };
        var resp = await _http.PostAsJsonAsync(
            $"/workitems/{Uri.EscapeDataString(id)}/retry",
            req,
            CliJsonContext.Default.RetryRequest,
            ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(CliJsonContext.Default.WorkItemDto, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new CodeyBoxApiException((int)resp.StatusCode, body);
    }
}
