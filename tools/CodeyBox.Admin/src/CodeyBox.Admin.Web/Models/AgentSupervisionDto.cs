namespace CodeyBox.Admin.Web.Models;

public sealed class AgentSupervisionSessionsResponse
{
    public bool Enabled { get; set; }
    public List<AgentSupervisionSessionDto> Sessions { get; set; } = [];
}

public sealed class AgentSupervisionSessionDto
{
    public string SessionId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Iteration { get; set; }
    public string Agent { get; set; } = "";
    public string? AgentInstanceId { get; set; }
    public string? ModelId { get; set; }
    public string? ReasoningMode { get; set; }
    public string SandboxId { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string State { get; set; } = "";
    public bool AcceptingInjections { get; set; }
    public int PendingInjections { get; set; }
    public string OutputTail { get; set; } = "";
    public List<AgentSupervisionCommandRecordDto> RecentCommands { get; set; } = [];
}

public sealed class AgentSupervisionCommandRecordDto
{
    public string Kind { get; set; } = "";
    public string? InjectionId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string Prompt { get; set; } = "";
}

public sealed class AgentSupervisionInjectionRequestDto
{
    public string Message { get; set; } = "";
    public string? Actor { get; set; }
}

public sealed class AgentSupervisionInjectionReceiptDto
{
    public bool Accepted { get; set; }
    public string Status { get; set; } = "";
    public string? InjectionId { get; set; }
    public string? Error { get; set; }
}

public sealed class AgentSupervisionCommandEventDto
{
    public string SessionId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? InjectionId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string Prompt { get; set; } = "";
}

public sealed class AgentSupervisionStdoutEventDto
{
    public string SessionId { get; set; } = "";
    public string Chunk { get; set; } = "";
}

public sealed class AgentSupervisionInjectionCompletedEventDto
{
    public string SessionId { get; set; } = "";
    public string InjectionId { get; set; } = "";
    public bool Success { get; set; }
    public string Summary { get; set; } = "";
}
