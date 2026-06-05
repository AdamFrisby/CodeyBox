using System.Text.Json;

namespace CodeyBox.Agents.Codex;

internal readonly record struct CodexTokenUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens);

internal static class CodexUsageParser
{
    private const int MaxUsageSearchDepth = 8;

    public static CodexTokenUsage? TryExtract(params JsonElement[] roots)
    {
        foreach (var root in roots)
        {
            if (TryExtract(root, depth: 0, out var usage))
                return usage;
        }

        return null;
    }

    private static bool TryExtract(JsonElement root, int depth, out CodexTokenUsage usage)
    {
        if (depth > MaxUsageSearchDepth)
        {
            usage = default;
            return false;
        }

        if (root.ValueKind == JsonValueKind.String)
            return TryExtractString(root, depth, out usage);

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryExtract(item, depth + 1, out usage))
                    return true;
            }

            usage = default;
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            usage = default;
            return false;
        }

        if (TryReadUsageObject(root, out usage))
            return true;

        foreach (var name in new[]
        {
            "usage", "token_usage", "total_token_usage", "token_usage_json",
            "last_token_usage", "payload", "item", "info",
        })
        {
            if (root.TryGetProperty(name, out var child)
                && TryExtract(child, depth + 1, out usage))
                return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if ((property.Value.ValueKind == JsonValueKind.Object
                    || property.Value.ValueKind == JsonValueKind.Array
                    || property.Value.ValueKind == JsonValueKind.String)
                && (property.Name.Contains("usage", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryExtract(property.Value, depth + 1, out usage))
                    return true;
            }
        }

        usage = default;
        return false;
    }

    private static bool TryExtractString(JsonElement root, int depth, out CodexTokenUsage usage)
    {
        var raw = root.GetString();
        if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return TryExtract(doc.RootElement, depth + 1, out usage);
            }
            catch (JsonException)
            {
            }
        }

        usage = default;
        return false;
    }

    private static bool TryReadUsageObject(JsonElement root, out CodexTokenUsage usage)
    {
        var hasInput = TryFirstNonNegativeInt32(root, out var totalInput, "prompt_tokens", "input_tokens");
        var hasOutput = TryFirstNonNegativeInt32(root, out var output, "completion_tokens", "output_tokens");
        var cached = TryReadCachedInputTokens(root);

        if (!hasInput && !hasOutput && cached == 0)
        {
            usage = default;
            return false;
        }

        usage = new CodexTokenUsage(
            InputTokens: Math.Max(0, totalInput - cached),
            CachedInputTokens: cached,
            OutputTokens: output);
        return usage.InputTokens > 0 || usage.CachedInputTokens > 0 || usage.OutputTokens > 0;
    }

    private static bool TryFirstNonNegativeInt32(JsonElement root, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element)
                && TryGetNonNegativeInt32(element, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static int TryReadCachedInputTokens(JsonElement usage)
    {
        if (usage.TryGetProperty("cached_input_tokens", out var cachedInput)
            && TryGetNonNegativeInt32(cachedInput, out var cachedInputValue))
            return cachedInputValue;

        if (TryReadDetailsCachedTokens(usage, "prompt_tokens_details", out var promptCachedValue))
            return promptCachedValue;

        if (TryReadDetailsCachedTokens(usage, "input_tokens_details", out var inputCachedValue))
            return inputCachedValue;

        if (usage.TryGetProperty("cache_read_input_tokens", out var cacheRead)
            && TryGetNonNegativeInt32(cacheRead, out var cacheReadValue))
            return cacheReadValue;

        if (usage.TryGetProperty("cached_tokens", out var cached)
            && TryGetNonNegativeInt32(cached, out var cachedValue))
            return cachedValue;

        return 0;
    }

    private static bool TryReadDetailsCachedTokens(JsonElement usage, string propertyName, out int cached)
    {
        if (usage.TryGetProperty(propertyName, out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var cachedElement)
            && TryGetNonNegativeInt32(cachedElement, out cached))
            return true;

        cached = 0;
        return false;
    }

    private static bool TryGetNonNegativeInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= 0)
            return true;

        value = 0;
        return false;
    }
}
