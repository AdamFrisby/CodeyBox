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
        _store = new SqliteWorkItemCostStore(_dbPath, MakeCostCalculator());
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
        Assert.True(row.HasExtractedTokenUsage);
    }

    [Fact]
    public async Task ReconcileFromAgentStreamSummaryAsync_TokenOnlySummaryUpdatesElapsedFallbackFlag()
    {
        var itemId = Guid.NewGuid().ToString("N");
        SeedWorkItem(itemId, state: WorkItemState.Done);
        await _store.RecordAsync(MakeCost(itemId, "audit") with
        {
            AgentKind = "gemini",
            ModelId = "gemini-2.5-pro",
            InputTokens = 0,
            CachedInputTokens = 0,
            OutputTokens = 0,
            EstimatedUsd = 0,
            RawMetadataJson = """{"source":"elapsed_fallback"}""",
            HasExtractedTokenUsage = false,
        });

        await _store.ReconcileFromAgentStreamSummaryAsync(new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(itemId)),
            "audit-llm-quality:llm-review-1-abcdef.jsonl",
            "audit-llm-quality:llm-review",
            1,
            AgentKind.Gemini,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                300,
                50,
                25,
                null,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var rows = await _store.GetByWorkItemAsync(itemId);

        var row = Assert.Single(rows);
        Assert.Equal("audit", row.Phase);
        Assert.Equal(300, row.InputTokens);
        Assert.Equal(25, row.CachedInputTokens);
        Assert.Equal(50, row.OutputTokens);
        Assert.Equal(0.0, row.EstimatedUsd);
        Assert.True(row.HasExtractedTokenUsage);

        var (avg, samples) = await _store.GetAvgTokensPerItemAsync("gemini", 10);
        Assert.Equal(1, samples);
        Assert.Equal(375, avg);
    }

    [Fact]
    public async Task ReconcileFromAgentStreamSummaryAsync_TokenOnlySummaryPricesExistingModel()
    {
        var itemId = Guid.NewGuid().ToString("N");
        SeedWorkItem(itemId, state: WorkItemState.Done);
        await _store.RecordAsync(MakeCost(itemId, "work") with
        {
            AgentKind = "codex",
            ModelId = "gpt-5.5",
            InputTokens = 0,
            CachedInputTokens = 0,
            OutputTokens = 0,
            EstimatedUsd = 0,
            RawMetadataJson = """{"source":"elapsed_fallback"}""",
            HasExtractedTokenUsage = false,
        });

        await _store.ReconcileFromAgentStreamSummaryAsync(new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(itemId)),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Codex,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                1000,
                200,
                100,
                0m,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var row = Assert.Single(await _store.GetByWorkItemAsync(itemId));

        Assert.Equal("gpt-5.5", row.ModelId);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(100, row.CachedInputTokens);
        Assert.Equal(200, row.OutputTokens);
        Assert.Equal(0.01105, row.EstimatedUsd, precision: 6);
        Assert.True(row.HasExtractedTokenUsage);
    }

    [Fact]
    public async Task ReconcileFromAgentStreamSummaryAsync_TokenOnlyNullModelUsesAgentDefaultRate()
    {
        var itemId = Guid.NewGuid().ToString("N");
        SeedWorkItem(itemId, state: WorkItemState.Done);
        await _store.RecordAsync(MakeCost(itemId, "work") with
        {
            AgentKind = "codex",
            ModelId = null,
            InputTokens = 0,
            CachedInputTokens = 0,
            OutputTokens = 0,
            EstimatedUsd = 0,
            RawMetadataJson = """{"source":"elapsed_fallback"}""",
            HasExtractedTokenUsage = false,
        });

        await _store.ReconcileFromAgentStreamSummaryAsync(new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(itemId)),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Codex,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                1000,
                200,
                100,
                null,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var row = Assert.Single(await _store.GetByWorkItemAsync(itemId));

        Assert.Equal("gpt-5.5", row.ModelId);
        Assert.Equal(0.01105, row.EstimatedUsd, precision: 6);
        Assert.True(row.HasExtractedTokenUsage);
    }

    [Fact]
    public async Task ReconcileFromAgentStreamSummaryAsync_InsertsPricedDefaultModelRowWhenNoCostExists()
    {
        var itemId = Guid.NewGuid().ToString("N");
        SeedWorkItem(itemId, state: WorkItemState.Done);

        await _store.ReconcileFromAgentStreamSummaryAsync(new AgentStreamSummaryRow(
            new WorkItemId(Guid.Parse(itemId)),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Codex,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                1000,
                200,
                100,
                0m,
                [],
                [],
                null),
            DateTimeOffset.UtcNow));

        var row = Assert.Single(await _store.GetByWorkItemAsync(itemId));

        Assert.Equal("stream-" + itemId + "-work-1-abcdef.jsonl", row.Id);
        Assert.Equal("gpt-5.5", row.ModelId);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(100, row.CachedInputTokens);
        Assert.Equal(200, row.OutputTokens);
        Assert.Equal(0.01105, row.EstimatedUsd, precision: 6);
        Assert.True(row.HasExtractedTokenUsage);
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
        Assert.Equal(12845, summaries[withCostsA].Iteration.TokensInput);
        Assert.Equal(12845, summaries[withCostsA].Total.TokensInput);
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

    [Fact]
    public async Task Constructor_MigratesLegacyInputTokens_SubtractsCachedAndIsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cost-legacy-input-{Guid.NewGuid():N}.db");
        try
        {
            // Seed a pre-fix schema (no usage_contract_version column) with rows
            // matching the old contract: input_tokens carries the TOTAL prompt
            // bucket and cached_input_tokens carries the cached subset already
            // included in input_tokens.
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
                    VALUES ('wi-typical',    'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('wi-skip-shape', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('wi-zero',       'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('wi-opencode-anth-warm', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('wi-opencode-anth-cold', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z');
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
                    -- Typical legacy row: input=10000 (TOTAL), cached=3000 (subset). After
                    -- migration input should become 7000 fresh-only; cached unchanged.
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('row-typical', 'wi-typical', 'work', 1, 'claude', 'claude-opus-4-7',
                         10000, 3000, 500, 0.05,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}');
                    -- Defensive guard: any row with cached > input violates the
                    -- pre-fix TOTAL-includes-cached contract, so it must already
                    -- be fresh-only. Migration must leave such rows untouched
                    -- (no subtraction, no clamp-to-zero).
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('row-skip-shape', 'wi-skip-shape', 'work', 1, 'claude', 'claude-opus-4-7',
                         100, 250, 0, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}');
                    -- Codex-shape legacy row: cached=0, input untouched by the subtraction.
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('row-zero', 'wi-zero', 'work', 1, 'codex', 'gpt-5',
                         4242, 0, 0, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}');
                    -- OpenCode Anthropic-shape "warm" legacy row: pre-fix extractor
                    -- read input_tokens (Anthropic spec: already fresh-only) into
                    -- input_tokens and cache_read_input_tokens into cached. Common
                    -- shape on warm Claude sessions through OpenCode: cached >> fresh.
                    -- Migration MUST NOT subtract — doing so destroys the fresh
                    -- portion and silently under-reports historical tokens.
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('row-opencode-anth-warm', 'wi-opencode-anth-warm', 'work', 1, 'opencode', 'claude-sonnet-4-5',
                         100, 9000, 200, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}');
                    -- OpenCode Anthropic-shape "cold" legacy row: fresh > cache_read
                    -- but still ambiguous shape. Migration also has to leave this
                    -- untouched, because we can't distinguish OpenAI-shape from
                    -- Anthropic-shape on an opencode legacy row.
                    INSERT INTO work_item_costs
                        (id, work_item_id, phase, iteration, agent_kind, model_id,
                         input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                         started_at, ended_at, raw_metadata_json)
                    VALUES
                        ('row-opencode-anth-cold', 'wi-opencode-anth-cold', 'work', 1, 'opencode', 'claude-sonnet-4-5',
                         500, 200, 100, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}');
                    """;
                await setup.ExecuteNonQueryAsync();
            }

            // First open: should add usage_contract_version, run the migration,
            // then flip all rows to version=1.
            using (var migrated = new SqliteWorkItemCostStore(dbPath))
            {
                var typical = Assert.Single(await migrated.GetByWorkItemAsync("wi-typical"));
                Assert.Equal(7000, typical.InputTokens);
                Assert.Equal(3000, typical.CachedInputTokens);

                // cached > input row is left untouched (input < cached is impossible
                // under the pre-fix TOTAL-includes-cached contract, so the row must
                // already be fresh-only — subtracting would corrupt it).
                var skipShape = Assert.Single(await migrated.GetByWorkItemAsync("wi-skip-shape"));
                Assert.Equal(100, skipShape.InputTokens);
                Assert.Equal(250, skipShape.CachedInputTokens);

                var zero = Assert.Single(await migrated.GetByWorkItemAsync("wi-zero"));
                Assert.Equal(4242, zero.InputTokens);
                Assert.Equal(0, zero.CachedInputTokens);

                // OpenCode rows: shape-ambiguous pre-fix (OpenAI vs Anthropic).
                // Both branches preserve input as-is, because corrupting genuine
                // Anthropic-shape rows (where input was fresh-only) is irreversible.
                var warm = Assert.Single(await migrated.GetByWorkItemAsync("wi-opencode-anth-warm"));
                Assert.Equal(100, warm.InputTokens);
                Assert.Equal(9000, warm.CachedInputTokens);

                var cold = Assert.Single(await migrated.GetByWorkItemAsync("wi-opencode-anth-cold"));
                Assert.Equal(500, cold.InputTokens);
                Assert.Equal(200, cold.CachedInputTokens);
            }

            await AssertAllRowsAtVersion(dbPath, expectedVersion: 1);

            // Re-open the store: migration must be a no-op (idempotent). If the
            // WHERE usage_contract_version=0 guard ever regresses, the typical
            // row's input would silently drop from 7000 -> 4000 (or to 0 after
            // enough restarts).
            using (var reopened = new SqliteWorkItemCostStore(dbPath))
            {
                var typical = Assert.Single(await reopened.GetByWorkItemAsync("wi-typical"));
                Assert.Equal(7000, typical.InputTokens);
                Assert.Equal(3000, typical.CachedInputTokens);

                var skipShape = Assert.Single(await reopened.GetByWorkItemAsync("wi-skip-shape"));
                Assert.Equal(100, skipShape.InputTokens);
                Assert.Equal(250, skipShape.CachedInputTokens);

                var zero = Assert.Single(await reopened.GetByWorkItemAsync("wi-zero"));
                Assert.Equal(4242, zero.InputTokens);
                Assert.Equal(0, zero.CachedInputTokens);

                var warm = Assert.Single(await reopened.GetByWorkItemAsync("wi-opencode-anth-warm"));
                Assert.Equal(100, warm.InputTokens);
                Assert.Equal(9000, warm.CachedInputTokens);

                var cold = Assert.Single(await reopened.GetByWorkItemAsync("wi-opencode-anth-cold"));
                Assert.Equal(500, cold.InputTokens);
                Assert.Equal(200, cold.CachedInputTokens);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Constructor_RepairsLegacyTokenRows_ModelAttributionAndZeroUsd()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cost-repair-{Guid.NewGuid():N}.db");
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
                    VALUES ('legacy-null-model', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('legacy-zero-usd', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('legacy-default-model', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z'),
                           ('legacy-zero-token', 'test-project', {{(int)WorkItemState.Done}}, '2026-06-01T00:00:00Z');

                    CREATE TABLE agent_usage_events (
                        id TEXT PRIMARY KEY,
                        time_utc TEXT NOT NULL,
                        agent_kind TEXT NOT NULL,
                        model_id TEXT,
                        phase TEXT,
                        work_item_id TEXT
                    );
                    INSERT INTO agent_usage_events (id, time_utc, agent_kind, model_id, phase, work_item_id)
                    VALUES ('usage-null-model', '2026-06-01T00:00:06Z', 'codex', 'gpt-5.5', 'work', 'legacy-null-model');

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
                        ('row-null-model', 'legacy-null-model', 'work', 1, 'codex', NULL,
                         1000, 0, 200, 58.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}'),
                        ('row-zero-usd', 'legacy-zero-usd', 'work', 1, 'codex', 'gpt-5.5',
                         1000, 0, 200, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}'),
                        ('row-default-model', 'legacy-default-model', 'work', 1, 'codex', NULL,
                         1000, 0, 200, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z', '{}'),
                        ('row-zero-token', 'legacy-zero-token', 'work', 1, 'codex', 'gpt-5.5',
                         0, 0, 0, 0.0,
                         '2026-06-01T00:00:00Z', '2026-06-01T00:00:05Z',
                         '{"source":"extractor_null_elapsed_fallback"}');
                    """;
                await setup.ExecuteNonQueryAsync();
            }

            using var migrated = new SqliteWorkItemCostStore(dbPath, MakeCostCalculator());

            var attributed = Assert.Single(await migrated.GetByWorkItemAsync("legacy-null-model"));
            Assert.Equal("gpt-5.5", attributed.ModelId);
            Assert.Equal(58.0, attributed.EstimatedUsd, precision: 6);

            var repriced = Assert.Single(await migrated.GetByWorkItemAsync("legacy-zero-usd"));
            Assert.Equal("gpt-5.5", repriced.ModelId);
            Assert.Equal(0.011, repriced.EstimatedUsd, precision: 6);

            var defaulted = Assert.Single(await migrated.GetByWorkItemAsync("legacy-default-model"));
            Assert.Equal("gpt-5.5", defaulted.ModelId);
            Assert.Equal(0.011, defaulted.EstimatedUsd, precision: 6);

            var failed = Assert.Single(await migrated.GetByWorkItemAsync("legacy-zero-token"));
            Assert.Equal("gpt-5.5", failed.ModelId);
            Assert.Equal(0.0, failed.EstimatedUsd);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Constructor_NewInsertsAreFlaggedAtCurrentContractVersion()
    {
        // Defends the migration's idempotency contract: every RecordAsync insert
        // must persist usage_contract_version=1 so the legacy backfill on the
        // next startup leaves it alone.
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId);
        await _store.RecordAsync(MakeCost(itemId));

        await AssertAllRowsAtVersion(_dbPath, expectedVersion: 1);
    }

    private static async Task AssertAllRowsAtVersion(string dbPath, int expectedVersion)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, usage_contract_version FROM work_item_costs";
        await using var reader = await cmd.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            var id = reader.GetString(0);
            var version = reader.GetInt32(1);
            Assert.True(version == expectedVersion,
                $"row {id} has usage_contract_version={version}, expected {expectedVersion}");
        }
        Assert.True(count > 0, "expected at least one row to verify");
    }

    private static AgentCostCalculator MakeCostCalculator()
    {
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = "gpt-5.5",
            });

        return new AgentCostCalculator(new AgentPricingOptions
        {
            Rates = new()
            {
                ["codex"] = new()
                {
                    ["gpt-5.5"] = new()
                    {
                        InputPerMillion = 5.0,
                        CachedInputPerMillion = 0.5,
                        OutputPerMillion = 30.0,
                    },
                },
            },
        }, defaultModels: defaults);
    }
}
