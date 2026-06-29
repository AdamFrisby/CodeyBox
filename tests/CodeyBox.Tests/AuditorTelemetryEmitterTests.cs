using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AuditorTelemetryEmitterTests
{
    [Fact]
    public async Task EmitAuditorSubStepsAsync_ParsesKnownAuditorOutputs()
    {
        var timings = new RecordingTimingStore();
        var emitter = new AuditorTelemetryEmitter(timings, toolCallCounters: null, NullLogger.Instance);
        var itemId = new WorkItemId(Guid.NewGuid());
        var phaseStart = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);

        await emitter.EmitAuditorSubStepsAsync(
            "csharp:build-WaE",
            "Build succeeded.\nTime Elapsed 00:00:01.234",
            itemId,
            iteration: 3,
            phaseStart);
        await emitter.EmitAuditorSubStepsAsync(
            "dotnet:format",
            "Time Elapsed 00:00:00.045",
            itemId,
            iteration: 3,
            phaseStart);
        await emitter.EmitAuditorSubStepsAsync(
            "dotnet:test-pass",
            "A total of 2 test files matched\nPassed! - Duration: 4.5 s",
            itemId,
            iteration: 3,
            phaseStart);
        await emitter.EmitAuditorSubStepsAsync(
            "security:gitleaks",
            "scan completed in 1.25s",
            itemId,
            iteration: 3,
            phaseStart);
        await emitter.EmitAuditorSubStepsAsync(
            "security:semgrep",
            """{"time":{"duration":2.75}}""",
            itemId,
            iteration: 3,
            phaseStart);

        AssertSubStep(timings, "csharp.build", 1_234, "{}");
        AssertSubStep(timings, "dotnet.format", 45, "{}");
        AssertSubStep(timings, "dotnet.test_discovery", 0, """{"count":2}""");
        AssertSubStep(timings, "dotnet.test_run", 4_500, "{}");
        AssertSubStep(timings, "gitleaks.scan", 1_250, "{}");
        AssertSubStep(timings, "semgrep.scan", 2_750, "{}");

        Assert.All(timings.CompletedRows, row =>
        {
            Assert.Equal(itemId, row.WorkItemId);
            Assert.Equal("audit", row.Phase);
            Assert.Equal(3, row.Iteration);
            Assert.Equal(phaseStart, row.StartedAt);
            Assert.Equal(row.StartedAt.AddMilliseconds(row.DurationMs!.Value), row.EndedAt);
        });
    }

    [Fact]
    public async Task EmitToolCallCountsAsync_EmitsToolCallAndThinkingRows()
    {
        var timings = new RecordingTimingStore();
        var counter = new StaticToolCallCounter(new AgentToolCallCounts(
            new Dictionary<string, int>
            {
                ["Bash"] = 2,
                ["bad tool\nname"] = 1,
            },
            FinalText: "done"));
        var emitter = new AuditorTelemetryEmitter(
            timings,
            new Dictionary<AgentKind, IAgentToolCallCounter> { [AgentKind.Claude] = counter },
            NullLogger.Instance);
        var itemId = new WorkItemId(Guid.NewGuid());

        await emitter.EmitToolCallCountsAsync(
            AgentKind.Claude,
            "stream-json",
            itemId,
            "audit",
            agentExecDurationMs: 1_234,
            CancellationToken.None,
            iteration: 4);

        var bash = Assert.Single(timings.CompletedRows, row => row.Step == "agent.tool_call.Bash");
        AssertToolRow(bash, itemId, "audit", 4, expectedCount: 2);
        Assert.Equal(0, bash.DurationMs);
        Assert.Equal(bash.StartedAt, bash.EndedAt);

        var sanitized = Assert.Single(timings.CompletedRows, row => row.Step == "agent.tool_call.bad_tool_name");
        AssertToolRow(sanitized, itemId, "audit", 4, expectedCount: 1);
        Assert.Equal(0, sanitized.DurationMs);
        Assert.Equal(sanitized.StartedAt, sanitized.EndedAt);

        var thinking = Assert.Single(timings.CompletedRows, row => row.Step == "agent.thinking_aggregate");
        Assert.Equal(itemId, thinking.WorkItemId);
        Assert.Equal("audit", thinking.Phase);
        Assert.Equal(4, thinking.Iteration);
        Assert.Equal(1_234, thinking.DurationMs);
        Assert.Equal("{}", thinking.MetadataJson);
        Assert.Equal(thinking.StartedAt.AddMilliseconds(1_234), thinking.EndedAt);
    }

    private static void AssertSubStep(
        RecordingTimingStore timings,
        string step,
        long expectedDurationMs,
        string expectedMetadataJson)
    {
        var row = Assert.Single(timings.CompletedRows, r => r.Step == step);
        Assert.Equal(expectedDurationMs, row.DurationMs);
        Assert.Equal(expectedMetadataJson, row.MetadataJson);
    }

    private static void AssertToolRow(
        TimingRecord row,
        WorkItemId itemId,
        string phase,
        int iteration,
        int expectedCount)
    {
        Assert.Equal(itemId, row.WorkItemId);
        Assert.Equal(phase, row.Phase);
        Assert.Equal(iteration, row.Iteration);
        using var doc = JsonDocument.Parse(row.MetadataJson);
        Assert.Equal(expectedCount, doc.RootElement.GetProperty("count").GetInt32());
    }

    private sealed class StaticToolCallCounter(AgentToolCallCounts counts) : IAgentToolCallCounter
    {
        public AgentKind Kind => AgentKind.Claude;

        public AgentToolCallCounts? TryCount(string? bufferedStdout)
            => bufferedStdout == "stream-json" ? counts : null;
    }

    private sealed class RecordingTimingStore : ITimingStore
    {
        private readonly Dictionary<string, TimingRecord> _inFlight = [];
        private readonly List<TimingRecord> _completed = [];

        public IReadOnlyList<TimingRecord> CompletedRows => [.. _completed];

        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
        {
            _ = ct;
            _inFlight[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
        {
            _ = ct;
            if (_inFlight.Remove(id, out var record))
                _completed.Add(record with { EndedAt = endedAt, DurationMs = durationMs });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<TimingRecord>>(
                _completed.Where(row => row.WorkItemId == id).ToList());
        }

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
        {
            _ = id;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
            int workItemLimit,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = workItemLimit;
            await Task.CompletedTask;
            foreach (var row in _completed)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
        }
    }
}
