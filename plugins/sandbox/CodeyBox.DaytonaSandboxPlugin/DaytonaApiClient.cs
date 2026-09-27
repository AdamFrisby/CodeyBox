using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// A resolved Daytona endpoint: the options plus the API key material resolved
/// from the host credential chain for one call. The key rides every request;
/// it is never stored on the options record (and never logged — error bodies
/// are scrubbed before they surface in exceptions).
/// </summary>
internal sealed record DaytonaEndpoint(
    Uri ApiBaseUri,
    Uri ToolboxProxyBaseUri,
    string ApiKey,
    string? OrganizationId,
    bool AllowUnsafeHttp = false);

/// <summary>
/// Thin REST client for the Daytona control plane (<c>{ApiUrl}/sandbox</c>,
/// <c>{ApiUrl}/snapshots</c>). Every non-2xx response becomes a
/// <see cref="DaytonaApiException"/> carrying the failure taxonomy the
/// provider maps onto infrastructure deferrals — callers never inspect status
/// codes themselves.
/// </summary>
internal sealed class DaytonaApiClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Error bodies are untrusted remote text: bounded before they are folded
    // into exception messages and logs.
    private const int MaxErrorBodyChars = 2048;

    // Control-plane list page size for managed-sandbox inventory scans.
    private const int ListPageSize = 200;

    private readonly HttpClient _http;

    public DaytonaApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<DaytonaSandboxDto> CreateSandboxAsync(
        DaytonaEndpoint endpoint, DaytonaCreateSandboxRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(endpoint, HttpMethod.Post, "sandbox", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<DaytonaSandboxDto>(response, "create sandbox", ct).ConfigureAwait(false);
        return dto ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "create sandbox", "empty response body");
    }

    /// <summary>Returns null when the sandbox does not exist (404).</summary>
    public async Task<DaytonaSandboxDto?> GetSandboxAsync(
        DaytonaEndpoint endpoint, string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"sandbox/{Uri.EscapeDataString(idOrName)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<DaytonaSandboxDto>(response, "get sandbox", ct).ConfigureAwait(false);
        return dto ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "get sandbox", "empty response body");
    }

    /// <summary>
    /// Pages <c>GET /sandbox</c> filtered to provider-owned sandboxes: the
    /// managed label AND the configured name prefix are both sent server-side
    /// and re-verified client-side (a prefix filter on a hosted service is a
    /// hint, never a proof of ownership).
    /// </summary>
    public async Task<IReadOnlyList<DaytonaSandboxListItem>> ListManagedSandboxesAsync(
        DaytonaEndpoint endpoint, string namePrefix, int maxPages, CancellationToken ct)
    {
        var labelsFilter = Uri.EscapeDataString(
            $"{{\"{DaytonaSandboxOptions.ManagedLabelKey}\":\"{DaytonaSandboxOptions.ManagedLabelValue}\"}}");
        var result = new List<DaytonaSandboxListItem>();
        string? cursor = null;
        for (var page = 0; page < maxPages; page++)
        {
            var query = $"name={Uri.EscapeDataString(namePrefix)}&labels={labelsFilter}&limit={ListPageSize.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(cursor))
                query += $"&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await SendAsync(
                endpoint, HttpMethod.Get, $"sandbox?{query}", null, ct).ConfigureAwait(false);
            var pageResult = await ReadJsonAsync<DaytonaListSandboxesResponse>(response, "list sandboxes", ct)
                .ConfigureAwait(false)
                ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "list sandboxes", "empty response body");
            foreach (var item in pageResult.Items)
            {
                if (item.Name is not null &&
                    item.Name.StartsWith(namePrefix, StringComparison.Ordinal) &&
                    item.Labels is not null &&
                    item.Labels.TryGetValue(DaytonaSandboxOptions.ManagedLabelKey, out var managed) &&
                    string.Equals(managed, DaytonaSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
                {
                    result.Add(item);
                }
            }
            if (string.IsNullOrEmpty(pageResult.NextCursor))
                return result;
            cursor = pageResult.NextCursor;
        }
        throw new DaytonaApiException(
            DaytonaFailureKind.Unexpected,
            "list sandboxes",
            $"sandbox inventory exceeded the {maxPages.ToString(CultureInfo.InvariantCulture)}-page bound; refusing to report a truncated inventory");
    }

    /// <summary>DELETE /sandbox/{id}. Returns false when the sandbox is already gone.</summary>
    public async Task<bool> DeleteSandboxAsync(DaytonaEndpoint endpoint, string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"sandbox/{Uri.EscapeDataString(idOrName)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete sandbox", ct).ConfigureAwait(false);
        return true;
    }

    public Task<DaytonaSandboxDto> StartSandboxAsync(DaytonaEndpoint endpoint, string idOrName, CancellationToken ct) =>
        PostPathAsync(endpoint, $"sandbox/{Uri.EscapeDataString(idOrName)}/start", "start sandbox", ct);

    public Task<DaytonaSandboxDto> StopSandboxAsync(
        DaytonaEndpoint endpoint, string idOrName, bool force, CancellationToken ct)
    {
        var path = $"sandbox/{Uri.EscapeDataString(idOrName)}/stop";
        if (force)
            path += "?force=true";
        return PostPathAsync(endpoint, path, "stop sandbox", ct);
    }

    public Task<DaytonaSandboxDto> PauseSandboxAsync(DaytonaEndpoint endpoint, string idOrName, CancellationToken ct) =>
        PostPathAsync(endpoint, $"sandbox/{Uri.EscapeDataString(idOrName)}/pause", "pause sandbox", ct);

    /// <summary>PUT /sandbox/{id}/labels — replaces the whole label set; callers merge first.</summary>
    public async Task SetLabelsAsync(
        DaytonaEndpoint endpoint, string idOrName, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Put, $"sandbox/{Uri.EscapeDataString(idOrName)}/labels",
            new DaytonaSandboxLabels(labels), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "set sandbox labels", ct).ConfigureAwait(false);
    }

    /// <summary>POST /sandbox/{id}/snapshot — snapshot the sandbox's disk (optionally RAM) into a named snapshot.</summary>
    public async Task<DaytonaSandboxDto> CreateSnapshotFromSandboxAsync(
        DaytonaEndpoint endpoint, string idOrName, string snapshotName, bool includeMemory, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandbox/{Uri.EscapeDataString(idOrName)}/snapshot",
            new DaytonaCreateSandboxSnapshot(snapshotName, includeMemory), ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<DaytonaSandboxDto>(response, "create sandbox snapshot", ct).ConfigureAwait(false);
        return dto ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "create sandbox snapshot", "empty response body");
    }

    /// <summary>
    /// POST /sandbox/{id}/network-settings — applies egress controls
    /// (block-all, CIDR allowlist, domain allowlist, proxy) to a running
    /// sandbox. Called AFTER operator setup commands so provisioning can reach
    /// package mirrors before the work item's deny-list locks egress down.
    /// </summary>
    public async Task SetNetworkSettingsAsync(
        DaytonaEndpoint endpoint, string idOrName, DaytonaNetworkSettings settings, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandbox/{Uri.EscapeDataString(idOrName)}/network-settings",
            settings, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "set network settings", ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when the snapshot does not exist (404).</summary>
    public async Task<DaytonaSnapshotDto?> GetSnapshotAsync(
        DaytonaEndpoint endpoint, string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"snapshots/{Uri.EscapeDataString(idOrName)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<DaytonaSnapshotDto>(response, "get snapshot", ct).ConfigureAwait(false);
        return dto ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "get snapshot", "empty response body");
    }

    /// <summary>GET /snapshots?name={prefix}&amp;page=&amp;limit= — bounded paging.</summary>
    public async Task<IReadOnlyList<DaytonaSnapshotDto>> ListSnapshotsAsync(
        DaytonaEndpoint endpoint, string namePrefix, int maxPages, CancellationToken ct)
    {
        var result = new List<DaytonaSnapshotDto>();
        for (var page = 1; page <= maxPages; page++)
        {
            var query = $"name={Uri.EscapeDataString(namePrefix)}&limit={ListPageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";
            using var response = await SendAsync(
                endpoint, HttpMethod.Get, $"snapshots?{query}", null, ct).ConfigureAwait(false);
            var pageResult = await ReadJsonAsync<DaytonaListSnapshotsResponse>(response, "list snapshots", ct)
                .ConfigureAwait(false)
                ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, "list snapshots", "empty response body");
            result.AddRange(pageResult.Items);
            if (page >= pageResult.TotalPages || pageResult.Items.Count == 0)
                return result;
        }
        throw new DaytonaApiException(
            DaytonaFailureKind.Unexpected,
            "list snapshots",
            $"snapshot listing exceeded the {maxPages.ToString(CultureInfo.InvariantCulture)}-page bound; refusing to report a truncated inventory");
    }

    /// <summary>DELETE /snapshots/{id}. Returns false when already gone.</summary>
    public async Task<bool> DeleteSnapshotAsync(DaytonaEndpoint endpoint, string id, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"snapshots/{Uri.EscapeDataString(id)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete snapshot", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// GET /sandbox/{id}/toolbox-proxy-url. Returns null when the endpoint is
    /// missing on this deployment — callers fall back to the configured
    /// toolbox proxy base.
    /// </summary>
    public async Task<Uri?> GetToolboxProxyUrlAsync(DaytonaEndpoint endpoint, string sandboxId, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get,
            $"sandbox/{Uri.EscapeDataString(sandboxId)}/toolbox-proxy-url", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<DaytonaToolboxProxyUrl>(response, "get toolbox proxy url", ct).ConfigureAwait(false);
        return TryParseAbsoluteUrl(dto?.Url, "toolbox proxy url", endpoint.AllowUnsafeHttp);
    }

    /// <summary>
    /// Cleartext http is permitted only for loopback test URLs under the
    /// dev-only <c>AllowUnsafeHttp</c> opt-in: the API key rides every
    /// toolbox request, so one operator edit must never send it cleartext to
    /// a remote host.
    /// </summary>
    internal static bool IsCleartextHttpPermitted(Uri uri, bool allowUnsafeHttp) =>
        allowUnsafeHttp
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    /// <summary>
    /// Remote URLs are untrusted input to an outbound-request sink: an absolute
    /// https URI (http only when <see cref="IsCleartextHttpPermitted"/> holds,
    /// since the API key rides every toolbox request) or nothing — never a
    /// relative path, never another scheme.
    /// </summary>
    internal static Uri? TryParseAbsoluteUrl(string? raw, string what, bool allowUnsafeHttp = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DaytonaApiException(
                DaytonaFailureKind.Unexpected,
                $"parse {what}",
                $"service returned a non-absolute or non-http(s) URL: '{raw.Trim()}'");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsCleartextHttpPermitted(uri, allowUnsafeHttp))
        {
            throw new DaytonaApiException(
                DaytonaFailureKind.Unexpected,
                $"parse {what}",
                "service returned a cleartext http toolbox URL that is not permitted; " +
                "refusing to send the API key over cleartext: '" + raw.Trim() + "'. " +
                "AllowUnsafeHttp permits http only for loopback test URLs, never for remote hosts.");
        }
        return uri;
    }

    private Task<DaytonaSandboxDto> PostPathAsync(
        DaytonaEndpoint endpoint, string path, string operation, CancellationToken ct) =>
        PostPathCoreAsync(endpoint, path, operation, ct);

    private async Task<DaytonaSandboxDto> PostPathCoreAsync(
        DaytonaEndpoint endpoint, string path, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, path, null, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<DaytonaSandboxDto>(response, operation, ct).ConfigureAwait(false);
        return dto ?? throw new DaytonaApiException(DaytonaFailureKind.Unexpected, operation, "empty response body");
    }

    private Task<HttpResponseMessage> SendJsonAsync(
        DaytonaEndpoint endpoint, HttpMethod method, string path, object body, CancellationToken ct)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return SendAsync(endpoint, method, path, content, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        DaytonaEndpoint endpoint, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var uri = new Uri(endpoint.ApiBaseUri, path);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        if (!string.IsNullOrEmpty(endpoint.OrganizationId))
            request.Headers.Add("X-Daytona-Organization-ID", endpoint.OrganizationId);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unreachable, $"{method.Method} {path}", "transport error", null, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unreachable, $"{method.Method} {path}", "request timed out", null, null, ex);
        }

        return response;
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
        where T : class
    {
        await EnsureSuccessAsync(response, operation, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unexpected, operation, "unparseable response body", null, null, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DaytonaApiException(DaytonaFailureKind.Unexpected, operation, "unsupported response body", null, null, ex);
        }
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
        var kind = DaytonaApiException.FromStatus(response.StatusCode, body);
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = delta;
        throw new DaytonaApiException(kind, operation, body ?? "no response body", response.StatusCode, retryAfter);
    }

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var trimmed = raw.Trim();
            var bounded = trimmed.Length > MaxErrorBodyChars ? trimmed[..MaxErrorBodyChars] : trimmed;
            return DaytonaTextUtil.SanitizeForLog(bounded);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Subset of the Daytona <c>Sandbox</c> DTO the provider consumes.</summary>
internal sealed class DaytonaSandboxDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("errorReason")] public string? ErrorReason { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("toolboxProxyUrl")] public string? ToolboxProxyUrl { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Subset of <c>SandboxListItem</c>.</summary>
internal sealed class DaytonaSandboxListItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class DaytonaListSandboxesResponse
{
    [JsonPropertyName("items")] public List<DaytonaSandboxListItem> Items { get; set; } = [];
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
}

internal sealed class DaytonaListSnapshotsResponse
{
    [JsonPropertyName("items")] public List<DaytonaSnapshotDto> Items { get; set; } = [];
    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
}

/// <summary>Subset of <c>SnapshotDto</c>.</summary>
internal sealed class DaytonaSnapshotDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("imageName")] public string? ImageName { get; set; }
    [JsonPropertyName("errorReason")] public string? ErrorReason { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset? LastUsedAt { get; set; }
}

internal sealed class DaytonaToolboxProxyUrl
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

internal sealed class DaytonaSandboxLabels
{
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string> Labels { get; }
    public DaytonaSandboxLabels(IReadOnlyDictionary<string, string> labels) => Labels = labels;
}

internal sealed class DaytonaCreateSandboxSnapshot
{
    [JsonPropertyName("name")] public string Name { get; }
    [JsonPropertyName("includeMemory")] public bool IncludeMemory { get; }
    public DaytonaCreateSandboxSnapshot(string name, bool includeMemory)
    {
        Name = name;
        IncludeMemory = includeMemory;
    }
}

/// <summary>Subset of <c>CreateSandbox</c> the provider sends.</summary>
internal sealed class DaytonaCreateSandboxRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("public")] public bool Public { get; set; }
    [JsonPropertyName("networkBlockAll")] public bool NetworkBlockAll { get; set; }
    [JsonPropertyName("networkAllowList")] public List<string>? NetworkAllowList { get; set; }
    [JsonPropertyName("domainAllowList")] public List<string>? DomainAllowList { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("sandboxClass")] public string? SandboxClass { get; set; }
    [JsonPropertyName("cpu")] public int? Cpu { get; set; }
    [JsonPropertyName("memory")] public int? Memory { get; set; }
    [JsonPropertyName("disk")] public int? Disk { get; set; }
    [JsonPropertyName("autoStopInterval")] public int? AutoStopInterval { get; set; }
    [JsonPropertyName("autoArchiveInterval")] public int? AutoArchiveInterval { get; set; }
    [JsonPropertyName("autoDeleteInterval")] public int? AutoDeleteInterval { get; set; }
}

/// <summary>Subset of <c>UpdateNetworkSettings</c> the provider sends.</summary>
internal sealed class DaytonaNetworkSettings
{
    [JsonPropertyName("networkBlockAll")] public bool? NetworkBlockAll { get; set; }
    [JsonPropertyName("networkAllowList")] public List<string>? NetworkAllowList { get; set; }
    [JsonPropertyName("domainAllowList")] public List<string>? DomainAllowList { get; set; }
    [JsonPropertyName("outboundProxyUrl")] public string? OutboundProxyUrl { get; set; }
}
