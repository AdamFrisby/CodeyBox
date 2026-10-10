namespace CodeyBox.Core;

/// <summary>
/// Raised when a CodeyBox-owned work branch cannot be pushed because the
/// remote ref exists but is not a fast-forward of the new head, and CodeyBox
/// has no recorded prior push to build a lease from. This is distinctly NOT
/// a merge conflict: the new head was already composed on a fresh base by the
/// merge phase, so there is nothing to rebase or resolve — the obstacle is
/// purely the stale remote ref left by a previous attempt. The item must be
/// parked with this message (never routed into conflict-rework) until the
/// operator re-drives the upstream step, which records the observed remote
/// sha as the lease base. The message names the working re-drive endpoint
/// and the exact ownership flag so the operator is never pointed at a
/// refused action.
/// </summary>
public sealed class UpstreamOwnedBranchDivergedException : InvalidOperationException
{
    /// <summary>
    /// Re-drive endpoint template named in the park message. The
    /// <c>{id}</c> placeholder is the work item id; the message keeps it
    /// symbolic because the push layer does not know the item id.
    /// </summary>
    public const string RedriveEndpointTemplate = "POST /workitems/{id}/redrive-upstream";

    /// <summary>Exact request flag that asserts the remote tip is CodeyBox's own history.</summary>
    public const string ConfirmOwnershipFlag = "confirmOwnership";

    public UpstreamOwnedBranchDivergedException(string branch, string remoteSha, string localSha)
        : this(branch, remoteSha, localSha, unownedCommitShas: null)
    {
    }

    /// <summary>
    /// Same refusal, naming the <paramref name="unownedCommitShas"/> that
    /// blocked the trailer proof so the operator can inspect exactly which
    /// commits look third-party. Null (unverifiable history) keeps the base
    /// message; an empty list is owned and should never reach this path.
    /// </summary>
    public UpstreamOwnedBranchDivergedException(
        string branch, string remoteSha, string localSha, IReadOnlyList<string>? unownedCommitShas)
        : base($"work branch '{branch}' is not a fast-forward of its previous upstream push " +
            $"(remote {remoteSha}, local {localSha}); this is not a merge conflict — the work was already recomposed on a fresh base. " +
            "CodeyBox has no recorded prior push for this branch, so it cannot prove the remote tip is its own history. " +
            (unownedCommitShas is { Count: > 0 }
                ? $"Commit(s) without a CodeyBox trailer: {BranchOwnershipPolicy.DescribeUnownedCommits(unownedCommitShas)}. "
                : string.Empty) +
            $"Re-drive the upstream step with {RedriveEndpointTemplate} after confirming no third party pushed to the branch. " +
            "When every commit on the remote tip carries the CodeyBox trailers the rewrite is accepted automatically; " +
            $"otherwise the re-drive must pass {{\"{ConfirmOwnershipFlag}\": true}} to assert the remote tip is CodeyBox's own history.")
    {
        Branch = branch;
        RemoteSha = remoteSha;
        LocalSha = localSha;
    }

    public string Branch { get; }
    public string RemoteSha { get; }
    public string LocalSha { get; }

    /// <summary>
    /// Single source of truth for recognizing the owned-branch-diverged
    /// contract in an exception chain. Only the typed contract qualifies —
    /// arbitrary message text never does.
    /// </summary>
    public static bool TryFindIn(
        Exception? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UpstreamOwnedBranchDivergedException? diverged)
    {
        for (var current = source; current is not null; current = current.InnerException)
        {
            if (current is UpstreamOwnedBranchDivergedException typed)
            {
                diverged = typed;
                return true;
            }
        }

        diverged = null;
        return false;
    }
}
