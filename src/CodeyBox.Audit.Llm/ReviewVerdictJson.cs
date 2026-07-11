using System.Text.Json;

namespace CodeyBox.Audit.Llm;

/// <summary>
/// Extracts the single JSON verdict object from less-trusted review-model
/// output. The rule is deliberately strict and shared by every review path
/// (code review, plan review, the plan-audit chain) so the anti-injection
/// policy has one source of truth: accept only a whole-response JSON object, or
/// a whole-response single JSON code fence whose contents are one JSON object.
/// It never scans for an embedded object, because a chatty or prompt-injected
/// reviewer could place a harmless pass before the real rejecting verdict.
/// </summary>
internal static class ReviewVerdictJson
{
    /// <summary>
    /// Returns the canonical JSON-object text from <paramref name="raw"/>, or
    /// throws <see cref="JsonException"/> when the response is not exactly one
    /// JSON object (optionally wrapped in a single json/untagged code fence).
    /// </summary>
    public static string ExtractObject(string raw)
    {
        var trimmed = raw.Trim();
        var candidate = StripSingleJsonCodeFence(trimmed);
        using var doc = JsonDocument.Parse(candidate);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("verdict must be a JSON object");
        return candidate;
    }

    private static string StripSingleJsonCodeFence(string trimmed)
    {
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var normalized = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal);
        var firstLineEnd = normalized.IndexOf('\n');
        if (firstLineEnd < 0)
            throw new JsonException("code-fenced verdict is missing content");

        var info = normalized[3..firstLineEnd].Trim();
        if (info.Length > 0 && !info.Equals("json", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("verdict code fence must be tagged json or untagged");

        const string ClosingFence = "\n```";
        var closingStart = normalized.LastIndexOf(ClosingFence, StringComparison.Ordinal);
        if (closingStart <= firstLineEnd)
            throw new JsonException("code-fenced verdict is missing a closing fence");

        var trailing = normalized[(closingStart + ClosingFence.Length)..].Trim();
        if (trailing.Length > 0)
            throw new JsonException("code-fenced verdict must not include text after the closing fence");

        return normalized[(firstLineEnd + 1)..closingStart].Trim();
    }
}
