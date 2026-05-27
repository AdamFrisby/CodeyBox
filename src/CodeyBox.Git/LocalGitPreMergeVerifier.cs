using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Git;

/// <summary>
/// Concrete <see cref="IPreMergeVerifier"/> that materialises the
/// orchestrator's local merge result into a worktree on the host bare repo
/// and runs the operator-configured argv against it. This is the in-process
/// answer to the gap GitHub's <c>mergeable == true</c> flag leaves open: it
/// catches the case where the local merge applies cleanly but the post-merge
/// tree no longer builds (a helper renamed on <c>main</c> that the PR still
/// calls under its old name, a constant whose value drifted, a previously
/// green test now broken by an interaction with newly-landed code).
///
/// <para>The pipeline guarantees <see cref="PreMergeVerifyRequest.MergeSha"/>
/// is the merge commit produced by the local merge phase, written onto
/// <see cref="PreMergeVerifyRequest.BaseBranch"/> in the host bare repo. We
/// check that exact tree out, run the configured build/test argv, and report
/// the outcome. We do NOT execute an additional fetch + rebase round here —
/// the local merge phase has already produced the result that would be
/// pushed; verifying that specific tree is what gates the auto-merge API
/// call.</para>
///
/// <para>Output capture is intentionally bounded (4 KiB head + tail; secrets
/// scrubbed via <see cref="RawOutputRedactor"/>). The resulting string flows
/// into the work item's <c>LastError</c> and the
/// <c>work_item.merge_conflict_resolution_failed</c> webhook payload, both
/// of which are operator-visible surfaces, so unbounded build/test stderr is
/// not appropriate.</para>
/// </summary>
public sealed class LocalGitPreMergeVerifier : IPreMergeVerifier
{
    private const int MaxCapturedStreamBytes = 4096;

    private readonly IGitHost _gitHost;
    private readonly ILogger<LocalGitPreMergeVerifier> _log;
    private readonly TimeSpan _commandTimeout;

    public LocalGitPreMergeVerifier(
        IGitHost gitHost,
        ILogger<LocalGitPreMergeVerifier> log,
        TimeSpan? commandTimeout = null)
    {
        _gitHost = gitHost;
        _log = log;
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(30);
    }

    public async Task<PreMergeVerifyResult> VerifyAsync(PreMergeVerifyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Argv.Count == 0)
            return PreMergeVerifyResult.Ok();

        string bareRepoPath;
        try
        {
            bareRepoPath = _gitHost.GetRepoPath(request.RepositoryId);
        }
        catch (NotSupportedException)
        {
            // No host bare repo means nothing to verify against. Treat as
            // "verifier is not applicable here" rather than throwing —
            // mirrors the IGitHost default behaviour for in-memory hosts.
            return PreMergeVerifyResult.Ok();
        }

        if (!Directory.Exists(bareRepoPath))
            throw new InvalidOperationException(
                $"bare repo for '{request.RepositoryId}' does not exist at {bareRepoPath}");

        Validation.ValidateCommitSha(request.MergeSha, nameof(request.MergeSha));

        var worktreePath = Path.Combine(
            Path.GetTempPath(),
            "codeybox-premerge-" + Guid.NewGuid().ToString("N"));
        var worktreeAdded = false;
        try
        {
            // --detach keeps the worktree HEAD on the merge sha without
            // claiming a branch ref, so we don't compete with other parts of
            // the pipeline that may also want a worktree on this repo.
            var add = await RunProcessAsync(
                "git",
                ["-c", $"core.hooksPath={DisabledHooksPath(bareRepoPath)}",
                 "worktree", "add", "--detach", worktreePath, request.MergeSha],
                workdir: bareRepoPath,
                extraEnv: null,
                ct);
            if (add.ExitCode != 0)
            {
                _log.LogWarning(
                    "Pre-merge verify: failed to add worktree for work item {Id}: {Stderr}",
                    request.WorkItemId, add.Stderr);
                return PreMergeVerifyResult.BuildOrTestFailed(
                    $"could not check out merge sha {request.MergeSha}: " +
                    SummariseOutput(add.Stderr.Length > 0 ? add.Stderr : add.Stdout));
            }
            worktreeAdded = true;

            // The argv is operator-configured (project config). It is invoked
            // directly with each element placed on argv — no shell, so there
            // is no opportunity for word splitting or metacharacter expansion
            // of values that arrived through configuration.
            var verifyRun = await RunProcessAsync(
                request.Argv[0],
                request.Argv.Skip(1).ToArray(),
                workdir: worktreePath,
                extraEnv: null,
                ct,
                timeout: _commandTimeout);

            if (verifyRun.ExitCode == 0)
            {
                _log.LogInformation(
                    "Pre-merge verify passed for work item {Id} (argv: {Argv})",
                    request.WorkItemId, string.Join(' ', request.Argv));
                return PreMergeVerifyResult.Ok();
            }

            var combined = verifyRun.Stderr.Length > 0
                ? verifyRun.Stderr
                : verifyRun.Stdout;
            var reason = $"{request.Argv[0]} exited {verifyRun.ExitCode}: {SummariseOutput(combined)}";

            // Build/test failure of an already-merged tree is exactly the
            // case the gate exists to catch: this is what GitHub's
            // mergeable=true flag misses.
            return PreMergeVerifyResult.BuildOrTestFailed(reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (worktreeAdded)
            {
                try
                {
                    await RunProcessAsync(
                        "git",
                        ["worktree", "remove", "--force", worktreePath],
                        workdir: bareRepoPath,
                        extraEnv: null,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _log.LogWarning(ex, "Failed to remove pre-merge verify worktree at {Path}", worktreePath);
                }
            }
            if (Directory.Exists(worktreePath))
            {
                try { Directory.Delete(worktreePath, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "Failed to clean up pre-merge verify worktree at {Path}", worktreePath);
                }
            }
        }
    }

    /// <summary>
    /// Reuse LocalGitHost's disabled-hooks directory so worktree creation
    /// doesn't trip user-installed bare-repo hooks. The directory is created
    /// by LocalGitHost on construction; if it does not exist we fall back to
    /// the empty string (git treats that as "no override").
    /// </summary>
    private static string DisabledHooksPath(string bareRepoPath)
    {
        var path = Path.Combine(bareRepoPath, "..", ".codeybox-disabled-hooks");
        return Directory.Exists(path) ? path : "/dev/null";
    }

    private static string SummariseOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "(no output)";

        var redacted = RawOutputRedactor.Redact(text);
        if (redacted.Length <= MaxCapturedStreamBytes)
            return redacted.Trim();

        // Head + tail trim keeps both the leading diagnostic and the final
        // error line in view; the middle bulk of build chatter is what gets
        // dropped first.
        var halfBudget = MaxCapturedStreamBytes / 2;
        var head = redacted[..halfBudget];
        var tail = redacted[^halfBudget..];
        return $"{head.Trim()}\n...\n{tail.Trim()}";
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workdir,
        IReadOnlyDictionary<string, string>? extraEnv,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv) psi.EnvironmentVariables[k] = v;

        using var p = new Process { StartInfo = psi };
        p.Start();

        using var timeoutCts = timeout is { } t
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeout is { } tspan && timeoutCts is not null)
            timeoutCts.CancelAfter(tspan);
        var effectiveCt = timeoutCts?.Token ?? ct;

        var stdoutTask = p.StandardOutput.ReadToEndAsync(effectiveCt);
        var stderrTask = p.StandardError.ReadToEndAsync(effectiveCt);
        try
        {
            await p.WaitForExitAsync(effectiveCt);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (124, await ReadOrEmpty(stdoutTask), $"verify command exceeded {timeout!.Value.TotalMinutes:0} min timeout");
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);

        static async Task<string> ReadOrEmpty(Task<string> t)
        {
            try { return await t; } catch { return string.Empty; }
        }
    }
}
