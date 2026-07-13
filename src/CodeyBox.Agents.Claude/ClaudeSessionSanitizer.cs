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
/// The production path (<see cref="SanitizeTranscriptsAsync"/>) pulls session
/// files from the sandbox, de-interleaves interleaved <c>msg_id</c> chunks,
/// strips <c>thinking</c> / <c>redacted_thinking</c> content blocks from
/// assistant messages, and removes trailing API-error tails — then pushes the
/// sanitised transcript back into the sandbox. Corner-case helpers
/// (<see cref="SanitizeLine"/>, <see cref="DeinterleaveTranscript"/>,
/// <see cref="EnumerateContentBlocks"/>) are also exercised directly by unit
/// tests to cover each corruption pattern independently.
/// </para>
/// </summary>
public static class ClaudeSessionSanitizer
{
    internal const string ThinkingType = "thinking";
    internal const string RedactedThinkingType = "redacted_thinking";
    internal const string TextType = "text";
    internal const string ToolUseType = "tool_use";
    internal const string ToolResultType = "tool_result";
    internal const string AssistantType = "assistant";
    internal const string UserType = "user";

    private const int MaxFileBytes = 52428800; // 50 MiB

    /// <summary>
    /// Executes transcript sanitisation inside the sandbox using a
    /// pull/sanitise/push pattern: discovers session JSONL files via a small
    /// bash helper, backs each one up, reads the content, de-interleaves and
    /// strips thinking blocks in C#, then writes the sanitised transcript back.
    /// Returns null on success; returns an <see cref="AgentResult"/> when a
    /// write-back fails.
    /// </summary>
    public static async Task<AgentResult?> SanitizeTranscriptsAsync(
        ISandbox sandbox,
        CancellationToken ct = default)
    {
        // 1. Discover files and create backups in one bash call.
        var listScript = BuildFileListAndBackupScript();
        var listResult = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", listScript],
        }, ct).ConfigureAwait(false);

        if (!listResult.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"transcript backup script failed (exit {listResult.ExitCode})",
                Stdout: listResult.Stdout,
                Stderr: listResult.Stderr)
            {
                ExecutionUnavailable = listResult.ExecutionUnavailable,
            };
        }

        var files = listResult.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static f => f.Trim())
            .Where(static f => f.Length > 0)
            .ToList();

        if (files.Count == 0)
            return null;

        // 2. For each file: read content, sanitise in C#, write back.
        foreach (var file in files)
        {
            var readResult = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["bash", "-c", "cat -- \"$1\" 2>/dev/null || true", "_", file],
            }, ct).ConfigureAwait(false);

            var sanitized = SanitizeFullTranscript(readResult.Stdout);

            var writeResult = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["bash", "-c", "cat > \"$1\"", "_", file],
                Stdin = sanitized,
            }, ct).ConfigureAwait(false);

            if (!writeResult.Success)
            {
                return new AgentResult(
                    Success: false,
                    Summary: $"transcript sanitisation failed writing {Path.GetFileName(file)} (exit {writeResult.ExitCode})",
                    Stdout: writeResult.Stdout,
                    Stderr: writeResult.Stderr)
                {
                    ExecutionUnavailable = writeResult.ExecutionUnavailable,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a self-contained bash script that discovers session JSONL
    /// files under <c>~/.claude/projects/</c> and creates timestamped backups
    /// in <c>~/.codeybox/transcript-backups/</c>. Outputs discovered file paths
    /// to stdout, one per line.
    ///
    /// <para>
    /// Backups land under <c>~/.codeybox/transcript-backups/</c> rather than
    /// next to the session file to keep the <c>projects/</c> directory clean
    /// for the CLI scanner.
    /// </para>
    ///
    /// <para>
    /// <c>find -maxdepth 3</c> matches the <c>projects/&lt;slug&gt;/&lt;session&gt;.jsonl</c>
    /// layout. <c>cp -P</c> is used so symlinks inside the projects tree
    /// cannot redirect the backup read to an arbitrary path.
    /// </para>
    /// </summary>
    internal static string BuildFileListAndBackupScript()
    {
        // The while loop runs in a pipeline subshell, but we only use it
        // for stdout output (printf), never for variable mutation that needs
        // to survive the loop.
        return new StringBuilder()
            .AppendLine("set -euo pipefail")
            .AppendLine("session_root=\"$HOME/.claude/projects\"")
            .AppendLine("[ -d \"$session_root\" ] || exit 0")
            .AppendLine("backup_dir=\"$HOME/.codeybox/transcript-backups\"")
            .AppendLine("mkdir -p \"$backup_dir\"")
            .AppendLine("ts=\"$(date +%s)\"")
            .AppendLine("find \"$session_root\" -maxdepth 3 -name '*.jsonl' -type f 2>/dev/null | while IFS= read -r session; do")
            .AppendLine("  [ -f \"$session\" ] || continue")
            .AppendLine("  size=\"$(wc -c < \"$session\")\"")
            .AppendLine("  [ \"$size\" -gt 0 ] || continue")
            .Append(' ').Append(' ').AppendLine($"[ \"$size\" -le {MaxFileBytes} ] || continue")
            .AppendLine("  cp -P \"$session\" \"$backup_dir/$(basename \"$session\").$ts.backup\"")
            .AppendLine("  printf '%s\\n' \"$session\"")
            .AppendLine("done")
            .AppendLine("exit 0")
            .ToString();
    }

    /// <summary>
    /// Full sanitisation of an entire JSONL transcript: de-interleaves
    /// interleaved <c>msg_id</c> chunks, strips thinking/redacted_thinking
    /// blocks from assistant messages, and removes any trailing API-error
    /// tail lines.
    /// </summary>
    internal static string SanitizeFullTranscript(string jsonlContent)
    {
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return string.Empty;

        var deinterleaved = DeinterleaveTranscript(jsonlContent);
        var lines = deinterleaved.Split('\n', StringSplitOptions.None);
        var sb = new StringBuilder(deinterleaved.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(SanitizeLine(line.Trim()));
        }
        return StripTrailingApiErrorTail(sb.ToString());
    }

    // ── Thinking-block 400 detection ──────────────────────────────────────────

    /// <summary>
    /// Returns true when the agent result carries the thinking-block 400
    /// signature in stderr or stdout.
    /// </summary>
    public static bool IsThinkingBlockFailure(AgentResult result)
        => IsThinkingBlockFailure(result.Stderr, result.Stdout);

    /// <summary>
    /// Returns true when the captured output carries the thinking-block 400
    /// signature. Checks text and stream-json error envelopes.
    /// </summary>
    public static bool IsThinkingBlockFailure(string? stderr, string? stdout)
    {
        if (!string.IsNullOrEmpty(stderr)
            && ClaudeQuotaFailureDetector.ContainsThinkingBlockSignature(stderr))
            return true;
        if (!string.IsNullOrEmpty(stdout)
            && ClaudeQuotaFailureDetector.ContainsThinkingBlockSignature(stdout))
            return true;

        foreach (var msg in ClaudeQuotaFailureDetector.ExtractStreamJsonErrorMessages(stdout))
        {
            if (ClaudeQuotaFailureDetector.ContainsThinkingBlockSignature(msg))
                return true;
        }

        return false;
    }

    // ── Line-level sanitisation ───────────────────────────────────────────────

    /// <summary>
    /// Sanitises a single JSONL line. Returns the sanitised line, or the
    /// original if the line was not an assistant message or parsing failed.
    /// Used by unit tests and by the full-transcript path.
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

    // ── Content block enumeration ─────────────────────────────────────────────

    internal static IEnumerable<AssistantContentBlock> EnumerateContentBlocks(ReadOnlySpan<byte> jsonlLine)
    {
        return EnumerateContentBlocksImpl(jsonlLine.ToArray());
    }

    /// <summary>
    /// Record describing a single content block within an assistant message.
    /// </summary>
    internal sealed record AssistantContentBlock(string Type);

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

        foreach (var block in contentProp.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (!block.TryGetProperty("type", out var blockTypeProp))
                continue;
            var blockType = blockTypeProp.GetString();
            if (blockType is null)
                continue;
            yield return new AssistantContentBlock(blockType);
        }
    }

    // ── JSON rewriting ────────────────────────────────────────────────────────

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

    // ── De-interleaving ───────────────────────────────────────────────────────

    /// <summary>
    /// De-interleaves a JSONL session transcript. Groups assistant message
    /// content blocks by <c>message.id</c> (when present), picks the line with
    /// the most content-block keys (as a proxy for completeness), and emits a
    /// single coalesced assistant line per message in the original order.
    /// Non-assistant lines and assistant lines without a <c>message.id</c>
    /// pass through unchanged.
    ///
    /// <para>Handles the interleaving pattern described in the upstream bug:
    /// streaming chunks from different <c>msg_id</c>s written adjacently.</para>
    /// </summary>
    internal static string DeinterleaveTranscript(string jsonlContent)
    {
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return string.Empty;

        var lines = jsonlContent.Split('\n', StringSplitOptions.None);

        // Group assistant lines by message.id. Track the index of the first
        // occurrence for each msg_id so we can emit the coalesced line in the
        // correct position.
        var assistantByMsgId = new Dictionary<string, List<(int origIndex, string line)>>(StringComparer.Ordinal);
        var firstIndexByMsgId = new Dictionary<string, int>(StringComparer.Ordinal);

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
                // Non-JSON line — pass through unchanged below.
            }

            if (!isAssistant || msgId is null)
                continue;

            if (!assistantByMsgId.TryGetValue(msgId, out var group))
            {
                group = new List<(int, string)>();
                assistantByMsgId[msgId] = group;
                firstIndexByMsgId[msgId] = i;
            }
            group.Add((i, line));
        }

        // Coalesce each msg_id group: pick the line with the most content-block
        // keys, then longest as a tie-breaker.
        var coalesced = new Dictionary<int, string>(); // firstIndex → best line
        foreach (var (msgId, group) in assistantByMsgId)
        {
            if (group.Count == 0)
                continue;

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

            coalesced[firstIndexByMsgId[msgId]] = best;
        }

        // Build ordered output: emit lines in original order, substituting
        // coalesced assistant lines at their first-occurrence position.
        // Non-assistant lines, lines from coalesced msg_id groups at non-first
        // positions, and lines already processed are skipped.
        var sb = new StringBuilder(jsonlContent.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // If this position has a coalesced assistant line, emit it.
            if (coalesced.TryGetValue(i, out var best))
            {
                sb.AppendLine(best);
                continue;
            }

            // Skip assistant lines that belong to a msg_id group (emitted
            // at the group's first position above).
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
            catch (JsonException) { }

            if (isAssistant && msgId is not null && assistantByMsgId.ContainsKey(msgId))
                continue; // handled by the coalesced entry above

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ── Trailing API-error tail removal ───────────────────────────────────────

    /// <summary>
    /// Strips trailing lines from the JSONL that look like API-error envelopes
    /// rather than conversation turns. A trailing line is removed when it is
    /// not a recognised session line type (user / assistant / tool_use /
    /// tool_result / system) and appears after the last recognised turn.
    ///
    /// <para>Handles the trailing API-error tail pattern from the upstream
    /// bug: when the CLI crashes it may write a raw error JSON object at the
    /// end of the session file.</para>
    /// </summary>
    internal static string StripTrailingApiErrorTail(string jsonlContent)
    {
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return string.Empty;

        var lines = jsonlContent.Split('\n', StringSplitOptions.None);
        var kept = new List<string>(lines.Length);

        // Find the last recognised turn index.
        int lastRecognised = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsRecognisedSessionLine(line))
                lastRecognised = i;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (i > lastRecognised)
                break; // trailing non-recognised lines — discard

            kept.Add(line);
        }

        return kept.Count == 0 ? string.Empty : string.Join('\n', kept);
    }

    private static bool IsRecognisedSessionLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{')
            return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                return false;

            var typ = typeProp.GetString();
            return typ is AssistantType or UserType or ToolUseType or ToolResultType or "system";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
