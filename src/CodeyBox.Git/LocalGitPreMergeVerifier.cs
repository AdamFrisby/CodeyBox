using System.Diagnostics;
using System.Text;
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

    /// <summary>
    /// Explicit allowlist of environment variables passed to subprocesses
    /// spawned by the verifier. .NET's default ProcessStartInfo behaviour is
    /// to inherit the full parent environment; the orchestrator's
    /// environment carries agent API keys
    /// (<c>CODEYBOX_CLAUDE_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>,
    /// <c>CODEYBOX_COPILOT_TOKEN</c>, <c>CODEYBOX_CODEX_API_KEY</c>,
    /// <c>CODEYBOX_CODEX_ACCOUNT_ID</c>, <c>CODEYBOX_GEMINI_OAUTH_TOKEN</c>,
    /// etc.) read at startup in <c>Program.cs</c>. The verifier executes
    /// agent-controlled build/test argv against an agent-controlled tree
    /// (<c>.csproj</c>, <c>Directory.Build.props</c>, <c>.targets</c>,
    /// <c>nuget.config</c> files can run inline MSBuild tasks during
    /// <c>dotnet build</c>), so an inherited environment would let a
    /// prompt-injected agent commit a build target that reads those keys and
    /// exfiltrates them. Symmetric to the CI-layer scrub in
    /// <c>.github/workflows/pre-merge-revalidate.yml</c>.
    /// </summary>
    private static readonly IReadOnlySet<string> EnvAllowList =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Required for the verify argv to find common toolchains.
            "PATH",
            // dotnet, nuget, msbuild all derive their per-user state from HOME.
            "HOME",
            // dotnet specifically; explicit so we don't depend on HOME alone.
            "DOTNET_ROOT", "DOTNET_CLI_HOME",
            // User identity used by some build tools' diagnostic output.
            "USER", "LOGNAME",
            // Some shells / make / cmake refuse to run under empty SHELL.
            "SHELL",
            // Locale: without these dotnet emits garbled error text.
            "LANG", "LC_ALL", "LC_CTYPE", "LC_MESSAGES", "LC_TIME",
            "LC_NUMERIC", "LC_COLLATE", "LC_MONETARY",
            // Time zone for any test that depends on local clocks.
            "TZ",
            // Some tools refuse to run without TERM set.
            "TERM",
            // Temporary directory; build tools default to /tmp if unset but
            // operators sometimes pin a larger volume here.
            "TMPDIR", "TMP", "TEMP",
        };

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
            _gitHost.PrepareRepositoryForHostGitOperations(request.RepositoryId);

            // --detach keeps the worktree HEAD on the merge sha without
            // claiming a branch ref, so we don't compete with other parts of
            // the pipeline that may also want a worktree on this repo.
            var add = await RunProcessAsync(
                "git",
                ["-c", $"core.hooksPath={DisabledHooksPath(bareRepoPath)}",
                 "worktree", "add", "--detach", worktreePath, request.MergeSha],
                workdir: bareRepoPath,
                ct,
                gitOperation: "worktree");
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
                        CancellationToken.None,
                        gitOperation: "worktree");
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
    /// by LocalGitHost on construction at <c>{rootDirectory}/.codeybox-disabled-hooks</c>;
    /// when present, pointing <c>core.hooksPath</c> at that empty directory
    /// makes git see no hooks for the duration of this command. If it is
    /// not present, we fall back to <c>/dev/null</c> — on Linux (the only
    /// supported host OS) git treats that as a non-directory hooks path
    /// and the lookup for any hook name fails, which is the behaviour we
    /// want here. POSIX-only; if this verifier ever needs to run on
    /// Windows the fallback should become a verifier-managed empty
    /// directory instead.
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

    /// <param name="gitOperation">
    /// When non-null, the git subcommand name (e.g. <c>worktree</c>) this
    /// invocation runs. The verifier launches git directly rather than through
    /// <c>LocalGitHost.RunGitAsync</c>, so these host-side git commands would
    /// otherwise be an unmeasured coordinator scaling pinch point; supplying
    /// the operation makes the call record
    /// <see cref="CoordinatorGitMetrics"/> with duration and outcome. Null for
    /// the operator-configured verify argv, which is a build/test command, not
    /// a git op.
    /// </param>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workdir,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? gitOperation = null)
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

        // .NET pre-populates psi.Environment from the parent process. Clear
        // that and re-add only the allowlist so an agent-controlled build
        // (executed inline by MSBuild / NuGet hooks during dotnet build)
        // cannot read CODEYBOX_*/ANTHROPIC_*/OPENAI_*/GH_TOKEN/GITHUB_TOKEN.
        // Applied uniformly to every subprocess (git plumbing and verify
        // argv alike) — the git invocations don't need those secrets and
        // scrubbing symmetrically avoids a future code path silently
        // regaining the inheritance.
        psi.Environment.Clear();
        foreach (var key in EnvAllowList)
        {
            var v = System.Environment.GetEnvironmentVariable(key);
            if (v is not null) psi.Environment[key] = v;
        }

        var stopwatch = gitOperation is null ? null : System.Diagnostics.Stopwatch.StartNew();
        var metricOutcome = "error";
        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();

            using var timeoutCts = timeout is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (timeout is { } tspan && timeoutCts is not null)
                timeoutCts.CancelAfter(tspan);
            var effectiveCt = timeoutCts?.Token ?? ct;

            // Bounded head+tail capture rather than ReadToEndAsync: the child
            // may be an agent-controlled build emitting unbounded output, and
            // the coordinator is a single lightweight process fanning VMs
            // across many hosts — an unbounded in-memory buffer here is exactly
            // the kind of silent host-side pinch point the bottleneck guard
            // must avoid. Only the head+tail survive (which is all
            // SummariseOutput surfaces anyway), so memory stays bounded
            // regardless of how much the child writes.
            var stdoutTask = ReadCappedAsync(p.StandardOutput, MaxCapturedStreamBytes, effectiveCt);
            var stderrTask = ReadCappedAsync(p.StandardError, MaxCapturedStreamBytes, effectiveCt);
            try
            {
                await p.WaitForExitAsync(effectiveCt);
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Process may have already exited between CancelAfter and Kill;
                // it may also have been a child of a stale shell that's gone.
                // Either way the kill is informational — the timeout outcome
                // below is the real signal. Exit code 124 mirrors GNU
                // timeout(1) so operators / downstream tooling see a familiar
                // sentinel for "command timed out".
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* process already exited */ }
                catch (System.ComponentModel.Win32Exception) { /* kill denied / process gone */ }
                metricOutcome = "timeout";
                return (124, await ReadWithGraceAsync(stdoutTask), $"verify command exceeded {timeout!.Value.TotalMinutes:0} min timeout");
            }
            catch (OperationCanceledException)
            {
                // Same kill-race as the timeout branch above; the re-thrown
                // OCE is what propagates the cancel to the orchestrator.
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* process already exited */ }
                catch (System.ComponentModel.Win32Exception) { /* kill denied / process gone */ }
                metricOutcome = "canceled";
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var truncated = stdout.Truncated || stderr.Truncated;
            metricOutcome = truncated
                ? "output_limit"
                : p.ExitCode == 0 ? "success" : "exit_nonzero";
            if (gitOperation is not null && truncated)
            {
                // A host-side git command producing more than the capture
                // ceiling is abnormal (worktree add/remove emit a handful of
                // lines) — surface it rather than silently degrading.
                _log.LogWarning(
                    "Host git '{Operation}' output exceeded the {Cap}-byte capture ceiling and was head/tail-truncated",
                    gitOperation, MaxCapturedStreamBytes);
            }
            return (p.ExitCode, stdout.Text, stderr.Text);
        }
        catch (OperationCanceledException)
        {
            if (metricOutcome == "error")
                metricOutcome = "canceled";
            throw;
        }
        finally
        {
            if (stopwatch is not null)
                CoordinatorGitMetrics.Record(gitOperation!, stopwatch, metricOutcome);
        }

        static async Task<string> ReadWithGraceAsync(Task<CapturedStream> t)
        {
            // Bounded drain after a timeout-kill.
            //
            // Why: Process.Kill(entireProcessTree: true) on Linux walks
            // /proc to enumerate descendants, which races against fork —
            // a grandchild created between Start and the enumeration can
            // be missed and survive as an orphan still holding our stdout
            // pipe open. The cancellation token bound to ReadCappedAsync's
            // inner ReadAsync does NOT interrupt a pipe ReadAsync
            // mid-syscall on Linux (token checks happen between reads, but
            // the read itself blocks in the kernel until data arrives or
            // the pipe closes). So a naive `await t` here would block until
            // the orphaned descendant naturally exits — turning a 500 ms
            // timeout into a 30 s wall-clock hang on a `sleep 30` argv.
            //
            // Give the read a short grace in case stdout has already
            // drained (every descendant actually exited), then abandon:
            // the synthetic timeout message in Stderr (third tuple
            // element) is the operator-visible signal, and stdout
            // content here is best-effort diagnostic.
            if (!t.IsCompleted)
            {
                var winner = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(2)));
                if (winner != t)
                {
                    // Observe the abandoned task's eventual exception so
                    // it doesn't surface as an unobserved-task exception.
                    _ = t.ContinueWith(static observed => observed.Exception,
                        TaskScheduler.Default);
                    return string.Empty;
                }
            }
            try { return (await t).Text; }
            catch (OperationCanceledException) { return string.Empty; }
            catch (IOException) { return string.Empty; }
        }
    }

    private readonly record struct CapturedStream(string Text, bool Truncated);

    /// <summary>
    /// Reads a child stream keeping only the first <paramref name="perSideBudgetBytes"/>
    /// bytes (the head) and a rolling last <paramref name="perSideBudgetBytes"/>
    /// bytes (the tail), joined. This bounds the coordinator's in-memory
    /// buffer to ~2× the budget (plus one read chunk) no matter how much the
    /// child writes, while preserving both the leading diagnostic and the
    /// trailing error line that <see cref="SummariseOutput"/> surfaces.
    /// <see cref="CapturedStream.Truncated"/> is true when any bytes between
    /// the head and tail were dropped.
    /// </summary>
    private static async Task<CapturedStream> ReadCappedAsync(
        TextReader reader, int perSideBudgetBytes, CancellationToken ct)
    {
        var head = new StringBuilder();
        var headBytes = 0;
        // Rolling tail retained as whole read-chunks so trimming the oldest is
        // O(1); memory stays bounded to the budget plus at most one chunk.
        var tailChunks = new Queue<(string Text, int Bytes)>();
        var tailBytes = 0;
        var dropped = false;
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;

            var i = 0;
            // Fill the head first, one scalar at a time so the budget cut never
            // splits a surrogate pair.
            while (i < read && headBytes < perSideBudgetBytes)
            {
                var charCount = char.IsHighSurrogate(buffer[i]) && i + 1 < read && char.IsLowSurrogate(buffer[i + 1])
                    ? 2
                    : 1;
                var scalarBytes = Encoding.UTF8.GetByteCount(buffer.AsSpan(i, charCount));
                if (headBytes + scalarBytes > perSideBudgetBytes)
                    break;
                head.Append(buffer, i, charCount);
                headBytes += scalarBytes;
                i += charCount;
            }

            if (i < read)
            {
                var rest = new string(buffer, i, read - i);
                var restBytes = Encoding.UTF8.GetByteCount(rest);
                tailChunks.Enqueue((rest, restBytes));
                tailBytes += restBytes;
                // Keep at least one chunk so the newest output always survives.
                while (tailBytes > perSideBudgetBytes && tailChunks.Count > 1)
                {
                    var (_, oldBytes) = tailChunks.Dequeue();
                    tailBytes -= oldBytes;
                    dropped = true;
                }
            }
        }

        if (tailChunks.Count == 0)
            return new CapturedStream(head.ToString(), Truncated: false);

        var tail = new StringBuilder();
        foreach (var (text, _) in tailChunks)
            tail.Append(text);
        return new CapturedStream(head.Append(tail).ToString(), dropped);
    }
}
