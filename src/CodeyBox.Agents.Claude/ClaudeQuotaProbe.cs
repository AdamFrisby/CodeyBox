using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Probes the Anthropic OAuth usage endpoint to estimate available Claude
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Fail-open: any network error, unexpected status code, or unrecognised
/// response shape returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1
/// so a broken endpoint never blocks work items.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class ClaudeQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _token;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<ClaudeQuotaProbe> _log;

    // Single-entry cache: (snapshot, expiry). Protected by _lock.
    private (AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _token = token;
        _cacheTtl = cacheTtl;
        _log = log;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_token))
            return Unknown("no token configured");

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry && DateTimeOffset.UtcNow < entry.ExpiresAt)
                return entry.Snapshot;

            var snapshot = await FetchAsync(ct);
            _cache = (snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private async Task<AgentQuotaSnapshot> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            // Do NOT log the Authorization header — it contains the OAuth token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Claude quota endpoint returned {StatusCode}; treating quota as unknown",
                    (int)response.StatusCode);
                return Unknown($"HTTP {(int)response.StatusCode}");
            }

            // Do NOT log the response body — it may contain account identifiers.
            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null) return Unknown("response too large");
            return ParseResponse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Claude quota probe failed; treating quota as unknown");
            return Unknown("network error");
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        // Allocate one extra char so we can detect bodies that exceed the cap.
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        int totalRead = 0, chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            totalRead += chunk;
        }
        while (chunk > 0 && totalRead < buffer.Length);
        if (totalRead > MaxResponseChars) return null;
        return new string(buffer, 0, totalRead);
    }

    /// <summary>
    /// Parses the Anthropic usage response. Expected shape:
    /// <code>{ "usedTokens": N, "quotaTokens": M, "resetAt": "2026-05-01T00:00:00Z" }</code>
    /// Returns unknown (-1) for any unrecognised format or invalid JSON.
    /// </summary>
    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("usedTokens", out var usedEl) &&
                root.TryGetProperty("quotaTokens", out var quotaEl))
            {
                var used = usedEl.GetDouble();
                var quota = quotaEl.GetDouble();
                if (quota > 0)
                {
                    var pct = 100.0 * (1.0 - used / quota);
                    DateTimeOffset? resetAt = null;
                    if (root.TryGetProperty("resetAt", out var resetEl) &&
                        DateTimeOffset.TryParse(resetEl.GetString(), out var parsed))
                        resetAt = parsed;
                    return new AgentQuotaSnapshot
                    {
                        AvailablePct = Math.Max(0.0, pct),
                        ResetAt = resetAt,
                    };
                }
            }

            return Unknown("unexpected response shape");
        }
        catch (JsonException)
        {
            return Unknown("invalid JSON");
        }
    }

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };
}
