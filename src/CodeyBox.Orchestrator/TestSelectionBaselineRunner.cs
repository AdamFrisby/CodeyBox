using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Failure running one baseline-production job. Recorded on the project
/// status and emitted as a structured event — never thrown to merge or audit
/// callers (the selector falls back to the full suite).
/// </summary>
public sealed class TestSelectionBaselineRunException : Exception
{
    public TestSelectionBaselineRunException(string message)
        : base(message)
    {
    }

    public TestSelectionBaselineRunException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One finished production run: the measured commit (the live base tip the
/// job resolved, which is the source of truth — not the scheduling hint),
/// the exact artifact bytes, and the test count parsed from them.
/// </summary>
public sealed record TestSelectionBaselineRunResult(
    string Commit,
    DateTimeOffset ProducedAtUtc,
    string Json,
    int TestCount);

/// <summary>
/// Executes one scheduled baseline-production job. The default implementation
/// runs the producer binary inside a freshly provisioned sandbox (through the
/// admission-controlled provider, so the job consumes the global sandbox
/// budget like any other phase) against a clean checkout of the live base tip.
/// </summary>
public interface ITestSelectionBaselineJobRunner
{
    Task<TestSelectionBaselineRunResult> RunAsync(
        TestSelectionBaselineRequest request,
        TimeSpan timeout,
        CancellationToken ct = default);
}

/// <summary>
/// Sandboxed baseline production: provisions one sandbox via the shared
/// admission-controlled provider (so the job respects the global sandbox cap
/// and queues behind work-item phases instead of starving them), clones the
/// live base tip, runs the baked-in producer binary there, and reads the
/// artifact back through a byte-capped <c>cat</c> before host-side validation.
/// Building a merged checkout executes repo-authored build logic (untrusted),
/// so production never runs on the orchestrator host itself.
/// </summary>
public sealed class SandboxTestSelectionBaselineRunner : ITestSelectionBaselineJobRunner
{
    internal const string CheckoutDir = "/work/repo";
    internal const string GuestOutputPath = "/work/baseline.json";
    internal const int ExecOutputCapBytes = 1 * 1024 * 1024;

    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly IProjectRepository _projects;
    private readonly PipelineOptions _pipelineOptions;
    private readonly Func<TestSelectionBaselineProductionOptions> _productionOptions;
    private readonly Func<CoverageTestSelectionOptions> _coverageOptions;
    private readonly ILogger<SandboxTestSelectionBaselineRunner> _log;
    private readonly TimeProvider _clock;

    public SandboxTestSelectionBaselineRunner(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        IProjectRepository projects,
        PipelineOptions pipelineOptions,
        Func<TestSelectionBaselineProductionOptions> productionOptions,
        Func<CoverageTestSelectionOptions> coverageOptions,
        ILogger<SandboxTestSelectionBaselineRunner> log,
        TimeProvider? clock = null)
    {
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _pipelineOptions = pipelineOptions ?? throw new ArgumentNullException(nameof(pipelineOptions));
        _productionOptions = productionOptions ?? throw new ArgumentNullException(nameof(productionOptions));
        _coverageOptions = coverageOptions ?? throw new ArgumentNullException(nameof(coverageOptions));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<TestSelectionBaselineRunResult> RunAsync(
        TestSelectionBaselineRequest request,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var productionOptions = _productionOptions();
        var producerBinary = ValidateProducerBinary(productionOptions.ProducerBinary);
        var project = await _projects.GetAsync(new ProjectId(request.ProjectId), ct)
            .ConfigureAwait(false);

        // The scheduled merge commit is a trigger hint; the measured commit is
        // the live base tip at execution. Squash-merge upstreams mint a new
        // main commit that is NOT a descendant of the local merge commit, and
        // a superseding merge may already have moved main — measuring the tip
        // keeps the artifact ancestry-reachable from later audits either way.
        var tip = await ResolveLiveBaseTipAsync(request, ct).ConfigureAwait(false);

        var access = _gitHost.GetSandboxAccess(request.RepositoryId);
        var spec = new SandboxSpec
        {
            ImageReference = _pipelineOptions.SandboxImageReference,
            Mounts = [.. access.Mounts, new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true }],
            Environment = new Dictionary<string, string>(),
            Network = new SandboxNetworkPolicy
            {
                HostGitEndpoint = access.Network.HostGitEndpoint,
                AllowedHosts = [],
                ProfileName = project?.NetworkProfiles.AuditTool,
            },
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingPhase = "test-selection-baseline",
        };

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct).ConfigureAwait(false);
        await RunSandboxGitAsync(sandbox, ["git", "clone", "--", access.CloneUrlInsideSandbox, CheckoutDir], "clone", ct)
            .ConfigureAwait(false);
        await RunSandboxGitAsync(sandbox, ["git", "-C", CheckoutDir, "checkout", "--detach", tip], "checkout", ct)
            .ConfigureAwait(false);

        var produce = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [producerBinary, "produce", "--repo", CheckoutDir, "--output", GuestOutputPath, "--commit", tip],
            MaxStdoutBytes = ExecOutputCapBytes,
            MaxStderrBytes = ExecOutputCapBytes,
        }, ct).ConfigureAwait(false);
        if (!produce.Success)
        {
            throw new TestSelectionBaselineRunException(
                $"baseline producer exited {produce.ExitCode} for {request.ProjectId}@{tip}: {Tail(produce.Stderr)}{Tail(produce.Stdout)}");
        }

        var coverageOptions = _coverageOptions();
        var maxBytes = (int)Math.Min(coverageOptions.MaxBaselineBytes, int.MaxValue);
        var read = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", GuestOutputPath],
            MaxStdoutBytes = maxBytes,
            MaxStderrBytes = ExecOutputCapBytes,
        }, ct).ConfigureAwait(false);
        if (!read.Success || string.IsNullOrWhiteSpace(read.Stdout))
        {
            throw new TestSelectionBaselineRunException(
                $"baseline artifact unreadable for {request.ProjectId}@{tip} (exit {read.ExitCode}): {Tail(read.Stderr)}");
        }

        TestSelectionBaseline baseline;
        try
        {
            baseline = TestSelectionBaselineParser.Parse(
                read.Stdout, BaselineReadLimits.FromOptions(coverageOptions));
        }
        catch (Exception ex)
        {
            throw new TestSelectionBaselineRunException(
                $"baseline artifact for {request.ProjectId}@{tip} failed validation: {ex.Message}", ex);
        }

        if (!string.Equals(baseline.Commit, tip, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Baseline producer for project {ProjectId} recorded commit {Recorded} for requested tip {Tip}; storing under the recorded commit",
                request.ProjectId, baseline.Commit, tip);
        }

        return new TestSelectionBaselineRunResult(
            baseline.Commit,
            _clock.GetUtcNow(),
            read.Stdout,
            baseline.Tests.Count);
    }

    private async Task<string> ResolveLiveBaseTipAsync(
        TestSelectionBaselineRequest request,
        CancellationToken ct)
    {
        try
        {
            var tip = await _gitHost.ResolveCommitAsync(request.RepositoryId, request.BaseBranch, ct)
                .ConfigureAwait(false);
            if (!IsHexSha(tip))
                throw new TestSelectionBaselineRunException(
                    $"base branch '{request.BaseBranch}' did not resolve to a commit sha for project {request.ProjectId}");
            if (!string.Equals(tip, request.Commit, StringComparison.Ordinal))
            {
                _log.LogInformation(
                    "Baseline job for project {ProjectId}: base moved {Scheduled} → {Tip} since schedule; measuring the live tip",
                    request.ProjectId, request.Commit, tip);
            }

            return tip;
        }
        catch (TestSelectionBaselineRunException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The bare repo may be mid-compaction; fall back to the scheduled
            // merge commit, which the merge phase just verified reachable.
            _log.LogWarning(
                ex,
                "Baseline job for project {ProjectId}: could not resolve live base tip, falling back to scheduled commit {Commit}",
                request.ProjectId, request.Commit);
            if (!IsHexSha(request.Commit))
                throw new TestSelectionBaselineRunException(
                    $"scheduled commit '{request.Commit}' for project {request.ProjectId} is not a commit sha", ex);
            return request.Commit;
        }
    }

    private static async Task RunSandboxGitAsync(
        ISandbox sandbox,
        IReadOnlyList<string> argv,
        string label,
        CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            MaxStdoutBytes = ExecOutputCapBytes,
            MaxStderrBytes = ExecOutputCapBytes,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new TestSelectionBaselineRunException($"git {label} failed (exit {result.ExitCode}): {Tail(result.Stderr)}");
    }

    internal static string ValidateProducerBinary(string? producerBinary)
    {
        if (string.IsNullOrWhiteSpace(producerBinary) || producerBinary.IndexOf('\0') >= 0)
            throw new TestSelectionBaselineRunException("Producer binary must be a bare name or an absolute guest path.");
        var trimmed = producerBinary.Trim();
        if (trimmed.StartsWith('/') || !trimmed.Contains('/'))
            return trimmed;
        throw new TestSelectionBaselineRunException(
            $"Refusing producer binary '{trimmed}': use a bare name (PATH-resolved) or an absolute guest path.");
    }

    internal static bool IsHexSha(string value)
    {
        if (value.Length is not (40 or 64))
            return false;
        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
                return false;
        }

        return true;
    }

    private static string Tail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var sanitized = new string(value.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? '_' : c).ToArray()).Trim();
        return sanitized.Length <= 2000 ? sanitized : sanitized[^2000..];
    }
}
