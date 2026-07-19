using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodeyBox.Tests;

public sealed class SandboxResourceUsageEndpointTests : IClassFixture<SandboxResourceUsageEndpointTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public SandboxResourceUsageEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAggregate_ReturnsPlanningStatsAcrossRecentRecords()
    {
        var store = _factory.Services.GetRequiredService<ISandboxResourceUsageStore>();
        var now = DateTimeOffset.UtcNow;
        await store.RecordAsync(MakeRecord(now.AddMinutes(-3), peak: 100, cpu: 10, rx: 1, tx: 2));
        await store.RecordAsync(MakeRecord(now.AddMinutes(-2), peak: 200, cpu: 30, rx: 2, tx: 5));
        await store.RecordAsync(MakeRecord(now.AddMinutes(-1), peak: 400, cpu: 50, rx: 3, tx: 12));

        var json = await _client.GetFromJsonAsync<JsonElement>("/admin/sandbox-resource-usage?n=10");

        Assert.Equal(3, json.GetProperty("recordCount").GetInt32());
        Assert.Equal(200, json.GetProperty("peakRamMb").GetProperty("p50").GetDouble());
        Assert.Equal(400, json.GetProperty("peakRamMb").GetProperty("p95").GetDouble());
        Assert.Equal(30, json.GetProperty("avgCpuPct").GetProperty("avg").GetDouble());
        Assert.Equal(50, json.GetProperty("avgCpuPct").GetProperty("p95").GetDouble());
        Assert.Equal(6, json.GetProperty("netRxMb").GetProperty("total").GetDouble());
        Assert.Equal(19, json.GetProperty("netTxMb").GetProperty("total").GetDouble());
        Assert.Equal(25, json.GetProperty("netTotalMb").GetProperty("total").GetDouble());
    }

    private static SandboxResourceUsageRecord MakeRecord(
        DateTimeOffset capturedAt,
        double peak,
        double cpu,
        double rx,
        double tx) => new()
        {
            WorkItemId = WorkItemId.New(),
            Phase = "work",
            VmName = $"vm-{Guid.NewGuid():N}",
            DurationSeconds = 60,
            PeakRamMb = peak,
            AvgCpuPercent = cpu,
            NetRxMb = rx,
            NetTxMb = tx,
            BaselineRef = "cb-baseline",
            NetworkProfile = "claude",
            CapturedAt = capturedAt,
        };

    public sealed class Factory : CodeyBoxWebApplicationFactory
    {
        private readonly string _dbPath;
        private readonly string _gitRoot;
        private readonly string _auditLogPath;
        private readonly string _auditPath;
        private readonly string _agentStreamsPath;

        public Factory()
        {
            _dbPath = TempDatabasePath("codeybox-resource-endpoint");
            _gitRoot = Temp.NewDirectoryPath("test-git-");
            _auditLogPath = Temp.NewLogPath("test-log");
            _auditPath = Temp.NewLogPath("test-audit");
            _agentStreamsPath = Temp.NewDirectoryPath("test-agent-streams-");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = _gitRoot,
                    ["CodeyBox:AuditLog:Path"] = _auditLogPath,
                    ["CodeyBox:AuditLog:AuditPath"] = _auditPath,
                    ["CodeyBox:AgentStreams:Path"] = _agentStreamsPath,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }
    }
}
