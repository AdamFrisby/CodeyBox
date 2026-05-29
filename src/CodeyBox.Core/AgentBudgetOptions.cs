namespace CodeyBox.Core;

/// <summary>
/// Kind of budget window. <see cref="Rolling"/> is a sliding window of N hours;
/// <see cref="Weekly"/> and <see cref="Monthly"/> are calendar windows (ISO week
/// starting Monday 00:00 UTC, and calendar month starting on the 1st 00:00 UTC)
/// to match how subscription providers phrase their limits.
/// </summary>
public enum BudgetWindowKind
{
    Rolling,
    Weekly,
    Monthly,
}

/// <summary>
/// Operator-configurable multi-window spend budget per (agent, model). Bound
/// under <c>CodeyBox:AgentBudgets</c> and hot-reloadable. See
/// <c>docs/agent-budgets.md</c> for the accounting caveats (this orchestrator
/// only counts what it dispatched; size budgets below the provider's real cap).
/// <para>
/// Lives in Core alongside <see cref="IAgentBudgetProvider"/> so host
/// configuration surfaces (e.g. <c>CodeyBoxOptions.AgentBudgets</c>) bind against
/// a Core contract rather than an Orchestrator implementation type.
/// </para>
/// </summary>
public sealed class AgentBudgetOptions
{
    /// <summary>How many days of <c>agent_usage_events</c> to retain before pruning. Default 90.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Per-agent-kind budget config. Key = agent kind (e.g. "opencode").</summary>
    public Dictionary<string, AgentBudgetMemberOptions> Members { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentBudgetMemberOptions
{
    /// <summary>Per-model budget config. Key = model id (e.g. "opencode-go/deepseek-v4-pro").</summary>
    public Dictionary<string, AgentBudgetModelOptions> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentBudgetModelOptions
{
    public List<AgentBudgetWindowOptions> Windows { get; set; } = [];
}

public sealed class AgentBudgetWindowOptions
{
    public BudgetWindowKind Kind { get; set; }

    /// <summary>Window length in hours; required for <see cref="BudgetWindowKind.Rolling"/>, ignored otherwise.</summary>
    public int? Hours { get; set; }

    /// <summary>Spend cap for this window, in cents (1 cent = 10000 microcents).</summary>
    public double LimitCents { get; set; }
}
