using CodeyBox.Majordomo;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Operator configuration for the majordomo MCP endpoint, bound from the
/// <c>CodeyBox:Majordomo</c> section and hot-reloadable: the executor reads
/// the current value on every call so a policy change applies to the next
/// tool call without a restart.
/// </summary>
public sealed class MajordomoServerOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "CodeyBox:Majordomo";

    /// <summary>
    /// The configured <c>CodeyBox:ApiClients</c> entry name that owns the
    /// majordomo's credential. Only requests authenticated as that client may
    /// reach the MCP endpoint — the operator key and other named clients are
    /// refused — so majordomo calls are attributable and the credential is
    /// revocable independently of the operator's key.
    /// </summary>
    public const string DefaultClientName = "majordomo";

    /// <summary>
    /// Default span over which per-identity mutations accumulate toward the
    /// turn cap. A majordomo turn is a burst of calls from one identity; the
    /// window bounds that burst without trusting a caller-supplied turn id.
    /// </summary>
    public const int DefaultTurnWindowSeconds = 120;

    /// <summary>Largest accepted <see cref="TurnWindowSeconds"/>.</summary>
    public const int MaxTurnWindowSeconds = 3600;

    /// <summary>Default proposal time-to-live in seconds (24 hours).</summary>
    public static readonly int DefaultProposalTimeToLiveSeconds =
        (int)MajordomoOptions.DefaultProposalTimeToLive.TotalSeconds;

    /// <summary>Shortest accepted proposal time-to-live in seconds (1 minute).</summary>
    public static readonly int MinProposalTimeToLiveSeconds =
        (int)MajordomoOptions.MinProposalTimeToLive.TotalSeconds;

    /// <summary>Longest accepted proposal time-to-live in seconds (30 days).</summary>
    public static readonly int MaxProposalTimeToLiveSeconds =
        (int)MajordomoOptions.MaxProposalTimeToLive.TotalSeconds;

    /// <summary>Shortest accepted decided-proposal retention in seconds (1 minute).</summary>
    public static readonly int MinDecidedProposalRetentionSeconds =
        (int)MajordomoOptions.MinDecidedProposalRetention.TotalSeconds;

    /// <summary>Longest accepted decided-proposal retention in seconds (90 days).</summary>
    public static readonly int MaxDecidedProposalRetentionSeconds =
        (int)MajordomoOptions.MaxDecidedProposalRetention.TotalSeconds;

    /// <summary>Default decided-proposal retention in seconds (7 days).</summary>
    public static readonly int DefaultDecidedProposalRetentionSeconds =
        (int)MajordomoOptions.DefaultDecidedProposalRetention.TotalSeconds;

    /// <summary>
    /// Whether MUTATE tools execute immediately or produce operator
    /// proposals. Mirrors <see cref="MajordomoOptions.Mode"/>; defaults to
    /// <see cref="MajordomoAutonomyMode.Proposed"/>.
    /// </summary>
    public MajordomoAutonomyMode Mode { get; set; } = MajordomoAutonomyMode.Proposed;

    /// <summary>
    /// Maximum number of work items mutated per majordomo turn, cumulative
    /// across calls. Mirrors <see cref="MajordomoOptions.MaxMutatedItemsPerTurn"/>.
    /// </summary>
    public int MaxMutatedItemsPerTurn { get; set; } = MajordomoOptions.DefaultMaxMutatedItemsPerTurn;

    /// <summary>
    /// Name of the <c>CodeyBox:ApiClients</c> entry whose bearer token is the
    /// majordomo credential.
    /// </summary>
    public string ClientName { get; set; } = DefaultClientName;

    /// <summary>
    /// The span over which mutations from one identity accumulate toward
    /// <see cref="MaxMutatedItemsPerTurn"/>. Between 1 and
    /// <see cref="MaxTurnWindowSeconds"/> seconds.
    /// </summary>
    public int TurnWindowSeconds { get; set; } = DefaultTurnWindowSeconds;

    /// <summary>
    /// How long a queued proposal stays approvable, in seconds. Approving
    /// past the deadline is refused and the proposal is marked expired, so a
    /// stale queue cannot be approved long after the reasoning behind it
    /// stopped being true. Between <see cref="MinProposalTimeToLiveSeconds"/>
    /// and <see cref="MaxProposalTimeToLiveSeconds"/> seconds. Mirrors
    /// <see cref="MajordomoOptions.ProposalTimeToLive"/>.
    /// </summary>
    public int ProposalTimeToLiveSeconds { get; set; } = DefaultProposalTimeToLiveSeconds;

    /// <summary>
    /// Maximum proposals awaiting an operator decision at once — pending rows
    /// plus claimed commits still in <c>applying</c>. Enqueue refuses past
    /// the cap so the proposal table cannot grow at request rate. Between 1
    /// and <see cref="MajordomoOptions.MaxAllowedPendingProposals"/>. Mirrors
    /// <see cref="MajordomoOptions.MaxPendingProposals"/>.
    /// </summary>
    public int MaxPendingProposals { get; set; } = MajordomoOptions.DefaultMaxPendingProposals;

    /// <summary>
    /// How long a decided (approved, rejected, expired, superseded) proposal
    /// row is retained for review before the enqueue sweep removes it, in
    /// seconds. Undecided rows are never reaped by retention. Between
    /// <see cref="MinDecidedProposalRetentionSeconds"/> and
    /// <see cref="MaxDecidedProposalRetentionSeconds"/> seconds. Mirrors
    /// <see cref="MajordomoOptions.DecidedProposalRetention"/>.
    /// </summary>
    public int DecidedProposalRetentionSeconds { get; set; } = DefaultDecidedProposalRetentionSeconds;

    /// <summary>Builds the policy record the authorization gate consumes.</summary>
    public MajordomoOptions ToPolicy() => new()
    {
        Mode = Mode,
        MaxMutatedItemsPerTurn = MaxMutatedItemsPerTurn,
        ProposalTimeToLive = TimeSpan.FromSeconds(ProposalTimeToLiveSeconds),
        MaxPendingProposals = MaxPendingProposals,
        DecidedProposalRetention = TimeSpan.FromSeconds(DecidedProposalRetentionSeconds),
    };

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoServerOptions opts)
    {
        if (!Enum.IsDefined(opts.Mode))
            return $"{SectionName}:Mode must be a defined {nameof(MajordomoAutonomyMode)}";
        if (opts.MaxMutatedItemsPerTurn < 1 || opts.MaxMutatedItemsPerTurn > MajordomoOptions.MaxAllowedMutatedItemsPerTurn)
            return $"{SectionName}:MaxMutatedItemsPerTurn must be within [1, {MajordomoOptions.MaxAllowedMutatedItemsPerTurn}]";
        if (string.IsNullOrWhiteSpace(opts.ClientName) || opts.ClientName.Any(char.IsControl))
            return $"{SectionName}:ClientName must be a non-empty ApiClients entry name";
        // The auth-disabled sentinel is reserved: pointing ClientName at it
        // would confine the loopback operator itself to the MCP route.
        if (string.Equals(opts.ClientName, ApiKeyAuth.AuthenticationDisabledClientName, StringComparison.Ordinal))
            return $"{SectionName}:ClientName '{ApiKeyAuth.AuthenticationDisabledClientName}' is reserved";
        if (opts.TurnWindowSeconds is < 1 or > MaxTurnWindowSeconds)
            return $"{SectionName}:TurnWindowSeconds must be within [1, {MaxTurnWindowSeconds}]";
        if (opts.ProposalTimeToLiveSeconds < MinProposalTimeToLiveSeconds
            || opts.ProposalTimeToLiveSeconds > MaxProposalTimeToLiveSeconds)
            return $"{SectionName}:ProposalTimeToLiveSeconds must be within [{MinProposalTimeToLiveSeconds}, {MaxProposalTimeToLiveSeconds}]";
        if (opts.MaxPendingProposals < 1 || opts.MaxPendingProposals > MajordomoOptions.MaxAllowedPendingProposals)
            return $"{SectionName}:MaxPendingProposals must be within [1, {MajordomoOptions.MaxAllowedPendingProposals}]";
        if (opts.DecidedProposalRetentionSeconds < MinDecidedProposalRetentionSeconds
            || opts.DecidedProposalRetentionSeconds > MaxDecidedProposalRetentionSeconds)
            return $"{SectionName}:DecidedProposalRetentionSeconds must be within [{MinDecidedProposalRetentionSeconds}, {MaxDecidedProposalRetentionSeconds}]";
        return null;
    }
}

/// <summary>
/// Options validator preserving <see cref="MajordomoServerOptions.Validate"/>'s
/// per-rule failure text — registered via <c>IValidateOptions</c> so the
/// operator sees the actual fault, and honoured by <c>ValidateOnStart</c> so
/// a bad mode/cap fails the host at startup, not at the first tool call.
/// </summary>
internal sealed class MajordomoServerOptionsValidator : IValidateOptions<MajordomoServerOptions>
{
    public ValidateOptionsResult Validate(string? name, MajordomoServerOptions options)
    {
        var failure = MajordomoServerOptions.Validate(options);
        return failure is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failure);
    }
}
