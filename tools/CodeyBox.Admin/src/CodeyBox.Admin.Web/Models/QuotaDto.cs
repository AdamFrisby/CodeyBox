namespace CodeyBox.Admin.Web.Models;

/// <summary>Response shape for GET /quota (subset the admin UI consumes).</summary>
public sealed class QuotaReportDto
{
    public DateTimeOffset GeneratedAt { get; set; }
    public int MinQuotaPct { get; set; }
    public string? UnknownPolicy { get; set; }
    public string? IntraKindRoutingPolicy { get; set; }
    public List<QuotaProbeDto> Probes { get; set; } = [];
    public List<QuotaKindAggregateDto> KindAggregates { get; set; } = [];
}

public sealed class QuotaProbeDto
{
    public string Agent { get; set; } = "";
    public string? AgentInstanceId { get; set; }
    public string? ClassId { get; set; }
    public string? ClassDisplayName { get; set; }
    public string? Billing { get; set; }
    public string? ModelId { get; set; }
    public QuotaSnapshotDto? LatestSnapshot { get; set; }
}

public sealed class QuotaSnapshotDto
{
    public int AvailablePct { get; set; }
    public bool? IsKnown { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class QuotaKindAggregateDto
{
    public string Agent { get; set; } = "";
    public int Instances { get; set; }
}

/// <summary>Response shape for GET /workers/status.</summary>
public sealed class WorkersStatusDto
{
    public int MaxConcurrent { get; set; }
    public int CurrentlyRunning { get; set; }
    public int QueuedCount { get; set; }
    public DateTimeOffset? LastSpawnAt { get; set; }
}

/// <summary>Response shape for GET /concurrency (subset).</summary>
public sealed class ConcurrencyDto
{
    public int GlobalMaxConcurrent { get; set; }
    public int CurrentlyRunningTotal { get; set; }
    public Dictionary<string, int> PerAgentCaps { get; set; } = [];
    public Dictionary<string, int> CurrentlyRunningPerAgent { get; set; } = [];
}
