using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.MicrosandboxPlugin;

/// <summary>
/// A resolved microsandbox endpoint: the server base URI plus the API key
/// resolved from the host credential chain for one call. The key rides every
/// request; it is never stored on the options record (and never logged —
/// error bodies are bounded before they surface in exceptions).
/// </summary>
internal sealed record MicrosandboxEndpoint(Uri ServerBaseUri, string ApiKey, bool AllowUnsafeHttp = false)
{
    public override string ToString() =>
        $"{nameof(MicrosandboxEndpoint)} {{ {nameof(ServerBaseUri)} = {ServerBaseUri}, {nameof(ApiKey)} = **redacted**, {nameof(AllowUnsafeHttp)} = {AllowUnsafeHttp} }}";
}

/// <summary>
/// Thin REST client for the microsandbox server (<c>{ServerUrl}/v1</c>).
/// Every non-2xx response becomes a <see cref="MicrosandboxApiException"/>
/// carrying the failure taxonomy the provider maps onto infrastructure
/// deferrals — callers never inspect status codes themselves.
/// </summary>
internal sealed class MicrosandboxApiClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal const long MaxErrorBodyBytes = 8192;
    private const int MaxErrorBodyChars = 2048;
    private const int CopyBufferBytes = 8192;
    private const long GuestJsonOverheadBytes = 64L * 1024;

    private readonly HttpClient _http;

    public MicrosandboxApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// True for non-http schemes (https included); for http, only loopback
    /// hosts when the operator explicitly opted in. Remote cleartext is
    /// refused unconditionally so the API key can never ride a cleartext
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
            || uri.Host.Equals("::1", StringComparison.Ordinal);
    }

    public async Task<MicrosandboxDto> CreateSandboxAsync(
        MicrosandboxEndpoint endpoint, MicrosandboxCreateRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(endpoint, HttpMethod.Post, "sandboxes", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxDto>(response, "create sandbox", ct).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "create sandbox", "empty response body");
    }

    /// <summary>Returns null when the sandbox does not exist (404).</summary>
    public async Task<MicrosandboxDto?> GetSandboxAsync(
        MicrosandboxEndpoint endpoint, string name, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"sandboxes/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var dto = await ReadJsonAsync<MicrosandboxDto>(response, "get sandbox", ct).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "get sandbox", "empty response body");
    }

    public async Task<IReadOnlyList<MicrosandboxListItem>> ListManagedAsync(
        MicrosandboxEndpoint endpoint, string namePrefix, int maxPages, CancellationToken ct)
    {
        var result = new List<MicrosandboxListItem>();
        string? cursor = null;
        for (var page = 0; page < maxPages; page++)
        {
            var path = $"sandboxes?prefix={Uri.EscapeDataString(namePrefix)}&limit=200"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await SendAsync(endpoint, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            var dto = await ReadJsonAsync<MicrosandboxListDto>(response, "list sandboxes", ct).ConfigureAwait(false)
                ?? throw new MicrosandboxApiException(
                    MicrosandboxFailureKind.Unexpected, "list sandboxes", "empty response body");
            if (dto.Sandboxes is not null)
                result.AddRange(dto.Sandboxes);
            cursor = dto.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                return result;
        }

        throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected,
            "list sandboxes",
            $"sandbox inventory exceeded {maxPages} pages; refusing to report a truncated inventory as complete");
    }

    /// <summary>True when the sandbox is gone afterwards (already-gone counts as gone).</summary>
    public async Task<bool> DeleteSandboxAsync(MicrosandboxEndpoint endpoint, string name, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"sandboxes/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound;
    }

    public async Task TransitionAsync(MicrosandboxEndpoint endpoint, string name, string action, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Post,
            $"sandboxes/{Uri.EscapeDataString(name)}/{action}", new StringContent("{}", Encoding.UTF8, "application/json"), ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MicrosandboxApiException(MicrosandboxFailureKind.NotFound, action, $"sandbox '{name}' not found");
    }

    public async Task<MicrosandboxExecCreatedDto> StartExecAsync(
        MicrosandboxEndpoint endpoint, string name, MicrosandboxStartExecRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandboxes/{Uri.EscapeDataString(name)}/exec", body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxExecCreatedDto>(response, "start exec", ct).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "start exec", "empty response body");
    }

    public async Task<MicrosandboxExecStatusDto> GetExecAsync(
        MicrosandboxEndpoint endpoint, string name, string execId, long maxBodyBytes, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get,
            $"sandboxes/{Uri.EscapeDataString(name)}/exec/{Uri.EscapeDataString(execId)}", null, ct, maxBodyBytes)
            .ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxExecStatusDto>(response, "poll exec", ct, maxBodyBytes).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "poll exec", "empty response body");
    }

    public async Task KillExecAsync(MicrosandboxEndpoint endpoint, string name, string execId, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Post,
            $"sandboxes/{Uri.EscapeDataString(name)}/exec/{Uri.EscapeDataString(execId)}/kill",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
    }

    public async Task<MicrosandboxFileDto?> ReadFileAsync(
        MicrosandboxEndpoint endpoint, string name, string path, long maxBodyBytes, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get,
            $"sandboxes/{Uri.EscapeDataString(name)}/files?path={Uri.EscapeDataString(path)}", null, ct, maxBodyBytes)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadJsonAsync<MicrosandboxFileDto>(response, "read file", ct, maxBodyBytes).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(
        MicrosandboxEndpoint endpoint, string name, MicrosandboxWriteFileRequest body, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Put, $"sandboxes/{Uri.EscapeDataString(name)}/files", body, ct).ConfigureAwait(false);
    }

    public async Task SetNetworkAsync(
        MicrosandboxEndpoint endpoint, string name, MicrosandboxNetworkDto network, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandboxes/{Uri.EscapeDataString(name)}/network", network, ct).ConfigureAwait(false);
    }

    public async Task SetLabelsAsync(
        MicrosandboxEndpoint endpoint, string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandboxes/{Uri.EscapeDataString(name)}/labels",
            new MicrosandboxLabelsRequest(labels), ct).ConfigureAwait(false);
    }

    public async Task<MicrosandboxSnapshotDto> CreateSnapshotAsync(
        MicrosandboxEndpoint endpoint, string name, string snapshot, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandboxes/{Uri.EscapeDataString(name)}/snapshot",
            new MicrosandboxSnapshotRequest(snapshot), ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxSnapshotDto>(response, "create snapshot", ct).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "create snapshot", "empty response body");
    }

    /// <summary>
    /// Live branch: copy-on-write fork of a running sandbox into a new sandbox
    /// with its own identity. The branch shares unmodified pages with the
    /// parent until it writes — this is the operation contract tests exercise
    /// for parallel contract-testing variants.
    /// </summary>
    public async Task<MicrosandboxDto> BranchSandboxAsync(
        MicrosandboxEndpoint endpoint, string name, string branchName, CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            endpoint, HttpMethod.Post, $"sandboxes/{Uri.EscapeDataString(name)}/branch",
            new MicrosandboxBranchRequest(branchName), ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxDto>(response, "branch sandbox", ct).ConfigureAwait(false);
        return dto ?? throw new MicrosandboxApiException(
            MicrosandboxFailureKind.Unexpected, "branch sandbox", "empty response body");
    }

    public async Task<IReadOnlyList<MicrosandboxSnapshotDto>> ListSnapshotsAsync(
        MicrosandboxEndpoint endpoint, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Get, "snapshots", null, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<MicrosandboxSnapshotListDto>(response, "list snapshots", ct).ConfigureAwait(false);
        return dto?.Snapshots ?? [];
    }

    public async Task<bool> DeleteSnapshotAsync(MicrosandboxEndpoint endpoint, string name, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"snapshots/{Uri.EscapeDataString(name)}", null, ct).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound;
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendJsonAsync<T>(
        MicrosandboxEndpoint endpoint, HttpMethod method, string path, T body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(endpoint, method, path, content, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        MicrosandboxEndpoint endpoint,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken ct,
        long maxBodyBytes = 8L * 1024 * 1024)
    {
        var uri = new Uri(endpoint.ServerBaseUri, "v1/" + path.TrimStart('/'));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
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
        catch (Exception ex) when (ex is not MicrosandboxApiException)
        {
            throw new MicrosandboxApiException(
                MicrosandboxFailureKind.Unreachable, $"{method} {path}", $"microsandbox server unreachable: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        string excerpt;
        try
        {
            excerpt = await ReadBoundedStringAsync(response.Content, MaxErrorBodyBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response.Dispose();
            throw;
        }
        catch (Exception)
        {
            excerpt = string.Empty;
        }

        response.Dispose();
        var kind = MicrosandboxApiException.ClassifyStatus(status, excerpt);
        var detail = string.IsNullOrEmpty(excerpt)
            ? $"microsandbox {method} {path} failed with {(int)status}"
            : $"microsandbox {method} {path} failed with {(int)status}: {Truncate(excerpt, MaxErrorBodyChars)}";
        throw new MicrosandboxApiException(kind, $"{method} {path}", detail, status);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, string operation, CancellationToken ct, long maxBodyBytes = 8L * 1024 * 1024)
    {
        var body = await ReadBoundedStringAsync(response.Content, maxBodyBytes, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException ex)
        {
            throw new MicrosandboxApiException(
                MicrosandboxFailureKind.Unexpected, operation, $"microsandbox returned malformed JSON: {ex.Message}", ex);
        }
    }

    private static async Task<string> ReadBoundedStringAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new MicrosandboxApiException(
                    MicrosandboxFailureKind.Unexpected, "read body",
                    $"microsandbox response exceeded {maxBytes} bytes; refusing to buffer unbounded guest-influenced output");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>Bounds one exec-poll body: both text snapshots at their accepted ceiling plus JSON framing.</summary>
    internal static long BoundExecPollResponseBytes(int stdoutCap, int stderrCap) =>
        ((long)stdoutCap + GuestJsonOverheadBytes) * 4 / 3
        + ((long)stderrCap + GuestJsonOverheadBytes) * 4 / 3
        + GuestJsonOverheadBytes;
}

// ----------------------------------------------------------------------
// Wire shapes (recorded from the microsandbox server v1 surface; the
// recorded-shape fixture in MicrosandboxSandboxProviderTests mirrors these.
// Field names are exact-match — the client never falls back to alternates.)
// ----------------------------------------------------------------------

internal sealed record MicrosandboxCreateRequest(
    string Name,
    string Image,
    int Cpu,
    int MemoryMib,
    int DiskGib,
    Dictionary<string, string> Labels,
    MicrosandboxNetworkDto Network,
    string? Snapshot = null);

internal sealed record MicrosandboxNetworkDto(string Mode, IReadOnlyList<string> AllowedHosts);

internal sealed record MicrosandboxLabelsRequest(IReadOnlyDictionary<string, string> Labels);

internal sealed record MicrosandboxSnapshotRequest(string Snapshot);

internal sealed record MicrosandboxBranchRequest(string Name);

internal sealed record MicrosandboxStartExecRequest(
    IReadOnlyList<string> Argv,
    string? Workdir,
    Dictionary<string, string> Env,
    string? StdinBase64);

internal sealed record MicrosandboxDto(string? Name, string? State, Dictionary<string, string>? Labels);

internal sealed record MicrosandboxListItem(string? Name, string? State, Dictionary<string, string>? Labels);

internal sealed record MicrosandboxListDto(IReadOnlyList<MicrosandboxListItem>? Sandboxes, string? NextCursor);

internal sealed record MicrosandboxExecCreatedDto(string? ExecId);

internal sealed record MicrosandboxExecStatusDto(
    string? Status,
    int? ExitCode,
    string? Stdout,
    string? Stderr);

internal sealed record MicrosandboxFileDto(string? Path, string? ContentBase64, bool Exists);

internal sealed record MicrosandboxWriteFileRequest(string Path, string ContentBase64);

internal sealed record MicrosandboxSnapshotDto(string? Snapshot, string? State);

internal sealed record MicrosandboxSnapshotListDto(IReadOnlyList<MicrosandboxSnapshotDto>? Snapshots);
