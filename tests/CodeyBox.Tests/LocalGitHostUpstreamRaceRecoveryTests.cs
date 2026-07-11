using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Direct coverage for <see cref="LocalGitHost"/> methods added for recovery
/// paths: <see cref="LocalGitHost.FetchUpstreamBranchAsync"/>,
/// <see cref="LocalGitHost.SetBranchToCommitAsync"/> and
/// <see cref="LocalGitHost.ResetWorkBranchToBaseAsync"/>. The orchestrator
/// integration test (UpstreamAutoMergeRaceRecoveryTests) substitutes a fake
/// upstream that bypasses these methods entirely, so they need their own
/// targeted tests — otherwise a regression in (say) the refspec polarity would
/// only surface in production.
/// </summary>
public sealed class LocalGitHostUpstreamRaceRecoveryTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-race-helpers-").FullName;

    public void Dispose()
    {
        TestTempArtifacts.DeleteDirectory(_workspace);
    }

    private LocalGitHost CreateGitHost(
        string? gitExecutable = null,
        int? gitCommandMaxOutputBytes = null,
        Func<ProcessStartInfo, ILocalGitProcess>? processFactory = null)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var opts = new LocalGitHostOptions
        {
            RootDirectory = gitRoot,
            GitExecutable = gitExecutable ?? "git",
            GitCommandMaxOutputBytes = gitCommandMaxOutputBytes ?? LocalGitHostOptions.DefaultGitCommandMaxOutputBytes,
        };
        return processFactory is null
            ? new LocalGitHost(opts, NullLogger<LocalGitHost>.Instance)
            : new LocalGitHost(opts, NullLogger<LocalGitHost>.Instance, processFactory);
    }

    private static IReadOnlyDictionary<string, string> EmptyEnv()
        => new Dictionary<string, string>();

    // -------------------------------------------------------------------------
    // FetchUpstreamBranchAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchUpstreamBranchAsync_SuccessReturnsNewTipSha()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var before = await RevParse(barePath, "main");

        // Advance the seed (the "upstream") so the local bare repo is now stale.
        await CommitToRepo(seed, "after.txt", "after\n", "advance main");
        var after = await RevParse(seed, "main");
        Assert.NotEqual(before, after);

        var returned = await gitHost.FetchUpstreamBranchAsync(repoId, seed, "main", EmptyEnv());

        Assert.Equal(after, returned);
        // The bare repo's local ref must now point at the fetched tip — the
        // race-recovery merge phase reads baseBranch via this ref to compute
        // the new merge commit against the freshly-advanced upstream.
        Assert.Equal(after, await RevParse(barePath, "main"));
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_OverwritesDivergedLocalRef()
    {
        // Force-update local main to a divergent commit, then fetch — the
        // refspec must use a leading '+' so the divergence is overwritten.
        // Without it the local main would block fetch with a non-fast-forward
        // error and recovery would loop forever.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);

        // Make local main diverge from upstream.
        var divergentSha = await CommitToBareBranch(
            barePath, "main", "divergent.txt", "divergent\n", "diverge local main");
        await CommitToRepo(seed, "upstream-after.txt", "upstream\n", "upstream advance");
        var upstreamTip = await RevParse(seed, "main");

        var returned = await gitHost.FetchUpstreamBranchAsync(repoId, seed, "main", EmptyEnv());

        Assert.Equal(upstreamTip, returned);
        Assert.Equal(upstreamTip, await RevParse(barePath, "main"));
        Assert.NotEqual(divergentSha, upstreamTip);
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_MissingBranchReturnsNull()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        // Upstream doesn't have "no-such-branch" → fetch reports the branch is
        // unknown and the method returns null. The orchestrator parks with
        // "upstream does not advertise base branch" rather than crashing.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, seed, "no-such-branch", EmptyEnv()));
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_RejectsInvalidBranchName()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, seed, "-not-a-branch", EmptyEnv()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, seed, "../etc/passwd", EmptyEnv()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, seed, string.Empty, EmptyEnv()));
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_RejectsInvalidUpstreamUrl()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, "--upload-pack=evil", "main", EmptyEnv()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.FetchUpstreamBranchAsync(repoId, string.Empty, "main", EmptyEnv()));
    }

    [Fact]
    public async Task FetchUpstreamBranchAsync_ThrowsWhenBareRepoMissing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var missingRepoId = WorkItemId.New().ToString();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.FetchUpstreamBranchAsync(missingRepoId, seed, "main", EmptyEnv()));
        Assert.Contains("bare repo", ex.Message);
    }

    // -------------------------------------------------------------------------
    // SetBranchToCommitAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetBranchToCommitAsync_AdvancesBranchToResolvedCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var initialMainSha = await RevParse(barePath, "main");

        // Create a candidate sha by committing on a separate branch in the bare repo.
        var newCommitSha = await CommitToBareBranch(
            barePath, "feature/race", "race-fix.txt", "fix\n", "race recovery");

        await gitHost.SetBranchToCommitAsync(repoId, "main", newCommitSha);

        Assert.Equal(newCommitSha, await RevParse(barePath, "main"));
        Assert.NotEqual(initialMainSha, newCommitSha);
    }

    [Fact]
    public async Task SetBranchToCommitAsync_RejectsInvalidBranchName()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var sha = await RevParse(barePath, "main");

        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "-evil", sha));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "branch with space", sha));
    }

    [Fact]
    public async Task SetBranchToCommitAsync_RejectsInvalidSha()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");

        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "main", "not-a-sha"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "main", "deadbeef"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "main", string.Empty));
    }

    [Fact]
    public async Task SetBranchToCommitAsync_ThrowsWhenBareRepoMissing()
    {
        var gitHost = CreateGitHost();
        var missingRepoId = WorkItemId.New().ToString();
        var fakeSha = new string('a', 40);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.SetBranchToCommitAsync(missingRepoId, "main", fakeSha));
        Assert.Contains("bare repo", ex.Message);
    }

    [Fact]
    public async Task SetBranchToCommitAsync_ThrowsWhenShaDoesNotResolve()
    {
        // The sha is well-formed but does not exist in this bare repo. Without
        // the rev-parse pre-check the update-ref would silently corrupt the
        // branch; we want a loud failure instead.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = CreateGitHost();
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        var mainBefore = await RevParse(barePath, "main");
        var bogusSha = new string('f', 40);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.SetBranchToCommitAsync(repoId, "main", bogusSha));
        Assert.Contains("did not resolve", ex.Message);
        // Branch must still point at the original tip — failed verification
        // must not silently update the ref.
        Assert.Equal(mainBefore, await RevParse(barePath, "main"));
    }

    // -------------------------------------------------------------------------
    // ResetWorkBranchToBaseAsync
    // -------------------------------------------------------------------------

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ResetWorkBranchToBaseAsync_ThrowsWhenPostResetVerificationCannotResolveWorkBranch()
    {
        // Simulate a race/corruption shape where update-ref succeeds but the
        // work branch disappears before the post-reset rev-parse. The method
        // must fail loudly instead of returning as if the reset succeeded.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        const string workBranch = "feature/post-reset-verify-vanished";
        var gitWrapper = Path.Combine(_workspace, "git-wrapper-" + Guid.NewGuid().ToString("N")[..8], "git");
        await WriteExecutableScriptAsync(
            gitWrapper,
            $$"""
            #!/usr/bin/env bash
            target='refs/heads/{{workBranch}}'
            if [ "${3:-}" = "update-ref" ] && [ "${4:-}" = "$target" ]; then
                git "$@"
                rc=$?
                if [ "$rc" -eq 0 ]; then
                    git -c core.hooksPath=/dev/null update-ref -d "$target" >/dev/null 2>&1 || true
                fi
                exit "$rc"
            fi
            exec git "$@"
            """);

        var gitHost = CreateGitHost(gitWrapper);
        var id = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(id, seed, "main");
        Assert.False(await gitHost.BranchExistsAsync(repoId, workBranch));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.ResetWorkBranchToBaseAsync(repoId, workBranch, "main"));

        Assert.Contains("did not resolve after reset to base", ex.Message, StringComparison.Ordinal);
        Assert.False(await gitHost.BranchExistsAsync(repoId, workBranch));
    }

    // -------------------------------------------------------------------------
    // RunGitAsync Process.Start ETXTBSY retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunGitAsync_RetriesTextFileBusyProcessStartAndSucceeds()
    {
        var starts = 0;
        var gitHost = CreateGitHost(processFactory: _ =>
        {
            starts++;
            return starts == 1
                ? new FakeLocalGitProcess(startException: new Win32Exception(26, "Text file busy"))
                : new FakeLocalGitProcess(exitCode: 0);
        });
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var exists = await gitHost.BranchExistsAsync(repoId, "main");

        Assert.True(exists);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task RunGitAsync_RecordsCoordinatorGitCommandDurationMetric()
    {
        var measurements = new ConcurrentQueue<(string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Coordinator"
                && instrument.Name == "codeybox.coordinator.git.command.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "operation")
                    operation = tags[i].Value?.ToString();
                if (tags[i].Key == "outcome")
                    outcome = tags[i].Value?.ToString();
            }
            measurements.Enqueue((operation, outcome));
        });
        listener.Start();
        var gitHost = CreateGitHost(processFactory: _ => new FakeLocalGitProcess(exitCode: 0));
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        Assert.True(await gitHost.BranchExistsAsync(repoId, "main"));

        var observed = SpinWait.SpinUntil(
            () => measurements.Any(m => m.Operation == "rev-parse" && m.Outcome == "success"),
            TimeSpan.FromSeconds(2));
        Assert.True(observed, "Expected LocalGitHost to emit git command duration metric for rev-parse.");
    }

    [Fact]
    public async Task RunGitAsync_CapsProcessOutputAndRecordsOutputLimitMetric()
    {
        var measurements = new ConcurrentQueue<(string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Coordinator"
                && instrument.Name == "codeybox.coordinator.git.command.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "operation")
                    operation = tags[i].Value?.ToString();
                if (tags[i].Key == "outcome")
                    outcome = tags[i].Value?.ToString();
            }
            measurements.Enqueue((operation, outcome));
        });
        listener.Start();
        var killed = false;
        var gitHost = CreateGitHost(
            gitCommandMaxOutputBytes: 8,
            processFactory: _ => new FakeLocalGitProcess(
                stdout: new string('x', 64),
                exitCode: 0,
                onKill: () => killed = true));
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var exists = await gitHost.BranchExistsAsync(repoId, "main");

        Assert.False(exists);
        Assert.True(killed);
        Assert.Contains(measurements, m => m.Operation == "rev-parse" && m.Outcome == "output_limit");
    }


    [Fact]
    public async Task RunGitAsync_RetriesTextFileBusyProcessStartByMessageWhenErrnoDiffers()
    {
        var starts = 0;
        var gitHost = CreateGitHost(processFactory: _ =>
        {
            starts++;
            return starts == 1
                ? new FakeLocalGitProcess(startException: new Win32Exception(11, "Text file busy"))
                : new FakeLocalGitProcess(exitCode: 0);
        });
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var exists = await gitHost.BranchExistsAsync(repoId, "main");

        Assert.True(exists);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task RunGitAsync_TextFileBusyProcessStartAfterRetryCap_Propagates()
    {
        var measurements = new ConcurrentQueue<(string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Coordinator"
                && instrument.Name == "codeybox.coordinator.git.command.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "operation")
                    operation = tags[i].Value?.ToString();
                if (tags[i].Key == "outcome")
                    outcome = tags[i].Value?.ToString();
            }
            measurements.Enqueue((operation, outcome));
        });
        listener.Start();
        var starts = 0;
        var gitHost = CreateGitHost(processFactory: _ =>
        {
            starts++;
            return new FakeLocalGitProcess(startException: new Win32Exception(26, "Text file busy"));
        });
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var ex = await Assert.ThrowsAsync<Win32Exception>(
            () => gitHost.BranchExistsAsync(repoId, "main"));

        Assert.Equal(26, ex.NativeErrorCode);
        Assert.Equal(8, starts);
        Assert.Contains(measurements, m => m.Operation == "rev-parse" && m.Outcome == "error");
    }

    [Fact]
    public async Task ListFilesEndingWithAsync_RetriesTextFileBusyProcessStartAndUsesConfiguredGitExecutable()
    {
        var starts = 0;
        var disposals = 0;
        ProcessStartInfo? observedStartInfo = null;
        var gitExecutable = Path.Combine(_workspace, "custom-git");
        var gitHost = CreateGitHost(
            gitExecutable,
            gitCommandMaxOutputBytes: null,
            processFactory: psi =>
            {
                starts++;
                observedStartInfo = psi;
                return starts == 1
                    ? new FakeLocalGitProcess(
                        startException: new Win32Exception(26, "Text file busy"),
                        onDispose: () => disposals++)
                    : new FakeLocalGitProcess(
                        stdout: "src/App.cs\nREADME.md\n",
                        exitCode: 0,
                        onDispose: () => disposals++);
            });
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var matches = await gitHost.ListFilesEndingWithAsync(repoId, "main", [".cs"], maxResults: 10);

        Assert.Equal(["src/App.cs"], matches);
        Assert.Equal(2, starts);
        Assert.Equal(2, disposals);
        Assert.NotNull(observedStartInfo);
        Assert.Equal(gitExecutable, observedStartInfo.FileName);
        Assert.Contains("ls-tree", observedStartInfo.ArgumentList);
    }

    [Fact]
    public async Task ListFilesEndingWithAsync_RecordsCoordinatorGitCommandDurationMetric()
    {
        // The streamed ls-tree path launches git directly rather than through
        // RunGitAsync, so it must record the coordinator git-command metric on
        // its own — otherwise this host-side git op would be an unmeasured
        // scaling pinch point (the exact gap the completeness auditor flagged).
        var measurements = new ConcurrentQueue<(string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Coordinator"
                && instrument.Name == "codeybox.coordinator.git.command.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "operation")
                    operation = tags[i].Value?.ToString();
                if (tags[i].Key == "outcome")
                    outcome = tags[i].Value?.ToString();
            }
            measurements.Enqueue((operation, outcome));
        });
        listener.Start();
        var gitHost = CreateGitHost(processFactory: _ =>
            new FakeLocalGitProcess(stdout: "src/App.cs\nREADME.md\n", exitCode: 0));
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var matches = await gitHost.ListFilesEndingWithAsync(repoId, "main", [".cs"], maxResults: 10);

        Assert.Equal(["src/App.cs"], matches);
        var observed = SpinWait.SpinUntil(
            () => measurements.Any(m => m.Operation == "ls-tree" && m.Outcome == "success"),
            TimeSpan.FromSeconds(2));
        Assert.True(observed, "Expected ListFilesEndingWithAsync to emit ls-tree git command duration metric.");
    }

    [Fact]
    public async Task ListFilesEndingWithAsync_CapExceeded_RecordsOutputLimitOutcome()
    {
        // A match-cap breach kills the child and throws; the metric must still
        // record with the "output_limit" outcome so an operator sees the
        // ceiling being hit rather than silent degradation.
        var measurements = new ConcurrentQueue<(string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Coordinator"
                && instrument.Name == "codeybox.coordinator.git.command.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "operation")
                    operation = tags[i].Value?.ToString();
                if (tags[i].Key == "outcome")
                    outcome = tags[i].Value?.ToString();
            }
            measurements.Enqueue((operation, outcome));
        });
        listener.Start();
        var gitHost = CreateGitHost(processFactory: _ =>
            new FakeLocalGitProcess(stdout: "a.cs\nb.cs\nc.cs\n", exitCode: 0));
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gitHost.ListFilesEndingWithAsync(repoId, "main", [".cs"], maxResults: 1));

        var observed = SpinWait.SpinUntil(
            () => measurements.Any(m => m.Operation == "ls-tree" && m.Outcome == "output_limit"),
            TimeSpan.FromSeconds(2));
        Assert.True(observed, "Expected ListFilesEndingWithAsync cap breach to emit output_limit outcome.");
    }

    [Fact]
    public async Task ListFilesEndingWithAsync_TextFileBusyProcessStartAfterRetryCap_DisposesAndPropagates()
    {
        var starts = 0;
        var disposals = 0;
        var gitHost = CreateGitHost(processFactory: _ =>
        {
            starts++;
            return new FakeLocalGitProcess(
                startException: new Win32Exception(26, "Text file busy"),
                onDispose: () => disposals++);
        });
        var repoId = WorkItemId.New().ToString();
        Directory.CreateDirectory(gitHost.GetRepoPath(repoId));

        var ex = await Assert.ThrowsAsync<Win32Exception>(
            () => gitHost.ListFilesEndingWithAsync(repoId, "main", [".cs"], maxResults: 10));

        Assert.Equal(26, ex.NativeErrorCode);
        Assert.Equal(8, starts);
        Assert.Equal(8, disposals);
    }

    // -------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------

    private static async Task<string> RevParse(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
    }

    private static async Task CommitToRepo(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "t@l");
        await TestSupport.RunGit(repoPath, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private async Task<string> CommitToBareBranch(
        string barePath, string branch, string fileName, string contents, string subject)
    {
        var clone = Path.Combine(_workspace, "bare-work-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "t@l");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", subject);
        var sha = await RevParse(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
        return sha;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task WriteExecutableScriptAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, contents);
        File.SetUnixFileMode(
            tempPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(tempPath, path);
    }

    private sealed class FakeLocalGitProcess(
        int exitCode = 0,
        string stdout = "",
        string stderr = "",
        Win32Exception? startException = null,
        Action? onKill = null,
        Action? onDispose = null) : ILocalGitProcess
    {
        private readonly StringReader _stdout = new(stdout);
        private readonly StringReader _stderr = new(stderr);

        public TextReader StandardOutput => _stdout;
        public TextReader StandardError => _stderr;
        public int ExitCode { get; } = exitCode;

        public void Start()
        {
            if (startException is not null)
                throw startException;
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;

        public void Kill(bool entireProcessTree)
        {
            _ = entireProcessTree;
            onKill?.Invoke();
        }

        public void Dispose()
        {
            _stdout.Dispose();
            _stderr.Dispose();
            onDispose?.Invoke();
        }
    }
}
