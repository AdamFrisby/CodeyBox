namespace CodeyBox.Api;

/// <summary>
/// Whether the binaries in <c>bin/</c> were built from the currently
/// checked-out revision.
/// </summary>
public enum DeployConsistencyStatus
{
    /// <summary>
    /// At least one side is unknown (no git metadata at build time or at
    /// runtime). Nothing can be concluded; startup must not treat this as a
    /// failure.
    /// </summary>
    Unknown,

    /// <summary>Built revision and checkout revision agree.</summary>
    Consistent,

    /// <summary>
    /// Both sides are known and differ — the working tree has moved ahead of
    /// <c>bin/</c> (e.g. a <c>git pull</c> under a <c>--no-build</c> deploy).
    /// </summary>
    Diverged,
}

/// <summary>
/// Point-in-time comparison of the revision baked into the binaries against
/// the revision checked out on disk.
/// </summary>
public sealed record DeployConsistencyReport(
    string BuiltRevision,
    string CheckoutRevision,
    DeployConsistencyStatus Status)
{
    public bool IsDiverged => Status == DeployConsistencyStatus.Diverged;
}

/// <summary>
/// Pure comparison of a built revision against a checkout revision. Kept free
/// of I/O so the decision logic is directly unit-testable; the revision
/// readers (<see cref="BuildRevision"/>, <see cref="CheckoutRevisionReader"/>)
/// supply the inputs.
/// </summary>
public static class DeployConsistency
{
    public const string UnknownRevision = "unknown";

    public static DeployConsistencyReport Evaluate(string? builtRevision, string? checkoutRevision)
    {
        var built = NormalizeRevision(builtRevision);
        var checkout = NormalizeRevision(checkoutRevision);

        if (!IsKnownRevision(built) || !IsKnownRevision(checkout))
            return new DeployConsistencyReport(built, checkout, DeployConsistencyStatus.Unknown);

        return string.Equals(built, checkout, StringComparison.OrdinalIgnoreCase)
            ? new DeployConsistencyReport(built, checkout, DeployConsistencyStatus.Consistent)
            : new DeployConsistencyReport(built, checkout, DeployConsistencyStatus.Diverged);
    }

    public static string NormalizeRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
            return UnknownRevision;
        return revision.Trim();
    }

    public static bool IsKnownRevision(string? revision) =>
        !string.IsNullOrWhiteSpace(revision)
        && !string.Equals(revision.Trim(), UnknownRevision, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names the stale build as the cause and a rebuild as the fix. Appended
    /// to the unbound-key failure only when the build is actually behind the
    /// checkout, so a genuinely unknown key keeps the original message.
    /// </summary>
    public static string FormatStaleBuildNote(DeployConsistencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return "The running build is stale: built revision "
            + report.BuiltRevision
            + " does not match the checked-out revision "
            + report.CheckoutRevision
            + ". These keys may be valid in the checkout but unknown to the older binaries in bin/. "
            + "Rebuild the service (dotnet build) and restart so the binaries match the checkout, "
            + "rather than suppressing this validation.";
    }
}
