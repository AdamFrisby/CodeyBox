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
}

/// <summary>A single agent option within an <see cref="AgentClass"/>.</summary>
public sealed record AgentMembership
{
    public required AgentKind Agent { get; init; }
    public required AgentBilling Billing { get; init; }

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
