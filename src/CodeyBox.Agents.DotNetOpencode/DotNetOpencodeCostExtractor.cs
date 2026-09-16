using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Best-effort token-count extractor for dotnet-opencode's
/// <c>run --format json</c> event stream.
///
/// <para>Each executed step closes with a <c>step_finish</c> frame whose
/// <c>part</c> carries the server token vocabulary
/// <c>tokens:{input, output, reasoning, cache:{read, write}}</c> (names
/// verified live via <c>stats --json</c> against the same server build; the
/// run-output layer copies the same fields onto the step part). The extractor
/// scans every JSON line of stdout/stderr and SUMS the step frames: unlike
/// pi's cumulative per-message usage, each <c>step_finish</c> reports only
/// its own step. The run never echoes the dispatch model id on any frame, so
/// the snapshot records no model — per-model rate lookup cannot apply, and
/// cost attribution stays at raw token counts.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped and no pricing bucket
/// exists in <c>agent-pricing-defaults.json</c>: dotnet-opencode is a
/// provider-agnostic BYOK front whose spend bills to the operator's own
/// provider accounts at that provider's list prices, and the CLI reports no
/// model id to key rates on. Any single fallback rate would be fabricated
/// data, so the adapter reports tokens with an unknown model and lets the
/// cost pipeline record them unattributed rather than priced.</para>
/// </summary>
public sealed class DotNetOpencodeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.DotNetOpencode;

    public ModelRateConfig? DefaultPricing { get; } = null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            var fromStdout = ScanStream(agentStdout);
            var fromStderr = ScanStream(agentStderr);
            // Prefer the stream with the larger reported total — the
            // --standalone hosting logs can interleave on either stream.
            if (fromStdout is null) return fromStderr;
            if (fromStderr is null) return fromStdout;
            var stdoutTotal = fromStdout.InputTokens + fromStdout.CachedInputTokens + fromStdout.OutputTokens;
            var stderrTotal = fromStderr.InputTokens + fromStderr.CachedInputTokens + fromStderr.OutputTokens;
            return stderrTotal > stdoutTotal ? fromStderr : fromStdout;
        }
        catch (Exception)
        {
            // Contract: implementations must never throw.
            return null;
        }
    }

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var input = 0;
        var cached = 0;
        var output = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('{'))
                continue;
            // Pre-screen: tokens only ride on step_finish lines, so the JSON
            // parse below never runs on tool-call chatter. Uses Ordinal (not
            // OrdinalIgnoreCase) — the wire type is lowercase `step_finish`
            // and a case-insensitive match would also trip on prose inside
            // message text.
            if (!line.Contains("\"step_finish\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryExtractStepTokens(doc.RootElement) is { } step)
                {
                    input += step.Input;
                    cached += step.Cached;
                    output += step.Output;
                }
            }
            catch (JsonException)
            {
                // Interleaved hosting logs or half-written frames — keep scanning.
            }
        }

        if (input == 0 && cached == 0 && output == 0)
            return null;
        // Model id is deliberately null: no run frame echoes the dispatch
        // model, so attributing these tokens to any model id would be a guess
        // that looks like data. Unknown, not defaulted.
        return new AgentCostSnapshot(input, cached, output, null);
    }

    private readonly record struct StepTokens(int Input, int Cached, int Output);

    private static StepTokens? TryExtractStepTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "step_finish", StringComparison.Ordinal))
            return null;
        if (!root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
            return null;
        if (!part.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadNonNegative(tokens, "input");
        var output = ReadNonNegative(tokens, "output");
        var cached = 0;
        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            cached = ReadNonNegative(cache, "read");

        if (input == 0 && cached == 0 && output == 0)
            return null;
        return new StepTokens(input, cached, output);
    }

    private static int ReadNonNegative(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n > 0)
            return n;
        return 0;
    }
}
