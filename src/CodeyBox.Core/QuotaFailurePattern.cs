namespace CodeyBox.Core;

/// <summary>
/// One config-driven pattern entry used by per-agent quota-failure detectors:
/// a substring (case-insensitive) and the <see cref="QuotaFailureKind"/> to
/// emit when stderr or stdout contains it. Operators can append entries via
/// configuration (e.g. <c>CodeyBox:QuotaFailurePatterns:cursor</c>) so new
/// vendor stderr shapes are recognised without recompilation.
/// </summary>
public sealed record QuotaFailurePattern(string Pattern, QuotaFailureKind Kind);
