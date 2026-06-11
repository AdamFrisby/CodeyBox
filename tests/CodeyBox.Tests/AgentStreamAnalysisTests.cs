using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class ClaudeStreamParserTests
{
    [Fact]
    public async Task ParseAsync_ComputesToolDurationsAndUsage()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        await using var stream = StreamOf("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:12Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:15Z","result":"done","total_cost_usd":0.42,"usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":5}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.True(tool.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.TimeToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Equal(5, summary.CachedInputTokens);
        Assert.Equal(0.42m, summary.EstimatedUsd);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_UsesClaudeCacheReadTokensBeforeCacheCreationTokens()
    {
        var parser = new ClaudeStreamParser();
        await using var stream = StreamOf("""
            {"type":"result","timestamp":"2026-01-01T00:00:00Z","result":"done","usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":0,"cache_read_input_tokens":77}}
            """);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(77, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_LeavesDurationsUnknownWhenTimestampsAreMissing()
    {
        var parser = new ClaudeStreamParser();
        await using var stream = StreamOf("""
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","result":"done","usage":{"input_tokens":100,"output_tokens":20}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Null(tool.StartedAt);
        Assert.Null(tool.EndedAt);
        Assert.Null(tool.Duration);
        Assert.Equal(TimeSpan.Zero, summary.TotalDuration);
        Assert.Null(summary.TimeToFirstToken);
        Assert.Empty(summary.Stalls);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_LeavesDurationsUnknownWithoutEventTimestampsOrTimingSource()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.Zero });
        await using var stream = StreamOf("""
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","result":"done","usage":{"input_tokens":100,"output_tokens":20}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Null(tool.StartedAt);
        Assert.Null(tool.EndedAt);
        Assert.Null(tool.Duration);
        Assert.Equal(TimeSpan.Zero, summary.TotalDuration);
        Assert.Null(summary.TimeToFirstToken);
        Assert.Empty(summary.Stalls);
    }

    [Fact]
    public async Task ParseAsync_ProjectsTimestamplessEventsIntoInvocationContext()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromMinutes(10) });
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var stream = StreamOf("""
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}]}}
            {"type":"result","result":"done","total_cost_usd":0.42,"usage":{"input_tokens":100,"output_tokens":20}}
            """);

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddMinutes(2),
            LineCount: 4,
            SizeBytes: stream.Length));

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal(started.AddSeconds(40), tool.StartedAt);
        Assert.Equal(started.AddSeconds(80), tool.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(40), tool.Duration);
        Assert.Equal(TimeSpan.FromMinutes(2), summary.TotalDuration);
        Assert.Equal(TimeSpan.FromSeconds(40), summary.TimeToFirstToken);
        Assert.Empty(summary.Stalls);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Equal(0.42m, summary.EstimatedUsd);
    }

    [Fact]
    public async Task ParseAsync_DetectsProjectedToolExecutionStalls()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var stream = StreamOf("""
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}]}}
            {"type":"result","result":"done"}
            """);

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddMinutes(2),
            LineCount: 4,
            SizeBytes: stream.Length));

        Assert.Contains(summary.Stalls, stall =>
            stall.Classification == "tool_execution"
            && stall.PreviousEventType == "tool_use"
            && stall.NextEventType == "tool_result"
            && stall.GapDuration == TimeSpan.FromSeconds(40));
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class UnknownAgentStreamParserTests
{
    [Fact]
    public async Task ParseAsync_WithContext_UsesDurationAndCallerCounts()
    {
        var parser = new UnknownAgentStreamParser();
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var stream = StreamOf("""
            starting plaintext run
            ERROR: compile failed
            final line
            """);

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddSeconds(45),
            LineCount: 99,
            SizeBytes: 1234));

        Assert.Equal(TimeSpan.FromSeconds(45), summary.TotalDuration);
        Assert.NotNull(summary.FinalAssistantMessage);
        Assert.Contains("lines=99", summary.FinalAssistantMessage);
        Assert.Contains("bytes=1234", summary.FinalAssistantMessage);
        Assert.Contains("errors=1", summary.FinalAssistantMessage);
        Assert.Contains("final line", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_StreamThrowsIOExceptionMidRead_PreservesPartialTailAndCounts()
    {
        // The IOException recovery branch in UnknownAgentStreamParser is
        // load-bearing: an in-flight capture file can be truncated, rotated,
        // or briefly contended, and we still want a non-blank summary out the
        // other side. Pin: we get the partial tail that WAS read, the
        // caller-supplied accounting takes over from the read-counters we
        // never finished tallying, and the parse does NOT propagate the
        // IOException out (which would re-create the "summary row went
        // missing" black hole this rework closed).
        var parser = new UnknownAgentStreamParser();
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var prefix = "first plaintext line\nsecond plaintext line\n";
        await using var stream = new ThrowAfterStream(
            new MemoryStream(Encoding.UTF8.GetBytes(prefix)),
            new IOException("simulated rotation mid-read"));

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddSeconds(12),
            LineCount: 77,
            SizeBytes: 8_888));

        Assert.False(summary.IsUnsupported);
        Assert.Equal(TimeSpan.FromSeconds(12), summary.TotalDuration);
        Assert.NotNull(summary.FinalAssistantMessage);
        // Caller-supplied accounting wins over the swallowed read counters.
        Assert.Contains("lines=77", summary.FinalAssistantMessage);
        Assert.Contains("bytes=8888", summary.FinalAssistantMessage);
        // The partial tail collected before the throw is still there.
        Assert.Contains("first plaintext line", summary.FinalAssistantMessage);
        Assert.Contains("second plaintext line", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_StreamThrowsDecoderFallbackOnMalformedUtf8_PreservesPartialTail()
    {
        // The DecoderFallbackException branch exists because agy / opencode
        // can spit terminal escapes or partial bytes when the CLI is killed
        // mid-write. A throwing decoder must not blank the summary — pin the
        // partial tail and that the malformed bytes don't surface.
        var parser = new UnknownAgentStreamParser();
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var prefix = "first plaintext line\nsecond plaintext line\n";
        await using var stream = new ThrowAfterStream(
            new MemoryStream(Encoding.UTF8.GetBytes(prefix)),
            new DecoderFallbackException("simulated malformed UTF-8"));

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddSeconds(7),
            LineCount: 5,
            SizeBytes: 64));

        Assert.False(summary.IsUnsupported);
        Assert.Equal(TimeSpan.FromSeconds(7), summary.TotalDuration);
        Assert.NotNull(summary.FinalAssistantMessage);
        Assert.Contains("lines=5", summary.FinalAssistantMessage);
        Assert.Contains("first plaintext line", summary.FinalAssistantMessage);
        Assert.Contains("second plaintext line", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_StreamThrowsUnauthorizedAccess_DoesNotSwallow()
    {
        // The flip side of the recovery branches: only IOException /
        // DecoderFallbackException are swallowed. Any other read failure
        // must surface so the sweep retries instead of writing a "successful"
        // summary that hides a real bug (this was called out explicitly in
        // the parser's comment block).
        var parser = new UnknownAgentStreamParser();
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var stream = new ThrowAfterStream(
            new MemoryStream(Encoding.UTF8.GetBytes("partial\n")),
            new UnauthorizedAccessException("not for the sweep to swallow"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            parser.ParseAsync(stream, new AgentStreamParserContext(
                started, started.AddSeconds(1), LineCount: 1, SizeBytes: 8)));
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reads the wrapped stream until it's drained, then throws the
    /// configured exception on the next read — simulates a real read
    /// failure that fires mid-parse rather than on first byte.
    /// </summary>
    private sealed class ThrowAfterStream : Stream
    {
        private readonly Stream _inner;
        private readonly Exception _toThrow;
        private bool _drained;

        public ThrowAfterStream(Stream inner, Exception toThrow)
        {
            _inner = inner;
            _toThrow = toThrow;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_drained)
                throw _toThrow;
            var read = _inner.Read(buffer, offset, count);
            if (read == 0)
            {
                _drained = true;
                throw _toThrow;
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_drained)
                throw _toThrow;
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _drained = true;
                throw _toThrow;
            }
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public sealed class CodexStreamParserTests
{
    private static IReadOnlyList<IAgentStreamParser> TestParsers() =>
    [
        new ClaudeStreamParser(),
        new CodexStreamParser(),
        new GeminiStreamParser(),
        new UnknownAgentStreamParser(),
    ];

    [Fact]
    public async Task SniffKindAsync_RecognizesInstalledPayloadResponseItemShape()
    {
        await using var stream = StreamOf("""
            {"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"unified_exec","arguments":"{\"cmd\":\"dotnet test\"}"}}
            """);

        var kind = await AgentStreamParserSelection.SniffKindAsync(stream, TestParsers());

        Assert.Equal(AgentKind.Codex, kind);
    }

    [Fact]
    public async Task ParseAsync_ParsesInstalledPayloadResponseItemsAndEventMsgTokenUsage()
    {
        var parser = new CodexStreamParser();
        await using var stream = StreamOf("""
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z","thread_id":"thread_1"}
            {"type":"response_item","timestamp":"2026-01-01T00:00:02Z","payload":{"type":"function_call","call_id":"call_1","name":"unified_exec","arguments":"{\"cmd\":\"dotnet test\"}"}}
            {"type":"event_msg","timestamp":"2026-01-01T00:00:03Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":42,"cached_input_tokens":5,"output_tokens":7,"reasoning_output_tokens":1,"total_tokens":54}}}}
            {"type":"response_item","timestamp":"2026-01-01T00:00:12Z","payload":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"response_item","timestamp":"2026-01-01T00:00:13Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done."}]}}
            {"type":"event_msg","timestamp":"2026-01-01T00:00:15Z","payload":{"type":"turn_complete","usage":{"input_tokens":50,"output_tokens":9,"cached_input_tokens":6},"time_to_first_token_ms":1000}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("call_1", tool.ToolUseId);
        Assert.Equal("unified_exec", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.Equal(2, tool.OutputBytes);
        Assert.Equal(44, summary.InputTokens);
        Assert.Equal(9, summary.OutputTokens);
        Assert.Equal(6, summary.CachedInputTokens);
        Assert.Equal("Done.", summary.FinalAssistantMessage);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), summary.TimeToFirstToken);
    }

    [Fact]
    public async Task ParseAsync_ParsesNestedItemCompletedEvents()
    {
        var parser = new CodexStreamParser();
        await using var stream = StreamOf("""
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z","thread_id":"thread_1"}
            {"type":"turn.started","timestamp":"2026-01-01T00:00:01Z"}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:02Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"cmd\":\"dotnet test\"}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:12Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:13Z","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}
            {"type":"turn.completed","timestamp":"2026-01-01T00:00:15Z","usage":{"input_tokens":30,"output_tokens":4,"cached_input_tokens":2}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("call_1", tool.ToolUseId);
        Assert.Equal("shell", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.Equal(28, summary.InputTokens);
        Assert.Equal(4, summary.OutputTokens);
        Assert.Equal(2, summary.CachedInputTokens);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_RecordsUsageFromNestedItemEvents()
    {
        var parser = new CodexStreamParser();
        await using var stream = StreamOf("""
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z","thread_id":"thread_1"}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:01Z","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}],"usage":{"input_tokens":1000,"cached_input_tokens":400,"output_tokens":7}}}
            """);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(600, summary.InputTokens);
        Assert.Equal(400, summary.CachedInputTokens);
        Assert.Equal(7, summary.OutputTokens);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_UsesCodexTimingFieldsWhenEventsDoNotHaveTopLevelTimestamps()
    {
        var parser = new CodexStreamParser();
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-codex-stream-{Guid.NewGuid():N}");
        var itemId = WorkItemId.New();
        var store = new AgentStreamStore(new AgentStreamsOptions { Path = root }, NullLogger<AgentStreamStore>.Instance);

        try
        {
            await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            {
                capture!.WriteChunk(
                    "{\"type\":\"thread.started\",\"thread_id\":\"thread_1\"}\n" +
                    "{\"type\":\"turn.started\"}\n" +
                    "{\"type\":\"item.started\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\",\"command\":\"/bin/bash -lc pwd\",\"aggregated_output\":\"\",\"exit_code\":null,\"status\":\"in_progress\",\"started_at_unix_ms\":1767225601000}}\n");
                capture.WriteChunk(
                    "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\",\"command\":\"/bin/bash -lc pwd\",\"aggregated_output\":\"/work\\n\",\"exit_code\":0,\"status\":\"completed\",\"completed_at_unix_ms\":1767225604000,\"duration_ms\":3000}}\n" +
                    "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"type\":\"agent_message\",\"text\":\"Done.\"}}\n" +
                    "{\"type\":\"turn.completed\",\"duration_ms\":15000,\"time_to_first_token_ms\":1200,\"usage\":{\"input_tokens\":29990,\"cached_input_tokens\":18176,\"output_tokens\":44,\"reasoning_output_tokens\":0}}\n");
            }

            var file = Assert.Single(await store.ListAsync(itemId));
            await using var stream = await store.OpenReadAsync(itemId, file.FileName);
            var summary = await parser.ParseAsync(stream!);

            var tool = Assert.Single(summary.ToolCalls);
            Assert.Equal("item_0", tool.ToolUseId);
            Assert.Equal("Bash", tool.ToolName);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225601000), tool.StartedAt);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225604000), tool.EndedAt);
            Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
            Assert.True(tool.Succeeded);
            Assert.Equal(6, tool.OutputBytes);
            Assert.Equal(11814, summary.InputTokens);
            Assert.Equal(44, summary.OutputTokens);
            Assert.Equal(18176, summary.CachedInputTokens);
            Assert.Equal("Done.", summary.FinalAssistantMessage);
            Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
            Assert.Equal(TimeSpan.FromMilliseconds(1200), summary.TimeToFirstToken);
            Assert.Empty(summary.Stalls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ParseAsync_ProjectsCodexCommandTimingsFromInvocationContext()
    {
        var parser = new CodexStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromMinutes(10) });
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var stream = StreamOf("""
            {"type":"thread.started","thread_id":"thread_1"}
            {"type":"turn.started"}
            {"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"","exit_code":null,"status":"in_progress"}}
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"/work\n","exit_code":0,"status":"completed"}}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Done."}}
            {"type":"turn.completed","usage":{"input_tokens":29990,"cached_input_tokens":18176,"output_tokens":44}}
            """);

        var summary = await parser.ParseAsync(stream, new AgentStreamParserContext(
            started,
            started.AddSeconds(100),
            LineCount: 6,
            SizeBytes: stream.Length));

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("item_0", tool.ToolUseId);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal(started.AddSeconds(40), tool.StartedAt);
        Assert.Equal(started.AddSeconds(60), tool.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(20), tool.Duration);
        Assert.Equal(TimeSpan.FromSeconds(100), summary.TotalDuration);
        Assert.Equal(TimeSpan.FromSeconds(80), summary.TimeToFirstToken);
        Assert.Empty(summary.Stalls);
    }

    [Fact]
    public async Task ParseAsync_RedactsSensitiveKeysInsideJsonArgumentStrings()
    {
        var parser = new CodexStreamParser();
        await using var stream = StreamOf("""
            {"type":"item.completed","timestamp":"2026-01-01T00:00:00Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"password\":\"hunter2\",\"headers\":{\"Authorization\":\"Bearer plain-value\"},\"cmd\":\"echo ok\"}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:01Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.DoesNotContain("hunter2", tool.InputSummary);
        Assert.DoesNotContain("plain-value", tool.InputSummary);
        Assert.Contains("\"password\":\"***\"", tool.InputSummary);
        Assert.Contains("\"Authorization\":\"***\"", tool.InputSummary);
        Assert.Contains("\"cmd\":\"echo ok\"", tool.InputSummary);
    }

    [Fact]
    public async Task ParseAsync_ParsesToolResultsLargerThanOneMiB()
    {
        var parser = new CodexStreamParser();
        var output = new string('x', 1024 * 1024 + 128);
        await using var stream = StreamOf(
            """
            {"type":"item.completed","timestamp":"2026-01-01T00:00:00Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}
            """ + "\n" +
            """
            {"type":"item.completed","timestamp":"2026-01-01T00:00:03Z","item":{"type":"function_call_output","call_id":"call_1","output":
            """ + JsonSerializer.Serialize(output) + "}}\n");

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
        Assert.Equal(Encoding.UTF8.GetByteCount(output), tool.OutputBytes);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class StreamSummaryCapTests
{
    [Fact]
    public async Task ParseAsync_TruncatesEventsToolCallsAndStallsAtConfiguredCaps()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions
        {
            StallThreshold = TimeSpan.FromSeconds(1),
            MaxEvents = 5,
            MaxToolCalls = 2,
            MaxStalls = 1,
        });
        await using var stream = StreamOf("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}},{"type":"tool_use","id":"t2","name":"Read","input":{}},{"type":"tool_use","id":"t3","name":"Write","input":{}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:06Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:07Z","tool_use_id":"t2","content":"ok","is_error":false}
            {"type":"assistant","timestamp":"2026-01-01T00:00:08Z","message":{"role":"assistant","content":"still working"}}
            {"type":"result","timestamp":"2026-01-01T00:00:09Z","result":"ignored after event cap","usage":{"input_tokens":100,"output_tokens":20}}
            """);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(2, summary.ToolCalls.Count);
        Assert.DoesNotContain(summary.ToolCalls, t => t.ToolUseId == "t3");
        Assert.Single(summary.Stalls);
        Assert.Equal("still working", summary.FinalAssistantMessage);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(TimeSpan.FromSeconds(8), summary.TotalDuration);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class GeminiStreamParserTests
{
    [Fact]
    public async Task ParseAsync_ParsesFunctionCallPartsAndUsageMetadata()
    {
        var parser = new GeminiStreamParser();
        await using var stream = StreamOf("""
            {"type":"response","timestamp":"2026-01-01T00:00:00Z","candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"g1","name":"read_file","args":{"path":"a.txt"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:03Z","candidates":[{"content":{"role":"model","parts":[{"functionResponse":{"id":"g1","name":"read_file","response":{"content":"ok"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:05Z","candidates":[{"content":{"role":"model","parts":[{"text":"done"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":3,"cachedContentTokenCount":1}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
        // Google's promptTokenCount (10) includes cachedContentTokenCount (1);
        // the parser normalises InputTokens to the fresh-only billable bucket
        // so it has the same meaning as codex/opencode's InputTokens.
        Assert.Equal(9, summary.InputTokens);
        Assert.Equal(3, summary.OutputTokens);
        Assert.Equal(1, summary.CachedInputTokens);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class AntigravityStreamParserTests
{
    private static IReadOnlyList<IAgentStreamParser> TestParsers() =>
    [
        new AntigravityStreamParser(),
        new ClaudeStreamParser(),
        new UnknownAgentStreamParser(),
    ];

    [Fact]
    public async Task SniffKindAsync_DoesNotClaimClaudeShape()
    {
        // Antigravity emits literal Claude shape for claude-* gateway models —
        // there is no on-wire marker to disambiguate. The parser must NOT claim
        // here; ResolveKind reaches Antigravity via cost rows / item.Agent.
        await using var stream = StreamOf("""
            {"type":"assistant","message":{"model":"claude-opus-4-6-thinking","usage":{"input_tokens":100,"cache_read_input_tokens":20}}}
            """);
        var kind = await AgentStreamParserSelection.SniffKindAsync(stream, TestParsers());
        // With production registration order Claude wins the sniff race. The
        // anti-regression is that the sniff result is NOT Antigravity.
        Assert.NotEqual(AgentKind.Antigravity, kind);
    }

    [Fact]
    public async Task ParseAsync_AnthropicShape_ParsesCacheBuckets()
    {
        var parser = new AntigravityStreamParser();
        await using var stream = StreamOf("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:12Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:15Z","result":"done","total_cost_usd":0.42,"usage":{"input_tokens":12000,"cache_creation_input_tokens":300,"cache_read_input_tokens":4000,"output_tokens":650}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.True(tool.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.TimeToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
        Assert.Equal(12000 + 300, summary.InputTokens);
        Assert.Equal(650, summary.OutputTokens);
        Assert.Equal(4000, summary.CachedInputTokens);
        Assert.Equal(0.42m, summary.EstimatedUsd);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_GeminiShape_ParsesCachedInputTokens()
    {
        var parser = new AntigravityStreamParser();
        await using var stream = StreamOf("""
            {"type":"result","timestamp":"2026-01-01T00:00:00Z","result":"done","total_cost_usd":0.07,"model":"gemini-3.5-flash-high","usage":{"prompt_tokens":5000,"cached_input_tokens":1000,"completion_tokens":420}}
            """);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(5000 - 1000, summary.InputTokens);
        Assert.Equal(1000, summary.CachedInputTokens);
        Assert.Equal(420, summary.OutputTokens);
        Assert.Equal(0.07m, summary.EstimatedUsd);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_GeminiStreamJsonShape_ParsesCandidatesToolsAndUsageMetadata()
    {
        var parser = new AntigravityStreamParser();
        await using var stream = StreamOf("""
            {"type":"response","timestamp":"2026-01-01T00:00:00Z","candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"g1","name":"read_file","args":{"path":"a.txt"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:03Z","candidates":[{"content":{"role":"model","parts":[{"functionResponse":{"id":"g1","name":"read_file","response":{"content":"ok"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:05Z","candidates":[{"content":{"role":"model","parts":[{"text":"done"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":3,"cachedContentTokenCount":1}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
        Assert.Equal(9, summary.InputTokens);
        Assert.Equal(3, summary.OutputTokens);
        Assert.Equal(1, summary.CachedInputTokens);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class CursorStreamParserTests
{
    private static IReadOnlyList<IAgentStreamParser> ProductionOrderParsers() =>
    [
        new AntigravityStreamParser(),
        new ClaudeStreamParser(),
        new CursorStreamParser(),
        new UnknownAgentStreamParser(),
    ];

    [Fact]
    public async Task SniffKindAsync_DoesNotMisattributeCursorToClaude()
    {
        // Cursor emits literal Claude shape. There is no on-wire marker to
        // disambiguate, so CursorStreamParser deliberately doesn't claim by
        // shape. Sniff returns Claude here (whichever provider parser claims
        // first); ResolveKind then uses item.Agent / cost rows to land
        // cursor work items in kind=cursor.
        await using var stream = StreamOf("""
            {"type":"assistant","timestamp":"2026-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            """);
        var kind = await AgentStreamParserSelection.SniffKindAsync(stream, ProductionOrderParsers());
        // Anti-regression: Cursor parser must NOT win on Claude-shape sniffing
        // (was a bug pre-fix when its TryClaim was identical to Claude's, and
        // would have flipped depending on registration order).
        Assert.NotEqual(AgentKind.Cursor, kind);
    }

    [Fact]
    public async Task ParseAsync_ComputesToolDurationsAndUsage()
    {
        var parser = new CursorStreamParser();
        await using var stream = StreamOf("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:12Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:15Z","result":"done","total_cost_usd":0.42,"usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":5}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.True(tool.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.TimeToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Equal(5, summary.CachedInputTokens);
        Assert.Equal(0.42m, summary.EstimatedUsd);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class StallDetectionTests
{
    [Fact]
    public async Task ParseAsync_RecordsToolExecutionStalls()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"sleep 45"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:45Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:46Z","result":"done"}
            """));

        var summary = await parser.ParseAsync(stream);

        var stall = Assert.Single(summary.Stalls);
        Assert.Equal(TimeSpan.FromSeconds(45), stall.GapDuration);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:45Z"), stall.DetectedAt);
        Assert.Equal("tool_execution", stall.Classification);
        Assert.Equal("tool_use", stall.PreviousEventType);
        Assert.Equal("tool_result", stall.NextEventType);
    }

    [Fact]
    public async Task ParseAsync_RecordsLlmStallsAfterAssistantOrToolResult()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"thinking"}}
            {"type":"assistant","timestamp":"2026-01-01T00:00:45Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:46Z","tool_use_id":"t1","content":"ok"}
            {"type":"assistant","timestamp":"2026-01-01T00:01:31Z","message":{"role":"assistant","content":"done"}}
            {"type":"result","timestamp":"2026-01-01T00:01:32Z","result":"done"}
            """));

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(2, summary.Stalls.Count);
        Assert.All(summary.Stalls, stall => Assert.Equal("llm", stall.Classification));
        Assert.Contains(summary.Stalls, stall =>
            stall.PreviousEventType == "assistant"
            && stall.NextEventType == "tool_use"
            && stall.DetectedAt == DateTimeOffset.Parse("2026-01-01T00:00:45Z")
            && stall.GapDuration == TimeSpan.FromSeconds(45));
        Assert.Contains(summary.Stalls, stall =>
            stall.PreviousEventType == "tool_result"
            && stall.NextEventType == "assistant"
            && stall.DetectedAt == DateTimeOffset.Parse("2026-01-01T00:01:31Z")
            && stall.GapDuration == TimeSpan.FromSeconds(45));
    }
}

public sealed class ThinkingVsExecutingSplitTests
{
    [Fact]
    public async Task Aggregate_SubtractsToolTimeFromAgentDuration()
    {
        var parser = new ClaudeStreamParser();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:10Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:25Z","tool_use_id":"t1","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:40Z","result":"done"}
            """));

        var summary = await parser.ParseAsync(stream);
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()), "work-1-abcdef.jsonl", "work", 1, AgentKind.Claude, summary, DateTimeOffset.UtcNow)]);

        Assert.Equal(40_000, aggregate.TotalAgentDurationMs);
        Assert.Equal(15_000, aggregate.ExecutingMs);
        Assert.Equal(25_000, aggregate.ThinkingMs);
    }

    [Fact]
    public void Aggregate_UsesWallClockUnionForOverlappingToolCalls()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", start, start.AddSeconds(10), TimeSpan.FromSeconds(10), true, 10),
                    new ToolCallInvocation("t2", "Read", "{}", start.AddSeconds(5), start.AddSeconds(20), TimeSpan.FromSeconds(15), true, 10),
                ],
                [],
                null),
            DateTimeOffset.UtcNow)]);

        Assert.Equal(25_000, aggregate.ByTool.Sum(t => t.TotalDurationMs));
        Assert.Equal(20_000, aggregate.ExecutingMs);
        Assert.Equal(10_000, aggregate.ThinkingMs);
    }

    [Fact]
    public void Aggregate_AddsDurationOnlyToolCallsToTimestampedUnion()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", start, start.AddSeconds(10), TimeSpan.FromSeconds(10), true, 10),
                    new ToolCallInvocation("t2", "Read", "{}", null, null, TimeSpan.FromSeconds(7), true, 10),
                ],
                [],
                null),
            DateTimeOffset.UtcNow)]);

        Assert.Equal(17_000, aggregate.ExecutingMs);
        Assert.Equal(13_000, aggregate.ThinkingMs);
    }

    [Fact]
    public void Aggregate_UsesDurationForToolCallsWithInvalidIntervals()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [new ToolCallInvocation("t1", "Bash", "{}", start, start.AddSeconds(-5), TimeSpan.FromSeconds(8), true, 10)],
                [],
                null),
            DateTimeOffset.UtcNow)]);

        Assert.Equal(8_000, aggregate.ExecutingMs);
        Assert.Equal(22_000, aggregate.ThinkingMs);
    }

    [Fact]
    public void Aggregate_UsesStatisticalMedianForEvenToolCallCounts()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", start, start.AddSeconds(10), TimeSpan.FromSeconds(10), true, 10),
                    new ToolCallInvocation("t2", "Bash", "{}", start.AddSeconds(10), start.AddSeconds(30), TimeSpan.FromSeconds(20), true, 10),
                ],
                [],
                null),
            DateTimeOffset.UtcNow)]);

        var byTool = Assert.Single(aggregate.ByTool);
        Assert.Equal(15_000, byTool.MedianMs);
    }
}

public sealed class StreamAnalysisServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-{Guid.NewGuid():N}.db");
    private readonly string _streamRoot = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-{Guid.NewGuid():N}");
    private readonly SqliteWorkItemStore _workItems;
    private readonly AgentStreamStore _streams;
    private readonly SqliteAgentStreamSummaryStore _summaries;
    private readonly SqliteWorkItemCostStore _costs;

    public StreamAnalysisServiceTests()
    {
        _workItems = new SqliteWorkItemStore(_dbPath);
        _streams = new AgentStreamStore(new AgentStreamsOptions { Path = _streamRoot }, NullLogger<AgentStreamStore>.Instance);
        _summaries = new SqliteAgentStreamSummaryStore(_dbPath);
        _costs = new SqliteWorkItemCostStore(_dbPath);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_WritesSummaries()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl", """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:01Z","tool_use_id":"t1","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:02Z","result":"done","total_cost_usd":0.75,"usage":{"input_tokens":10,"output_tokens":2}}
            """);
        await _costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            Iteration = 1,
            AgentKind = "claude",
            InputTokens = 1,
            OutputTokens = 1,
            EstimatedUsd = 0.01,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var rows = await _summaries.GetByWorkItemAsync(item.Id);
        var row = Assert.Single(rows);
        Assert.Equal("work-1-abcdef.jsonl", row.FileName);
        Assert.Single(row.Summary.ToolCalls);
        Assert.Equal("done", row.Summary.FinalAssistantMessage);
        var costs = await _costs.GetByWorkItemAsync(item.Id.ToString());
        var cost = Assert.Single(costs);
        Assert.Equal(0.75, cost.EstimatedUsd);
        Assert.Equal(10, cost.InputTokens);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_UsesParserDetectedFromEachStreamFile()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "audit-llm-security:llm-review-1-abcdef.jsonl", """
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:01Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:02Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"turn.completed","timestamp":"2026-01-01T00:00:03Z","usage":{"input_tokens":5,"output_tokens":1}}
            """);

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new CodexStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Codex, row.AgentKind);
        Assert.Equal("shell", Assert.Single(row.Summary.ToolCalls).ToolName);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_ProjectsTiminglessCodexSummaryFromCostWindow()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl", """
            {"type":"thread.started","thread_id":"thread_1"}
            {"type":"turn.started"}
            {"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"","exit_code":null,"status":"in_progress"}}
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"/work\n","exit_code":0,"status":"completed"}}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Done."}}
            {"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}
            """);
        await _costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            Iteration = 1,
            AgentKind = "codex",
            InputTokens = 10,
            OutputTokens = 2,
            StartedAt = started,
            EndedAt = started.AddSeconds(60),
        });

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new CodexStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Codex, row.AgentKind);
        Assert.Equal(TimeSpan.FromSeconds(60), row.Summary.TotalDuration);
        Assert.NotNull(row.Summary.TimeToFirstToken);
        Assert.Equal(10, row.Summary.InputTokens);
        Assert.Equal(2, row.Summary.OutputTokens);

        var tool = Assert.Single(row.Summary.ToolCalls);
        Assert.Equal("item_0", tool.ToolUseId);
        Assert.Equal("Bash", tool.ToolName);
        Assert.NotNull(tool.StartedAt);
        Assert.NotNull(tool.EndedAt);
        Assert.NotNull(tool.Duration);
        Assert.True(tool.Duration > TimeSpan.Zero);
        Assert.True(tool.Succeeded);
        Assert.Equal(6, tool.OutputBytes);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_UsesMatchingCostAgentWhenSniffingIsInconclusive()
    {
        var item = CreateItem(WorkItemState.Done) with { Agent = AgentKind.Claude };
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "audit-llm-security:llm-review-1-abcdef.jsonl",
            string.Join("\n", Enumerable.Repeat("""{"type":"metadata","source":"capture"}""", 20)) + "\n" + """
            {"type":"item.completed","timestamp":"2026-01-01T00:00:01Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:02Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"turn.completed","timestamp":"2026-01-01T00:00:03Z","usage":{"input_tokens":5,"output_tokens":1}}
            """);
        await _costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "audit",
            Iteration = 1,
            AgentKind = "codex",
            InputTokens = 1,
            OutputTokens = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new CodexStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Codex, row.AgentKind);
        Assert.Equal("shell", Assert.Single(row.Summary.ToolCalls).ToolName);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_UsesWorkItemAgentWhenSniffingIsInconclusive()
    {
        var item = CreateItem(WorkItemState.Done) with { Agent = AgentKind.Claude };
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl",
            string.Join("\n", Enumerable.Repeat("""{"type":"metadata","source":"capture"}""", 20)) + "\n" + """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:01Z","tool_use_id":"t1","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:02Z","usage":{"input_tokens":10,"output_tokens":2}}
            """);

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Claude, row.AgentKind);
        Assert.Equal("Read", Assert.Single(row.Summary.ToolCalls).ToolName);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_PlaintextFallback_PreservesAgentKindAndCapturesTail()
    {
        // A known agent kind (Claude) whose stream contains no recognised
        // structured events should still produce a usable plaintext-fallback
        // summary: the row keeps kind=claude (so dashboards still attribute
        // the run) and carries a tail summary with line/byte counts. The
        // observability black hole this rework was written to close.
        var item = CreateItem(WorkItemState.Done) with { Agent = AgentKind.Claude };
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl",
            string.Join("\n", Enumerable.Repeat("""{"type":"metadata","source":"capture"}""", 20)));

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        // Plaintext fallback preserves the originally-resolved kind so
        // agent_kind=claude / antigravity / opencode rows still aggregate
        // correctly under their dashboard buckets.
        Assert.Equal(AgentKind.Claude, row.AgentKind);
        Assert.False(row.Summary.IsUnsupported);
        Assert.NotNull(row.Summary.FinalAssistantMessage);
        Assert.Contains("plaintext-fallback", row.Summary.FinalAssistantMessage);
        Assert.Contains("lines=", row.Summary.FinalAssistantMessage);
        Assert.Empty(row.Summary.ToolCalls);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_PlaintextFallback_RawNonJsonStreamForOpencode()
    {
        // The keystone scenario: an agent whose CLI emits NO structured
        // stream-json (opencode) writes plaintext stdout. Without the
        // fallback, opencode rows would disappear (kind=unknown +
        // IsUnsupported). Now they land with kind=opencode and a tail.
        var item = CreateItem(WorkItemState.Done) with { Agent = AgentKind.Opencode };
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl",
            "starting opencode run\n"
            + "applying patch to /work/foo.ts\n"
            + "ERROR: compile failed\n"
            + "  Traceback: src/foo.ts:12\n"
            + "done after 14.2s\n");

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new OpencodeStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Opencode, row.AgentKind);
        Assert.False(row.Summary.IsUnsupported);
        Assert.NotNull(row.Summary.FinalAssistantMessage);
        // line/byte/error accounting surfaces in the summary header.
        Assert.Contains("lines=5", row.Summary.FinalAssistantMessage);
        Assert.Contains("errors=2", row.Summary.FinalAssistantMessage); // ERROR + Traceback
        // Tail is preserved so an operator can see the final-output context.
        Assert.Contains("done after 14.2s", row.Summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_PropagatesStreamAnalysisFailures()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl", """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z"}
            """);

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ThrowingAgentStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));
        Assert.Empty(await _summaries.GetByWorkItemAsync(item.Id));
    }

    private void WriteStreamFile(WorkItemId id, string fileName, string content)
    {
        var dir = Path.Combine(_streamRoot, id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private static WorkItem CreateItem(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "stream",
        Prompt = "stream",
        Agent = AgentKind.Claude,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    public void Dispose()
    {
        _summaries.Dispose();
        _costs.Dispose();
        _workItems.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_streamRoot, recursive: true); } catch { }
    }
}

public sealed class AgentStreamParserProductionRegistrationTests
    : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public AgentStreamParserProductionRegistrationTests(AgentStreamAnalysisApiFactory factory)
        => _factory = factory;

    /// <summary>
    /// Resolve the IAgentStreamParser collection from the running production
    /// DI container. The whole point of the rework was the DI registration
    /// itself — if Program.cs forgot to register
    /// AntigravityStreamParser/CursorStreamParser/OpencodeStreamParser the
    /// real service container would not surface them, and the runtime
    /// fallback to UnknownAgentStreamParser would still apply. Building a
    /// hand-rolled list "that mirrors Program.cs" cannot catch that
    /// regression; resolving from the real container does.
    /// </summary>
    private IReadOnlyList<IAgentStreamParser> ResolvedFromProductionContainer()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider
            .GetServices<IAgentStreamParser>()
            .ToList();
    }

    [Fact]
    public void DiContainer_RegistersParserForEveryProductionAgentKind()
    {
        // Anti-regression: the rework's acceptance criterion was that every
        // dispatched agent kind has a parser registered in DI. A missing
        // registration is the failure mode that put antigravity/cursor/
        // opencode in the unknown bucket pre-fix.
        var resolved = ResolvedFromProductionContainer();
        var resolvedKinds = resolved.Select(p => p.Kind).ToHashSet();
        Assert.Contains(AgentKind.Antigravity, resolvedKinds);
        Assert.Contains(AgentKind.Claude, resolvedKinds);
        Assert.Contains(AgentKind.Codex, resolvedKinds);
        Assert.Contains(AgentKind.Cursor, resolvedKinds);
        Assert.Contains(AgentKind.Gemini, resolvedKinds);
        Assert.Contains(AgentKind.Opencode, resolvedKinds);
        // Unknown fallback is the residual catch-all for kinds we don't
        // register — must stay present so plaintext-fallback continues to
        // operate.
        Assert.Contains(resolved, p => p.Kind.Value == "unknown");
    }

    [Fact]
    public async Task SniffKindAsync_ClaudeShape_AttributesToClaudeNotAntigravityOrCursor()
    {
        // Real Claude stream-json includes message.model = "claude-..." and
        // usage.cache_read_input_tokens. Pre-fix this routed to Antigravity
        // (registered first). It must now route to Claude even when the
        // parser list comes from the real DI container.
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"assistant","message":{"id":"msg_01","model":"claude-opus-4-7","usage":{"input_tokens":100,"cache_read_input_tokens":20}}}
            """));
        var kind = await AgentStreamParserSelection.SniffKindAsync(
            stream, ResolvedFromProductionContainer());
        Assert.Equal(AgentKind.Claude, kind);
    }
}

public sealed class AgentStreamParserSelectionTests
{
    [Fact]
    public void ResolveKind_WhenSniffingFails_UsesMatchingCostAgentBeforeWorkItemAgent()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "stream",
            Prompt = "stream",
            Agent = AgentKind.Claude,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "work-1-abcdef.jsonl",
            "work",
            1,
            12,
            null,
            DateTimeOffset.UtcNow);
        var costs = new[]
        {
            new WorkItemCost
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id.ToString(),
                Phase = "work",
                Iteration = 1,
                AgentKind = "claude",
                InputTokens = 0,
                OutputTokens = 0,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
            },
        };

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: null,
            costs,
            new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Gemini });

        Assert.Equal(AgentKind.Claude, kind);
    }

    [Fact]
    public void ResolveKind_CursorWorkPhase_OverridesSharedShapeSniffWithItemAgent()
    {
        // Anti-regression for the core finding: a cursor work item whose
        // structured stream-json is byte-identical to Claude (cursor's CLI
        // emits the literal Claude shape) must NOT be attributed to claude
        // just because ClaudeStreamParser claimed the shape. WorkItem.Agent
        // is the authoritative work-phase dispatch record, so it overrides
        // the shape-based sniff. Pre-fix, ResolveKind returned the sniffed
        // kind first and persisted these runs under agent_kind=claude —
        // exactly the bug the rework was meant to close.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "cursor work",
            Prompt = "cursor work",
            Agent = AgentKind.Cursor,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "work-1-abcdef.jsonl",
            "work",
            1,
            12,
            null,
            DateTimeOffset.UtcNow);

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: AgentKind.Claude,
            costs: [],
            parsers: new IAgentStreamParser[]
            {
                new ClaudeStreamParser(),
                new CursorStreamParser(),
                new AntigravityStreamParser(),
            });

        Assert.Equal(AgentKind.Cursor, kind);
    }

    [Fact]
    public void ResolveKind_AntigravityCostRow_OverridesSharedShapeSniff()
    {
        // Same shape, different vehicle: antigravity gateway runs through a
        // claude-* model emit literal claude stream-json, but the work_item_costs
        // row records the dispatched agy run and its invocation window contains
        // this capture file. The cost-row branch lands the summary under
        // agent_kind=antigravity regardless of what the sniffer claimed by shape.
        var capturedAt = DateTimeOffset.UtcNow;
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "agy work",
            Prompt = "agy work",
            Agent = AgentKind.Antigravity,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "work-1-abcdef.jsonl",
            "work",
            1,
            12,
            null,
            capturedAt);
        var costs = new[]
        {
            new WorkItemCost
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id.ToString(),
                Phase = "work",
                Iteration = 1,
                AgentKind = "antigravity",
                InputTokens = 0,
                OutputTokens = 0,
                StartedAt = capturedAt.AddSeconds(-1),
                EndedAt = capturedAt.AddSeconds(1),
            },
        };

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: AgentKind.Claude,
            costs,
            parsers: new IAgentStreamParser[]
            {
                new ClaudeStreamParser(),
                new CursorStreamParser(),
                new AntigravityStreamParser(),
            });

        Assert.Equal(AgentKind.Antigravity, kind);
    }

    [Fact]
    public void ResolveKind_ConclusiveSniffBeatsUnrelatedPhaseCostRow()
    {
        var capturedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "fallback work",
            Prompt = "fallback work",
            Agent = AgentKind.Antigravity,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "work-1-abcdef.jsonl",
            "work",
            1,
            12,
            null,
            capturedAt);
        var costs = new[]
        {
            new WorkItemCost
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id.ToString(),
                Phase = "work",
                Iteration = 1,
                AgentKind = "antigravity",
                InputTokens = 0,
                OutputTokens = 0,
                StartedAt = capturedAt.AddMinutes(10),
                EndedAt = capturedAt.AddMinutes(11),
            },
        };

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: AgentKind.Codex,
            costs,
            knownKinds: new[] { AgentKind.Antigravity, AgentKind.Claude, AgentKind.Codex });

        Assert.Equal(AgentKind.Codex, kind);
    }

    [Fact]
    public void ResolveKind_AuditPhase_ConclusiveSniffBeatsGenericAuditCostRows()
    {
        var capturedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "audit streams",
            Prompt = "audit streams",
            Agent = AgentKind.Claude,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "audit-llm-security:llm-review-1-abcdef.jsonl",
            "audit-llm-security:llm-review",
            1,
            12,
            null,
            capturedAt);
        var costs = new[]
        {
            new WorkItemCost
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id.ToString(),
                Phase = "audit",
                Iteration = 1,
                AgentKind = "claude",
                InputTokens = 0,
                OutputTokens = 0,
                StartedAt = capturedAt.AddSeconds(-1),
                EndedAt = capturedAt.AddSeconds(1),
            },
            new WorkItemCost
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = item.Id.ToString(),
                Phase = "audit",
                Iteration = 1,
                AgentKind = "antigravity",
                InputTokens = 0,
                OutputTokens = 0,
                StartedAt = capturedAt.AddSeconds(2),
                EndedAt = capturedAt.AddSeconds(4),
            },
        };

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: AgentKind.Codex,
            costs,
            knownKinds: new[] { AgentKind.Antigravity, AgentKind.Claude, AgentKind.Codex });

        Assert.Equal(AgentKind.Codex, kind);
    }

    [Fact]
    public void ResolveKind_AuditPhase_PrefersSniffOverWorkItemAgent()
    {
        // Audit phases run a different auditor agent than the work phase,
        // so WorkItem.Agent is NOT authoritative for the audit-* phase. The
        // sniff catches the auditor's parser shape (codex/claude/gemini),
        // overriding the work agent — keeps the existing
        // "audit was dispatched to codex" attribution path working.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "claude work, codex audit",
            Prompt = "claude work, codex audit",
            Agent = AgentKind.Claude,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "audit-llm-security:llm-review-1-abcdef.jsonl",
            "audit-llm-security:llm-review",
            1,
            12,
            null,
            DateTimeOffset.UtcNow);

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: AgentKind.Codex,
            costs: [],
            knownKinds: new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Cursor });

        Assert.Equal(AgentKind.Codex, kind);
    }

    [Fact]
    public void ResolveKind_WhenSniffingFails_UsesSupportedWorkItemAgent()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "stream",
            Prompt = "stream",
            Agent = AgentKind.Gemini,
            State = WorkItemState.Done,
        };
        var file = new AgentStreamFile(
            "work-1-abcdef.jsonl",
            "work",
            1,
            12,
            null,
            DateTimeOffset.UtcNow);

        var kind = AgentStreamParserSelection.ResolveKind(
            item,
            file,
            sniffedKind: null,
            costs: [],
            knownKinds: new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Gemini });

        Assert.Equal(AgentKind.Gemini, kind);
    }
}

public sealed class ThrowingAgentStreamParser : IAgentStreamParser
{
    public AgentKind Kind => AgentKind.Claude;

    public Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default) =>
        throw new InvalidOperationException("stream parser failed");
}

public sealed class AggregateEndpointTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public AggregateEndpointTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task WorkItemAggregate_GroupsByTool()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);
        await _factory.Summaries.UpsertAsync(new AgentStreamSummaryRow(
            item.Id,
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0, 0, 0, 1.25m,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(4), TimeSpan.FromSeconds(4), true, 10),
                    new ToolCallInvocation("t2", "Read", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromSeconds(1), true, 10),
                ],
                [],
                "done"),
            DateTimeOffset.UtcNow));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(4_000, body.GetProperty("executingMs").GetInt64());
        Assert.Equal(6_000, body.GetProperty("thinkingMs").GetInt64());
        var bash = body.GetProperty("byTool").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "Bash");
        Assert.Equal(4_000, bash.GetProperty("totalDurationMs").GetInt64());
        var invocation = Assert.Single(body.GetProperty("invocations").EnumerateArray());
        Assert.Equal("done", invocation.GetProperty("finalAssistantMessage").GetString());
    }
}

public sealed class FleetAggregateEndpointTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public FleetAggregateEndpointTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task FleetAggregate_UsesRecentTerminalWorkItems()
    {
        var first = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done) with
        {
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var second = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.AuditFailed) with
        {
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        var active = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Working) with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Store.CreateAsync(first);
        await _factory.Store.CreateAsync(second);
        await _factory.Store.CreateAsync(active);

        var now = DateTimeOffset.UtcNow;
        await _factory.Summaries.UpsertAsync(Row(first.Id, "Bash", TimeSpan.FromSeconds(3), now));
        await _factory.Summaries.UpsertAsync(Row(second.Id, "Read", TimeSpan.FromSeconds(1), now));
        await _factory.Summaries.UpsertAsync(Row(active.Id, "Write", TimeSpan.FromSeconds(9), now));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/agent-streams/aggregate?n=50");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(4_000, body.GetProperty("executingMs").GetInt64());
        Assert.Empty(body.GetProperty("invocations").EnumerateArray());
        var slowest = Assert.Single(body.GetProperty("slowestToolCalls").EnumerateArray(), t => t.GetProperty("toolName").GetString() == "Bash");
        Assert.Equal(first.Id.ToString(), slowest.GetProperty("workItemId").GetString());
        Assert.Equal(3_000, slowest.GetProperty("durationMs").GetInt64());
        Assert.DoesNotContain(
            body.GetProperty("byTool").EnumerateArray(),
            t => t.GetProperty("tool").GetString() == "Write");
    }

    private static AgentStreamSummaryRow Row(
        WorkItemId id,
        string tool,
        TimeSpan duration,
        DateTimeOffset now)
    {
        var start = now - duration;
        return new AgentStreamSummaryRow(
            id,
            $"{tool.ToLowerInvariant()}-1-abcdef.jsonl",
            tool.ToLowerInvariant(),
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                duration,
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [new ToolCallInvocation($"{tool}-1", tool, "{}", start, now, duration, true, 1)],
                [],
                null),
            now);
    }
}

public sealed class MissingFileTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public MissingFileTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task WorkItemAggregate_NoSummaries_ReturnsZeros()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalAgentDurationMs").GetInt64());
        Assert.Equal(0, body.GetProperty("totalToolCalls").GetInt32());
    }

    [Fact]
    public async Task AnalyzeFile_MissingFile_Returns404()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/missing-1-abcdef.jsonl/analysis");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AnalyzeFile_ParsesStreamOnDemandEvenWhenCachedSummaryExists()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);
        const string fileName = "work-1-abcdef.jsonl";
        _factory.WriteStreamFile(item.Id, fileName, """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"fresh","name":"Read","input":{"path":"fresh.txt"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:03Z","tool_use_id":"fresh","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:04Z","result":"fresh done","usage":{"input_tokens":123,"output_tokens":45,"cached_input_tokens":6}}
            """);
        await _factory.Summaries.UpsertAsync(new AgentStreamSummaryRow(
            item.Id,
            fileName,
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(99),
                TimeSpan.Zero,
                1,
                2,
                3,
                null,
                [new ToolCallInvocation("stale", "Bash", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(99), TimeSpan.FromSeconds(99), true, 1)],
                [],
                "stale"),
            DateTimeOffset.UtcNow));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}/analysis");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(123, body.GetProperty("inputTokens").GetInt32());
        Assert.Equal(45, body.GetProperty("outputTokens").GetInt32());
        Assert.Equal(6, body.GetProperty("cachedInputTokens").GetInt32());
        Assert.Equal(4_000, body.GetProperty("totalDurationMs").GetInt64());
        Assert.Equal("fresh done", body.GetProperty("finalAssistantMessage").GetString());
        var tool = Assert.Single(body.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal("fresh", tool.GetProperty("toolUseId").GetString());
        Assert.Equal("Read", tool.GetProperty("toolName").GetString());
    }
}

public sealed class OnDemandAnalysisEndpointTests
{
    [Fact]
    public async Task AnalyzeFile_DoesNotRequestLineCounts()
    {
        using var factory = new AgentStreamAnalysisApiFactory();
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await factory.Store.CreateAsync(item);
        const string fileName = "work-1-abcdef.jsonl";
        factory.WriteStreamFile(item.Id, fileName, """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"done"}}
            {"type":"result","timestamp":"2026-01-01T00:00:01Z","result":"done"}
            """);

        var client = factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}/analysis");
        resp.EnsureSuccessStatusCode();

        Assert.Equal(0, factory.Streams.ListRequestCount);
        Assert.DoesNotContain(true, factory.Streams.IncludeLineCountRequests);
    }

    [Fact]
    public async Task AnalyzeFile_PlaintextCapture_ReturnsFallbackTailInsteadOfUnsupported()
    {
        // Anti-regression: the on-demand endpoint must run the plaintext
        // fallback when the dispatched-kind parser fails to recognise any
        // events — same shape StreamAnalysisService does for the persisted
        // summary path. Pre-fix, the endpoint returned kind=unknown with an
        // Unsupported summary for plaintext opencode/cursor/antigravity
        // captures, defeating the post-mortem inspection path even though
        // the background summariser was fixed.
        using var factory = new AgentStreamAnalysisApiFactory();
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done) with
        {
            Agent = AgentKind.Opencode,
        };
        await factory.Store.CreateAsync(item);
        const string fileName = "work-1-abcdef.jsonl";
        factory.WriteStreamFile(item.Id, fileName,
            "starting opencode run\n"
            + "applying patch to plaintext.txt\n"
            + "ERROR: compile failed\n"
            + "done after 9.1s\n");

        var client = factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}/analysis");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("opencode", body.GetProperty("agentKind").GetString());
        var final = body.GetProperty("finalAssistantMessage").GetString();
        Assert.NotNull(final);
        Assert.Contains("plaintext-fallback", final);
        Assert.Contains("lines=4", final);
        Assert.Contains("errors=1", final);
        Assert.Contains("done after 9.1s", final);
    }
}

public sealed class AgentStreamAnalysisApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-api-{Guid.NewGuid():N}.db");
    private readonly string _streamRoot = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-api-{Guid.NewGuid():N}");

    public SqliteWorkItemStore Store { get; }
    public SqliteAgentStreamSummaryStore Summaries { get; }
    public RecordingAgentStreamStore Streams { get; }

    public AgentStreamAnalysisApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        Summaries = new SqliteAgentStreamSummaryStore(_dbPath);
        Streams = new RecordingAgentStreamStore(new AgentStreamStore(
            new AgentStreamsOptions { Path = _streamRoot },
            NullLogger<AgentStreamStore>.Instance));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(Path.GetTempPath(), $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(Path.GetTempPath(), $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(Path.GetTempPath(), $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = _streamRoot,
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            services.RemoveAll<IAgentStreamSummaryStore>();
            services.AddSingleton<IAgentStreamSummaryStore>(Summaries);
            services.RemoveAll<IAgentStreamStore>();
            services.AddSingleton<IAgentStreamStore>(Streams);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    public static WorkItem CreateItem(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "stream",
        Prompt = "stream",
        Agent = AgentKind.Claude,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    public void WriteStreamFile(WorkItemId id, string fileName, string content)
    {
        var dir = Path.Combine(_streamRoot, id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Summaries.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { }
            try { Directory.Delete(_streamRoot, recursive: true); } catch { }
        }
        base.Dispose(disposing);
    }
}

public sealed class RecordingAgentStreamStore : IAgentStreamStore
{
    private readonly IAgentStreamStore _inner;
    private readonly List<bool> _includeLineCountRequests = [];
    private int _listRequestCount;

    public RecordingAgentStreamStore(IAgentStreamStore inner) => _inner = inner;

    public AgentStreamsOptions Options => _inner.Options;

    public IReadOnlyList<bool> IncludeLineCountRequests
    {
        get
        {
            lock (_includeLineCountRequests)
                return _includeLineCountRequests.ToArray();
        }
    }

    public int ListRequestCount => Volatile.Read(ref _listRequestCount);

    public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default) =>
        _inner.BeginCaptureAsync(workItemId, phase, iteration, ct);

    public Task<IReadOnlyList<AgentStreamFile>> ListAsync(
        WorkItemId workItemId,
        int limit = AgentStreamStore.DefaultListLimit,
        bool includeLineCount = false,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _listRequestCount);
        lock (_includeLineCountRequests)
            _includeLineCountRequests.Add(includeLineCount);
        return _inner.ListAsync(workItemId, limit, includeLineCount, ct);
    }

    public Task<AgentStreamFile?> GetAsync(
        WorkItemId workItemId,
        string fileName,
        bool includeLineCount = false,
        CancellationToken ct = default)
    {
        lock (_includeLineCountRequests)
            _includeLineCountRequests.Add(includeLineCount);
        return _inner.GetAsync(workItemId, fileName, includeLineCount, ct);
    }

    public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default) =>
        _inner.OpenReadAsync(workItemId, fileName, ct);

    public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default) =>
        _inner.SweepAsync(now, ct);
}
