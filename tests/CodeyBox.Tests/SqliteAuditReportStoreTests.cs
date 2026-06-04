using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class SqliteAuditReportStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-audit-store-{Guid.NewGuid():N}.db");
    private readonly SqliteAuditReportStore _store;

    public SqliteAuditReportStoreTests()
    {
        using var workItems = new SqliteWorkItemStore(_dbPath);
        _store = new SqliteAuditReportStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static AuditReport Make(
        string workItemId = "wi-001",
        int iteration = 1,
        string auditorName = "Lint",
        string auditorKind = "diff-pattern",
        string? rawOutput = null,
        IReadOnlyList<AuditReportFinding>? findings = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = iteration,
            AuditorName = auditorName,
            AuditorKind = auditorKind,
            WorstSeverity = findings?.Count > 0 ? findings[0].Severity : "none",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            Findings = findings ?? [],
            RawOutput = rawOutput,
        };

    private static AuditReportFinding MakeFinding(string title = "Bad thing", string sev = "Error") =>
        new(
            Id: FindingIdComputer.Compute("Lint", title, []),
            Severity: sev,
            Title: title,
            Message: "Details here",
            Files: [],
            LineHints: []);

    [Fact]
    public async Task RoundTrip_PreservesAllFields()
    {
        var finding = new AuditReportFinding(
            Id: "f-aabbccdd",
            Severity: "Error",
            Title: "Missing null check",
            Message: "The foo method doesn't check for null.",
            Files: ["src/Foo.cs"],
            LineHints: [42]);
        var report = Make(
            workItemId: "wi-roundtrip",
            iteration: 3,
            auditorName: "LlmReview",
            auditorKind: "llm",
            rawOutput: "Some raw output text",
            findings: [finding]);

        await CreateAsync(report);
        var results = await _store.GetByWorkItemAsync("wi-roundtrip");

        Assert.Single(results);
        var got = results[0];
        Assert.Equal(report.Id, got.Id);
        Assert.Equal("wi-roundtrip", got.WorkItemId);
        Assert.Equal(3, got.Iteration);
        Assert.Equal("LlmReview", got.AuditorName);
        Assert.Equal("llm", got.AuditorKind);
        Assert.Equal("Error", got.WorstSeverity);
        Assert.InRange(got.DurationMs, 0, 10_000);
        Assert.Equal("Some raw output text", got.RawOutput);
        Assert.Single(got.Findings);
        var f = got.Findings[0];
        Assert.Equal("f-aabbccdd", f.Id);
        Assert.Equal("Error", f.Severity);
        Assert.Equal("Missing null check", f.Title);
        Assert.Equal("The foo method doesn't check for null.", f.Message);
        Assert.Equal(["src/Foo.cs"], f.Files);
        Assert.Equal([42], f.LineHints);
    }

    [Fact]
    public async Task GetByWorkItem_OrderedByIterationThenAuditorName()
    {
        var wi = "wi-order";
        await CreateAsync(Make(wi, iteration: 2, auditorName: "Zebra"));
        await CreateAsync(Make(wi, iteration: 1, auditorName: "Beta"));
        await CreateAsync(Make(wi, iteration: 2, auditorName: "Alpha"));
        await CreateAsync(Make(wi, iteration: 1, auditorName: "Alpha"));

        var results = await _store.GetByWorkItemAsync(wi);

        Assert.Equal(4, results.Count);
        Assert.Equal((1, "Alpha"), (results[0].Iteration, results[0].AuditorName));
        Assert.Equal((1, "Beta"), (results[1].Iteration, results[1].AuditorName));
        Assert.Equal((2, "Alpha"), (results[2].Iteration, results[2].AuditorName));
        Assert.Equal((2, "Zebra"), (results[3].Iteration, results[3].AuditorName));
    }

    [Fact]
    public async Task GetByWorkItem_OnlyReturnsMatchingWorkItem()
    {
        await CreateAsync(Make("wi-A"));
        await CreateAsync(Make("wi-B"));

        var results = await _store.GetByWorkItemAsync("wi-A");

        Assert.Single(results);
        Assert.Equal("wi-A", results[0].WorkItemId);
    }

    [Fact]
    public async Task GetByWorkItem_ReturnsEmpty_WhenNoReports()
    {
        var results = await _store.GetByWorkItemAsync("wi-nonexistent");
        Assert.Empty(results);
    }

    [Fact]
    public async Task RawOutput_IsNullByDefault()
    {
        var report = Make("wi-null-raw");
        await CreateAsync(report);

        var raw = await _store.GetRawOutputAsync("wi-null-raw", 1, "Lint");

        Assert.Null(raw);
    }

    [Fact]
    public async Task GetRawOutput_ReturnsStoredValue()
    {
        var report = Make("wi-raw", rawOutput: "stdout goes here");
        await CreateAsync(report);

        var raw = await _store.GetRawOutputAsync("wi-raw", 1, "Lint");

        Assert.Equal("stdout goes here", raw);
    }

    [Fact]
    public async Task GetRawOutput_ReturnsNull_WhenNotFound()
    {
        var raw = await _store.GetRawOutputAsync("wi-missing", 99, "NoSuchAuditor");
        Assert.Null(raw);
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesExpiredRows()
    {
        var wi = "wi-expire";
        var old = Make(wi) with { StartedAt = DateTimeOffset.UtcNow.AddDays(-40) };
        var fresh = Make(wi) with { StartedAt = DateTimeOffset.UtcNow.AddDays(-5) };
        // StartedAt is immutable on record; reconstruct with correct EndedAt too
        old = old with { EndedAt = old.StartedAt.AddSeconds(1) };
        fresh = fresh with { EndedAt = fresh.StartedAt.AddSeconds(1) };
        await CreateAsync(old);
        await CreateAsync(fresh);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, deleted);
        var remaining = await _store.GetByWorkItemAsync(wi);
        Assert.Single(remaining);
        Assert.Equal(fresh.Id, remaining[0].Id);
    }

    [Fact]
    public async Task DeleteOlderThan_ContinuesPastFirstBatch()
    {
        const int oldCount = 501;
        var wi = "wi-expire-batches";
        var now = DateTimeOffset.UtcNow;
        await SeedWorkItemAsync(wi);

        for (var i = 0; i < oldCount; i++)
        {
            var started = now.AddDays(-40).AddMilliseconds(i);
            await _store.CreateAsync(Make(wi, iteration: i + 1) with
            {
                StartedAt = started,
                EndedAt = started.AddSeconds(1),
            });
        }

        var freshStarted = now.AddDays(-5);
        var fresh = Make(wi, iteration: oldCount + 1) with
        {
            StartedAt = freshStarted,
            EndedAt = freshStarted.AddSeconds(1),
        };
        await _store.CreateAsync(fresh);

        var deleted = await _store.DeleteOlderThanAsync(now.AddDays(-30));

        Assert.Equal(oldCount, deleted);
        var remaining = await _store.GetByWorkItemAsync(wi);
        var survivor = Assert.Single(remaining);
        Assert.Equal(fresh.Id, survivor.Id);
    }

    [Fact]
    public async Task DeleteOlderThan_KeepsRowsAtOrAfterCutoff()
    {
        var wi = "wi-keep";
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var atCutoff = Make(wi) with { StartedAt = cutoff, EndedAt = cutoff.AddSeconds(1) };
        await CreateAsync(atCutoff);

        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(0, deleted);
        var remaining = await _store.GetByWorkItemAsync(wi);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task MultipleFindings_RoundTrip_PreservesAll()
    {
        var findings = new List<AuditReportFinding>
        {
            new("f-aa", "Error", "Missing null check", "Desc1", ["src/A.cs"], [10]),
            new("f-bb", "Warning", "Long method", "Desc2", ["src/B.cs", "src/C.cs"], []),
            new("f-cc", "Info", "Unused var", "Desc3", [], []),
        };
        var report = Make("wi-multi", findings: findings);
        await CreateAsync(report);

        var results = await _store.GetByWorkItemAsync("wi-multi");
        Assert.Single(results);
        Assert.Equal(3, results[0].Findings.Count);
        Assert.Equal("f-aa", results[0].Findings[0].Id);
        Assert.Equal("f-bb", results[0].Findings[1].Id);
        Assert.Equal("f-cc", results[0].Findings[2].Id);
        Assert.Equal(["src/B.cs", "src/C.cs"], results[0].Findings[1].Files);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingWorkItem()
    {
        await Assert.ThrowsAsync<SqliteException>(() => _store.CreateAsync(Make("wi-missing-parent")));
    }

    private async Task CreateAsync(AuditReport report)
    {
        await SeedWorkItemAsync(report.WorkItemId);
        await _store.CreateAsync(report);
    }

    private async Task SeedWorkItemAsync(string workItemId)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
        await pragma.ExecuteNonQueryAsync();

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
