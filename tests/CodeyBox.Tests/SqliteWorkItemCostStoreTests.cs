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

    private void SeedWorkItem(string id, string projectId = "test-project")
    {
        using var cmd = _rawConn.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items (id, project_id, state, updated_at) VALUES ($id, $proj, 0, $now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$proj", projectId);
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
        RawMetadataJson = """{"usageSource":"provider_metadata"}""",
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
    public async Task CostHistoryQuotaHeadroomEstimator_AveragesRecentProjectIterationTokens()
    {
        var itemA = Guid.NewGuid().ToString();
        var itemB = Guid.NewGuid().ToString();
        var itemC = Guid.NewGuid().ToString();
        SeedWorkItem(itemA, "proj-headroom");
        SeedWorkItem(itemB, "proj-headroom");
        SeedWorkItem(itemC, "proj-headroom");

        await _store.RecordAsync(MakeCost(itemA) with
        {
            InputTokens = 80_000,
            OutputTokens = 20_000,
            EstimatedUsd = 1.0,
        });
        await _store.RecordAsync(MakeCost(itemB) with
        {
            InputTokens = 40_000,
            OutputTokens = 10_000,
            EstimatedUsd = 1.0,
        });
        await _store.RecordAsync(MakeCost(itemC, phase: "rework") with
        {
            Iteration = 1,
            InputTokens = 50_000,
            OutputTokens = 10_000,
            EstimatedUsd = 1.0,
        });
        await _store.RecordAsync(MakeCost(itemC, phase: "rework") with
        {
            Iteration = 2,
            InputTokens = 50_000,
            OutputTokens = 10_000,
            EstimatedUsd = 1.0,
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId("proj-headroom"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.NotNull(estimate);
        Assert.Equal(7.0, estimate!.EstimatedIterPctCost, precision: 2);
        Assert.Equal(70_000, estimate.AverageTokensPerIteration, precision: 2);
        Assert.Equal(3, estimate.SampledItemCount);
        Assert.True(estimate.TrustedForEnforcement);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_ProviderMetadataRowsEnableManagerEnforcement()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, "proj-provider-headroom");
        await _store.RecordAsync(MakeCost(itemId) with
        {
            InputTokens = 80_000,
            OutputTokens = 20_000,
        });

        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            HeadroomTokensPerQuotaPct = 10_000,
            HeadroomHistoryItemCount = 20,
            HeadroomHistoryWindow = TimeSpan.FromDays(7),
        };
        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);
        var manager = new InProcessQuotaHeadroomManager(
            estimator,
            [new FakeProbe(AgentKind.Claude, 15.0)],
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InProcessQuotaHeadroomManager>.Instance);

        var result = await manager.EvaluateAsync(new QuotaHeadroomGateRequest(
            new ProjectId("proj-provider-headroom"),
            new AgentMembership
            {
                Agent = AgentKind.Claude,
                Billing = AgentBilling.Subscription,
                ModelId = "claude-opus-4-7",
                QualityScore = 100,
            },
            AvailablePct: 15.0,
            ResetAt: null,
            AuditOnRefusal: false));

        Assert.False(result.Allow);
        Assert.True(result.InsufficientHeadroom);
        Assert.NotNull(result.Estimate);
        Assert.True(result.Estimate!.TrustedForEnforcement);
        Assert.Equal(10.0, result.Estimate.EstimatedIterPctCost, precision: 2);
        Assert.Equal(5.0, result.ProjectedAvailablePct!.Value, precision: 2);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_UsesProductionDefaultsAsUntrustedAndIgnoresRejectedRows()
    {
        var productionDefaultItem = Guid.NewGuid().ToString();
        var explicitUntrustedItem = Guid.NewGuid().ToString();
        var overCapItem = Guid.NewGuid().ToString();
        var trustedItem = Guid.NewGuid().ToString();
        SeedWorkItem(productionDefaultItem, "proj-headroom-trust");
        SeedWorkItem(explicitUntrustedItem, "proj-headroom-trust");
        SeedWorkItem(overCapItem, "proj-headroom-trust");
        SeedWorkItem(trustedItem, "proj-headroom-trust");

        await _store.RecordAsync(MakeCost(productionDefaultItem) with
        {
            InputTokens = 30_000,
            OutputTokens = 10_000,
            RawMetadataJson = "{}",
        });
        await _store.RecordAsync(MakeCost(explicitUntrustedItem) with
        {
            InputTokens = 30_000,
            OutputTokens = 0,
            RawMetadataJson = """{"source":"manual_import"}""",
        });
        await _store.RecordAsync(MakeCost(overCapItem) with
        {
            InputTokens = 600_000,
            OutputTokens = 1,
        });
        await _store.RecordAsync(MakeCost(trustedItem) with
        {
            InputTokens = 40_000,
            OutputTokens = 10_000,
            RawMetadataJson = """{"quotaHeadroomTrusted":true}""",
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
                HeadroomMaxTokensPerCostRow = 500_000,
                HeadroomMaxTokensPerIteration = 500_000,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId("proj-headroom-trust"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.NotNull(estimate);
        Assert.False(estimate!.TrustedForEnforcement);
        Assert.Equal(4.5, estimate.EstimatedIterPctCost, precision: 2);
        Assert.Equal(45_000, estimate.AverageTokensPerIteration, precision: 2);
        Assert.Equal(2, estimate.SampledItemCount);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_UsesAgentStreamAnalyserRowsAsUntrusted()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, "proj-headroom-stream");
        await _store.RecordAsync(MakeCost(itemId) with
        {
            InputTokens = 70_000,
            OutputTokens = 30_000,
            RawMetadataJson = """{"source":"agent_stream_analyser","fileName":"agent.ndjson"}""",
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId("proj-headroom-stream"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.NotNull(estimate);
        Assert.Equal(10.0, estimate!.EstimatedIterPctCost, precision: 2);
        Assert.Equal(100_000, estimate.AverageTokensPerIteration, precision: 2);
        Assert.False(estimate.TrustedForEnforcement);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_ExplicitTrustedRowsEnableEnforcementAndRejectBadMetadata()
    {
        var trustedItem = Guid.NewGuid().ToString();
        var malformedItem = Guid.NewGuid().ToString();
        var nonObjectItem = Guid.NewGuid().ToString();
        var negativeItem = Guid.NewGuid().ToString();
        SeedWorkItem(trustedItem, "proj-headroom-explicit-trust");
        SeedWorkItem(malformedItem, "proj-headroom-explicit-trust");
        SeedWorkItem(nonObjectItem, "proj-headroom-explicit-trust");
        SeedWorkItem(negativeItem, "proj-headroom-explicit-trust");

        await _store.RecordAsync(MakeCost(trustedItem) with
        {
            InputTokens = 50_000,
            OutputTokens = 10_000,
            RawMetadataJson = """{"quotaHeadroomTrusted":true,"source":"provider_api"}""",
        });
        await _store.RecordAsync(MakeCost(malformedItem) with
        {
            InputTokens = 90_000,
            OutputTokens = 0,
            RawMetadataJson = "{",
        });
        await _store.RecordAsync(MakeCost(nonObjectItem) with
        {
            InputTokens = 90_000,
            OutputTokens = 0,
            RawMetadataJson = "[]",
        });
        await _store.RecordAsync(MakeCost(negativeItem) with
        {
            InputTokens = -1,
            OutputTokens = 900_000,
            RawMetadataJson = """{"quotaHeadroomTrusted":true}""",
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId("proj-headroom-explicit-trust"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.NotNull(estimate);
        Assert.True(estimate!.TrustedForEnforcement);
        Assert.Equal(6.0, estimate.EstimatedIterPctCost, precision: 2);
        Assert.Equal(60_000, estimate.AverageTokensPerIteration, precision: 2);
        Assert.Equal(1, estimate.SampledItemCount);
    }

    [Theory]
    [InlineData(500_000, 50.0)]
    [InlineData(0, null)]
    public async Task CostHistoryQuotaHeadroomEstimator_AppliesIterationTokenCapAndDisabledBranch(
        int maxTokensPerIteration,
        double? expectedPctCost)
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, $"proj-headroom-iteration-cap-{maxTokensPerIteration}");
        await _store.RecordAsync(MakeCost(itemId) with
        {
            InputTokens = 800_000,
            OutputTokens = 0,
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
                HeadroomMaxTokensPerCostRow = 1_000_000,
                HeadroomMaxTokensPerIteration = maxTokensPerIteration,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId($"proj-headroom-iteration-cap-{maxTokensPerIteration}"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        if (expectedPctCost is null)
        {
            Assert.Null(estimate);
            return;
        }

        Assert.NotNull(estimate);
        Assert.Equal(expectedPctCost.Value, estimate!.EstimatedIterPctCost, precision: 2);
        Assert.Equal(maxTokensPerIteration, estimate.AverageTokensPerIteration, precision: 2);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_AppliesHistoryWindowBoundary()
    {
        var now = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var boundary = now.AddDays(-7);
        var staleItem = Guid.NewGuid().ToString();
        var boundaryItem = Guid.NewGuid().ToString();
        var freshItem = Guid.NewGuid().ToString();
        SeedWorkItem(staleItem, "proj-headroom-window");
        SeedWorkItem(boundaryItem, "proj-headroom-window");
        SeedWorkItem(freshItem, "proj-headroom-window");

        await _store.RecordAsync(MakeCost(staleItem) with
        {
            InputTokens = 1_000_000,
            OutputTokens = 0,
            StartedAt = boundary.AddTicks(-1),
            EndedAt = boundary.AddTicks(-1),
        });
        await _store.RecordAsync(MakeCost(boundaryItem) with
        {
            InputTokens = 20_000,
            OutputTokens = 0,
            StartedAt = boundary,
            EndedAt = boundary.AddMinutes(1),
        });
        await _store.RecordAsync(MakeCost(freshItem) with
        {
            InputTokens = 40_000,
            OutputTokens = 0,
            StartedAt = now.AddMinutes(-10),
            EndedAt = now.AddMinutes(-9),
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance,
            new FakeTimeProvider(now));

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId("proj-headroom-window"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.NotNull(estimate);
        Assert.Equal(3.0, estimate!.EstimatedIterPctCost, precision: 2);
        Assert.Equal(30_000, estimate.AverageTokensPerIteration, precision: 2);
        Assert.Equal(2, estimate.SampledItemCount);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_DisabledProjectionReturnsNullAndRouterAllows()
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, "proj-disabled-headroom");
        await _store.RecordAsync(MakeCost(itemId) with
        {
            InputTokens = 100_000,
            OutputTokens = 0,
        });
        var opts = new QuotaRouterOptions
        {
            HeadroomProjectionEnabled = false,
            HeadroomTokensPerQuotaPct = 10_000,
            HeadroomHistoryItemCount = 20,
            HeadroomHistoryWindow = TimeSpan.FromDays(7),
            MinQuotaPct = 10.0,
        };
        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-disabled-headroom"), AgentKind.Claude, "claude-opus-4-7"));

        Assert.Null(estimate);

        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = "claude-opus-4-7",
                },
            ],
        };
        var probes = new IAgentQuotaProbe[] { new FakeProbe(AgentKind.Claude, 15.0) };
        var manager = new InProcessQuotaHeadroomManager(
            estimator,
            probes,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InProcessQuotaHeadroomManager>.Instance);
        var router = new AgentClassRouter(
            [cls],
            probes,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);
        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj-disabled-headroom"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        }, null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_EmptyHistoryReturnsNullAndRouterAllows()
    {
        var opts = new QuotaRouterOptions
        {
            HeadroomTokensPerQuotaPct = 10_000,
            HeadroomHistoryItemCount = 20,
            HeadroomHistoryWindow = TimeSpan.FromDays(7),
            MinQuotaPct = 10.0,
        };
        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-empty-headroom"), AgentKind.Claude, "claude-opus-4-7"));

        Assert.Null(estimate);

        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = "claude-opus-4-7",
                },
            ],
        };
        var probes = new IAgentQuotaProbe[] { new FakeProbe(AgentKind.Claude, 15.0) };
        var manager = new InProcessQuotaHeadroomManager(
            estimator,
            probes,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InProcessQuotaHeadroomManager>.Instance);
        var router = new AgentClassRouter(
            [cls],
            probes,
            opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);
        var decision = await router.ResolveAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj-empty-headroom"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        }, null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10_000.0)]
    public async Task CostHistoryQuotaHeadroomEstimator_InvalidTokensPerQuotaPctReturnsNullWithoutHistoryQuery(
        double tokensPerPct)
    {
        var estimator = new CostHistoryQuotaHeadroomEstimator(
            new ThrowingCostStore(),
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = tokensPerPct,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-invalid-headroom"), AgentKind.Claude, "claude-opus-4-7"));

        Assert.Null(estimate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CostHistoryQuotaHeadroomEstimator_InvalidHistoryItemCountReturnsNullWithoutHistoryQuery(
        int historyItemCount)
    {
        var estimator = new CostHistoryQuotaHeadroomEstimator(
            new ThrowingCostStore(),
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = historyItemCount,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-invalid-headroom"), AgentKind.Claude, "claude-opus-4-7"));

        Assert.Null(estimate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CostHistoryQuotaHeadroomEstimator_InvalidMaxTokensPerCostRowReturnsNull(
        int maxTokensPerCostRow)
    {
        var itemId = Guid.NewGuid().ToString();
        SeedWorkItem(itemId, $"proj-invalid-row-cap-{maxTokensPerCostRow}");
        await _store.RecordAsync(MakeCost(itemId) with
        {
            InputTokens = 100_000,
            OutputTokens = 0,
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomHistoryItemCount = 20,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
                HeadroomMaxTokensPerCostRow = maxTokensPerCostRow,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(
                new ProjectId($"proj-invalid-row-cap-{maxTokensPerCostRow}"),
                AgentKind.Claude,
                "claude-opus-4-7"));

        Assert.Null(estimate);
    }

    [Fact]
    public async Task CostHistoryQuotaHeadroomEstimator_SelectsBoundedModelAgentAndProjectSources()
    {
        var now = DateTimeOffset.UtcNow;
        var opusOld = Guid.NewGuid().ToString();
        var opusNew = Guid.NewGuid().ToString();
        var sonnetNewer = Guid.NewGuid().ToString();
        var projectOnly = Guid.NewGuid().ToString();
        foreach (var id in new[] { opusOld, opusNew, sonnetNewer, projectOnly })
            SeedWorkItem(id, "proj-selection");

        await _store.RecordAsync(MakeCost(opusOld) with
        {
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = 100_000,
            OutputTokens = 0,
            StartedAt = now.AddMinutes(-10),
            EndedAt = now.AddMinutes(-9),
        });
        await _store.RecordAsync(MakeCost(opusNew) with
        {
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = 20_000,
            OutputTokens = 0,
            StartedAt = now.AddMinutes(-4),
            EndedAt = now.AddMinutes(-3),
        });
        await _store.RecordAsync(MakeCost(sonnetNewer) with
        {
            AgentKind = "claude",
            ModelId = "claude-sonnet-4",
            InputTokens = 60_000,
            OutputTokens = 0,
            StartedAt = now.AddMinutes(-2),
            EndedAt = now.AddMinutes(-1),
        });
        await _store.RecordAsync(MakeCost(projectOnly) with
        {
            AgentKind = "codex",
            ModelId = "gpt-5.2",
            InputTokens = 90_000,
            OutputTokens = 0,
            StartedAt = now.AddMinutes(-6),
            EndedAt = now.AddMinutes(-5),
        });

        var estimator = new CostHistoryQuotaHeadroomEstimator(
            _store,
            new QuotaRouterOptions
            {
                HeadroomTokensPerQuotaPct = 10_000,
                HeadroomTokensPerQuotaPctByAgent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = 5_000,
                },
                HeadroomHistoryItemCount = 1,
                HeadroomHistoryWindow = TimeSpan.FromDays(7),
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CostHistoryQuotaHeadroomEstimator>.Instance);

        var modelEstimate = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-selection"), AgentKind.Claude, "claude-opus-4-7"));
        Assert.NotNull(modelEstimate);
        Assert.Equal("agent+model", modelEstimate!.Source);
        Assert.Equal(20_000, modelEstimate.AverageTokensPerIteration, precision: 2);
        Assert.Equal(4.0, modelEstimate.EstimatedIterPctCost, precision: 2);

        var agentFallback = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-selection"), AgentKind.Claude, "missing-model"));
        Assert.NotNull(agentFallback);
        Assert.Equal("agent", agentFallback!.Source);
        Assert.Equal(60_000, agentFallback.AverageTokensPerIteration, precision: 2);
        Assert.Equal(12.0, agentFallback.EstimatedIterPctCost, precision: 2);

        var projectFallback = await estimator.EstimateAsync(
            new QuotaHeadroomRequest(new ProjectId("proj-selection"), AgentKind.Gemini, "gemini-pro"));
        Assert.NotNull(projectFallback);
        Assert.Equal("project", projectFallback!.Source);
        Assert.Equal(60_000, projectFallback.AverageTokensPerIteration, precision: 2);
        Assert.Equal(6.0, projectFallback.EstimatedIterPctCost, precision: 2);
    }

    private sealed class ThrowingCostStore : IWorkItemCostStore
    {
        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => throw UnexpectedQuery();

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(
            string workItemId,
            CancellationToken ct = default) => throw UnexpectedQuery();

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken ct = default) => throw UnexpectedQuery();

        public Task<IReadOnlyList<WorkItemCost>> GetRecentByProjectAsync(
            string projectId,
            DateTimeOffset from,
            DateTimeOffset to,
            string? agentKind,
            string? modelId,
            int maxItems,
            CancellationToken ct = default) => throw UnexpectedQuery();

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken ct = default) => throw UnexpectedQuery();

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => throw UnexpectedQuery();

        public Task<decimal> SumEstimatedUsdAsync(
            string projectId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken ct = default) => throw UnexpectedQuery();

        private static InvalidOperationException UnexpectedQuery() =>
            new("Invalid headroom configuration should short-circuit before querying cost history.");
    }
}
