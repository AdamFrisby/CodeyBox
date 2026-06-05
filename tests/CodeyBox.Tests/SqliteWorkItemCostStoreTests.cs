using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class SqliteWorkItemCostStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cost-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _rawConn;
    private readonly SqliteWorkItemCostStore _store;

    public SqliteWorkItemCostStoreTests()
    {
        _rawConn = new SqliteConnection($"Data Source={_dbPath}");
        _rawConn.Open();
        using var setupCmd = _rawConn.CreateCommand();
        setupCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL DEFAULT '',
                state INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT ''
            );
            """;
        setupCmd.ExecuteNonQuery();
        _store = new SqliteWorkItemCostStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _rawConn.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    private void SeedWorkItem(string id, string projectId = "test-project", WorkItemState state = WorkItemState.Queued)
    {
        using var cmd = _rawConn.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items (id, project_id, state, updated_at) VALUES ($id, $proj, $state, $now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$proj", projectId);
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static WorkItemCost MakeCost(string workItemId, string phase = "work") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkItemId = workItemId,
        Phase = phase,
        AgentKind = "claude",
        ModelId = "claude-opus-4-7",
        InputTokens = 12345,
        CachedInputTokens = 500,
        OutputTokens = 678,
        EstimatedUsd = 0.168525,
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
        EndedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task RoundTrip_RecordAndGetByWorkItem_AllFieldsCorrect()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId);
        var cost = MakeCost(itemId);

        await _store.RecordAsync(cost);
        var rows = await _store.GetByWorkItemAsync(itemId);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(cost.Id, row.Id);
        Assert.Equal(itemId, row.WorkItemId);
        Assert.Equal("work", row.Phase);
        Assert.Equal("claude", row.AgentKind);
        Assert.Equal("claude-opus-4-7", row.ModelId);
        Assert.Equal(12345, row.InputTokens);
        Assert.Equal(500, row.CachedInputTokens);
        Assert.Equal(678, row.OutputTokens);
        Assert.Equal(0.168525, row.EstimatedUsd, precision: 5);
        Assert.True(row.HasExtractedTokenUsage);
    }

    [Fact]
    public async Task GetByProjectAsync_ReturnsCostForProjectWorkItem()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, "proj-alpha");
        var cost = MakeCost(itemId);
        await _store.RecordAsync(cost);

        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var to = DateTimeOffset.UtcNow.AddHours(1);
        var rows = await _store.GetByProjectAsync("proj-alpha", from, to);

        Assert.Single(rows);
        Assert.Equal(itemId, rows[0].WorkItemId);
    }

    [Fact]
    public async Task DeleteByWorkItemAsync_RemovesRows()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId);
        await _store.RecordAsync(MakeCost(itemId));

        await _store.DeleteByWorkItemAsync(itemId);
        var rows = await _store.GetByWorkItemAsync(itemId);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task FkCascadeDelete_WorkItemDelete_RemovesCostRows()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId);
        await _store.RecordAsync(MakeCost(itemId));

        using var deleteCmd = _rawConn.CreateCommand();
        deleteCmd.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM work_items WHERE id = $id";
        deleteCmd.Parameters.AddWithValue("$id", itemId);
        deleteCmd.ExecuteNonQuery();

        var rows = await _store.GetByWorkItemAsync(itemId);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task MultipleRowsForSameWorkItem_AllReturned()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId);
        await _store.RecordAsync(MakeCost(itemId, "work"));
        await _store.RecordAsync(MakeCost(itemId, "merge"));

        var rows = await _store.GetByWorkItemAsync(itemId);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ReconcileFromAgentStreamSummaryAsync_UpdatesCanonicalAuditCostRow()
    {
        var itemId = Guid.NewGuid().ToString("N");
        SeedWorkItem(itemId);
        await _store.RecordAsync(MakeCost(itemId, "audit") with
        {
            AgentKind = "codex",
            InputTokens = 1,
            CachedInputTokens = 0,
            OutputTokens = 1,
            EstimatedUsd = 0.01,
        });

        await _store.ReconcileFromAgentStreamSummaryAsync(new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(itemId)),
            "audit-llm-security:llm-review-1-abcdef.jsonl",
            "audit-llm-security:llm-review",
            1,
            AgentKind.Codex,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                100,
                20,
                10,
                0.42m,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var rows = await _store.GetByWorkItemAsync(itemId);

        var row = Assert.Single(rows);
        Assert.Equal("audit", row.Phase);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(10, row.CachedInputTokens);
        Assert.Equal(20, row.OutputTokens);
        Assert.Equal(0.42, row.EstimatedUsd, precision: 5);
    }

    [Fact]
    public async Task SummariseManyAsync_BatchesAcrossWorkItems_OmitsEntriesWithoutCosts()
    {
        // Pins the IN-list override: K items must come back in O(1) read
        // connections, only entries that actually had cost rows appear in the
        // returned map, and the unknown id is silently absent.
        var withCostsA = Guid.NewGuid().ToString();
        var withCostsB = Guid.NewGuid().ToString();
        var withoutCosts = Guid.NewGuid().ToString();
        var unknown = Guid.NewGuid().ToString();
        SeedWorkItem(withCostsA);
        SeedWorkItem(withCostsB);
        SeedWorkItem(withoutCosts);

        await _store.RecordAsync(MakeCost(withCostsA, "work"));
        await _store.RecordAsync(MakeCost(withCostsB, "work"));

        var summaries = await _store.SummariseManyAsync(
            new[] { withCostsA, withCostsB, withoutCosts, unknown });

        Assert.Equal(2, summaries.Count);
        Assert.True(summaries.ContainsKey(withCostsA));
        Assert.True(summaries.ContainsKey(withCostsB));
        Assert.False(summaries.ContainsKey(withoutCosts));
        Assert.False(summaries.ContainsKey(unknown));
        // Single-row work cost: iter delta == total.
        Assert.Equal(12345, summaries[withCostsA].Iteration.TokensInput);
        Assert.Equal(12345, summaries[withCostsA].Total.TokensInput);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_LimitZero_ShortCircuitsToEmpty()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, state: WorkItemState.Done);
        await _store.RecordAsync(MakeCost(itemId));

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("claude", 0);

        Assert.Equal(0, avg);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_EmptyTable_ReturnsZeros()
    {
        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("claude", 10);
        Assert.Equal(0, avg);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_AveragesPerItemSumAcrossMostRecentItems()
    {
        // Two Done items, one with 2 cost rows (so the per-item SUM is exercised).
        // item1: input=100 + output=10 + cached=5 = 115; second row: 200+20+5 = 225 → total 340
        // item2: input=400 + output=30 + cached=5 = 435 → total 435
        // expected avg = (340 + 435) / 2 = 387.5 → rounded 388
        var itemA = "item-a";
        var itemB = "item-b";
        SeedWorkItem(itemA, state: WorkItemState.Done);
        SeedWorkItem(itemB, state: WorkItemState.Done);

        await _store.RecordAsync(MakeCost(itemA) with
        {
            AgentKind = "codex",
            InputTokens = 100,
            OutputTokens = 10,
            CachedInputTokens = 5,
        });
        await _store.RecordAsync(MakeCost(itemA) with
        {
            AgentKind = "codex",
            InputTokens = 200,
            OutputTokens = 20,
            CachedInputTokens = 5,
        });
        await _store.RecordAsync(MakeCost(itemB) with
        {
            AgentKind = "codex",
            InputTokens = 400,
            OutputTokens = 30,
            CachedInputTokens = 5,
        });

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("codex", 10);

        Assert.Equal(2, samples);
        Assert.Equal(388, avg);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_ExcludesElapsedFallbackRows()
    {
        var itemId = "fallback-only";
        SeedWorkItem(itemId, state: WorkItemState.Done);

        await _store.RecordAsync(MakeCost(itemId) with
        {
            AgentKind = "cursor",
            ModelId = "cursor-model",
            InputTokens = 0,
            CachedInputTokens = 0,
            OutputTokens = 0,
            EstimatedUsd = 0,
            HasExtractedTokenUsage = false,
        });

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("cursor", 10);

        Assert.Equal(0, avg);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task Constructor_MigratesLegacyElapsedFallbackRows_ToStructuredFlag()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cost-legacy-{Guid.NewGuid():N}.db");
        try
        {
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var setup = conn.CreateCommand();
                setup.CommandText = $$"""
                    CREATE TABLE work_items (
                        id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL DEFAULT '',
                        state INTEGER NOT NULL DEFAULT 0,
                        updated_at TEXT NOT NULL DEFAULT ''
                    );
                    INSERT INTO work_items (id, project_id, state, updated_at)
                    VALUES ('legacy-fallback', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z');
                    CREATE TABLE work_item_costs (
                        id                  TEXT PRIMARY KEY,
                        work_item_id        TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                        phase               TEXT NOT NULL,
                        iteration           INTEGER,
                        agent_kind          TEXT NOT NULL,
                        model_id            TEXT,
                        input_tokens        INTEGER NOT NULL,
                        cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                        output_tokens       INTEGER NOT NULL,
                        estimated_usd       REAL NOT NULL DEFAULT 0,
                        started_at          TEXT NOT NULL,
                        ended_at            TEXT NOT NULL,
                        raw_metadata_json   TEXT NOT NULL DEFAULT '{}'
                    );
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('legacy-row', 'legacy-fallback', 'work', NULL, 'cursor', 'cursor-model',
                         0, 0, 0, 0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z',
                         '{"source":"extractor_null_elapsed_fallback"}');
                    """;
                await setup.ExecuteNonQueryAsync();
            }

            using var migrated = new SqliteWorkItemCostStore(dbPath);

            var rows = await migrated.GetByWorkItemAsync("legacy-fallback");
            var row = Assert.Single(rows);
            Assert.False(row.HasExtractedTokenUsage);

            var (avg, samples) = await migrated.GetAvgTokensPerItemAsync("cursor", 10);
            Assert.Equal(0, avg);
            Assert.Equal(0, samples);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_FiltersByAgentKind()
    {
        var itemA = "item-codex";
        var itemB = "item-claude";
        SeedWorkItem(itemA, state: WorkItemState.Done);
        SeedWorkItem(itemB, state: WorkItemState.Done);

        await _store.RecordAsync(MakeCost(itemA) with
        { AgentKind = "codex", InputTokens = 1000, OutputTokens = 0, CachedInputTokens = 0 });
        await _store.RecordAsync(MakeCost(itemB) with
        { AgentKind = "claude", InputTokens = 2000, OutputTokens = 0, CachedInputTokens = 0 });

        var (codexAvg, codexN) = await _store.GetAvgTokensPerItemAsync("codex", 10);
        var (claudeAvg, claudeN) = await _store.GetAvgTokensPerItemAsync("claude", 10);

        Assert.Equal(1, codexN);
        Assert.Equal(1000, codexAvg);
        Assert.Equal(1, claudeN);
        Assert.Equal(2000, claudeAvg);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_LimitsToMostRecentByLatestStartedAt()
    {
        // 3 Done items with distinct started_at; limit=2 should keep the two most recent.
        var older = "old";
        var middle = "mid";
        var newest = "new";
        SeedWorkItem(older, state: WorkItemState.Done);
        SeedWorkItem(middle, state: WorkItemState.Done);
        SeedWorkItem(newest, state: WorkItemState.Done);
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);

        await _store.RecordAsync(MakeCost(older) with
        { AgentKind = "codex", InputTokens = 100, OutputTokens = 0, CachedInputTokens = 0, StartedAt = baseTime, EndedAt = baseTime.AddSeconds(1) });
        await _store.RecordAsync(MakeCost(middle) with
        { AgentKind = "codex", InputTokens = 200, OutputTokens = 0, CachedInputTokens = 0, StartedAt = baseTime.AddHours(1), EndedAt = baseTime.AddHours(1).AddSeconds(1) });
        await _store.RecordAsync(MakeCost(newest) with
        { AgentKind = "codex", InputTokens = 300, OutputTokens = 0, CachedInputTokens = 0, StartedAt = baseTime.AddHours(2), EndedAt = baseTime.AddHours(2).AddSeconds(1) });

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("codex", 2);

        Assert.Equal(2, samples);
        // Average of newest two (200, 300) = 250.
        Assert.Equal(250, avg);
    }

    [Fact]
    public async Task GetAvgTokensPerItemAsync_ExcludesNonDoneItems()
    {
        // Spec requires Done-only samples so partial in-flight cost rows don't
        // bias the rolling average (the codex-heavy scenario in the goal).
        var done = "i-done";
        var queued = "i-queued";
        var failed = "i-failed";
        SeedWorkItem(done, state: WorkItemState.Done);
        SeedWorkItem(queued, state: WorkItemState.Queued);
        SeedWorkItem(failed, state: WorkItemState.Failed);

        await _store.RecordAsync(MakeCost(done) with
        { AgentKind = "codex", InputTokens = 100, OutputTokens = 0, CachedInputTokens = 0 });
        await _store.RecordAsync(MakeCost(queued) with
        { AgentKind = "codex", InputTokens = 1, OutputTokens = 0, CachedInputTokens = 0 });
        await _store.RecordAsync(MakeCost(failed) with
        { AgentKind = "codex", InputTokens = 2, OutputTokens = 0, CachedInputTokens = 0 });

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("codex", 10);

        Assert.Equal(1, samples);
        Assert.Equal(100, avg);
    }

    [Fact]
    public async Task GetByProjectAsync_DateRangeFilter_ExcludesOutsideRange()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, "proj-beta");

        // Record a cost with StartedAt well in the past
        var pastCost = MakeCost(itemId) with
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
            EndedAt = DateTimeOffset.UtcNow.AddDays(-10).AddSeconds(5),
        };
        await _store.RecordAsync(pastCost);

        // Query a range that excludes the past cost
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var rows = await _store.GetByProjectAsync("proj-beta", from, to);

        Assert.Empty(rows);
    }
}
