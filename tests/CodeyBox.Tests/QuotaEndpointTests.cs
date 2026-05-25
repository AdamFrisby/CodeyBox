using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuotaEndpointTests
{
    [Fact]
    public async Task GetQuota_ReturnsSnapshotsPerModelFailuresAndWouldAllow()
    {
        using var factory = new WorkItemApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 60,
                    PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude-opus-4-7"] = new() { AvailablePct = 0, Window = "weekly" },
                    },
                }));
            });
        }).CreateClient();

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.Equal("UseObservedFailures", root.GetProperty("unknownPolicy").GetString());
        var probe = root.GetProperty("probes")[0];
        Assert.Equal("claude", probe.GetProperty("agent").GetString());
        Assert.True(probe.GetProperty("wouldAllow").GetBoolean());
        Assert.False(probe.GetProperty("perModelWouldAllow").GetProperty("claude-opus-4-7").GetBoolean());
        Assert.True(probe.TryGetProperty("observedFailuresLast60m", out _));
    }

    [Fact]
    public async Task GetQuota_ModelSpecificObservedFailureAffectsWouldAllowEvenWhenProbeOmitsModel()
    {
        using var factory = new WorkItemApiFactory();
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 60,
                }));
            });
        });
        var client = configuredFactory.CreateClient();
        var failures = configuredFactory.Services.GetRequiredService<IQuotaFailureStore>();
        await failures.RecordForProjectAsync(
            AgentKind.Claude,
            "claude-opus-4-7",
            new ProjectId("test-project"),
            QuotaFailureKind.LimitReached,
            DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var probe = doc.RootElement.GetProperty("probes")[0];
        Assert.False(probe.GetProperty("wouldAllow").GetBoolean());
        Assert.True(probe.GetProperty("defaultModelWouldAllow").GetBoolean());
        Assert.False(probe.GetProperty("perModelWouldAllow").GetProperty("claude-opus-4-7").GetBoolean());
    }

    [Fact]
    public async Task GetQuota_ReturnsHeadroomProjectionPerProject()
    {
        using var factory = new WorkItemApiFactory();
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 15,
                }));
            });
        });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "historical item",
            Prompt = "p",
        };
        await factory.Store.CreateAsync(item);
        var costs = configuredFactory.Services.GetRequiredService<IWorkItemCostStore>();
        await costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            ModelId = null,
            InputTokens = 80_000,
            OutputTokens = 20_000,
            EstimatedUsd = 1.0,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RawMetadataJson = """{"usageSource":"provider_metadata"}""",
        });

        var untrustedItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("second-project"),
            Title = "untrusted historical item",
            Prompt = "p",
        };
        await factory.Store.CreateAsync(untrustedItem);
        await costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = untrustedItem.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            ModelId = null,
            InputTokens = 80_000,
            OutputTokens = 20_000,
            EstimatedUsd = 1.0,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RawMetadataJson = """{"source":"agent_stream_analyser"}""",
        });

        var client = configuredFactory.CreateClient();
        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var projection = doc.RootElement
            .GetProperty("probes")[0]
            .GetProperty("headroomProjections")
            .EnumerateArray()
            .Single(p => p.GetProperty("projectId").GetString() == "test-project");

        Assert.Equal(10, projection.GetProperty("estimatedIterPctCost").GetDouble(), precision: 2);
        Assert.True(projection.GetProperty("trustedForEnforcement").GetBoolean());
        Assert.Equal(5, projection.GetProperty("projectedAvailablePct").GetDouble(), precision: 2);
        Assert.False(projection.GetProperty("wouldAllow").GetBoolean());
        Assert.True(projection.GetProperty("insufficientHeadroom").GetBoolean());
        Assert.Contains("insufficient headroom", projection.GetProperty("reason").GetString());

        var untrustedProjection = doc.RootElement
            .GetProperty("probes")[0]
            .GetProperty("headroomProjections")
            .EnumerateArray()
            .Single(p => p.GetProperty("projectId").GetString() == "second-project");

        Assert.Equal(10, untrustedProjection.GetProperty("estimatedIterPctCost").GetDouble(), precision: 2);
        Assert.False(untrustedProjection.GetProperty("trustedForEnforcement").GetBoolean());
        Assert.Equal(15, untrustedProjection.GetProperty("projectedAvailablePct").GetDouble(), precision: 2);
        Assert.True(untrustedProjection.GetProperty("wouldAllow").GetBoolean());
    }

    [Fact]
    public async Task GetQuota_ProjectionSubtractsReservedHeadroom()
    {
        using var factory = new WorkItemApiFactory();
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, new AgentQuotaSnapshot
                {
                    AvailablePct = 25,
                }));
            });
        });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "reserved historical item",
            Prompt = "p",
        };
        await factory.Store.CreateAsync(item);
        var costs = configuredFactory.Services.GetRequiredService<IWorkItemCostStore>();
        await costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            AgentKind = "claude",
            ModelId = null,
            InputTokens = 80_000,
            OutputTokens = 20_000,
            EstimatedUsd = 1.0,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RawMetadataJson = """{"usageSource":"provider_metadata"}""",
        });

        var manager = configuredFactory.Services.GetRequiredService<IQuotaHeadroomManager>();
        var gate = await manager.TryReserveAsync(new QuotaHeadroomGateRequest(
            item.ProjectId,
            new AgentMembership
            {
                Agent = AgentKind.Claude,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            },
            AvailablePct: 25,
            ResetAt: null,
            AuditOnRefusal: false));
        Assert.True(gate.Allow, gate.Reason);
        var lease = Assert.IsAssignableFrom<IQuotaReservationLease>(gate.Reservation);

        var client = configuredFactory.CreateClient();
        var response = await client.GetAsync("/quota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var projection = doc.RootElement
            .GetProperty("probes")[0]
            .GetProperty("headroomProjections")
            .EnumerateArray()
            .Single(p => p.GetProperty("projectId").GetString() == "test-project");

        Assert.Equal(10, projection.GetProperty("reservedQuotaPct").GetDouble(), precision: 2);
        Assert.Equal(5, projection.GetProperty("projectedAvailablePct").GetDouble(), precision: 2);
        Assert.False(projection.GetProperty("wouldAllow").GetBoolean());

        await lease.ReleaseAsync(quotaMayHaveBeenConsumed: false);
    }
}
