namespace CodeyBox.Admin.Web.Models;

/// <summary>Response shape for GET /stats/capacity (subset the admin UI consumes).</summary>
public sealed class CapacityReportDto
{
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public List<CapacityEntryDto> Entries { get; set; } = [];
}

public sealed class CapacityEntryDto
{
    public string Agent { get; set; } = "";
    public string WindowName { get; set; } = "";
    public string? ModelId { get; set; }
    public int SampleIntervals { get; set; }
    public double TotalDeltaPct { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalCachedInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalRequests { get; set; }
    public long TotalCostMicroCents { get; set; }
    public double? InputTokensPerPercent { get; set; }
    public double? CachedInputTokensPerPercent { get; set; }
    public double? OutputTokensPerPercent { get; set; }
    public double? RequestsPerPercent { get; set; }
    public double? EstimatedFullWindowInputTokens { get; set; }
    public double? EstimatedFullWindowCachedInputTokens { get; set; }
    public double? EstimatedFullWindowOutputTokens { get; set; }
    public double? EstimatedFullWindowRequests { get; set; }
    public double? CurrentPct { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public DateTimeOffset? EstimatedExhaustionAt { get; set; }
    public string Confidence { get; set; } = "None";
    public List<string> Notes { get; set; } = [];
    public List<CapacityIntervalDto> Intervals { get; set; } = [];
}

public sealed class CapacityIntervalDto
{
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public double DeltaPct { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long Requests { get; set; }
    public long CostMicroCents { get; set; }
    public bool IsWindowReset { get; set; }
}
