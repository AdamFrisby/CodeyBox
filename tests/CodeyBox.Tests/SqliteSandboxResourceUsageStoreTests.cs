using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

public sealed class SqliteSandboxResourceUsageStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-resource-usage-store-{Guid.NewGuid():N}.db");
    private readonly SqliteSandboxResourceUsageStore _store;

    public SqliteSandboxResourceUsageStoreTests()
    {
        _store = new SqliteSandboxResourceUsageStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    [Fact]
    public async Task RecordAsync_PersistsRequiredCapacityPlanningFields()
    {
        var id = WorkItemId.New();
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _store.RecordAsync(new SandboxResourceUsageRecord
        {
            WorkItemId = id,
            Phase = "rework",
            VmName = "codeybox-vm",
            DurationSeconds = 123.4,
            AvgCpuPercent = 37.5,
            PeakRamMb = 512,
            NetRxMb = 12,
            NetTxMb = 34,
            BaselineRef = "cb-baseline-abc123",
            NetworkProfile = "claude",
            LoadAvg1 = 0.4,
            LoadAvg5 = 0.5,
            LoadAvg15 = 0.6,
            CapturedAt = capturedAt,
        });

        var rows = await _store.ListRecentAsync(10);
        var row = Assert.Single(rows);
        Assert.Equal(id, row.WorkItemId);
        Assert.Equal("rework", row.Phase);
        Assert.Equal("codeybox-vm", row.VmName);
        Assert.Equal(123.4, row.DurationSeconds);
        Assert.Equal(37.5, row.AvgCpuPercent);
        Assert.Equal(512, row.PeakRamMb);
        Assert.Equal(12, row.NetRxMb);
        Assert.Equal(34, row.NetTxMb);
        Assert.Equal("cb-baseline-abc123", row.BaselineRef);
        Assert.Equal("claude", row.NetworkProfile);
        Assert.Equal(0.4, row.LoadAvg1);
        Assert.Equal(0.5, row.LoadAvg5);
        Assert.Equal(0.6, row.LoadAvg15);
        Assert.Equal(capturedAt.ToUniversalTime().ToString("O"), row.CapturedAt.ToUniversalTime().ToString("O"));
    }

    [Fact]
    public async Task ListRecentAsync_FiltersBySinceAndLimit()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _store.RecordAsync(MakeRecord(old, "old"));
        await _store.RecordAsync(MakeRecord(recent, "recent"));

        var rows = await _store.ListRecentAsync(1, DateTimeOffset.UtcNow.AddHours(-1));

        var row = Assert.Single(rows);
        Assert.Equal("recent", row.Phase);
    }

    private static SandboxResourceUsageRecord MakeRecord(DateTimeOffset capturedAt, string phase) => new()
    {
        WorkItemId = WorkItemId.New(),
        Phase = phase,
        VmName = $"vm-{phase}",
        PeakRamMb = 128,
        AvgCpuPercent = 10,
        NetRxMb = 1,
        NetTxMb = 2,
        CapturedAt = capturedAt,
    };
}
