using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Stages the freshest ancestry-reachable baseline into an audit sandbox at
/// <c>Audit:TestSelection:Coverage:BaselineSandboxPath</c> (read-only).
/// Fail-safe by construction: no baseline, a stale-only set, an unresolvable
/// base tip, an unstageable path, or any sandbox/IO fault stages nothing and
/// returns null — the coverage selector then falls back to the full suite, so
/// staging can never block or corrupt an audit.
/// </summary>
public sealed class TestSelectionBaselineAuditStager
{
    private readonly ITestSelectionBaselineStore _store;
    private readonly IGitHost _gitHost;
    private readonly Func<CoverageTestSelectionOptions> _coverageOptions;
    private readonly ILogger<TestSelectionBaselineAuditStager> _log;
    private readonly TimeProvider _clock;

    public TestSelectionBaselineAuditStager(
        ITestSelectionBaselineStore store,
        IGitHost gitHost,
        Func<CoverageTestSelectionOptions> coverageOptions,
        ILogger<TestSelectionBaselineAuditStager> log,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _coverageOptions = coverageOptions ?? throw new ArgumentNullException(nameof(coverageOptions));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves the item's base tip, selects the newest stored baseline that
    /// is both an ancestor of it and fresh within <c>MaxBaselineAge</c>, and
    /// writes it to <paramref name="sandboxPathOverride"/> (or the configured
    /// <c>BaselineSandboxPath</c>) as a read-only file. Returns the staged
    /// commit, or null when nothing was staged.
    /// </summary>
    public async Task<string?> StageAsync(
        ISandbox sandbox,
        string projectId,
        string repositoryId,
        string baseBranch,
        CancellationToken ct = default,
        string? sandboxPathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (string.IsNullOrWhiteSpace(baseBranch))
            return null;

        try
        {
            return await StageCoreAsync(sandbox, projectId, repositoryId, baseBranch, ct, sandboxPathOverride)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Staging is advisory — a fault here must never fail the audit.
            _log.LogWarning(ex, "Baseline staging skipped for project {ProjectId}; audit proceeds without a baseline", projectId);
            return null;
        }
    }

    private async Task<string?> StageCoreAsync(
        ISandbox sandbox,
        string projectId,
        string repositoryId,
        string baseBranch,
        CancellationToken ct,
        string? sandboxPathOverride)
    {
        var options = _coverageOptions();
        var sandboxPath = string.IsNullOrWhiteSpace(sandboxPathOverride)
            ? options.BaselineSandboxPath
            : sandboxPathOverride;
        if (!TestSelectionBaselineStagingIO.IsStageableSandboxPath(sandboxPath))
            return null;

        string baseTip;
        try
        {
            baseTip = await _gitHost.ResolveCommitAsync(repositoryId, baseBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Baseline staging skipped for project {ProjectId}: base branch did not resolve", projectId);
            return null;
        }

        var candidates = await _store.ListAsync(projectId, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
            return null;

        var selected = await TestSelectionBaselineStaging.SelectNewestAncestorAsync(
            candidates,
            baseTip,
            (ancestor, descendant, ancestorCt) => _gitHost.IsAncestorAsync(repositoryId, ancestor, descendant, ancestorCt),
            _clock.GetUtcNow(),
            options.MaxBaselineAge,
            ct).ConfigureAwait(false);
        if (selected is null)
            return null;

        // Bound the payload BEFORE buffering it into the sandbox exec: the
        // stored bytes were capped at production read-back time, but a second
        // caller must not trust that — re-check against the live cap.
        if (selected.Json.Length == 0 || selected.Json.Length > options.MaxBaselineBytes)
        {
            _log.LogWarning(
                "Baseline staging skipped for project {ProjectId}@{Commit}: payload size {Bytes} outside the {Cap}-byte cap",
                projectId, selected.Commit, selected.Json.Length, options.MaxBaselineBytes);
            return null;
        }

        var parent = TestSelectionBaselineStagingIO.ParentDirectory(sandboxPath);
        var mkdir = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", "--", parent],
        }, ct).ConfigureAwait(false);
        if (!mkdir.Success)
            return null;

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["tee", "--", sandboxPath],
            Stdin = selected.Json,
        }, ct).ConfigureAwait(false);
        if (!write.Success)
            return null;

        var chmod = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["chmod", "444", "--", sandboxPath],
        }, ct).ConfigureAwait(false);
        if (!chmod.Success)
            return null;

        _log.LogInformation(
            "Staged test-selection baseline {Commit} for project {ProjectId} at {Path} (read-only)",
            selected.Commit, projectId, sandboxPath);
        return selected.Commit;
    }
}
