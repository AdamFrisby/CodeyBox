namespace CodeyBox.Core;

/// <summary>
/// Provider-agnostic placement attributes for one sandbox host candidate.
/// Carries exactly what <see cref="ExecutorPlacement"/> and
/// <see cref="ExecutorEligibility"/> need and nothing else, so anything that
/// is not an executor host registration — a local sandbox provider, a remote
/// provider's host entry, an E2E lease target — can reuse the decider without
/// modelling an <see cref="ExecutorRegistration"/>.
/// </summary>
public sealed record SandboxPlacementMember
{
    /// <summary>Stable member id (the host id for executor registrations).</summary>
    public required string MemberId { get; init; }

    /// <summary>
    /// Host-local sandbox capacity. Null means uncapped. Zero means the member
    /// registers but is never selected (see <see cref="ExecutorEligibility"/>).
    /// </summary>
    public int? MaxConcurrentSandboxes { get; init; }

    /// <summary>
    /// Logical network profiles this member accepts. Empty (or a "*" entry)
    /// accepts every profile; otherwise matched by exact ordinal equality,
    /// never substring.
    /// </summary>
    public IReadOnlyList<string> NetworkProfiles { get; init; } = [];

    /// <summary>
    /// Names of the agent credential sets this member holds, matched by exact
    /// ordinal equality — never by substring.
    /// </summary>
    public IReadOnlyList<string> Credentials { get; init; } = [];

    /// <summary>
    /// Clearance tags this member is trusted to handle, matched ordinal,
    /// case-insensitive per tag. An empty required set is covered by any member.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// When true the member is draining: it registers and heartbeats but is
    /// never selected for new placements.
    /// </summary>
    public bool Cordoned { get; init; }

    /// <summary>
    /// Operator health gate. False routes new placements away without removing
    /// the registration.
    /// </summary>
    public bool Healthy { get; init; } = true;

    /// <summary>
    /// Projects an executor registration to its placement attributes.
    /// <see cref="ExecutorRegistration"/> keeps its existing shape; this is an
    /// adapter, not a migration.
    /// </summary>
    public static SandboxPlacementMember FromExecutorRegistration(ExecutorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new SandboxPlacementMember
        {
            MemberId = registration.HostId,
            MaxConcurrentSandboxes = registration.MaxConcurrentSandboxes,
            NetworkProfiles = registration.AllowedNetworkProfiles,
            Credentials = registration.DeclaredCredentials,
            Capabilities = registration.DeclaredCapabilities,
            Cordoned = registration.Cordoned,
            Healthy = registration.Healthy,
        };
    }
}
