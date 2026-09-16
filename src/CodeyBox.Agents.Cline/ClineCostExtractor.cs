using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Best-effort token extraction from cline's <c>--json</c> output.
///
/// <para><b>Measured, not fabricated.</b> The terminal <c>run_result</c>
/// frame carries <c>usage</c> / <c>aggregateUsage</c> with
/// <c>inputTokens</c> / <c>outputTokens</c> / <c>cacheReadTokens</c>
/// (verified against cline 3.0.62 live runs, including tool runs and
/// auth/quota failures) plus the dispatch model in <c>model.id</c>. The
/// extractor prefers the last <c>run_result</c>'s <c>aggregateUsage</c>
/// (cumulative across iterations), falling back to its <c>usage</c>, then to
/// the last <c>done</c>-event usage, then to the last per-iteration
/// <c>usage</c> totals. <c>cacheReadTokens</c> maps to the cached-input
/// bucket (charged at the cached rate); <c>cacheWriteTokens</c> has no
/// cost-bucket counterpart in <see cref="AgentCostSnapshot"/> and is not
/// folded into either bucket — folding it into input would double-bill
/// tokens already counted there.</para>
///
/// <para><b>Unknown, not zero.</b> Output with no machine-readable usage
/// frame (empty response, truncated stream, non-JSON mode) returns null —
/// callers record no cost row — rather than a zero snapshot that looks like
/// measured data. A failure <c>run_result</c> whose usage is all zeros IS
/// returned as a zero snapshot: the CLI measured no token spend. Never
/// throws.</para>
/// </summary>
public sealed class ClineCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Cline;

    /// <summary>
    /// No built-in fallback rate: cost is computed from the measured token
    /// snapshot against the configured per-model rates, and a fallback would
    /// book a rate that was never listed. The shipped free-tier member's
    /// <c>$0</c> rate lives in the pricing bucket, not here.
    /// </summary>
    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            return TryExtractCore(agentStdout, agentStderr);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentCostSnapshot? TryExtractCore(string? agentStdout, string? agentStderr)
    {
        UsageReading? runResult = null;
        UsageReading? done = null;
        UsageReading? iteration = null;
        string? modelId = null;

        foreach (var text in new[] { agentStdout, agentStderr })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || !line.StartsWith('{'))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!root.TryGetProperty("type", out var typeProp)
                        || typeProp.ValueKind != JsonValueKind.String)
                        continue;

                    var type = typeProp.GetString();
                    if (string.Equals(type, "run_result", StringComparison.OrdinalIgnoreCase))
                    {
                        var usage = ClineJson.FirstObject(root, "aggregateUsage", "usage");
                        if (usage is { } u && TryReadUsage(u) is { } reading)
                            runResult = reading;
                        if (root.TryGetProperty("model", out var model)
                            && model.ValueKind == JsonValueKind.Object
                            && model.TryGetProperty("id", out var id)
                            && id.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(id.GetString()))
                        {
                            modelId = id.GetString();
                        }
                    }
                    else if (string.Equals(type, "agent_event", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("event", out var inner)
                        && inner.ValueKind == JsonValueKind.Object
                        && inner.TryGetProperty("type", out var innerType)
                        && innerType.ValueKind == JsonValueKind.String)
                    {
                        var innerTypeName = innerType.GetString();
                        if (string.Equals(innerTypeName, "done", StringComparison.OrdinalIgnoreCase)
                            && ClineJson.FirstObject(inner, "usage") is { } doneUsage
                            && TryReadUsage(doneUsage) is { } doneReading)
                        {
                            done = doneReading;
                        }
                        else if (string.Equals(innerTypeName, "usage", StringComparison.OrdinalIgnoreCase)
                            && TryReadUsageTotals(inner) is { } iterationReading)
                        {
                            iteration = iterationReading;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Half-written or interleaved chatter — keep scanning.
                }
            }
        }

        var best = runResult ?? done ?? iteration;
        if (best is null)
            return null;

        return new AgentCostSnapshot(
            SaturatingToInt(best.InputTokens),
            CachedInputTokens: SaturatingToInt(best.CachedInputTokens),
            SaturatingToInt(best.OutputTokens),
            ModelId: modelId);
    }

    private sealed record UsageReading(long InputTokens, long CachedInputTokens, long OutputTokens);

    private static UsageReading? TryReadUsage(JsonElement usage)
    {
        var input = ReadNonNegative(usage, "inputTokens");
        var output = ReadNonNegative(usage, "outputTokens");
        var cached = ReadNonNegative(usage, "cacheReadTokens");
        if (input is null && output is null && cached is null)
            return null;

        return new UsageReading(input ?? 0, cached ?? 0, output ?? 0);
    }

    private static UsageReading? TryReadUsageTotals(JsonElement inner)
    {
        // Per-iteration usage events carry both the iteration slice
        // (inputTokens) and the running totals (totalInputTokens); the
        // cumulative form is the one that survives a truncated stream.
        var input = ReadNonNegative(inner, "totalInputTokens") ?? ReadNonNegative(inner, "inputTokens");
        var output = ReadNonNegative(inner, "totalOutputTokens") ?? ReadNonNegative(inner, "outputTokens");
        var cached = ReadNonNegative(inner, "totalCacheReadTokens") ?? ReadNonNegative(inner, "cacheReadTokens");
        if (input is null && output is null && cached is null)
            return null;

        return new UsageReading(input ?? 0, cached ?? 0, output ?? 0);
    }

    private static long? ReadNonNegative(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt64(out var value)
                && value >= 0)
            {
                return value;
            }
        }

        return null;
    }

    private static int SaturatingToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;
}
