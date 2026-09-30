using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.BoxLiteSandboxPlugin;

/// <summary>
/// A resolved BoxLite endpoint: the daemon base URI plus the API token
/// resolved from the host credential chain for one call. The token rides
/// every request; it is never stored on the options record (and never logged
/// — error bodies are scrubbed before they surface in exceptions).
/// </summary>
internal sealed record BoxLiteEndpoint(Uri DaemonBaseUri, string ApiToken, bool AllowUnsafeHttp = false)
{
    // The compiler-generated record ToString would render the live API token;
    // redact it so a future log interpolation of the endpoint cannot leak it.
    public override string ToString() =>
        $"{nameof(BoxLiteEndpoint)} {{ {nameof(DaemonBaseUri)} = {DaemonBaseUri}, {nameof(ApiToken)} = **redacted**, {nameof(AllowUnsafeHttp)} = {AllowUnsafeHttp} }}";
}

/// <summary>
/// Thin REST client for the BoxLite embedded-microVM daemon (<c>{DaemonUrl}/v1</c>).
/// Every non-2xx response becomes a <see cref="BoxLiteApiException"/> carrying
/// the failure taxonomy the provider maps onto infrastructure deferrals —
/// callers never inspect status codes themselves.
/// </summary>
internal sealed class BoxLiteApiClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Transport-level ceilings, enforced BEFORE the client buffers a body.
    // Guest-influenced JSON (exec snapshots, file/archive reads) arrives as
    // one HTTP body per poll tick, so per-call caps derived from the matching
    // option bound host memory to O(cap) no matter how much the guest wrote.
    // A body past the ceiling throws BoxLiteApiException (Unexpected —
    // infrastructure, never a diff verdict) before deserialization runs.
    internal const long DefaultMaxResponseBytes = 8L * 1024 * 1024;

    // Slack on top of each exec-stream cap when bounding one poll's snapshot,
    // covering multi-byte decoding growth. Shared with the snapshot decoder
    // so the transport ceiling always covers what the decoder accepts.
    internal const int ExecSnapshotSlackBytes = 1024 * 1024;

    internal const long MaxErrorBodyBytes = 8192;

    private const long GuestJsonOverheadBytes = 64L * 1024;

    private const int CopyBufferBytes = 8192;

    // Error bodies are untrusted remote text: bounded before they are folded
    // into exception messages and logs.
    private const int MaxErrorBodyChars = 2048;

    private const int ListPageSize = 200;

    private readonly HttpClient _http;

    public BoxLiteApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Bounds one exec-poll body: both base64 snapshots at their accepted
    /// ceiling (cap plus snapshot slack, inflated for base64) plus JSON
    /// framing. A guest emitting past this is unbounded output: the poll
    /// throws and the caller kills the guest process.
    /// </summary>
    internal static long BoundExecPollResponseBytes(int stdoutCap, int stderrCap) =>
        ((long)stdoutCap + ExecSnapshotSlackBytes) * 4 / 3
        + ((long)stderrCap + ExecSnapshotSlackBytes) * 4 / 3
        + GuestJsonOverheadBytes;

    /// <summary>
    /// Bounds one file/archive body: the accepted base64 length plus JSON
    /// framing (base64 is ASCII, so body bytes track base64 chars 1:1).
    /// </summary>
    internal static long BoundPayloadResponseBytes(long maxBase64Chars) =>
        maxBase64Chars + GuestJsonOverheadBytes;

    /// <summary>
    /// True for non-http schemes (https included); for http, only loopback
    /// hosts when the operator explicitly opted in. Remote cleartext is
    /// refused unconditionally so the API token can never ride a cleartext
    /// request to a remote host because of one config edit.
    /// </summary>
    public static bool IsCleartextHttpPermitted(Uri uri, bool allowUnsafeHttp)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!allowUnsafeHttp)
            return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || uri.Host.Equals("::1", StringComparison.Ordinal)
            || uri.Host.Equals("[::1]", StringComparison.Ordinal);
    }

    public async Task<BoxLiteVmDto> CreateVmAsync(
        BoxLiteEndpoint endpoint, BoxLiteCreateVmRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(endpoint, HttpMethod.Post, "vms", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteVmDto>(response, "create vm", ct).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "create vm", "empty response body");
    }

    /// <summary>Returns null when the VM does not exist (404).</summary>
    public async Task<BoxLiteVmDto?> GetVmAsync(BoxLiteEndpoint endpoint, string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"vms/{Uri.EscapeDataString(idOrName)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<BoxLiteVmDto>(response, "get vm", ct).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "get vm", "empty response body");
    }

    /// <summary>
    /// Pages <c>GET /vms</c> filtered to provider-owned VMs: the managed label
    /// AND the configured name prefix are both sent server-side and re-verified
    /// client-side (a prefix filter on a local daemon is a hint, never a proof
    /// of ownership).
    /// </summary>
    public async Task<IReadOnlyList<BoxLiteVmListItem>> ListManagedVmsAsync(
        BoxLiteEndpoint endpoint, string namePrefix, int maxPages, CancellationToken ct)
    {
        var result = new List<BoxLiteVmListItem>();
        string? cursor = null;
        for (var page = 0; page < maxPages; page++)
        {
            var query = $"vms?name={Uri.EscapeDataString(namePrefix)}&label={Uri.EscapeDataString($"{BoxLiteSandboxOptions.ManagedLabelKey}={BoxLiteSandboxOptions.ManagedLabelValue}")}&limit={ListPageSize.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(cursor))
                query += $"&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await SendAsync(endpoint, HttpMethod.Get, query, null, ct).ConfigureAwait(false);
            var pageResult = await ReadJsonAsync<BoxLiteListVmsResponse>(response, "list vms", ct).ConfigureAwait(false)
                ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "list vms", "empty response body");
            foreach (var item in pageResult.Vms)
            {
                if (item.Name is not null &&
                    item.Name.StartsWith(namePrefix, StringComparison.Ordinal) &&
                    item.Labels is not null &&
                    item.Labels.TryGetValue(BoxLiteSandboxOptions.ManagedLabelKey, out var managed) &&
                    string.Equals(managed, BoxLiteSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
                {
                    result.Add(item);
                }
            }
            if (string.IsNullOrEmpty(pageResult.NextCursor))
                return result;
            cursor = pageResult.NextCursor;
        }
        throw new BoxLiteApiException(
            BoxLiteFailureKind.Unexpected,
            "list vms",
            $"vm inventory exceeded the {maxPages.ToString(CultureInfo.InvariantCulture)}-page bound; refusing to report a truncated inventory");
    }

    /// <summary>DELETE /vms/{id}. Returns false when the VM is already gone.</summary>
    public async Task<bool> DeleteVmAsync(BoxLiteEndpoint endpoint, string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"vms/{Uri.EscapeDataString(idOrName)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete vm", ct).ConfigureAwait(false);
        return true;
    }

    public async Task<BoxLiteVmDto> TransitionVmAsync(
        BoxLiteEndpoint endpoint, string idOrName, string transition, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Post, $"vms/{Uri.EscapeDataString(idOrName)}/{transition}", null, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteVmDto>(response, $"{transition} vm", ct).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, $"{transition} vm", "empty response body");
    }

    public async Task<BoxLiteExecCreatedDto> StartExecAsync(
        BoxLiteEndpoint endpoint, string vmId, BoxLiteStartExecRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"vms/{Uri.EscapeDataString(vmId)}/exec", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteExecCreatedDto>(response, "start exec", ct).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "start exec", "empty response body");
    }

    public async Task<BoxLiteExecStatusDto> GetExecAsync(
        BoxLiteEndpoint endpoint, string vmId, string execId, CancellationToken ct,
        long maxResponseBytes = DefaultMaxResponseBytes)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"vms/{Uri.EscapeDataString(vmId)}/exec/{Uri.EscapeDataString(execId)}", null, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteExecStatusDto>(response, "poll exec", ct, maxResponseBytes).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "poll exec", "empty response body");
    }

    /// <summary>DELETE the exec: kills the guest process. 404 (already reaped) is success.</summary>
    public async Task KillExecAsync(BoxLiteEndpoint endpoint, string vmId, string execId, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"vms/{Uri.EscapeDataString(vmId)}/exec/{Uri.EscapeDataString(execId)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, "kill exec", ct).ConfigureAwait(false);
    }

    /// <summary>PUT a single file into the guest. Returns the stored byte count.</summary>
    public async Task<long> WriteFileAsync(
        BoxLiteEndpoint endpoint, string vmId, string guestPath, string contentBase64, string? mode, CancellationToken ct)
    {
        var body = new BoxLiteWriteFileRequest(guestPath, contentBase64, mode);
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Put, $"vms/{Uri.EscapeDataString(vmId)}/files", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteWriteFileResponse>(response, "write file", ct).ConfigureAwait(false);
        return dto?.Bytes ?? 0;
    }

    /// <summary>GET a single file from the guest. Returns null when absent (404).</summary>
    public async Task<BoxLiteFileDto?> ReadFileAsync(
        BoxLiteEndpoint endpoint, string vmId, string guestPath, CancellationToken ct,
        long maxResponseBytes = DefaultMaxResponseBytes)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"vms/{Uri.EscapeDataString(vmId)}/files?path={Uri.EscapeDataString(guestPath)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<BoxLiteFileDto>(response, "read file", ct, maxResponseBytes).ConfigureAwait(false);
    }

    /// <summary>POST a base64 tar.gz the daemon extracts at <paramref name="guestPath"/>.</summary>
    public async Task<int> ExtractArchiveAsync(
        BoxLiteEndpoint endpoint, string vmId, string guestPath, string archiveBase64, CancellationToken ct)
    {
        var body = new BoxLiteArchiveRequest(guestPath, archiveBase64);
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"vms/{Uri.EscapeDataString(vmId)}/archive", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteArchiveResult>(response, "extract archive", ct).ConfigureAwait(false);
        return dto?.Files ?? 0;
    }

    /// <summary>GET a base64 tar.gz of <paramref name="guestPath"/>.</summary>
    public async Task<BoxLiteArchiveResult?> CreateArchiveAsync(
        BoxLiteEndpoint endpoint, string vmId, string guestPath, CancellationToken ct,
        long maxResponseBytes = DefaultMaxResponseBytes)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"vms/{Uri.EscapeDataString(vmId)}/archive?path={Uri.EscapeDataString(guestPath)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<BoxLiteArchiveResult>(response, "create archive", ct, maxResponseBytes).ConfigureAwait(false);
    }

    /// <summary>POST /vms/{id}/network: replaces the VM network restriction after setup (bake-then-lock).</summary>
    public async Task SetNetworkAsync(
        BoxLiteEndpoint endpoint, string idOrName, BoxLiteNetworkRequest network, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"vms/{Uri.EscapeDataString(idOrName)}/network", network, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "set vm network", ct).ConfigureAwait(false);
    }

    /// <summary>POST /vms/{id}/labels: merges <paramref name="labels"/> into the VM record.</summary>
    public async Task SetVmLabelsAsync(
        BoxLiteEndpoint endpoint, string idOrName, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var body = new BoxLiteSetLabelsRequest(labels);
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"vms/{Uri.EscapeDataString(idOrName)}/labels", body, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "set vm labels", ct).ConfigureAwait(false);
    }

    /// <summary>POST /snapshots: snapshot <paramref name="sourceVm"/> as <paramref name="name"/>.</summary>
    public async Task<BoxLiteSnapshotDto> CreateSnapshotAsync(
        BoxLiteEndpoint endpoint, string name, string sourceVm, CancellationToken ct)
    {
        var body = new BoxLiteCreateSnapshotRequest(name, sourceVm);
        using var response = await SendJsonAsync(endpoint, HttpMethod.Post, "snapshots", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<BoxLiteSnapshotDto>(response, "create snapshot", ct).ConfigureAwait(false);
        return dto ?? throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "create snapshot", "empty response body");
    }

    public async Task<IReadOnlyList<BoxLiteSnapshotDto>> ListSnapshotsAsync(BoxLiteEndpoint endpoint, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Get, "snapshots", null, ct).ConfigureAwait(false);
        var page = await ReadJsonAsync<BoxLiteListSnapshotsResponse>(response, "list snapshots", ct).ConfigureAwait(false);
        return page?.Snapshots ?? [];
    }

    /// <summary>DELETE /snapshots/{name}. Returns false when already gone.</summary>
    public async Task<bool> DeleteSnapshotAsync(BoxLiteEndpoint endpoint, string name, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"snapshots/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete snapshot", ct).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendJsonAsync<T>(
        BoxLiteEndpoint endpoint, HttpMethod method, string relativePath, T body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(endpoint, method, relativePath, content, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        BoxLiteEndpoint endpoint, HttpMethod method, string relativePath, HttpContent? content, CancellationToken ct)
    {
        var uri = new Uri(endpoint.DaemonBaseUri, "v1/" + relativePath.TrimStart('/'));
        if (uri.Scheme == Uri.UriSchemeHttp && !IsCleartextHttpPermitted(uri, endpoint.AllowUnsafeHttp))
        {
            throw new BoxLiteApiException(
                BoxLiteFailureKind.Unauthorized,
                $"{method} {relativePath}",
                "refusing cleartext http to a non-loopback BoxLite daemon: the API token rides every request");
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (content is not null)
            request.Content = content;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not BoxLiteApiException)
        {
            throw new BoxLiteApiException(
                BoxLiteFailureKind.Unreachable, $"{method} {relativePath}",
                $"transport failure reaching the BoxLite daemon: {Trim(ex.Message)}", innerException: ex);
        }
        return response;
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, string operation, CancellationToken ct,
        long maxResponseBytes = DefaultMaxResponseBytes)
    {
        await EnsureSuccessStaticAsync(response, operation, ct).ConfigureAwait(false);
        try
        {
            // Fast path: a declared length past the ceiling is unbounded
            // guest output without reading a single body byte.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxResponseBytes)
                throw OverCap(operation, maxResponseBytes);
            var body = await CopyBoundedAsync(response, maxResponseBytes, operation, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(body.AsSpan(), Json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not BoxLiteApiException)
        {
            throw new BoxLiteApiException(
                BoxLiteFailureKind.Unexpected, operation,
                $"unreadable BoxLite response: {Trim(ex.Message)}", response.StatusCode, innerException: ex);
        }
    }

    /// <summary>
    /// Copies the response body up to <paramref name="maxBytes"/> plus one
    /// probe byte: the probe distinguishes "exactly at the ceiling" (kept)
    /// from "past it" (over cap) without buffering the excess.
    /// </summary>
    private static async Task<byte[]> CopyBoundedAsync(
        HttpResponseMessage response, long maxBytes, string operation, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[CopyBufferBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                var room = maxBytes + 1 - buffer.Length;
                if (read >= room)
                {
                    buffer.Write(chunk, 0, (int)room);
                    break;
                }
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > maxBytes)
                throw OverCap(operation, maxBytes);
            return buffer.ToArray();
        }
    }

    private static BoxLiteApiException OverCap(string operation, long maxBytes) =>
        new(BoxLiteFailureKind.Unexpected, operation,
            $"daemon response exceeded the {maxBytes.ToString(CultureInfo.InvariantCulture)}-byte transport bound; " +
            "treating unbounded daemon output as infrastructure");

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        await EnsureSuccessStaticAsync(response, operation, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessStaticAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
        var kind = BoxLiteApiException.FromStatus(response.StatusCode, body);
        throw new BoxLiteApiException(kind, operation, Trim(body), response.StatusCode, ReadRetryAfter(response));
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            // Error bodies are diagnostics, not data: truncate past the
            // ceiling (rather than failing) so a chatty error page can never
            // grow host memory, while the status that matters still throws.
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[CopyBufferBytes];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0
                    && buffer.Length < MaxErrorBodyBytes)
                {
                    buffer.Write(chunk, 0, (int)Math.Min(read, MaxErrorBodyBytes - buffer.Length));
                }
                var body = Encoding.UTF8.GetString(buffer.ToArray());
                return string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                    : Scrub(body);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        try
        {
            var values = response.Headers.RetryAfter;
            if (values?.Delta is { } delta && delta > TimeSpan.Zero)
                return delta > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delta;
            if (values?.Date is { } date)
            {
                var delay = date - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero
                    ? (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay)
                    : TimeSpan.Zero;
            }
        }
        catch (FormatException)
        {
        }
        return null;
    }

    private static string Trim(string value) =>
        value.Length <= MaxErrorBodyChars ? value : value[..MaxErrorBodyChars] + "…";

    private static string Scrub(string body)
    {
        var scrubbed = body;
        foreach (var marker in new[] { "Bearer ", "Basic " })
        {
            var index = scrubbed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var end = index + marker.Length;
                while (end < scrubbed.Length && !char.IsWhiteSpace(scrubbed[end]) && scrubbed[end] != '"' && scrubbed[end] != '\'')
                    end++;
                scrubbed = scrubbed[..(index + marker.Length)] + "***" + scrubbed[end..];
                index = scrubbed.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
        return Trim(scrubbed);
    }
}

// ------------------------------------------------------------------
// Wire shapes (recorded from the BoxLite daemon OpenAPI surface; the
// FakeBoxLiteServer in tests mirrors these shapes — see
// docs/extending/boxlite-sandbox-plugin.md, "recorded-shape fixture").
// ------------------------------------------------------------------

internal sealed record BoxLiteCreateVmRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("cpu")] int? Cpu,
    [property: JsonPropertyName("memoryMib")] int? MemoryMib,
    [property: JsonPropertyName("diskGib")] int? DiskGib,
    [property: JsonPropertyName("persistent")] bool Persistent,
    [property: JsonPropertyName("network")] BoxLiteNetworkRequest? Network,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string>? Labels,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env);

internal sealed record BoxLiteNetworkRequest(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("allowedHosts")] IReadOnlyList<string>? AllowedHosts);

internal sealed record BoxLiteVmDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels);

internal sealed record BoxLiteVmListItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels);

internal sealed record BoxLiteListVmsResponse(
    [property: JsonPropertyName("vms")] List<BoxLiteVmListItem> Vms,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

internal sealed record BoxLiteStartExecRequest(
    [property: JsonPropertyName("argv")] IReadOnlyList<string> Argv,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? EnvBase64,
    [property: JsonPropertyName("stdinBase64")] string? StdinBase64,
    [property: JsonPropertyName("timeoutSeconds")] long? TimeoutSeconds);

internal sealed record BoxLiteExecCreatedDto(
    [property: JsonPropertyName("execId")] string? ExecId,
    [property: JsonPropertyName("running")] bool Running);

internal sealed record BoxLiteExecStatusDto(
    [property: JsonPropertyName("execId")] string? ExecId,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("stdoutBase64")] string? StdoutBase64,
    [property: JsonPropertyName("stderrBase64")] string? StderrBase64);

internal sealed record BoxLiteWriteFileRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("contentBase64")] string ContentBase64,
    [property: JsonPropertyName("mode")] string? Mode);

internal sealed record BoxLiteWriteFileResponse(
    [property: JsonPropertyName("bytes")] long Bytes);

internal sealed record BoxLiteFileDto(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("contentBase64")] string? ContentBase64);

internal sealed record BoxLiteArchiveRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("archiveBase64")] string ArchiveBase64);

internal sealed record BoxLiteArchiveResult(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("archiveBase64")] string? ArchiveBase64,
    [property: JsonPropertyName("files")] int Files);

internal sealed record BoxLiteCreateSnapshotRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sourceVm")] string SourceVm);

internal sealed record BoxLiteSetLabelsRequest(
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels);

internal sealed record BoxLiteSnapshotDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt);

internal sealed record BoxLiteListSnapshotsResponse(
    [property: JsonPropertyName("snapshots")] List<BoxLiteSnapshotDto> Snapshots);
