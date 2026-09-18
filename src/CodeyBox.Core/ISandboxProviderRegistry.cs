namespace CodeyBox.Core;

/// <summary>
/// One constructed sandbox provider, keyed by its provider kind (for example
/// <c>"incus"</c> or <c>"multipass"</c>). The composition root builds each
/// configured kind once; members that name the same kind share the instance.
/// </summary>
/// <param name="Kind">Normalised provider kind, lowercase.</param>
/// <param name="Provider">The shared provider instance for the kind.</param>
public sealed record SandboxProviderRegistration(string Kind, ISandboxProvider Provider)
{
    /// <summary>Normalised provider kind, lowercase.</summary>
    public string Kind { get; init; } = Kind?.Trim().ToLowerInvariant()
        ?? throw new ArgumentNullException(nameof(Kind));

    /// <summary>The shared provider instance for the kind.</summary>
    public ISandboxProvider Provider { get; init; } = Provider
        ?? throw new ArgumentNullException(nameof(Provider));
}

/// <summary>
/// Resolves the provider backing a placement member. The composition root
/// implements this so each configured provider kind is constructed once and
/// shared across every member that names it; core code never names a concrete
/// provider kind, keeping the placement path provider-agnostic.
/// </summary>
public interface ISandboxProviderRegistry
{
    /// <summary>
    /// Returns the shared provider instance for <paramref name="member"/>'s
    /// provider kind. Fails closed (throws
    /// <see cref="InvalidOperationException"/>) when the member names no kind
    /// or a kind the composition root cannot build — a member is never
    /// silently re-pointed at another provider.
    /// </summary>
    ISandboxProvider Resolve(SandboxMember member);

    /// <summary>
    /// Returns the shared provider for <paramref name="kind"/> (normalised),
    /// building it on first use. Used at startup to warm every configured
    /// kind so a bad provider fails the host fast instead of the first
    /// placement.
    /// </summary>
    ISandboxProvider EnsureKind(string kind);

    /// <summary>
    /// Every constructed provider with its kind, ordered by kind (ordinal) so
    /// enumeration is independent of registration order.
    /// </summary>
    IReadOnlyList<SandboxProviderRegistration> ListRegistered();

    /// <summary>
    /// Derives every constructed kind's admission gate from
    /// <paramref name="catalog"/>: each kind's gate target becomes the sum of
    /// member capacities naming it (first member wins for a repeated member
    /// id, matching placement flattening; sums saturate at
    /// <see cref="int.MaxValue"/>), so a kind gate always fits the member
    /// gates beneath it and never clamps them. Kinds with no members keep
    /// their seed target; kinds with a non-positive derived sum are skipped.
    /// Idempotent: unchanged targets are a no-op.
    /// </summary>
    void SyncKindCapacities(IReadOnlyList<SandboxClass> catalog);
}
