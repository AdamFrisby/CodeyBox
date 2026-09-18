namespace CodeyBox.Core;

/// <summary>
/// A named group of interchangeable sandbox hosts. When a work item needs a
/// sandbox, the placement decider picks the member with the highest
/// <see cref="SandboxMember.PreferenceScore"/> that is eligible (covers the
/// work item's <see cref="WorkItem.RequiredCapabilities"/> and accepts its
/// network profile and credential needs), then spills to the next member when
/// one is at its cap, and defers when all are exhausted.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AgentClass"/> deliberately: sandbox capacity is modelled
/// with the same vocabulary (class, members, preference score, capability
/// tags) so operators do not learn a second one. The demand signal is the
/// work item; this type is the supply side it matches against.
/// </remarks>
public sealed record SandboxClass
{
    /// <summary>Stable identifier, e.g. "default".</summary>
    public required string Id { get; init; }

    /// <summary>Human label, e.g. "Default pool (local first, remote spillover)".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Members of this class. List order is only a last-resort tiebreaker when
    /// two members have identical <see cref="SandboxMember.PreferenceScore"/>
    /// values; selection is driven by score, not position.
    /// </summary>
    public required IReadOnlyList<SandboxMember> Members { get; init; }
}

/// <summary>A single sandbox host option within a <see cref="SandboxClass"/>.</summary>
public sealed record SandboxMember
{
    /// <summary>
    /// Stable member identifier, e.g. "local-incus". Unique within the
    /// containing class; surfaced verbatim in validation errors and placement
    /// decisions.
    /// </summary>
    public required string MemberId { get; init; }

    /// <summary>
    /// Sandbox provider kind backing this member, e.g. "incus", "multipass".
    /// Must name a registered provider kind; anything else fails closed at
    /// load. Normalised to lowercase at config bind time.
    /// </summary>
    public required string ProviderKind { get; init; }

    /// <summary>
    /// Optional host id disambiguating members that share a provider kind
    /// (e.g. two remote hosts behind "multipass-remote"). Null means the
    /// provider's default host.
    /// </summary>
    public string? HostId { get; init; }

    /// <summary>
    /// Maximum concurrent sandboxes this member may hold. Must be positive,
    /// and — because a worker holds its work sandbox while acquiring a second
    /// sandbox for audit — at least twice the number of workers that can
    /// target this member, or the pipeline can deadlock with every permit
    /// held by a worker waiting for a second permit that never frees.
    /// Projects to <see cref="SandboxPlacementMember.MaxConcurrentSandboxes"/>.
    /// </summary>
    public required int Capacity { get; init; }

    /// <summary>
    /// Operator-declared clearance tags this member is trusted to handle,
    /// e.g. <c>"baseline-bake"</c>. Distinct from
    /// <see cref="PreferenceScore"/>: capabilities gate WHICH members are
    /// eligible, PreferenceScore ranks WHICH eligible member wins.
    /// Tag comparison is ordinal, case-insensitive. Default empty.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Logical network profiles this member accepts. Empty (or a "*" entry)
    /// accepts every profile; otherwise matched by exact ordinal equality,
    /// never substring. Projects verbatim to
    /// <see cref="SandboxPlacementMember.NetworkProfiles"/>.
    /// </summary>
    public IReadOnlyList<string> NetworkProfiles { get; init; } = [];

    /// <summary>
    /// Names of the agent credential sets this member holds, matched by exact
    /// ordinal equality — never by substring. Projects verbatim to
    /// <see cref="SandboxPlacementMember.Credentials"/>.
    /// </summary>
    public IReadOnlyList<string> Credentials { get; init; } = [];

    /// <summary>
    /// Operator-curated preference score on a roughly 0–100 scale. Higher =
    /// preferred. Equality means "interchangeable for this work" — the
    /// decider spills freely between tied members. The analogue of
    /// <see cref="AgentMembership.QualityScore"/>: its purpose is to express
    /// "prefer local, spill to remote" without hardcoding a ratio.
    /// </summary>
    public required int PreferenceScore { get; init; }

    /// <summary>
    /// Projects this member to the placement attributes the placement decider
    /// consumes. The decider stays the one that decides: provider routing
    /// (<see cref="ProviderKind"/>, <see cref="HostId"/>) and preference
    /// ranking (<see cref="PreferenceScore"/>) travel with the class catalog,
    /// not the placement member.
    /// </summary>
    public SandboxPlacementMember ToPlacementMember() =>
        new()
        {
            MemberId = MemberId,
            MaxConcurrentSandboxes = Capacity,
            NetworkProfiles = NetworkProfiles,
            Credentials = Credentials,
            Capabilities = Capabilities,
        };
}
