using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// An agent's explicit, structured determination that a work item requires no
/// action (e.g. a conditional item whose precondition does not hold).
/// Reported via <c>.codeybox/no-action-required.json</c> instead of being
/// inferred from an empty diff.
/// </summary>
/// <param name="Reason">
/// Why no action is warranted. Shown to the operator on the resolved item.
/// </param>
/// <param name="Precondition">
/// Optional description of the precondition that was checked and found not to
/// hold, so the determination can be revisited if conditions change.
/// </param>
public sealed record NoActionRequiredReport(string Reason, string? Precondition);

/// <summary>
/// Parses and validates <c>.codeybox/no-action-required.json</c> emitted by
/// agents. The file is agent-controlled (untrusted): malformed content never
/// resolves an item — it is dropped with a warning and the empty diff keeps
/// the pre-existing no-changes failure path. A valid report carries a
/// non-empty reason (max 2 000 chars) and an optional precondition
/// (max 500 chars); anything else is rejected.
/// </summary>
public static class NoActionRequiredFileParser
{
    public const int MaxReasonLength = 2000;
    public const int MaxPreconditionLength = 500;

    /// <summary>
    /// Returns the parsed report, or null when no determination was reported
    /// (missing/blank file, invalid JSON, or a failed validation).
    /// </summary>
    public static NoActionRequiredReport? Parse(string? json, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            log.LogWarning("no-action-required.json is not valid JSON: {Error}", ex.Message);
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                log.LogWarning(
                    "no-action-required.json must be a JSON object with a 'reason' string; got root kind {Kind}",
                    doc.RootElement.ValueKind);
                return null;
            }

            var reason = GetString(doc.RootElement, "reason")?.Trim();
            if (string.IsNullOrEmpty(reason))
            {
                log.LogWarning("no-action-required.json: missing or empty required field 'reason'; ignoring");
                return null;
            }
            if (reason.Length > MaxReasonLength)
            {
                log.LogWarning(
                    "no-action-required.json: 'reason' exceeds {Max} chars ({Len}); ignoring",
                    MaxReasonLength, reason.Length);
                return null;
            }

            string? precondition = null;
            if (doc.RootElement.TryGetProperty("precondition", out var preconditionEl))
            {
                if (preconditionEl.ValueKind == JsonValueKind.Null)
                {
                    precondition = null;
                }
                else if (preconditionEl.ValueKind != JsonValueKind.String)
                {
                    log.LogWarning("no-action-required.json: 'precondition' must be a string; ignoring the field");
                }
                else
                {
                    var trimmed = preconditionEl.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        if (trimmed.Length > MaxPreconditionLength)
                        {
                            log.LogWarning(
                                "no-action-required.json: 'precondition' exceeds {Max} chars ({Len}); ignoring the field",
                                MaxPreconditionLength, trimmed.Length);
                        }
                        else
                        {
                            precondition = trimmed;
                        }
                    }
                }
            }

            return new NoActionRequiredReport(reason, precondition);
        }
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
