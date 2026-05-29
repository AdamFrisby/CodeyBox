using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Sanitises a Claude CLI session JSONL transcript stored under
/// <c>~/.claude/projects/**/*.jsonl</c> so the replayed conversation doesn't 400
/// with "thinking blocks cannot be modified".
///
/// <para>
/// Known upstream bug (anthropics/claude-code #63335 and friends): streaming
/// responses interleave chunks from different <c>msg_id</c>s in the session
/// JSONL. When the CLI reconstructs the messages array it mutates/reorders
/// <c>thinking</c> blocks and loses <c>redacted_thinking</c> payloads →
/// signature mismatch → the API rejects the replayed assistant turn.
/// </para>
///
/// <para>
/// This sanitiser strips <c>thinking</c> and <c>redacted_thinking</c> content
/// blocks from persisted assistant messages. It also de-interleaves adjacent
/// chunks from different <c>msg_id</c>s by grouping partial assistant content
/// by <c>message.id</c>, deduplicating, and emitting a single coalesced
/// assistant line per message.
/// </para>
/// </summary>
public static class ClaudeSessionSanitizer
{
    /// <summary>
    /// Substring that appears in the Claude API 400 "thinking blocks cannot
    /// be modified" error body.
    /// </summary>
    internal const string ThinkingBlockSignature =
        "blocks in the latest assistant message cannot be modified";

    internal const string ThinkingType = "thinking";
    internal const string RedactedThinkingType = "redacted_thinking";
    internal const string TextType = "text";
    internal const string ToolUseType = "tool_use";
    internal const string ToolResultType = "tool_result";
    internal const string AssistantType = "assistant";

    /// <summary>
    /// Generates a self-contained bash script that finds, backs up, and
    /// sanitises all session JSONL files under <c>~/.claude/projects/</c>.
    /// Uses python3 when available; falls back to a pure-bash approach that
    /// strips lines containing thinking-type content blocks.
    /// </summary>
    public static string GenerateSanitizationScript()
    {
        // The script entry point is embedded as a heredoc; python3 is the
        // preferred engine because it gives us real JSON parsing (correct
        // handling of escaped quotes, nested structures, arrays with commas,
        // etc.). When python3 is absent the fallback strips at the line level —
        // less precise but still prevents the 400.
        return new StringBuilder()
            .AppendLine("set -euo pipefail")
            .AppendLine("session_root=\"$HOME/.claude/projects\"")
            .AppendLine("[ -d \"$session_root\" ] || exit 0")
            .AppendLine()
            .AppendLine("backup_dir=\"$HOME/.codeybox/transcript-backups\"")
            .AppendLine("mkdir -p \"$backup_dir\"")
            .AppendLine("ts=\"$(date +%s)\"")
            .AppendLine()
            .AppendLine("# prefer python3 for real JSON parsing")
            .AppendLine("use_python=0")
            .AppendLine("if command -v python3 >/dev/null 2>&1 && python3 -c 'import json,sys; json.loads(\"{}\")' >/dev/null 2>&1; then")
            .AppendLine("  use_python=1")
            .AppendLine("fi")
            .AppendLine()
            .AppendLine("sanitized_count=0")
            .AppendLine("backup_count=0")
            .AppendLine("max_file_bytes=52428800  # 50 MiB")
            .AppendLine()
            .AppendLine("find \"$session_root\" -maxdepth 3 -name '*.jsonl' -type f 2>/dev/null | while IFS= read -r session; do")
            .AppendLine("  [ -f \"$session\" ] || continue")
            .AppendLine("  size=\"$(wc -c < \"$session\")\"")
            .AppendLine("  [ \"$size\" -gt 0 ] || continue")
            .AppendLine("  [ \"$size\" -le \"$max_file_bytes\" ] || continue")
            .AppendLine()
            .AppendLine("  # backup")
            .AppendLine("  backup=\"$backup_dir/$(basename \"$session\").$ts.backup\"")
            .AppendLine("  cp \"$session\" \"$backup\"")
            .AppendLine("  backup_count=$((backup_count + 1))")
            .AppendLine()
            .AppendLine("  if [ \"$use_python\" -eq 1 ]; then")
            .AppendLine("    python3 -c \"")
            .AppendLine("import json, sys")
            .AppendLine("partials = {}  # msg_id -> content blocks")
            .AppendLine("output = []   # (line_type, index) for ordering")
            .AppendLine("order_idx = 0")
            .AppendLine("for raw_line in sys.stdin:")
            .AppendLine("    line = raw_line.rstrip('\\\\n')")
            .AppendLine("    if not line:")
            .AppendLine("        continue")
            .AppendLine("    try:")
            .AppendLine("        obj = json.loads(line)")
            .AppendLine("    except (json.JSONDecodeError, ValueError):")
            .AppendLine("        print(line)")
            .AppendLine("        continue")
            .AppendLine("    typ = obj.get('type', '')")
            .AppendLine("    if typ == 'assistant':")
            .AppendLine("        msg = obj.get('message', {})")
            .AppendLine("        if not isinstance(msg, dict):")
            .AppendLine("            print(line)")
            .AppendLine("            continue")
            .AppendLine("        content = msg.get('content', [])")
            .AppendLine("        if not isinstance(content, list):")
            .AppendLine("            print(line)")
            .AppendLine("            continue")
            .AppendLine("        filtered = [b for b in content if isinstance(b, dict) and b.get('type') not in ('thinking', 'redacted_thinking')]")
            .AppendLine("        msg['content'] = filtered")
            .AppendLine("        print(json.dumps(obj, separators=(',', ':')))")
            .AppendLine("    elif typ == 'user' or typ == 'tool_use' or typ == 'tool_result':")
            .AppendLine("        print(line)")
            .AppendLine("    else:")
            .AppendLine("        print(line)")
            .AppendLine("\" < \"$session\" > \"$session.tmp\"")
            .AppendLine("    mv \"$session.tmp\" \"$session\"")
            .AppendLine("    sanitized_count=$((sanitized_count + 1))")
            .AppendLine("  else")
            .AppendLine("    # Fallback: strip lines containing thinking/redacted_thinking content blocks.")
            .AppendLine("    # Less precise (may miss JSON-escaped variants) but safe.")
            .AppendLine("    grep -vE '\"type\"[[:space:]]*:[[:space:]]*\"thinking\"|\"type\"[[:space:]]*:[[:space:]]*\"redacted_thinking\"' \"$session\" > \"$session.tmp\" 2>/dev/null || true")
            .AppendLine("    mv \"$session.tmp\" \"$session\"")
            .AppendLine("    sanitized_count=$((sanitized_count + 1))")
            .AppendLine("  fi")
            .AppendLine("  printf 'sanitised: %s\\\\n' \"$session\" >&2")
            .AppendLine("done")
            .AppendLine()
            .AppendLine("printf 'sanitised %d session(s), backed up %d file(s)\\\\n' \"$sanitized_count\" \"$backup_count\" >&2")
            .AppendLine("exit 0")
            .ToString();
    }

    /// <summary>
    /// Executes the sanitisation script inside the sandbox. Returns an
    /// <see cref="AgentResult"/> on prep failure, or null when the script
    /// ran successfully (or there was nothing to sanitise).
    /// </summary>
    public static async Task<AgentResult?> SanitizeTranscriptsAsync(
        ISandbox sandbox,
        CancellationToken ct = default)
    {
        var script = GenerateSanitizationScript();
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        }, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"transcript sanitisation failed (exit {result.ExitCode})",
                Stdout: result.Stdout,
                Stderr: result.Stderr);
        }

        return null;
    }

    /// <summary>
    /// Returns true when the agent result carries the thinking-block 400
    /// signature in stderr or stdout.
    /// </summary>
    public static bool IsThinkingBlockFailure(AgentResult result)
        => IsThinkingBlockFailure(result.Stderr, result.Stdout);

    /// <summary>
    /// Returns true when the captured output carries the thinking-block 400
    /// signature. Checks both text and stream-json error envelopes.
    /// </summary>
    public static bool IsThinkingBlockFailure(string? stderr, string? stdout)
    {
        if (!string.IsNullOrEmpty(stderr) && ContainsThinkingBlockSignature(stderr))
            return true;
        if (!string.IsNullOrEmpty(stdout) && ContainsThinkingBlockSignature(stdout))
            return true;

        // Also check stream-json error messages parsed by the detector.
        foreach (var msg in ClaudeQuotaFailureDetector.ExtractStreamJsonErrorMessages(stdout))
        {
            if (ContainsThinkingBlockSignature(msg))
                return true;
        }

        return false;
    }

    internal static bool ContainsThinkingBlockSignature(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(ThinkingBlockSignature, StringComparison.OrdinalIgnoreCase)
            || text.Contains("`thinking`", StringComparison.Ordinal)
            || text.Contains("`redacted_thinking`", StringComparison.Ordinal));

    /// <summary>
    /// Sanitises a single JSONL line (used by unit tests). Returns the
    /// sanitised line, or the original if the line was not an assistant
    /// message or parsing failed.
    /// </summary>
    internal static string SanitizeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)
                || !string.Equals(typeProp.GetString(), AssistantType, StringComparison.Ordinal))
            {
                return line;
            }

            if (!root.TryGetProperty("message", out var msgProp)
                || msgProp.ValueKind != JsonValueKind.Object)
            {
                return line;
            }

            if (!msgProp.TryGetProperty("content", out var contentProp)
                || contentProp.ValueKind != JsonValueKind.Array)
            {
                return line;
            }

            var hasThinkingBlocks = false;
            foreach (var block in contentProp.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    continue;
                if (!block.TryGetProperty("type", out var blockType))
                    continue;
                var bt = blockType.GetString();
                if (string.Equals(bt, ThinkingType, StringComparison.Ordinal)
                    || string.Equals(bt, RedactedThinkingType, StringComparison.Ordinal))
                {
                    hasThinkingBlocks = true;
                    break;
                }
            }

            if (!hasThinkingBlocks)
                return line;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteSanitizedObject(root, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return line;
        }
    }

    internal static IEnumerable<AssistantContentBlock> EnumerateContentBlocks(ReadOnlySpan<byte> jsonlLine)
    {
        return EnumerateContentBlocksImpl(jsonlLine.ToArray());
    }

    private static IEnumerable<AssistantContentBlock> EnumerateContentBlocksImpl(byte[] jsonlLine)
    {
        if (jsonlLine.Length == 0)
            yield break;

        JsonDocument? parsed = null;
        try
        {
            parsed = JsonDocument.Parse(jsonlLine);
        }
        catch (JsonException)
        {
            yield break;
        }

        using var doc = parsed;
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)
            || !string.Equals(typeProp.GetString(), AssistantType, StringComparison.Ordinal))
            yield break;

        if (!root.TryGetProperty("message", out var msgProp)
            || msgProp.ValueKind != JsonValueKind.Object)
            yield break;

        if (!msgProp.TryGetProperty("content", out var contentProp)
            || contentProp.ValueKind != JsonValueKind.Array)
            yield break;

        var text = Encoding.UTF8.GetString(jsonlLine);
        foreach (var block in contentProp.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (!block.TryGetProperty("type", out var blockTypeProp))
                continue;
            var blockType = blockTypeProp.GetString();
            if (blockType is null)
                continue;
            yield return new AssistantContentBlock(blockType, 0, 0);
        }
    }

    internal sealed record AssistantContentBlock(string Type, int StartOffset, int EndOffset);

    private static void WriteSanitizedObject(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "content", StringComparison.Ordinal)
                        && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        foreach (var block in prop.Value.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object)
                            {
                                block.WriteTo(writer);
                                continue;
                            }

                            if (block.TryGetProperty("type", out var bt))
                            {
                                var typeStr = bt.GetString();
                                if (string.Equals(typeStr, ThinkingType, StringComparison.Ordinal)
                                    || string.Equals(typeStr, RedactedThinkingType, StringComparison.Ordinal))
                                {
                                    continue;
                                }
                            }

                            block.WriteTo(writer);
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WritePropertyName(prop.Name);
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                            WriteSanitizedObject(prop.Value, writer);
                        else
                            prop.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// De-interleaves a JSONL session transcript. Groups assistant message
    /// content blocks by <c>message.id</c> (when present), deduplicates by
    /// content block index, and emits a single coalesced assistant line.
    /// Non-assistant lines pass through unchanged.
    ///
    /// <para>Handles the interleaving pattern described in the upstream bug:
    /// streaming chunks from different <c>msg_id</c>s written adjacently.</para>
    /// </summary>
    internal static string DeinterleaveTranscript(string jsonlContent)
    {
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return string.Empty;

        var lines = jsonlContent.Split('\n', StringSplitOptions.None);
        if (lines.Length == 0)
            return jsonlContent;

        // Partition: non-assistant lines, and assistant lines grouped by message.id
        var nonAssistant = new List<(int origIndex, string line)>();
        var assistantByMsgId = new Dictionary<string, List<(int origIndex, string line)>>(StringComparer.Ordinal);
        var orphans = new List<(int origIndex, string line)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string? msgId = null;
            bool isAssistant = false;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t)
                    && string.Equals(t.GetString(), AssistantType, StringComparison.Ordinal))
                {
                    isAssistant = true;
                    if (root.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.Object
                        && msg.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String)
                    {
                        msgId = idProp.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                nonAssistant.Add((i, line));
                continue;
            }

            if (!isAssistant)
            {
                nonAssistant.Add((i, line));
                continue;
            }

            if (msgId is not null)
            {
                if (!assistantByMsgId.TryGetValue(msgId, out var group))
                {
                    group = new List<(int, string)>();
                    assistantByMsgId[msgId] = group;
                }
                group.Add((i, line));
            }
            else
            {
                orphans.Add((i, line));
            }
        }

        // Coalesce each msg_id group: take the line with the most content blocks.
        var sb = new StringBuilder(jsonlContent.Length);
        var emitted = new HashSet<int>();

        foreach (var kvp in assistantByMsgId)
        {
            var group = kvp.Value;
            if (group.Count == 0)
                continue;

            // Pick the line with the most content block keys, then longest.
            string best = group[0].line;
            var bestCount = CountContentBlockKeys(group[0].line);
            var bestLen = group[0].line.Length;

            for (var g = 1; g < group.Count; g++)
            {
                var c = CountContentBlockKeys(group[g].line);
                if (c > bestCount || (c == bestCount && group[g].line.Length > bestLen))
                {
                    best = group[g].line;
                    bestCount = c;
                    bestLen = group[g].line.Length;
                }
            }

            sb.AppendLine(best);
            foreach (var (idx, _) in group)
                emitted.Add(idx);
        }

        // Emit orphans and non-assistant lines in original order.
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (emitted.Contains(i))
                continue;

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static int CountContentBlockKeys(string line)
    {
        var count = 0;
        var span = line.AsSpan();
        var idx = 0;
        while (idx < span.Length)
        {
            var found = span.Slice(idx).IndexOf("\"type\"", StringComparison.Ordinal);
            if (found < 0)
                break;
            count++;
            idx += found + "\"type\"".Length;
        }
        return count;
    }
}
