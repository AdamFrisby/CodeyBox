using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// Minimal 1Password Connect REST client: vault/item lookup and single
/// field extraction against the documented shapes
/// (<c>GET /v1/vaults</c>, <c>GET /v1/vaults/{vault}/items</c>,
/// <c>GET /v1/vaults/{vault}/items/{item}</c>).
/// <para>Every response body is bounded <em>before</em> buffering
/// (<c>ResponseHeadersRead</c> + content-length pre-check + capped copy), so
/// an unbounded upstream can never fill host memory. Every failure surfaces
/// as <see cref="OnePasswordException"/> with safe fields only — values and
/// tokens never reach messages, logs, or exceptions. A <c>message</c>
/// echoed by the server is truncated and may name vaults, items, or fields,
/// never values.</para>
/// </summary>
public sealed class OnePasswordRestClient
{
    private readonly HttpClient _http;
    private readonly ILogger _log;

    /// <summary>Maximum characters kept from a server-echoed error message.</summary>
    public const int MaxServerDetailChars = 200;

    public OnePasswordRestClient(HttpClient http, ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Reads one field value out of an item: resolves vault/item names to
    /// UUIDs with exact-match lookups when the mapping named them, fetches
    /// the full item, and selects the field whose label (then id) matches
    /// <paramref name="field"/> exactly (ordinal). Only the selected field
    /// value is returned; every other field is dropped without logging.
    /// </summary>
    public async Task<string> GetItemFieldAsync(
        string serverUrl,
        string token,
        OnePasswordSecretMapping mapping,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(mapping);
        var vaultId = string.IsNullOrWhiteSpace(mapping.VaultId)
            ? await ResolveVaultIdAsync(serverUrl, token, mapping.VaultName, maxResponseBytes, ct).ConfigureAwait(false)
            : mapping.VaultId.Trim();
        var itemId = string.IsNullOrWhiteSpace(mapping.ItemId)
            ? await ResolveItemIdAsync(serverUrl, token, vaultId, mapping.ItemTitle, maxResponseBytes, ct).ConfigureAwait(false)
            : mapping.ItemId.Trim();
        var field = string.IsNullOrWhiteSpace(mapping.Field) ? OnePasswordOptions.DefaultField : mapping.Field.Trim();

        var url = $"{serverUrl.TrimEnd('/')}/v1/vaults/{Uri.EscapeDataString(vaultId)}/items/{Uri.EscapeDataString(itemId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, $"read item '{itemId}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"read item '{itemId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    $"1Password item '{itemId}' returned no fields array.");
            foreach (var candidate in fields.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                var label = GetString(candidate, "label");
                var id = GetString(candidate, "id");
                if (!string.Equals(label, field, StringComparison.Ordinal)
                    && !string.Equals(id, field, StringComparison.Ordinal))
                    continue;
                var value = GetString(candidate, "value");
                if (value is null)
                    throw new OnePasswordException(
                        OnePasswordFailureKind.InvalidResponse,
                        $"1Password item '{itemId}' field '{field}' has no value.");
                _log.LogDebug(
                    "1Password read field '{Field}' from item '{Item}' in vault '{Vault}'.",
                    field, itemId, vaultId);
                return value;
            }
            throw new OnePasswordException(
                OnePasswordFailureKind.NotFound,
                $"1Password item '{itemId}' has no field '{field}'.");
        }
    }

    /// <summary>
    /// Resolves a vault name to its UUID with an exact (ordinal) match over
    /// <c>GET /v1/vaults</c>. Substring or case-insensitive matching would
    /// let similarly-named vaults shadow each other; exact match fails
    /// loudly instead.
    /// </summary>
    public async Task<string> ResolveVaultIdAsync(
        string serverUrl,
        string token,
        string vaultName,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultName);
        var url = $"{serverUrl.TrimEnd('/')}/v1/vaults";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, "list vaults", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "list vaults", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    "1Password vault listing returned an unexpected JSON shape.");
            string? match = null;
            foreach (var vault in doc.RootElement.EnumerateArray())
            {
                if (vault.ValueKind != JsonValueKind.Object)
                    continue;
                if (!string.Equals(GetString(vault, "name"), vaultName, StringComparison.Ordinal))
                    continue;
                var id = GetString(vault, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                if (match is not null)
                    throw new OnePasswordException(
                        OnePasswordFailureKind.Misconfigured,
                        $"1Password vault name '{vaultName}' is ambiguous; use VaultId instead.");
                match = id;
            }
            if (match is null)
                throw new OnePasswordException(
                    OnePasswordFailureKind.NotFound,
                    $"1Password vault '{vaultName}' was not found.");
            return match;
        }
    }

    /// <summary>
    /// Resolves an item title to its UUID with an exact (ordinal) match
    /// over the vault's item listing (which omits fields — names only, no
    /// values cross this call).
    /// </summary>
    public async Task<string> ResolveItemIdAsync(
        string serverUrl,
        string token,
        string vaultId,
        string itemTitle,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemTitle);
        var url = $"{serverUrl.TrimEnd('/')}/v1/vaults/{Uri.EscapeDataString(vaultId)}/items";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, "list items", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "list items", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    "1Password item listing returned an unexpected JSON shape.");
            string? match = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!string.Equals(GetString(item, "title"), itemTitle, StringComparison.Ordinal))
                    continue;
                var id = GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                if (match is not null)
                    throw new OnePasswordException(
                        OnePasswordFailureKind.Misconfigured,
                        $"1Password item title '{itemTitle}' is ambiguous in vault '{vaultId}'; use ItemId instead.");
                match = id;
            }
            if (match is null)
                throw new OnePasswordException(
                    OnePasswordFailureKind.NotFound,
                    $"1Password item '{itemTitle}' was not found in vault '{vaultId}'.");
            return match;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OnePasswordException(
                OnePasswordFailureKind.Unreachable, $"1Password {operation} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OnePasswordException(
                OnePasswordFailureKind.Unreachable, $"1Password {operation} could not reach the backend.", ex);
        }

        if (OnePasswordHttpClients.IsRedirect(response.StatusCode))
        {
            // Never follow: the backend's 3xx (and its Location) is
            // untrusted runtime output, and re-sending would carry the
            // bearer token to the redirect target. Fail closed as a backend
            // fault — never a verdict on the item.
            var redirect = (int)response.StatusCode;
            response.Dispose();
            throw new OnePasswordException(
                OnePasswordFailureKind.InvalidResponse,
                $"1Password {operation} returned redirect HTTP {redirect}; refusing to follow.",
                redirect);
        }

        if (response.RequestMessage?.RequestUri is { } finalUri
            && request.RequestUri is { } originalUri
            && !OnePasswordHttpClients.IsSameOrigin(finalUri, originalUri))
        {
            // The handler followed a redirect before this code saw the
            // response (only possible with an externally supplied
            // following client — the plugin builds non-following ones).
            // The credential may already have been re-sent off-origin,
            // so fail loudly rather than trusting this response.
            var followedStatus = (int)response.StatusCode;
            response.Dispose();
            throw new OnePasswordException(
                OnePasswordFailureKind.InvalidResponse,
                $"1Password {operation} was redirected to another origin; refusing the response.",
                followedStatus);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
        var retryAfter = ParseRetryAfter(response);
        response.Dispose();
        throw OnePasswordException.FromStatus(status, operation, detail, retryAfter);
    }

    private async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, string operation, int maxBytes, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response, maxBytes, ct).ConfigureAwait(false);
        }
        catch (OnePasswordException)
        {
            response.Dispose();
            throw;
        }
        response.Dispose();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The body may contain secret-adjacent text; never echo it.
            throw new OnePasswordException(
                OnePasswordFailureKind.InvalidResponse,
                $"1Password {operation} returned a non-JSON success body.", ex);
        }
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            doc.Dispose();
            throw new OnePasswordException(
                OnePasswordFailureKind.InvalidResponse,
                $"1Password {operation} returned an unexpected JSON shape.");
        }
        return doc;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new OnePasswordException(
                OnePasswordFailureKind.InvalidResponse,
                $"1Password response declares {contentLength.Value} bytes, above the {maxBytes}-byte cap.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Cap BEFORE buffering: a lying or missing content-length can never
        // fill host memory.
        var buffer = new byte[Math.Min(maxBytes + 1, 64 * 1024)];
        using var sink = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sink.Write(buffer, 0, read);
            if (sink.Length > maxBytes)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    $"1Password response exceeds the {maxBytes}-byte cap.");
        }
        return sink.ToArray();
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[2049];
            using var sink = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sink.Write(buffer, 0, read);
                if (sink.Length > 2048)
                    break;
            }
            text = Encoding.UTF8.GetString(sink.ToArray());
        }
        catch (IOException)
        {
            return "no readable error body";
        }
        catch (HttpRequestException)
        {
            return "no readable error body";
        }
        catch (ObjectDisposedException)
        {
            return "no readable error body";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "message", "error" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        var detail = message.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(detail))
                        {
                            // Flatten CR/LF: this text flows into exception
                            // messages, host logs, and the lease store, so a
                            // newline-bearing server message must not forge
                            // log lines.
                            var flatDetail = detail.Replace('\n', ' ').Replace('\r', ' ');
                            return flatDetail.Length <= MaxServerDetailChars ? flatDetail : flatDetail[..MaxServerDetailChars];
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the truncated raw text (status context only).
        }
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxServerDetailChars ? flat : flat[..MaxServerDetailChars];
    }

    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                return (int)Math.Clamp(delta.TotalSeconds, 0, 3600);
            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                return (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 3600);
        }
        catch (FormatException)
        {
            // Malformed header: no backoff hint.
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => null,
        };
    }
}
