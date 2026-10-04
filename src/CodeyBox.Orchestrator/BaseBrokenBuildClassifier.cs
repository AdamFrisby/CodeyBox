using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Evidence produced when a required-build failure is attributed to the
/// base branch rather than the item's diff: the base tip SHA that
/// reproduced the failure plus the (already redacted/bounded) base build
/// output for reporting.
/// </summary>
internal sealed record BaseBrokenVerdict(string BaseSha, string? BaseBuildOutput);

/// <summary>
/// Attribution step between "the required build failed on the work branch"
/// and "the item is at fault". Decision order:
/// <list type="number">
///   <item>Parse compiler error file locations from the work-branch build
///   output. If any error file is inside the item's diff (base..work), the
///   failure is item-attributed and no base build runs.</item>
///   <item>Otherwise build the base branch tip in a sandbox (verdicts
///   cached per SHA by <see cref="BaseBuildVerifier"/>).</item>
///   <item>Base also fails → <see cref="BaseBrokenVerdict"/>; base builds
///   or cannot be determined → null and the caller keeps the historical
///   item-attributed behaviour.</item>
/// </list>
/// Fail-closed toward the item: any difficulty producing evidence (diff
/// unavailable, base build inconclusive) returns null so the feature never
/// acquits an item on a guess.
/// </summary>
internal sealed class BaseBrokenBuildClassifier
{
    private readonly IGitHost _gitHost;
    private readonly BaseBuildVerifier _baseBuilds;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly ILogger<BaseBrokenBuildClassifier>? _log;

    public BaseBrokenBuildClassifier(
        IGitHost gitHost,
        BaseBuildVerifier baseBuilds,
        PipelineTuningSnapshot tuning,
        ILogger<BaseBrokenBuildClassifier>? log = null)
    {
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _baseBuilds = baseBuilds ?? throw new ArgumentNullException(nameof(baseBuilds));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _log = log;
    }

    /// <summary>
    /// Returns a <see cref="BaseBrokenVerdict"/> when
    /// <paramref name="failedResult"/> is reproducible on the base tip with
    /// no contributing error file inside the item's diff; otherwise null.
    /// </summary>
    public async Task<BaseBrokenVerdict?> TryClassifyAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        RequiredBuildVerificationResult failedResult,
        RequiredBuildSandboxPolicy sandboxPolicy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(failedResult);
        if (failedResult.Status != RequiredBuildVerificationStatus.Failed)
            return null;
        if (!_tuning.Current.BaseBrokenDetectionEnabled)
            return null;

        var errorPaths = BuildErrorLocationParser.ParseErrorPaths(failedResult.Output);

        IReadOnlyCollection<string> diffPaths;
        try
        {
            diffPaths = await LoadDiffPathsAsync(repoId, baseBranch, workBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No trustworthy diff → cannot rule out item attribution.
            _log?.LogWarning(
                ex,
                "Could not load diff {Base}..{Work} for work item {Id}; keeping item-attributed build failure",
                baseBranch, workBranch, item.Id);
            return null;
        }

        if (BuildErrorLocationParser.AnyErrorPathInDiff(errorPaths, diffPaths))
            return null;

        string baseSha;
        try
        {
            baseSha = await _gitHost.ResolveCommitAsync(repoId, baseBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(
                ex,
                "Could not resolve base tip '{Base}' for work item {Id}; keeping item-attributed build failure",
                baseBranch, item.Id);
            return null;
        }

        var verdict = await _baseBuilds.VerifyTipAsync(
            baseSha, repoId, baseBranch, item.Id, project.Id, sandboxPolicy, ct).ConfigureAwait(false);

        if (verdict.Outcome != BaseBuildOutcome.Failed)
            return null;

        return new BaseBrokenVerdict(verdict.BaseSha, verdict.Output);
    }

    private async Task<IReadOnlyCollection<string>> LoadDiffPathsAsync(
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        var changes = await _gitHost.GetChangedPathsAsync(repoId, baseBranch, workBranch, ct)
            .ConfigureAwait(false);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            AddNormalized(paths, change.Path);
            AddNormalized(paths, change.OldPath);
        }
        return paths;
    }

    private static void AddNormalized(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length > 0)
            paths.Add(normalized);
    }
}
