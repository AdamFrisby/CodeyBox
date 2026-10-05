namespace CodeyBox.Core;

/// <summary>
/// Raised when a lease-guarded rewrite of a CodeyBox-owned work branch is
/// refused because the remote ref moved unexpectedly: the observed remote sha
/// does not match the sha CodeyBox last pushed (or the operator-authorized
/// sha), so a third party has pushed to the branch since. The branch is left
/// untouched — nothing was clobbered — and the item must be parked for the
/// operator instead of retried. Retrying cannot succeed: the lease will keep
/// failing until a human inspects the foreign commits.
/// </summary>
public sealed class UpstreamLeaseMismatchException : InvalidOperationException
{
    public UpstreamLeaseMismatchException(string branch, string expectedSha, string? actualSha)
        : base(BuildMessage(branch, expectedSha, actualSha))
    {
        Branch = branch;
        ExpectedSha = expectedSha;
        ActualSha = actualSha;
    }

    public string Branch { get; }
    public string ExpectedSha { get; }
    public string? ActualSha { get; }

    private static string BuildMessage(string branch, string expectedSha, string? actualSha)
    {
        var actual = string.IsNullOrEmpty(actualSha) ? "(unknown)" : actualSha;
        return $"refusing to overwrite work branch '{branch}': remote ref moved from the last CodeyBox push {expectedSha} to {actual}; " +
            "a third party pushed to this branch, so the lease failed and nothing was overwritten. " +
            "Inspect the foreign commits manually; re-drive the upstream step only after confirming the branch may be rewritten.";
    }

    /// <summary>
    /// Single source of truth for recognizing the lease-mismatch contract in
    /// an exception chain. Only the typed contract qualifies — arbitrary
    /// message text never does — so the orchestrator never confuses this
    /// with a merge conflict or a transient push failure.
    /// </summary>
    public static bool TryFindIn(
        Exception? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UpstreamLeaseMismatchException? mismatch)
    {
        for (var current = source; current is not null; current = current.InnerException)
        {
            if (current is UpstreamLeaseMismatchException typed)
            {
                mismatch = typed;
                return true;
            }
        }

        mismatch = null;
        return false;
    }
}
