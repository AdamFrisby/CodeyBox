using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class SqliteAgentUsageStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-usage-test-{Guid.NewGuid():N}.db");
    private readonly SqliteAgentUsageStore _store;

    public SqliteAgentUsageStoreTests()
    {
        _store = new SqliteAgentUsageStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    private static AgentUsageEvent Event(
        DateTimeOffset time, string agent = "opencode", string? model = "m1", long costMicroCents = 10_000) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            TimeUtc = time,
            AgentKind = agent,
            ModelId = model,
            InputTokens = 100,
            CachedInputTokens = 10,
            OutputTokens = 20,
            CostMicroCents = costMicroCents,
            WorkItemId = "wi-1",
        };

    [Fact]
    public async Task SumWindow_SumsOnlyEventsInRange()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now.AddHours(-1), costMicroCents: 100));
        await _store.RecordAsync(Event(now.AddHours(-2), costMicroCents: 200));
        await _store.RecordAsync(Event(now.AddHours(-10), costMicroCents: 999)); // outside 5h window

        var agg = await _store.SumWindowAsync("opencode", "m1", now.AddHours(-5), now.AddHours(1));

        Assert.Equal(300, agg.SumMicroCents);
        Assert.Equal(2, agg.Count);
        Assert.NotNull(agg.EarliestUtc);
    }

    [Fact]
    public async Task SumWindow_FiltersByAgentAndModel()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now, agent: "opencode", model: "m1", costMicroCents: 100));
        await _store.RecordAsync(Event(now, agent: "opencode", model: "m2", costMicroCents: 500));
        await _store.RecordAsync(Event(now, agent: "claude", model: "m1", costMicroCents: 700));

        var agg = await _store.SumWindowAsync("opencode", "m1", now.AddHours(-1), now.AddHours(1));

        Assert.Equal(100, agg.SumMicroCents);
        Assert.Equal(1, agg.Count);
    }

    [Fact]
    public async Task SumWindow_NullModel_MatchesOnlyNullModelRows()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now, model: null, costMicroCents: 42));
        await _store.RecordAsync(Event(now, model: "m1", costMicroCents: 999));

        var agg = await _store.SumWindowAsync("opencode", null, now.AddHours(-1), now.AddHours(1));

        Assert.Equal(42, agg.SumMicroCents);
        Assert.Equal(1, agg.Count);
    }

    [Fact]
    public async Task SumWindow_MatchesAgentAndModelCaseInsensitively()
    {
        // The dispatch gate queries with the canonical lowercase AgentKind.Value,
        // while the /quota visibility summary iterates config dictionary keys
        // verbatim (e.g. "OpenCode"/"M1"). The COLLATE NOCASE clauses ensure both
        // paths sum the same rows; without them a casing mismatch would sum to
        // zero and overstate remaining budget.
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now, agent: "opencode", model: "m1", costMicroCents: 100));
        await _store.RecordAsync(Event(now, agent: "opencode", model: "m1", costMicroCents: 200));

        var agg = await _store.SumWindowAsync("OpenCode", "M1", now.AddHours(-1), now.AddHours(1));

        Assert.Equal(300, agg.SumMicroCents);
        Assert.Equal(2, agg.Count);
    }

    [Fact]
    public async Task RecordAsync_PersistsPhaseAndTimingMetadata()
    {
        var id = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        var ended = started.AddSeconds(7);
        await _store.RecordAsync(new AgentUsageEvent
        {
            Id = id,
            TimeUtc = ended,
            AgentKind = "cursor",
            ModelId = "cursor-model",
            Phase = "work",
            StartedUtc = started,
            EndedUtc = ended,
            ElapsedMs = 7000,
            InputTokens = 0,
            CachedInputTokens = 0,
            OutputTokens = 0,
            CostMicroCents = 0,
            WorkItemId = "wi-timing",
        });

        await using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT phase, started_utc, ended_utc, elapsed_ms
            FROM agent_usage_events
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("work", reader.GetString(0));
        Assert.Equal(started.ToString("O"), reader.GetString(1));
        Assert.Equal(ended.ToString("O"), reader.GetString(2));
        Assert.Equal(7000, reader.GetInt64(3));
    }

    [Fact]
    public async Task Constructor_MigratesOldUsageTableShape_ThenRecordsTimingMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-usage-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE agent_usage_events (
                        id                  TEXT PRIMARY KEY,
                        time_utc            TEXT NOT NULL,
                        agent_kind          TEXT NOT NULL,
                        model_id            TEXT,
                        input_tokens        INTEGER NOT NULL,
                        cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                        output_tokens       INTEGER NOT NULL,
                        cost_microcents     INTEGER NOT NULL DEFAULT 0,
                        work_item_id        TEXT
                    );
                    """;
                await create.ExecuteNonQueryAsync();
            }

            using var migrated = new SqliteAgentUsageStore(dbPath);
            var started = DateTimeOffset.Parse("2026-06-01T01:00:00Z");
            var ended = started.AddMilliseconds(1234);
            var id = Guid.NewGuid().ToString("N");
            await migrated.RecordAsync(new AgentUsageEvent
            {
                Id = id,
                TimeUtc = ended,
                AgentKind = "copilot",
                ModelId = "copilot-model",
                Phase = "work",
                StartedUtc = started,
                EndedUtc = ended,
                ElapsedMs = 1234,
                InputTokens = 0,
                CachedInputTokens = 0,
                OutputTokens = 0,
                CostMicroCents = 0,
                WorkItemId = "wi-migrated",
            });

            await using var verify = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await verify.OpenAsync();
            await using var cmd = verify.CreateCommand();
            cmd.CommandText = """
                SELECT phase, started_utc, ended_utc, elapsed_ms
                FROM agent_usage_events
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("work", reader.GetString(0));
            Assert.Equal(started.ToString("O"), reader.GetString(1));
            Assert.Equal(ended.ToString("O"), reader.GetString(2));
            Assert.Equal(1234, reader.GetInt64(3));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SumWindow_NoRows_ReturnsZero()
    {
        var now = DateTimeOffset.UtcNow;
        var agg = await _store.SumWindowAsync("opencode", "m1", now.AddHours(-1), now);

        Assert.Equal(0, agg.SumMicroCents);
        Assert.Equal(0, agg.Count);
        Assert.Null(agg.EarliestUtc);
    }

    [Fact]
    public async Task RecordedRows_DriveBudgetCalculatorSnapshot_EndToEnd()
    {
        // End-to-end: events recorded through the real SQLite store must be summed
        // by AgentBudgetCalculator under real config into the right AvailablePct.
        // The split fake-based suites (FakeUsageStore + direct store calls) cannot
        // catch wiring mistakes here: time_utc bounds, the model-key bucket match
        // between RecordAsync and SumWindowAsync, and the legacy cost-unit-to-percent math.
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now.AddHours(-1), costMicroCents: AgentUsageEvent.UsdToMicroCents(1.00m))); // 100c, in window
        await _store.RecordAsync(Event(now.AddHours(-2), costMicroCents: AgentUsageEvent.UsdToMicroCents(0.80m))); // 80c, in window
        await _store.RecordAsync(Event(now.AddHours(-10), costMicroCents: AgentUsageEvent.UsdToMicroCents(5.00m))); // 500c, outside 5h window

        var opts = new AgentBudgetOptions();
        opts.Members["opencode"] = new AgentBudgetMemberOptions
        {
            Models =
            {
                ["m1"] = new AgentBudgetModelOptions
                {
                    Windows =
                    {
                        new AgentBudgetWindowOptions
                        {
                            Kind = BudgetWindowKind.Rolling, Hours = 5, LimitCents = 200,
                        },
                    },
                },
            },
        };
        var calc = new AgentBudgetCalculator(_store, opts, NullLogger<AgentBudgetCalculator>.Instance);

        var snapshot = await calc.GetBudgetSnapshotAsync(AgentKind.Opencode, "m1");

        // 180c spent of 200c limit -> 10% remaining; the out-of-window 500c row is
        // excluded by the time bound, not double-counted.
        Assert.NotNull(snapshot);
        Assert.Equal(10.0, snapshot!.AvailablePct, precision: 6);
    }

    [Fact]
    public async Task SumTokensWindow_SumsInputCachedOutput_OnlyInRange()
    {
        // Anchor in the past so the [from, to) bounds in this test stay deterministic
        // regardless of wall-clock skew between the test record and the read.
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        await _store.RecordAsync(EventWithTokens(anchor, input: 100, cached: 10, output: 20));
        await _store.RecordAsync(EventWithTokens(anchor.AddMinutes(30), input: 200, cached: 20, output: 40));
        await _store.RecordAsync(EventWithTokens(anchor.AddHours(10), input: 999_999, cached: 999_999, output: 999_999)); // outside window

        var tokens = await _store.SumTokensWindowAsync(
            "opencode", "m1", anchor.AddMinutes(-1), anchor.AddHours(1));

        Assert.Equal(300, tokens.InputTokens);
        Assert.Equal(30, tokens.CachedInputTokens);
        Assert.Equal(60, tokens.OutputTokens);
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens.EarliestUtc);
    }

    [Fact]
    public async Task SumTokensWindow_NullModel_AggregatesEveryModelForAgent()
    {
        // CapacityCalculator contract: a null modelId is a cross-model rollup
        // (every billable token attributed to the agent). This is the opposite
        // of SumWindowAsync's null-model semantics, and the production query
        // is the only path the capacity calculator hits — a regression that
        // gated on `model_id IS NULL` would silently zero out subscription
        // capacity for every operator using model-specific recording.
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        await _store.RecordAsync(EventWithTokens(anchor, model: "m1", input: 100, output: 20));
        await _store.RecordAsync(EventWithTokens(anchor, model: "m2", input: 300, output: 60));
        await _store.RecordAsync(EventWithTokens(anchor, model: null, input: 50, output: 10));

        var tokens = await _store.SumTokensWindowAsync(
            "opencode", null, anchor.AddMinutes(-1), anchor.AddMinutes(1));

        Assert.Equal(450, tokens.InputTokens);
        Assert.Equal(90, tokens.OutputTokens);
        Assert.Equal(3, tokens.Count);
    }

    [Fact]
    public async Task SumTokensWindow_NonNullModel_NarrowsToSingleBucket()
    {
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        await _store.RecordAsync(EventWithTokens(anchor, model: "m1", input: 100, output: 20));
        await _store.RecordAsync(EventWithTokens(anchor, model: "m2", input: 300, output: 60));

        var tokens = await _store.SumTokensWindowAsync(
            "opencode", "m2", anchor.AddMinutes(-1), anchor.AddMinutes(1));

        Assert.Equal(300, tokens.InputTokens);
        Assert.Equal(60, tokens.OutputTokens);
        Assert.Equal(1, tokens.Count);
    }

    [Fact]
    public async Task SumTokensWindow_RangeIsHalfOpen_ExcludesUpperBound()
    {
        // The capacity calculator stitches consecutive intervals as [s_i, s_{i+1});
        // a row at the boundary must land in exactly one interval — verify the
        // upper bound is exclusive so two adjacent intervals don't double-count.
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        await _store.RecordAsync(EventWithTokens(anchor.AddMinutes(30), input: 100, output: 10));

        var first = await _store.SumTokensWindowAsync("opencode", "m1", anchor, anchor.AddHours(1));
        var second = await _store.SumTokensWindowAsync("opencode", "m1", anchor.AddHours(1), anchor.AddHours(2));
        var boundaryExclusion = await _store.SumTokensWindowAsync(
            "opencode", "m1", anchor.AddMinutes(30), anchor.AddMinutes(31));

        Assert.Equal(1, first.Count);
        Assert.Equal(0, second.Count);
        Assert.Equal(1, boundaryExclusion.Count); // from inclusive
    }

    [Fact]
    public async Task SumTokensWindow_MatchesAgentAndModelCaseInsensitively()
    {
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        await _store.RecordAsync(EventWithTokens(anchor, agent: "opencode", model: "m1", input: 100, output: 20));
        await _store.RecordAsync(EventWithTokens(anchor, agent: "opencode", model: "m1", input: 200, output: 40));

        var tokens = await _store.SumTokensWindowAsync(
            "OpenCode", "M1", anchor.AddMinutes(-1), anchor.AddMinutes(1));

        Assert.Equal(300, tokens.InputTokens);
        Assert.Equal(60, tokens.OutputTokens);
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public async Task SumTokensWindow_NoRows_ReturnsEmpty()
    {
        var anchor = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        var tokens = await _store.SumTokensWindowAsync(
            "opencode", "m1", anchor, anchor.AddHours(1));

        Assert.Equal(0, tokens.InputTokens);
        Assert.Equal(0, tokens.CachedInputTokens);
        Assert.Equal(0, tokens.OutputTokens);
        Assert.Equal(0, tokens.SumMicroCents);
        Assert.Equal(0, tokens.Count);
        Assert.Null(tokens.EarliestUtc);
    }

    private static AgentUsageEvent EventWithTokens(
        DateTimeOffset time,
        string agent = "opencode",
        string? model = "m1",
        int input = 0,
        int cached = 0,
        int output = 0,
        long costMicroCents = 0) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            TimeUtc = time,
            AgentKind = agent,
            ModelId = model,
            InputTokens = input,
            CachedInputTokens = cached,
            OutputTokens = output,
            CostMicroCents = costMicroCents,
            WorkItemId = "wi-tokens",
        };

    [Fact]
    public async Task Prune_DeletesOnlyEventsOlderThanCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.RecordAsync(Event(now.AddDays(-100)));
        await _store.RecordAsync(Event(now.AddDays(-100)));
        await _store.RecordAsync(Event(now.AddDays(-1)));

        var deleted = await _store.PruneAsync(now.AddDays(-90));

        Assert.Equal(2, deleted);
        var agg = await _store.SumWindowAsync("opencode", "m1", now.AddYears(-1), now.AddDays(1));
        Assert.Equal(1, agg.Count);
    }
}
