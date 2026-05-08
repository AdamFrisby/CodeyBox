namespace CodeyBox.Admin.Web.Models;

public sealed class AgentStreamAggregateDto
{
    public string? WorkItemId { get; set; }
    public long TotalAgentDurationMs { get; set; }
    public int TotalToolCalls { get; set; }
    public List<AgentStreamToolAggregateDto> ByTool { get; set; } = [];
    public long ThinkingMs { get; set; }
    public long ExecutingMs { get; set; }
    public int StallCount { get; set; }
    public long LongestStallMs { get; set; }
    public decimal EstimatedUsdTotal { get; set; }
    public List<AgentStreamSlowToolCallDto> SlowestToolCalls { get; set; } = [];
    public List<AgentStreamInvocationDto> Invocations { get; set; } = [];
}

public sealed class AgentStreamToolAggregateDto
{
    public string Tool { get; set; } = "";
    public int Count { get; set; }
    public long TotalDurationMs { get; set; }
    public long MedianMs { get; set; }
}

public sealed class AgentStreamInvocationDto
{
    public string FileName { get; set; } = "";
    public string? Phase { get; set; }
    public int? Iteration { get; set; }
    public string AgentKind { get; set; } = "";
    public long TotalDurationMs { get; set; }
    public long? TimeToFirstTokenMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public decimal? EstimatedUsd { get; set; }
    public List<AgentStreamToolCallDto> ToolCalls { get; set; } = [];
    public List<AgentStreamStallDto> Stalls { get; set; } = [];
    public string? FinalAssistantMessage { get; set; }
}

public sealed class AgentStreamToolCallDto
{
    public string ToolUseId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string InputSummary { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public bool? Succeeded { get; set; }
    public int OutputBytes { get; set; }
}

public sealed class AgentStreamSlowToolCallDto
{
    public string WorkItemId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Phase { get; set; }
    public int? Iteration { get; set; }
    public string ToolUseId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string InputSummary { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public bool? Succeeded { get; set; }
    public int OutputBytes { get; set; }
}

public sealed class AgentStreamStallDto
{
    public DateTimeOffset DetectedAt { get; set; }
    public long GapDurationMs { get; set; }
    public string? PreviousEventType { get; set; }
    public string? NextEventType { get; set; }
    public string Classification { get; set; } = "";
}
