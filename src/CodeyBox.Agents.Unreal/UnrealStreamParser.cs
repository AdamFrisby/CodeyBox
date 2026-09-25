using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Structured stream parser for Unreal Labs' unreal-agent session items.
///
/// <para>The CLI writes session items to stdout as JSONL (types: <c>input</c>,
/// <c>turn</c>, <c>model_response</c>, <c>tool_call_status</c>, <c>fork</c>).
/// Each item carries PascalCase properties: <c>Sequence</c>, <c>RecordedAt</c>,
/// <c>Kind</c>, and <c>Data</c>. On error, it also writes a terminal
/// <c>{"type":"error","message":"..."}</c> event to stdout.</para>
/// </summary>
public sealed class UnrealStreamParser : FlexibleAgentStreamParser
{
    public UnrealStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Unreal, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsUnrealSessionJsonEvent(line);

    internal static bool IsUnrealSessionJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;

        // Distinctive Unreal session item schema: Sequence (number), Kind (string), RecordedAt (string), Data (object)
        return line.TryGetProperty("Sequence", out var seq) && seq.ValueKind == JsonValueKind.Number
            && line.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String
            && line.TryGetProperty("RecordedAt", out var rec) && rec.ValueKind == JsonValueKind.String
            && line.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object;
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type");
        if (CliAgentRunnerBase.TryReadStderrEnvelope(root, out _))
            return base.ParseEvent(root);

        // Terminal error event from unreal-agent-runner
        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = FirstString(root, "message") ?? string.Empty;
            return new ParsedEvent(
                EventType: "error",
                Timestamp: null,
                IsAssistant: false,
                ToolStarts: [],
                ToolResults: [],
                InputTokens: null,
                OutputTokens: null,
                CachedInputTokens: null,
                EstimatedUsd: null,
                TotalDuration: null,
                TimeToFirstToken: null,
                FinalText: errorMessage,
                IsRecognized: true,
                StderrText: errorMessage);
        }

        if (root.TryGetProperty("Kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String)
        {
            var kind = kindProp.GetString()!;
            var timestamp = TryTimestamp(root, "RecordedAt");

            if (string.Equals(kind, "model_response", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("Data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("Response", out var resp)
                && resp.ValueKind == JsonValueKind.Object)
            {
                var starts = new List<ToolBuilder>();
                string? finalText = null;
                var isAssistant = false;

                if (resp.TryGetProperty("Output", out var outputArray)
                    && outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        var itemType = FirstString(item, "Type");
                        if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetProperty("Data", out var msgData)
                            && msgData.ValueKind == JsonValueKind.Object)
                        {
                            isAssistant = true;
                            if (msgData.TryGetProperty("Text", out var textProp)
                                && textProp.ValueKind == JsonValueKind.String)
                            {
                                finalText = textProp.GetString();
                            }
                        }
                        else if (string.Equals(itemType, "tool_call", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetProperty("Data", out var toolData)
                            && toolData.ValueKind == JsonValueKind.Object)
                        {
                            isAssistant = true;
                            var callId = FirstString(toolData, "CallID") ?? Guid.NewGuid().ToString("N");
                            var name = FirstString(toolData, "Name") ?? "unknown";
                            var args = FirstString(toolData, "Arguments") ?? string.Empty;
                            starts.Add(new ToolBuilder(callId, name, args, timestamp));
                        }
                    }
                }

                int? inputTokens = null;
                int? outputTokens = null;
                int? cachedTokens = null;

                if (resp.TryGetProperty("Usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("InputTokens", out var inProp) && inProp.TryGetInt32(out var inVal))
                        inputTokens = inVal;
                    else if (usage.TryGetProperty("input_tokens", out var inSnake) && inSnake.TryGetInt32(out var inSnakeVal))
                        inputTokens = inSnakeVal;

                    if (usage.TryGetProperty("OutputTokens", out var outProp) && outProp.TryGetInt32(out var outVal))
                        outputTokens = outVal;
                    else if (usage.TryGetProperty("output_tokens", out var outSnake) && outSnake.TryGetInt32(out var outSnakeVal))
                        outputTokens = outSnakeVal;

                    if (usage.TryGetProperty("CachedInputTokens", out var cProp) && cProp.TryGetInt32(out var cVal))
                        cachedTokens = cVal;
                    else if (usage.TryGetProperty("cached_input_tokens", out var cSnake) && cSnake.TryGetInt32(out var cSnakeVal))
                        cachedTokens = cSnakeVal;
                }

                return new ParsedEvent(
                    EventType: kind,
                    Timestamp: timestamp,
                    IsAssistant: isAssistant,
                    ToolStarts: starts,
                    ToolResults: [],
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    CachedInputTokens: cachedTokens,
                    EstimatedUsd: null,
                    TotalDuration: null,
                    TimeToFirstToken: null,
                    FinalText: finalText,
                    IsRecognized: true,
                    StderrText: null);
            }

            if (string.Equals(kind, "tool_call_status", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("Data", out var statusData)
                && statusData.ValueKind == JsonValueKind.Object)
            {
                var callId = FirstString(statusData, "CallID") ?? "unknown";
                var results = new List<ToolResultBuilder>();

                var statusError = string.Empty;
                if (statusData.TryGetProperty("Status", out var statusObj)
                    && statusObj.ValueKind == JsonValueKind.Object)
                {
                    statusError = FirstString(statusObj, "Error") ?? string.Empty;
                }

                if (statusData.TryGetProperty("Operations", out var ops) && ops.ValueKind == JsonValueKind.Array)
                {
                    foreach (var op in ops.EnumerateArray())
                    {
                        var opStatus = FirstString(op, "Status");
                        if (string.Equals(opStatus, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            var exitCode = 0;
                            long outputBytes = 0;

                            if (op.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.Object
                                && state.TryGetProperty("Result", out var res) && res.ValueKind == JsonValueKind.Object)
                            {
                                if (res.TryGetProperty("ExitCode", out var ec) && ec.TryGetInt32(out var exitVal))
                                    exitCode = exitVal;
                                if (res.TryGetProperty("OutSize", out var os) && os.TryGetInt64(out var outSizeVal))
                                    outputBytes += outSizeVal;
                                if (res.TryGetProperty("ErrSize", out var es) && es.TryGetInt64(out var errSizeVal))
                                    outputBytes += errSizeVal;
                            }

                            var succeeded = string.IsNullOrEmpty(statusError) && exitCode == 0;
                            results.Add(new ToolResultBuilder(callId, succeeded, (int)Math.Min(int.MaxValue, outputBytes), timestamp, Duration: null));
                        }
                    }
                }

                return new ParsedEvent(
                    EventType: kind,
                    Timestamp: timestamp,
                    IsAssistant: false,
                    ToolStarts: [],
                    ToolResults: results,
                    InputTokens: null,
                    OutputTokens: null,
                    CachedInputTokens: null,
                    EstimatedUsd: null,
                    TotalDuration: null,
                    TimeToFirstToken: null,
                    FinalText: null,
                    IsRecognized: true,
                    StderrText: null);
            }

            return new ParsedEvent(
                EventType: kind,
                Timestamp: timestamp,
                IsAssistant: false,
                ToolStarts: [],
                ToolResults: [],
                InputTokens: null,
                OutputTokens: null,
                CachedInputTokens: null,
                EstimatedUsd: null,
                TotalDuration: null,
                TimeToFirstToken: null,
                FinalText: null,
                IsRecognized: true,
                StderrText: null);
        }

        return base.ParseEvent(root);
    }
}
