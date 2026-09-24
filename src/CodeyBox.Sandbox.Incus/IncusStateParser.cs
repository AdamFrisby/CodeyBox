using System.Text.Json;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Safe JSON parser for Incus instance state responses.
/// </summary>
internal static class IncusStateParser
{
    public static bool TryParseGuestCpuUsage(string? json, out long cpuUsageNanoseconds)
    {
        cpuUsageNanoseconds = 0;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var target = root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                ? meta
                : root;

            if (target.ValueKind != JsonValueKind.Object)
                return false;

            if (target.TryGetProperty("status", out var statusProp)
                && statusProp.ValueKind == JsonValueKind.String
                && !string.Equals(statusProp.GetString(), "Running", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (target.TryGetProperty("cpu", out var cpuProp) && cpuProp.ValueKind == JsonValueKind.Object)
            {
                if (cpuProp.TryGetProperty("usage", out var usageProp) && usageProp.TryGetInt64(out var ns) && ns >= 0)
                {
                    cpuUsageNanoseconds = ns;
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
