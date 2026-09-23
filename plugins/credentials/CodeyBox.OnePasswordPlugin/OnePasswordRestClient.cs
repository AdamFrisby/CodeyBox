using System.Net.Http.Headers;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;
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
    private readonly CredentialTransport _transport;
    private readonly ILogger _log;

    /// <summary>JSON error-body fields relayed as detail, in preference order.</summary>
    private static readonly string[] ErrorDetailFields = ["message", "error"];

    public OnePasswordRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _transport = new CredentialTransport(
            http ?? throw new ArgumentNullException(nameof(http)),
            "1Password",
            OnePasswordException.Create,
            ErrorDetailFields,
            relayRawErrorText: true,
            clock: clock);
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
        using var response = await _transport.SendAsync(request, $"read item '{itemId}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"read item '{itemId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    CredentialFailureKind.InvalidResponse,
                    $"1Password item '{itemId}' returned no fields array.");
            foreach (var candidate in fields.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                var label = CredentialJson.GetString(candidate, "label");
                var id = CredentialJson.GetString(candidate, "id");
                if (!string.Equals(label, field, StringComparison.Ordinal)
                    && !string.Equals(id, field, StringComparison.Ordinal))
                    continue;
                var value = CredentialJson.GetString(candidate, "value");
                if (value is null)
                    throw new OnePasswordException(
                        CredentialFailureKind.InvalidResponse,
                        $"1Password item '{itemId}' field '{field}' has no value.");
                _log.LogDebug(
                    "1Password read field '{Field}' from item '{Item}' in vault '{Vault}'.",
                    field, itemId, vaultId);
                return value;
            }
            throw new OnePasswordException(
                CredentialFailureKind.NotFound,
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
        using var response = await _transport.SendAsync(request, "list vaults", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, "list vaults", maxResponseBytes, ct, allowArrayRoot: true).ConfigureAwait(false);
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    CredentialFailureKind.InvalidResponse,
                    "1Password vault listing returned an unexpected JSON shape.");
            string? match = null;
            foreach (var vault in doc.RootElement.EnumerateArray())
            {
                if (vault.ValueKind != JsonValueKind.Object)
                    continue;
                if (!string.Equals(CredentialJson.GetString(vault, "name"), vaultName, StringComparison.Ordinal))
                    continue;
                var id = CredentialJson.GetString(vault, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                if (match is not null)
                    throw new OnePasswordException(
                        CredentialFailureKind.Misconfigured,
                        $"1Password vault name '{vaultName}' is ambiguous; use VaultId instead.");
                match = id;
            }
            if (match is null)
                throw new OnePasswordException(
                    CredentialFailureKind.NotFound,
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
        using var response = await _transport.SendAsync(request, "list items", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, "list items", maxResponseBytes, ct, allowArrayRoot: true).ConfigureAwait(false);
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new OnePasswordException(
                    CredentialFailureKind.InvalidResponse,
                    "1Password item listing returned an unexpected JSON shape.");
            string? match = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!string.Equals(CredentialJson.GetString(item, "title"), itemTitle, StringComparison.Ordinal))
                    continue;
                var id = CredentialJson.GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                if (match is not null)
                    throw new OnePasswordException(
                        CredentialFailureKind.Misconfigured,
                        $"1Password item title '{itemTitle}' is ambiguous in vault '{vaultId}'; use ItemId instead.");
                match = id;
            }
            if (match is null)
                throw new OnePasswordException(
                    CredentialFailureKind.NotFound,
                    $"1Password item '{itemTitle}' was not found in vault '{vaultId}'.");
            return match;
        }
    }

}
