namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for which provider failure signals may feed a
/// quota-exhaustion verdict. Only genuine quota/rate-limit responses count:
/// <see cref="QuotaFailureKind.RateLimitExceeded"/> (transient throughput cap)
/// and <see cref="QuotaFailureKind.LimitReached"/> (spent account cap).
/// <see cref="QuotaFailureKind.Unauthorized"/> (401/403, expired or missing
/// credentials) never clears on a quota window — parking or benching on it
/// loops forever and skips the auth-required handling — so it must never
/// reach the exhaustion classifier, the observed-failure store, or an
/// in-process exhaustion gate.
/// </summary>
public static class QuotaFailureKindExtensions
{
    /// <summary>
    /// True only for provider quota/rate-limit responses. Anything else —
    /// auth failures, unknown future enum values arriving via unchecked casts —
    /// is not exhaustion evidence.
    /// </summary>
    public static bool IsExhaustionSignal(this QuotaFailureKind kind) =>
        kind is QuotaFailureKind.RateLimitExceeded or QuotaFailureKind.LimitReached;
}

/// <summary>
/// Provenance for a cached quota-exhaustion verdict. Every in-process
/// exhaustion gate must name the provider signal that produced it, so an
/// operator reading a "refused: in-process exhaustion cache" log line can see
/// <em>why</em> the agent is benched rather than only <em>that</em> it is.
/// The constructor enforces the narrowing at the type level: only
/// <see cref="QuotaFailureKindExtensions.IsExhaustionSignal"/> kinds can be
/// captured, so a git-transport blip, a 401, or any other non-quota failure
/// cannot be laundered into exhaustion evidence.
/// </summary>
public sealed record QuotaExhaustionEvidence
{
    /// <summary>The provider quota/rate-limit signal. Always an exhaustion signal.</summary>
    public QuotaFailureKind Signal { get; }

    /// <summary>
    /// Where the signal was observed, e.g. <c>"work-phase:copilot/rate_limit_exceeded"</c>
    /// or <c>"merge:codex/limit-reached"</c>. Names the pipeline phase and the
    /// originating failure so the verdict is attributable.
    /// </summary>
    public string Origin { get; }

    /// <summary>Optional free-text detail (detector name, matched pattern family).</summary>
    public string? Detail { get; }

    public QuotaExhaustionEvidence(QuotaFailureKind signal, string origin, string? detail = null)
    {
        if (!signal.IsExhaustionSignal())
            throw new ArgumentException(
                $"Only provider quota/rate-limit signals may back an exhaustion verdict, not {signal}.",
                nameof(signal));
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Signal = signal;
        Origin = origin;
        Detail = detail;
    }

    public override string ToString() =>
        Detail is null ? $"{Signal} via {Origin}" : $"{Signal} via {Origin} ({Detail})";
}
