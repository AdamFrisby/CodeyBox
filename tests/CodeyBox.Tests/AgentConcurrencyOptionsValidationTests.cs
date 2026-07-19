using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the MaxConcurrent value semantics on
/// <see cref="AgentConcurrencyOptions"/> and that they propagate through the
/// orchestrator dispatch gate. Regression scope: an operator setting
/// <c>MaxConcurrent: 0</c> (intent: pause this agent) used to be silently
/// reinterpreted as "unlimited", letting the agent run unbounded.
/// </summary>
public sealed class AgentConcurrencyOptionsValidationTests : IDisposable
{
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Claude = AgentKind.Claude;

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-cap-validation-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public AgentConcurrencyOptionsValidationTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    [Fact]
    public void Validate_EmptyOptions_HasNoFailures()
    {
        var failures = AgentConcurrencyOptions.Validate(new AgentConcurrencyOptions());
        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_PositiveCaps_HaveNoFailures()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        };
        Assert.Empty(AgentConcurrencyOptions.Validate(opts));
    }

    [Fact]
    public void Validate_Zero_IsRejectedWithGuidance()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 0 } },
        };
        var failures = AgentConcurrencyOptions.Validate(opts);

        var failure = Assert.Single(failures);
        Assert.Contains("claude", failure, StringComparison.Ordinal);
        Assert.Contains("MaxConcurrent", failure, StringComparison.Ordinal);
        // The message must direct the operator to the safe alternatives, since
        // the original footgun was 'I set it to 0 to pause the agent'.
        Assert.Contains("omit the entry", failure, StringComparison.Ordinal);
        Assert.Contains("pause the queue", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Negative_IsRejected()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = -3 } },
        };
        var failures = AgentConcurrencyOptions.Validate(opts);
        var failure = Assert.Single(failures);
        Assert.Contains("-3", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MultipleBadEntries_AllReported()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 0 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = -1 },
                ["gemini"] = new AgentConcurrencyEntry { MaxConcurrent = 2 }, // valid
            },
        };
        var failures = AgentConcurrencyOptions.Validate(opts);
        // Both bad entries surfaced together so the operator fixes them in one pass.
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("codex", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("claude", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAndThrow_OnInvalid_ThrowsArgumentException()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 0 } },
        };
        var ex = Assert.Throws<ArgumentException>(
            () => AgentConcurrencyOptions.ValidateAndThrow(opts));
        Assert.Equal("opts", ex.ParamName);
        Assert.Contains("AgentConcurrency", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorConstructor_RejectsZeroCap()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 0 } },
        };
        Assert.Throws<ArgumentException>(() => new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts));
    }

    [Fact]
    public void OrchestratorConstructor_RejectsNegativeCap()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = -1 } },
        };
        Assert.Throws<ArgumentException>(() => new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts));
    }

    [Fact]
    public void OrchestratorConstructor_AcceptsValidCaps_AndExposesThemViaConcurrencyState()
    {
        var opts = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts);

        var state = orchestrator.GetConcurrencyState();
        Assert.Equal(1, state.PerAgentCaps["codex"]);
        Assert.Equal(2, state.PerAgentCaps["claude"]);
    }

    [Fact]
    public void OrchestratorConstructor_NoMembers_AllAgentsUncapped()
    {
        // The "uncapped" expression is an empty Members dictionary — confirms
        // the documented way to omit a cap continues to work post-validation.
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: new AgentConcurrencyOptions());

        var state = orchestrator.GetConcurrencyState();
        Assert.Empty(state.PerAgentCaps);

        // And reservations succeed unboundedly (subject to the global pool).
        for (var i = 0; i < 10; i++)
            Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.Equal(10, orchestrator.GetRunning(Claude));
    }

    [Fact]
    public void ApplyAgentConcurrencyReload_RejectsZeroCap_LeavesPriorViewIntact()
    {
        var initial = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 } },
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: initial);
        Assert.Equal(2, orchestrator.GetConcurrencyState().PerAgentCaps["claude"]);

        var bad = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 0 } },
        };
        Assert.Throws<ArgumentException>(
            () => orchestrator.ApplyAgentConcurrencyReload(bad));

        // Prior cap survives — the reload rejection must not partially apply.
        Assert.Equal(2, orchestrator.GetConcurrencyState().PerAgentCaps["claude"]);
    }

    [Fact]
    public void ApplyAgentConcurrencyReload_RemovingEntry_DropsCap()
    {
        // The supported way to remove a per-agent cap is to drop the entry.
        var initial = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 } },
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: initial);

        orchestrator.ApplyAgentConcurrencyReload(new AgentConcurrencyOptions());

        Assert.Empty(orchestrator.GetConcurrencyState().PerAgentCaps);
        // And the gate becomes permissive again.
        for (var i = 0; i < 5; i++)
            Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));
    }

    [Fact]
    public void Caps_Of_1_And_2_ActuallyBlockAtCeiling()
    {
        // The core spec case: cap=1 admits one reservation, cap=2 admits two.
        var opts = new AgentConcurrencyOptions
        {
            Members =
            {
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        };
        var orchestrator = new OrchestratorService(
            new InMemoryTaskQueue(), _store, new PinnedPipelineRunner(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 10 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: opts);

        Assert.True(orchestrator.TryReserveAgentSlotForTest(Codex));
        Assert.False(orchestrator.TryReserveAgentSlotForTest(Codex)); // cap=1 hit.

        Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.True(orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.False(orchestrator.TryReserveAgentSlotForTest(Claude)); // cap=2 hit.
    }
}
