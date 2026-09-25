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

    /// <summary>Builds the policy record the authorization gate consumes.</summary>
    public MajordomoOptions ToPolicy() => new()
    {
        Mode = Mode,
        MaxMutatedItemsPerTurn = MaxMutatedItemsPerTurn,
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
        if (opts.TurnWindowSeconds is < 1 or > MaxTurnWindowSeconds)
            return $"{SectionName}:TurnWindowSeconds must be within [1, {MaxTurnWindowSeconds}]";
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
