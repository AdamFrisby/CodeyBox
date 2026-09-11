using System.Globalization;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.OpencodeGoQuotaPlugin;

/// <summary>
/// Pure parser for the opencode-go plan usage endpoint
/// (<c>GET /usage</c> on the zen go API root, captured verbatim 2026-09-07):
///
/// <code>
/// {"usage":{
///   "rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-07T06:42:12.975Z"},
///   "weekly": {"status":"ok","percent":0,"resetsAt":"2026-09-14T00:00:00.975Z"},
///   "monthly":{"status":"ok","percent":0,"resetsAt":"2026-09-26T04:00:22.975Z"}}}
/// </code>
///
/// <para><b>Direction.</b> <c>percent</c> is the fraction USED, so availability
/// is <c>100 - percent</c>. This is inverted relative to meters that report a
/// remaining fraction (e.g. antigravity): reading it the wrong way round would
/// report a drained account as 100% available and dispatch straight into
/// exhaustion. The inversion is pinned by direction tests, not by convention.
/// </para>
/// </summary>
internal static class OpencodeGoUsageParser
{
    private static readonly string[] WindowNames = ["rolling", "weekly", "monthly"];

    /// <summary>
    /// Parses a <c>/usage</c> body into a quota snapshot. Returns a known
    /// snapshot only when every expected window carries a usable reading; any
    /// missing window, non-<c>"ok"</c> status, non-numeric percent, or
    /// unparseable reset maps the whole reading to Unknown (Permanent) rather
    /// than inventing availability from a subset of windows.
    /// </summary>
    public static AgentQuotaSnapshot Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent, "invalid JSON");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return AgentQuotaSnapshot.UnknownSnapshot(
                    QuotaUnknownReason.Permanent, "unexpected response shape: missing 'usage'");
            }

            var windows = new List<WindowQuota>(WindowNames.Length);
            foreach (var name in WindowNames)
            {
                var window = ParseWindow(usage, name);
                if (window is null)
                    return AgentQuotaSnapshot.UnknownSnapshot(
                        QuotaUnknownReason.Permanent, $"window '{name}' has no usable reading");
                windows.Add(window);
            }

            // The binding window is the scarcest one; surface its reset so a
            // quota park waits the correct span.
            var binding = windows.MinBy(w => w.AvailablePct)!;
            return new AgentQuotaSnapshot
            {
                AvailablePct = binding.AvailablePct,
                ResetAt = binding.ResetAt,
                Notes = $"opencode-go usage: {string.Join(
                    ", ", windows.Select(w => $"{w.Name}={w.AvailablePct.ToString("0.#", CultureInfo.InvariantCulture)}%"))}",
                Windows = windows,
            };
        }
    }

    private static WindowQuota? ParseWindow(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!element.TryGetProperty("percent", out var percentElement)
            || !TryGetDouble(percentElement, out var percentUsed)
            || double.IsNaN(percentUsed)
            || double.IsInfinity(percentUsed))
            return null;

        if (!element.TryGetProperty("resetsAt", out var resetsAtElement)
            || resetsAtElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                resetsAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var resetsAt))
            return null;

        var available = Math.Clamp(100.0 - percentUsed, 0.0, 100.0);
        return new WindowQuota
        {
            Name = name,
            AvailablePct = available,
            ResetAt = resetsAt,
            UsedPercent = Math.Clamp(percentUsed, 0.0, 100.0),
        };
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return true;
        value = 0;
        return false;
    }
}
