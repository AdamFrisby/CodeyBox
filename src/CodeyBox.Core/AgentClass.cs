namespace CodeyBox.Core;

/// <summary>
/// A named group of interchangeable agents. When a work item requests a class,
/// the router picks the member with the highest effective quality score that is
/// eligible (covers the work item's <see cref="WorkItem.RequiredCapabilities"/>
/// AND meets the legacy <see cref="WorkItem.MinModelScore"/> floor during the
/// transition window), then probes quota; it waits if all subscription-billed
/// eligible members are exhausted.
/// </summary>
public sealed record AgentClass
{
    /// <summary>Stable identifier, e.g. "frontier-coding".</summary>
    public required string Id { get; init; }

    /// <summary>Human label, e.g. "Frontier coding (≈Claude 4.7 / Codex 5.5)".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Members of this class. List order is only a last-resort tiebreaker when
    /// two members have identical effective scores after TOD modifiers; selection
    /// is driven by <see cref="AgentMembership.QualityScore"/>, not position.
    /// </summary>
    public required IReadOnlyList<AgentMembership> Members { get; init; }

    /// <summary>
    /// Optional class-level opt-in for the resumable Claude session pipeline.
    /// Null or <c>Enabled=false</c> keeps members on the legacy per-phase
    /// sandbox path unless an individual member overrides it.
    /// </summary>
    public AgentClassClaudeSessionConfig? ClaudeSession { get; init; }
}

/// <summary>A single agent option within an <see cref="AgentClass"/>.</summary>
public sealed record AgentMembership
{
    public required AgentKind Agent { get; init; }
    public required AgentBilling Billing { get; init; }

    /// <summary>
    /// Optional stable instance identifier for this membership. Null means the
    /// default single instance for <see cref="Agent"/>; named instances route as
    /// <c>{agent}/{instance}</c> via <see cref="RouteKey"/>.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// Per-instance credential source. When null, the legacy per-kind
    /// credential chain is used.
    /// </summary>
    public AgentCredentialReference? CredentialReference { get; init; }

    /// <summary>
    /// Stable routing/accounting key for this member. Default legacy members
    /// return the bare agent kind (for example <c>claude</c>); named members
    /// return <c>claude/acct-a</c>.
    /// </summary>
    public string RouteKey => AgentInstanceIds.RouteKey(Agent, InstanceId);

    /// <summary>
    /// Optional model override, e.g. "claude-opus-4-7", "codex-5.5". When set,
    /// the orchestrator passes <c>--model &lt;ModelId&gt;</c> to the agent CLI.
    /// Null means the agent uses its own default. Copilot ignores this field
    /// (its CLI does not expose a --model flag).
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Operator-curated capability score on a roughly 0–100 scale. Higher = more
    /// capable. Equality means "interchangeable for this work" — the router will
    /// swap freely between them. Recommended seed values:
    ///   Opus 4.7 = 100, GPT-5.5 = 100      (frontier, tied)
    ///   Gemini 3 Flash (high reasoning) = 95  (frontier-adjacent)
    ///   Sonnet 4.6 = 80, GPT-5 base = 80
    ///   Gemini 3 Flash (standard) = 70
    ///   Haiku = 50, mini variants = 50
    /// These are operator-tunable in config; the framework ships sensible
    /// defaults but does not pin them in code.
    /// </summary>
    public required int QualityScore { get; init; }

    /// <summary>
    /// Agent-CLI-specific reasoning-effort knob. The runner translates this into
    /// the right CLI flag (e.g. <c>--thinking</c> on gemini). For Gemini
    /// specifically: a score of >= 90 REQUIRES <c>ReasoningMode="high"</c> —
    /// config validation rejects Gemini-90+-without-high-reasoning at startup.
    /// </summary>
    public string? ReasoningMode { get; init; }

    /// <summary>
    /// Operator-declared clearance tags this member is trusted to handle, e.g.
    /// <c>"sensitive"</c>, <c>"architectural"</c>, <c>"security"</c>. Distinct from
    /// <see cref="QualityScore"/>: capabilities are an explicit trust/clearance gate
    /// (which models may touch the work), whereas QualityScore is a routing PREFERENCE
    /// (which eligible model is strongest).
    /// <para>
    /// The router treats a work item with
    /// <see cref="WorkItem.RequiredCapabilities"/> set as eligible-on-this-member only
    /// when this list contains every required tag. Members with no declared tags can
    /// still run any item whose required set is empty (open-by-default).
    /// </para>
    /// Tag comparison is ordinal, case-insensitive. Default empty.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Optional member-level override for the resumable Claude session pipeline.
    /// Null inherits the containing class setting; <c>Enabled=false</c> opts
    /// this member out even when the class is enabled.
    /// </summary>
    public AgentClassClaudeSessionConfig? ClaudeSession { get; init; }
}

/// <summary>
/// Agent-class/member opt-in for the Claude session worker. The generic
/// routing model carries the flag so the orchestrator can decide without
/// referencing a concrete Claude implementation assembly.
/// </summary>
public sealed record AgentClassClaudeSessionConfig
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Framework-defined capability tag names with built-in routing meaning.
/// Operators still declare which members carry them in
/// <c>CodeyBox:AgentClasses[].Members[].Capabilities</c> — the framework
/// never hardcodes which agent kinds get tagged. Free-form tags
/// (<c>sensitive</c>, <c>architectural</c>, etc.) live alongside these.
/// </summary>
public static class WellKnownCapabilities
{
    /// <summary>
    /// Marks a class member as eligible to run the audit phase. When AT LEAST
    /// ONE member of the routed class carries this tag, the audit phase is
    /// restricted to tagged members ("opt-in pool"); a non-tagged member is
    /// NEVER picked for auditing — including the project's
    /// <see cref="ProjectAudit.AuditAgent"/> preference and the work agent.
    /// When NO class member carries the tag, the audit phase falls back to
    /// the legacy "any agent is allowed" routing for backward compatibility.
    /// </summary>
    public const string Audit = "audit";
}

/// <summary>How the agent is billed, which determines quota-wait behaviour.</summary>
public enum AgentBilling
{
    /// <summary>
    /// Subscription / quota-bound (e.g. Claude Pro, Codex Plus). The orchestrator
    /// probes quota before firing and waits if available percentage is below
    /// MinQuotaPct.
    /// </summary>
    Subscription,

    /// <summary>
    /// Pure pay-per-API call. Exceeding usage just costs money — it never causes
    /// the call to fail. The orchestrator never waits for PayPerApi members.
    /// </summary>
    PayPerApi,
}

/// <summary>
/// A configured credential source for one routable agent instance. Exactly how
/// the source is materialized is agent-specific: file-based CLIs receive the
/// file JSON through their existing sandbox env vars, token-based CLIs receive
/// the selected token under the CLI's expected env var.
/// </summary>
public sealed record AgentCredentialReference
{
    /// <summary>Host file containing OAuth/auth JSON for this instance.</summary>
    public string? FilePath { get; init; }

    /// <summary>Host env var containing a raw access token or API key.</summary>
    public string? TokenEnvironmentVariable { get; init; }

    /// <summary>Host env var containing CLI auth JSON for this instance.</summary>
    public string? AuthJsonEnvironmentVariable { get; init; }

    /// <summary>Optional companion settings file, used by Gemini OAuth.</summary>
    public string? SettingsFilePath { get; init; }

    /// <summary>Optional sandbox destination path for file-materializing runners.</summary>
    public string? DestinationPath { get; init; }

    /// <summary>Optional override for the sandbox env var used for token injection.</summary>
    public string? SandboxEnvironmentVariable { get; init; }

    public bool HasAnyReference =>
        !string.IsNullOrWhiteSpace(FilePath)
        || !string.IsNullOrWhiteSpace(TokenEnvironmentVariable)
        || !string.IsNullOrWhiteSpace(AuthJsonEnvironmentVariable)
        || !string.IsNullOrWhiteSpace(SettingsFilePath)
        || !string.IsNullOrWhiteSpace(DestinationPath)
        || !string.IsNullOrWhiteSpace(SandboxEnvironmentVariable);
}

/// <summary>Helpers for stable agent instance route keys.</summary>
public static class AgentInstanceIds
{
    public static string RouteKey(AgentKind kind, string? instanceId)
    {
        var agent = kind.Value.Trim();
        var id = NormalizeInstanceId(instanceId);
        if (id is null)
            return agent;

        return id.Contains('/', StringComparison.Ordinal)
            ? id
            : $"{agent}/{id}";
    }

    public static string? NormalizeInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return null;
        return instanceId.Trim();
    }

    public static string KindFromRouteKey(string routeKey)
    {
        var trimmed = routeKey.Trim();
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    public static bool Matches(AgentMembership member, string? routeKeyOrInstanceId)
    {
        if (string.IsNullOrWhiteSpace(routeKeyOrInstanceId))
            return false;

        var candidate = routeKeyOrInstanceId.Trim();
        return string.Equals(member.RouteKey, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(member.InstanceId, candidate, StringComparison.OrdinalIgnoreCase)
            || (candidate.Contains('/', StringComparison.Ordinal)
                && string.Equals(RouteKey(member.Agent, candidate), member.RouteKey, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Convenience helpers for inspecting <see cref="AgentMembership"/> capability tags.</summary>
public static class AgentMembershipExtensions
{
    /// <summary>
    /// Returns true when <paramref name="member"/> declares <paramref name="capability"/>
    /// in its <see cref="AgentMembership.Capabilities"/> list. Ordinal,
    /// case-insensitive — matches the comparison the router uses.
    /// </summary>
    public static bool HasCapability(this AgentMembership member, string capability)
    {
        if (string.IsNullOrEmpty(capability)) return false;
        foreach (var tag in member.Capabilities)
        {
            if (string.Equals(tag, capability, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
