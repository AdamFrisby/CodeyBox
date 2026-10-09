using System.Text.RegularExpressions;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Frozen-candidate source handoff for Unity Build Automation. Unity builds
/// from hosted Git, so the only supported handoff is the host-published
/// candidate ref naming an exact commit. Snapshot uploads, branch names, and
/// "latest on branch" are unsupported and fail before dispatch: a mutable
/// branch tip can never be authoritative evidence.
/// LFS pointer pins and submodule pins travel inside the host-published
/// candidate commit (the shared snapshot policy records them); the adapter
/// never materializes blobs itself.
/// </summary>
public static class UnityBuildSourceValidator
{
    private static readonly Regex FullSha = new(
        "^[0-9a-f]{40}([0-9a-f]{24})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingSha = new(
        "([0-9a-f]{40}(?:[0-9a-f]{24})?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the exact checkout commit from a host-published candidate ref.
    /// Throws <see cref="UnityBuildSourceException"/> when exact immutable
    /// candidate correlation cannot be established.
    /// </summary>
    public static string RequireCommit(
        ExternalBuildSourceIdentity? source,
        ExternalBuildSubmitInput? input,
        UnityBuildAutomationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (source?.CandidateRef is null)
            throw new UnityBuildSourceException(
                "Unity Build Automation needs a host-published Git candidate ref; " +
                "snapshot-only handoffs cannot name an immutable checkout.");
        new ExternalBuildGitPublicationPolicy().ValidateRef(source.CandidateRef);
        if (input?.Snapshot is not null)
            throw new UnityBuildSourceException(
                "Unity Build Automation accepts a Git candidate ref or nothing: " +
                "a snapshot upload alongside the ref is an ambiguous handoff and is rejected.");
        if (input?.CandidateRef is not null
            && !string.Equals(input.CandidateRef, source.CandidateRef, StringComparison.Ordinal))
            throw new UnityBuildSourceException(
                "Conflicting candidate refs in the frozen source and the submit input; refusing an ambiguous handoff.");
        if (input?.Parameters is not null)
            foreach (var forbidden in UnityBuildTarget.ForbiddenSourceOverrides)
                if (input.Parameters.ContainsKey(forbidden))
                    throw new UnityBuildSourceException(
                        $"Submit input carries forbidden source override '{forbidden}': " +
                        "the checkout commit comes only from the host-published candidate ref.");
        if (options.RequireExactCommit)
        {
            var match = TrailingSha.Match(source.CandidateRef.Trim());
            if (!match.Success)
                throw new UnityBuildSourceException(
                    $"Candidate ref '{RedactRef(source.CandidateRef)}' names no exact commit SHA; " +
                    "branch names and latest-on-branch are never dispatched.");
            return match.Groups[1].Value.ToLowerInvariant();
        }
        return source.CandidateRef;
    }

    public static bool LooksLikeExactSha(string? value) =>
        !string.IsNullOrWhiteSpace(value) && FullSha.IsMatch(value.Trim().ToLowerInvariant());

    private static string RedactRef(string candidateRef) =>
        candidateRef.Length <= 48 ? candidateRef : candidateRef[..48] + "…";
}
