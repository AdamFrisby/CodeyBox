using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Soundness report over accumulated shadow telemetry: the pure computer
/// (unsafe-skip counting, would-be savings, selector-agnostic filtering, the
/// explicit enforce-readiness gate), the newest-first store slice through real
/// SQLite wiring, and the read-only API endpoint rendering from real persisted
/// shadow records.
/// </summary>
public sealed class TestSelectionSoundnessTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ts-soundness-{Guid.NewGuid():N}.db");
    private readonly SqliteAuditReportStore _store;

    public TestSelectionSoundnessTests()
    {
        using var workItems = new SqliteWorkItemStore(_dbPath);
        _store = new SqliteAuditReportStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static TestSelectionSoundnessSample Sample(
        string selector,
        string assessment,
        int selected = 3,
        int total = 4,
        double fraction = 0.25,
        long durationMs = 100_000) => new()
        {
            Selector = selector,
            Assessment = assessment,
            SelectedCount = selected,
            TotalCount = total,
            EstimatedSavedFraction = fraction,
            DurationMs = durationMs,
        };

    [Fact]
    public void Build_CountsUnsafeSkips_AndSumsWouldBeSavings()
    {
        var samples = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnsafe,
                selected: 2, total: 4, fraction: 0.5, durationMs: 200_000),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentFullSuite,
                selected: 4, total: 4, fraction: 0.0),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnverifiable,
                selected: 0, total: 0, fraction: 0.0),
        };

        var report = TestSelectionSoundnessComputer.Build(samples, windowSize: 10, selectorFilter: null, calibrationWindowSize: 100);

        Assert.Equal(1, report.UnsafeSkipCount);
        Assert.Equal(2, report.SafeCount);
        Assert.Equal(1, report.FullSuiteCount);
        Assert.Equal(1, report.UnverifiableCount);
        // Only assessable (safe + unsafe) runs contribute savings: (4-3)*2 + (4-2).
        Assert.Equal(4, report.TotalTestsSaved);
        // 100000*0.25*2 + 200000*0.5.
        Assert.Equal(150_000, report.EstimatedSavedMs);
        // Full-suite and unverifiable runs carry no safety evidence.
        Assert.Equal(3, report.Gate.AssessableCount);
        Assert.False(report.Gate.ReadyForEnforcement);
        Assert.Equal(0, report.Gate.MaxAllowedUnsafeSkips);
    }

    [Fact]
    public void Build_MutatingOneSafeRunToUnsafe_FlipsCounts()
    {
        var safe = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
        };
        var mutated = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnsafe),
        };

        var before = TestSelectionSoundnessComputer.Build(safe, 10, null, 2);
        var after = TestSelectionSoundnessComputer.Build(mutated, 10, null, 2);

        Assert.Equal(0, before.UnsafeSkipCount);
        Assert.True(before.Gate.ReadyForEnforcement);
        Assert.Equal(1, after.UnsafeSkipCount);
        Assert.False(after.Gate.ReadyForEnforcement);
    }

    [Fact]
    public void Build_SelectorFilter_IsExactMatch_AndBreakdownCoversAllSelectors()
    {
        var samples = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(ProjectGraphTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(ProjectGraphTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnsafe),
        };

        var filtered = TestSelectionSoundnessComputer.Build(
            samples, 10, ProjectGraphTestSelector.SelectorName, 100);

        Assert.Equal(2, filtered.EvaluatedCount);
        Assert.Equal(1, filtered.UnsafeSkipCount);
        Assert.Equal(ProjectGraphTestSelector.SelectorName, filtered.SelectorFilter);
        Assert.DoesNotContain(filtered.BySelector, s => s.Selector == CoverageTestSelector.SelectorName);

        var unfiltered = TestSelectionSoundnessComputer.Build(samples, 10, null, 100);
        Assert.Equal(3, unfiltered.EvaluatedCount);
        Assert.Equal(2, unfiltered.BySelector.Count);

        // Substring must not match: exact equality only.
        var substring = TestSelectionSoundnessComputer.Build(samples, 10, "cover", 100);
        Assert.Equal(0, substring.EvaluatedCount);
    }

    [Fact]
    public void Build_Gate_RequiresFullCalibrationWindow_AndZeroUnsafe()
    {
        var samples = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
        };

        var incomplete = TestSelectionSoundnessComputer.Build(samples, 10, null, 100);
        Assert.False(incomplete.Gate.ReadyForEnforcement);
        Assert.Contains("calibration incomplete", incomplete.Gate.Reason, StringComparison.Ordinal);

        var ready = TestSelectionSoundnessComputer.Build(samples, 10, null, 1);
        Assert.True(ready.Gate.ReadyForEnforcement);
        Assert.Contains("zero unsafe skips", ready.Gate.Reason, StringComparison.Ordinal);

        var unsafeSamples = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnsafe),
        };
        var blocked = TestSelectionSoundnessComputer.Build(unsafeSamples, 10, null, 1);
        Assert.False(blocked.Gate.ReadyForEnforcement);
        Assert.Contains("unsafe skip", blocked.Gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsNonPositiveWindow_AndTakesOnlyRequestedHead()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestSelectionSoundnessComputer.Build([], 0, null, 10));
        var samples = new[]
        {
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
            Sample(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe),
        };
        var report = TestSelectionSoundnessComputer.Build(samples, windowSize: 1, selectorFilter: null, calibrationWindowSize: 10);
        Assert.Equal(1, report.EvaluatedCount);
    }

    [Fact]
    public void SoundnessOptions_Validation()
    {
        Assert.True(TestSelectionSoundnessOptions.IsValid(new TestSelectionSoundnessOptions()));
        Assert.False(TestSelectionSoundnessOptions.IsValid(null));
        Assert.False(TestSelectionSoundnessOptions.IsValid(new TestSelectionSoundnessOptions { CalibrationWindowSize = 0 }));
        Assert.False(TestSelectionSoundnessOptions.IsValid(new TestSelectionSoundnessOptions { MaxLimit = 0 }));
    }

    [Fact]
    public async Task Store_RecentSlice_IsNewestFirst_Bounded_AndExcludesRowsWithoutTelemetry()
    {
        var workItemId = Guid.NewGuid().ToString("N");
        await EnsureWorkItemAsync(workItemId);

        var safe = Telemetry(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe);
        // Oldest: no telemetry (must be excluded, never counted as safe).
        await _store.CreateAsync(Report(workItemId, null, DateTimeOffset.UtcNow.AddHours(-3)));
        await _store.CreateAsync(Report(workItemId, safe, DateTimeOffset.UtcNow.AddHours(-2)));
        await _store.CreateAsync(Report(workItemId, safe, DateTimeOffset.UtcNow.AddHours(-1)));

        var all = await _store.GetRecentTestSelectionAsync(10);
        Assert.Equal(2, all.Count);
        Assert.True(all[0].EndedAt >= all[1].EndedAt);
        Assert.All(all, r => Assert.NotNull(r.TestSelection));

        var bounded = await _store.GetRecentTestSelectionAsync(1);
        Assert.Single(bounded);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.GetRecentTestSelectionAsync(0));
    }

    [Fact]
    public async Task Store_RecentSlice_RendersReport_FromRealShadowRecords()
    {
        var workItemId = Guid.NewGuid().ToString("N");
        await EnsureWorkItemAsync(workItemId);

        await _store.CreateAsync(Report(
            workItemId,
            Telemetry(CoverageTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentSafe, selected: 3, total: 4),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            durationMs: 100_000));
        await _store.CreateAsync(Report(
            workItemId,
            Telemetry(ProjectGraphTestSelector.SelectorName, TestSelectionShadowRecord.AssessmentUnsafe, selected: 2, total: 4),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            durationMs: 200_000));

        var rows = await _store.GetRecentTestSelectionAsync(100);
        var samples = rows.Select(TestSelectionSoundnessSample.FromReport).ToList();
        var report = TestSelectionSoundnessComputer.Build(samples, 100, null, 100);

        Assert.Equal(2, report.EvaluatedCount);
        Assert.Equal(1, report.UnsafeSkipCount);
        Assert.Equal(1, report.SafeCount);
        Assert.Equal((4 - 3) + (4 - 2), report.TotalTestsSaved);
        Assert.False(report.Gate.ReadyForEnforcement);
        Assert.Equal(2, report.BySelector.Count);
    }

    private static TestSelectionTelemetry Telemetry(
        string selector, string assessment, int selected = 3, int total = 4) => new()
        {
            Mode = TestSelectionMode.CoverageShadow.ToString(),
            Selector = selector,
            Layers = [selector],
            SelectedCount = selected,
            TotalCount = total,
            EstimatedSavedFraction = total > 0 ? (double)(total - selected) / total : 0.0,
            Assessment = assessment,
            Fallbacks = [],
            Detail = "test-selection soundness fixture",
        };

    private AuditReport Report(
        string workItemId,
        TestSelectionTelemetry? telemetry,
        DateTimeOffset endedAt,
        long durationMs = 60_000) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = 1,
            Target = AuditTarget.Code,
            AuditorName = "csharp:test-pass",
            AuditorKind = "shell",
            WorstSeverity = "none",
            StartedAt = endedAt.AddSeconds(-30),
            EndedAt = endedAt,
            DurationMs = durationMs,
            Findings = [],
            RawOutput = null,
            TestSelection = telemetry,
        };

    private async Task EnsureWorkItemAsync(string workItemId)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO work_items
                (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks,
                 push_upstream, state, created_at, updated_at)
            VALUES
                ($id, 'test-project', 'test', 'test', $workTimeout, $mergeTimeout,
                 1, 0, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", workItemId);
        cmd.Parameters.AddWithValue("$workTimeout", TimeSpan.FromMinutes(30).Ticks);
        cmd.Parameters.AddWithValue("$mergeTimeout", TimeSpan.FromMinutes(15).Ticks);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// HTTP-level coverage for <c>GET /audit/test-selection/soundness</c>: seeds
/// real telemetry-carrying audit reports through the real SQLite store and
/// asserts the endpoint renders unsafe-skip counts, savings, and the explicit
/// gate from those rows.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class TestSelectionSoundnessEndpointTests : IDisposable
{
    private readonly SoundnessApiFactory _factory = new();
    private readonly HttpClient _client;

    public TestSelectionSoundnessEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_ReturnsUnsafeCountSavingsAndGate_FromSeededShadowTelemetry()
    {
        var workItemId = Guid.NewGuid().ToString("N");
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_factory.DbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO work_items
                    (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks,
                     push_upstream, state, created_at, updated_at)
                VALUES
                    ($id, 'test-project', 'test', 'test', $workTimeout, $mergeTimeout,
                     1, 0, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", workItemId);
            cmd.Parameters.AddWithValue("$workTimeout", TimeSpan.FromMinutes(30).Ticks);
            cmd.Parameters.AddWithValue("$mergeTimeout", TimeSpan.FromMinutes(15).Ticks);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        var now = DateTimeOffset.UtcNow;
        await _factory.AuditReportStore.CreateAsync(SoundnessReport(
            workItemId, CoverageTestSelector.SelectorName,
            TestSelectionShadowRecord.AssessmentSafe,
            selected: 3, total: 4, durationMs: 100_000, endedAt: now.AddMinutes(-2)));
        await _factory.AuditReportStore.CreateAsync(SoundnessReport(
            workItemId, CoverageTestSelector.SelectorName,
            TestSelectionShadowRecord.AssessmentUnsafe,
            selected: 2, total: 4, durationMs: 200_000, endedAt: now.AddMinutes(-1)));

        var resp = await _client.GetAsync("/audit/test-selection/soundness?limit=100");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, doc.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(1, doc.GetProperty("unsafeSkipCount").GetInt32());
        Assert.Equal(1, doc.GetProperty("safeCount").GetInt32());
        Assert.Equal((4 - 3) + (4 - 2), doc.GetProperty("totalTestsSaved").GetInt64());
        var gate = doc.GetProperty("gate");
        Assert.Equal(0, gate.GetProperty("maxAllowedUnsafeSkips").GetInt32());
        Assert.False(gate.GetProperty("readyForEnforcement").GetBoolean());

        var filtered = await _client.GetAsync(
            "/audit/test-selection/soundness?limit=100&selector=project-graph");
        filtered.EnsureSuccessStatusCode();
        var filteredDoc = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, filteredDoc.GetProperty("evaluatedCount").GetInt32());
    }

    private static AuditReport SoundnessReport(
        string workItemId, string selector, string assessment,
        int selected, int total, long durationMs, DateTimeOffset endedAt) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = 1,
            Target = AuditTarget.Code,
            AuditorName = "csharp:test-pass",
            AuditorKind = "shell",
            WorstSeverity = "none",
            StartedAt = endedAt.AddSeconds(-30),
            EndedAt = endedAt,
            DurationMs = durationMs,
            Findings = [],
            RawOutput = null,
            TestSelection = new TestSelectionTelemetry
            {
                Mode = TestSelectionMode.CoverageShadow.ToString(),
                Selector = selector,
                Layers = [selector],
                SelectedCount = selected,
                TotalCount = total,
                EstimatedSavedFraction = (double)(total - selected) / total,
                Assessment = assessment,
                Fallbacks = [],
                Detail = "endpoint fixture",
            },
        };
}

internal sealed class SoundnessApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(
        Path.GetTempPath(), $"codeybox-soundness-api-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteAuditReportStore AuditReportStore { get; }

    public SoundnessApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(DbPath);
        AuditReportStore = new SqliteAuditReportStore(DbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = DbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:Audit:TestSelection:Soundness:CalibrationWindowSize"] = "100",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);
            services.RemoveAll<IAuditReportStore>();
            services.AddSingleton<IAuditReportStore>(AuditReportStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            AuditReportStore.Dispose();
            try { File.Delete(DbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
