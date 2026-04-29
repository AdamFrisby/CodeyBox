using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Probes the OpenAI billing endpoints to estimate available Codex subscription
/// quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Makes two calls per refresh: subscription (for hard_limit_usd) and usage
/// (for current-month total_usage in cents). AvailablePct = 100 × (1 − used/limit).
///
/// Fail-open: any network error, unexpected status code, or unrecognised
/// response shape returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1
/// so a broken endpoint never blocks work items.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class CodexQuotaProbe : IAgentQuotaProbe
{
    internal const string SubscriptionEndpoint = "https://api.openai.com/v1/dashboard/billing/subscription";
    internal const string UsageEndpointBase = "https://api.openai.com/v1/dashboard/billing/usage";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _token;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CodexQuotaProbe> _log;

    // Single-entry cache: (snapshot, expiry). Protected by _lock.
    private (AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Codex;

    public CodexQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<CodexQuotaProbe> log)
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

    private async Task<AgentQuotaSnapshot> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");

            // Step 1: fetch subscription limit.
            // Do NOT log the Authorization header — it contains the API key.
            using var subReq = new HttpRequestMessage(HttpMethod.Get, SubscriptionEndpoint);
            subReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var subResp = await client.SendAsync(subReq, ct);
            if (!subResp.IsSuccessStatusCode)
            {
                _log.LogDebug("Codex subscription endpoint returned {StatusCode}; treating quota as unknown",
                    (int)subResp.StatusCode);
                return Unknown($"HTTP {(int)subResp.StatusCode}");
            }

            // Do NOT log the response body — it may contain account identifiers.
            var subBody = await ReadCappedAsync(subResp.Content, ct);
            if (subBody is null) return Unknown("response too large");

            var hardLimitUsd = ParseHardLimit(subBody);
            if (hardLimitUsd <= 0) return Unknown("unexpected subscription response shape");

            // Step 2: fetch current-month usage.
            var today = DateTimeOffset.UtcNow;
            var startDate = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero).ToString("yyyy-MM-dd");
            var endDate = today.AddDays(1).ToString("yyyy-MM-dd");
            var usageUrl = $"{UsageEndpointBase}?start_date={startDate}&end_date={endDate}";

            using var usageReq = new HttpRequestMessage(HttpMethod.Get, usageUrl);
            usageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var usageResp = await client.SendAsync(usageReq, ct);
            if (!usageResp.IsSuccessStatusCode)
            {
                _log.LogDebug("Codex usage endpoint returned {StatusCode}; treating quota as unknown",
                    (int)usageResp.StatusCode);
                return Unknown($"HTTP {(int)usageResp.StatusCode}");
            }

            var usageBody = await ReadCappedAsync(usageResp.Content, ct);
            if (usageBody is null) return Unknown("response too large");

            var totalUsageCents = ParseTotalUsage(usageBody);
            if (totalUsageCents < 0) return Unknown("unexpected usage response shape");

            var usedUsd = totalUsageCents / 100.0;
            var pct = 100.0 * (1.0 - usedUsd / hardLimitUsd);
            return new AgentQuotaSnapshot { AvailablePct = Math.Max(0.0, pct) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex quota probe failed; treating quota as unknown");
            return Unknown("network error");
        }
    }

    /// <summary>
    /// Parses the OpenAI billing subscription response.
    /// Expected shape: <c>{ "hard_limit_usd": 100.0, ... }</c>
    /// Returns the hard limit in USD, or -1 if unrecognised.
    /// </summary>
    internal static double ParseHardLimit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("hard_limit_usd", out var el))
            {
                var limit = el.GetDouble();
                if (limit > 0) return limit;
            }
            return -1;
        }
        catch (JsonException) { return -1; }
    }

    /// <summary>
    /// Parses the OpenAI billing usage response.
    /// Expected shape: <c>{ "data": [...], "total_usage": 1234 }</c>
    /// where <c>total_usage</c> is in cents. Returns the value, or -1 if unrecognised.
    /// </summary>
    internal static double ParseTotalUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("total_usage", out var el))
            {
                var usage = el.GetDouble();
                if (usage >= 0) return usage;
            }
            return -1;
        }
        catch (JsonException) { return -1; }
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

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };
}
