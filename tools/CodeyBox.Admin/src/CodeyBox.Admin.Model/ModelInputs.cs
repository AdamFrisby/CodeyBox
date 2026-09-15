namespace CodeyBox.Admin.Model;

/// <summary>
/// Minimal work-item shape the projection layer reasons about.
/// Mirrors the orchestrator's <c>GET /workitems</c> response fields the layer
/// needs; the admin web app maps its local DTOs onto this record at the edge.
/// All collections are treated as untrusted input: null-tolerant and
/// length-bounded by the consumer before buffering (see <see cref="FleetSnapshot"/>).
/// </summary>
public sealed record AdminWorkItem
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Orchestrator lifecycle state name (e.g. "Queued", "Working", "Done").</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Canonical agent name (e.g. "Claude", "Codex").</summary>
    public string Agent { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Direct dependency ids (the <c>DependsOn</c> edge list).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Orchestrator-computed gate bit. Only consulted for dependency ids that
    /// are absent from the snapshot (e.g. a filtered view); ids present in
    /// the snapshot are re-evaluated from their states so the same inputs
    /// always produce the same screen.
    /// </summary>
    public bool DependsOnSatisfied { get; init; } = true;
}

/// <summary>Per-agent availability as observed by the existing surfaces.</summary>
public sealed record AdminAgentStatus
{
    public required string Agent { get; init; }

    /// <summary>True when an operator pause covers this agent.</summary>
    public bool Paused { get; init; }

    public string? PauseReason { get; init; }

    /// <summary>
    /// False when the agent is benched by a non-pause signal (smoke gate,
    /// fast-fail breaker, missing probe). Null means unknown — never treated
    /// as unavailable.
    /// </summary>
    public bool? Available { get; init; }

    public string? UnavailableReason { get; init; }

    /// <summary>Remaining quota 0–100, when the quota surface reported it.</summary>
    public int? QuotaAvailablePct { get; init; }

    public DateTimeOffset? QuotaResetAt { get; init; }
}

/// <summary>Worker-slot capacity as reported by the existing surfaces.</summary>
public sealed record AdminWorkerCapacity
{
    public int GlobalMaxConcurrent { get; init; }

    public int GlobalRunning { get; init; }

    /// <summary>Per-agent concurrency caps (agent name → cap).</summary>
    public IReadOnlyDictionary<string, int> PerAgentCaps { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-agent running counts (agent name → running).</summary>
    public IReadOnlyDictionary<string, int> PerAgentRunning { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Recent per-vital history series, oldest sample first. The admin retains
/// these from its own polling loop (one value per projection); the model only
/// bands the current value against them and never fetches anything itself.
/// </summary>
public sealed record VitalHistories
{
    public IReadOnlyList<double> QueueDepth { get; init; } = [];
    public IReadOnlyList<double> InFlight { get; init; } = [];
    public IReadOnlyList<double> FailureRate { get; init; } = [];
    public IReadOnlyList<double> ThroughputPerHour { get; init; } = [];
    public IReadOnlyList<double> Parked { get; init; } = [];
}

/// <summary>
/// The complete input to the projection layer. A pure function of
/// (items, agents, workers, quota, now): no I/O, no cache reads, no clock.
/// The same snapshot always produces the same screen.
/// </summary>
public sealed record FleetSnapshot
{
    /// <summary>Upper bound on items accepted; input beyond it is ignored.</summary>
    public const int MaxItems = 10_000;

    public required DateTimeOffset Now { get; init; }

    public IReadOnlyList<AdminWorkItem> Items { get; init; } = [];

    public IReadOnlyList<AdminAgentStatus> Agents { get; init; } = [];

    public AdminWorkerCapacity Workers { get; init; } = new();

    public VitalHistories History { get; init; } = new();
}
