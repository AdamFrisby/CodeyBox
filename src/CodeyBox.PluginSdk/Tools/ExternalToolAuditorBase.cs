using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared base for auditors that wrap an external binary: locate the tool,
/// build its argument list, run it against the work tree with a bounded
/// timeout, capture stdout/stderr separately, and map its output to findings.
///
/// A plugin author supplies four things — the tool name, its arguments, a
/// parser, and a severity map — and gets the shared behaviour for free:
/// absent binaries and failed executions are infrastructure failures
/// (<see cref="AuditUnavailableException"/>) naming the tool, never a passing
/// audit and never a finding against the diff; only "the tool ran" produces a
/// verdict. Findings always carry the producing tool, the rule id, and
/// file/line where the tool supplies them.
/// </summary>
public abstract class ExternalToolAuditorBase : IAuditor
{
    private const int CommandCannotExecuteExitCode = 126;
    private const int CommandNotFoundExitCode = 127;
    private const int UnavailableOutputTailMaxChars = 4096;
    private const int TitleMaxChars = 200;
    private const int MaxBuiltArguments = 256;
    private const int MessageValueMaxChars = 64;

    /// <summary>Per-stream capture cap for precondition probes (version checks, repository-suppression gates).</summary>
    protected const int ProbeMaxOutputBytes = 16 * 1024;

    // Precondition probes are liveness checks, not the scan: they never need
    // more than this and share the operator-configured timeout below it.
    private static readonly TimeSpan ProbeTimeoutCap = TimeSpan.FromSeconds(30);

    private static readonly Regex ToolVersionPattern = new(
        @"\d+\.\d+\.\d+[\w.\-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Each candidate is probed with -e (exists) and -L (symlink — catches a
    // dangling symlink that -e would miss) and echoed when present; the
    // script always exits 0 once it completes, so the exit code carries only
    // probe health while stdout carries the verdict. The "./" prefix keeps a
    // leading-dash name from being read as a test operator.
    private const string RepositoryFilePresenceScript =
        "for f in \"$@\"; do if [ -e \"./$f\" ] || [ -L \"./$f\" ]; then printf '%s\\n' \"$f\"; fi; done; exit 0";

    // Stricter variant for paths an audit tool will OPEN: the candidate must
    // be a regular file and no component of its path — leaf or any ancestor
    // directory — may be a symlink. [ -f ] follows links, so the walk strips
    // one component at a time (p=${p%/*}) and applies -L to each prefix; a
    // symlinked leaf OR a symlinked directory both stop the walk with p
    // non-empty and the path is not echoed. A path passing every check
    // resolves to a regular file canonically inside the worktree — a
    // repo-committed symlink cannot redirect the tool's read outside the
    // audited tree. (A hardlink stays invisible to any path-level check;
    // sandbox mounts keep the worktree on its own filesystem, so one cannot
    // reach outside it.) Same contract as above: exit 0 once complete, names
    // on stdout carry the verdict.
    private const string RepositoryRegularFilePresenceScript =
        "for f in \"$@\"; do "
        + "if [ -f \"./$f\" ]; then "
        + "p=$f; while [ -n \"$p\" ] && [ ! -L \"./$p\" ]; do "
        + "case $p in */*) p=${p%/*} ;; *) p= ;; esac; "
        + "done; "
        + "if [ -z \"$p\" ]; then printf '%s\\n' \"$f\"; fi; "
        + "fi; "
        + "done; exit 0";

    /// <summary>Stable name for logs and findings.</summary>
    public abstract string Name { get; }

    /// <summary>Implementation kind for observability storage. External tools report <c>"tool"</c>.</summary>
    public virtual string Kind => "tool";

    /// <summary>What the auditor needs to do its job.</summary>
    public virtual AuditCapabilities Required => AuditCapabilities.None;

    /// <summary>Bare binary name the auditor invokes (e.g. <c>"trivy"</c>). Validated fail-closed.</summary>
    protected abstract string ToolName { get; }

    /// <summary>Author-defined arguments for the tool (before any operator <c>ExtraArguments</c>).</summary>
    protected abstract IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options);

    /// <summary>Parses finished tool output. SARIF tools use <see cref="SarifToolOutputParser"/>.</summary>
    protected abstract IExternalToolOutputParser OutputParser { get; }

    /// <summary>
    /// Maps the tool's severity vocabulary to <see cref="AuditSeverity"/>.
    /// Defaults to <see cref="ExternalToolSeverityMapping.Default"/>; override
    /// for tools whose levels need different meanings. Raw tool levels are
    /// never passed through.
    /// </summary>
    protected virtual ExternalToolSeverityMapping SeverityMapping => ExternalToolSeverityMapping.Default;

    /// <summary>
    /// Resolves the effective options per invocation so operator config edits
    /// apply to later audits. Defaults to fresh built-in defaults.
    /// </summary>
    protected virtual Func<ExternalToolAuditorOptions> OptionsAccessor => static () => new ExternalToolAuditorOptions();

    /// <summary>
    /// Extra environment for the tool process, merged over the sandbox
    /// baseline environment at exec time. Override when a tool's behavior is
    /// env-controlled — e.g. to pin configuration that must not come from the
    /// audited repository. Values should be author-chosen constants, never
    /// untrusted data. Default: none.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string>? BuildToolEnvironment(ExternalToolAuditorOptions options)
        => null;

    /// <summary>
    /// Optional pinned-version declaration. Non-null makes
    /// <see cref="RunAsync"/> probe the tool with the pin's
    /// <see cref="ToolVersionPin.VersionProbeArguments"/> before every scan —
    /// after the presence check, before <see cref="VerifyToolAsync"/> — and
    /// fail closed on a missing binary, an unrecognised version string, or a
    /// version other than the configured or default expectation. Default
    /// null: no version precondition.
    /// </summary>
    protected virtual ToolVersionPin? VersionPin => null;

    /// <summary>
    /// Pre-scan precondition hook, invoked inside <see cref="RunAsync"/> after
    /// the tool's presence and declared <see cref="VersionPin"/> are confirmed
    /// and before the scan executes. Override for tool requirements the base
    /// cannot express — e.g. a repository-state gate — and throw
    /// <see cref="AuditUnavailableException"/> to fail closed: a failed
    /// precondition is infrastructure, never a pass.
    /// Use <see cref="RunBoundedProbeAsync"/> for precondition probes — it
    /// supplies the standard bounded envelope over
    /// <see cref="ExecToolBoundedAsync"/> — or the latter directly when the
    /// probe needs a nonstandard envelope;
    /// <see cref="ProbeRepositoryFilesPresentAsync"/> covers the common
    /// "does a repository-controlled file exist" gate and
    /// <see cref="ThrowIfBinaryMissingAsync"/> an auxiliary binary the tool
    /// shells out to. The default imposes no extra preconditions.
    /// </summary>
    protected virtual Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Optional per-run argument hook invoked inside <see cref="RunAsync"/>
    /// after options resolve and before argv is built — for arguments that
    /// need the <see cref="AuditContext"/> or bounded sandbox probes to
    /// compute (e.g. a baseline revision resolved from the work item's base
    /// branch, which <see cref="BuildToolArguments"/> cannot see). The
    /// returned arguments are appended between the author's
    /// <see cref="BuildToolArguments"/> output and the operator's
    /// <c>ExtraArguments</c>, and count toward the built-argument bound.
    /// The hook runs before the tool-presence, version, and
    /// <see cref="VerifyToolAsync"/> checks; use
    /// <see cref="RunBoundedProbeAsync"/> for probes so they share the
    /// timeout bounding and failure classification. Default: no arguments.
    /// </summary>
    protected virtual Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Optional per-run hook invoked inside <see cref="RunAsync"/> after the
    /// tool-presence, version, and <see cref="VerifyToolAsync"/> checks and
    /// immediately before the scan executes — for tools whose reports carry
    /// absolute paths but embed no working directory. Return the absolute
    /// directory the scan will actually run in: sandbox providers may
    /// translate <paramref name="workingDirectory"/> (the process provider
    /// maps it onto a host path), so resolve it with a bounded probe such as
    /// <c>pwd</c> through <see cref="RunBoundedProbeAsync"/>. The value is
    /// carried to the parser on <see cref="ExternalToolParseInput.ScanRoot"/>
    /// — a per-invocation channel, so output parsing stays a pure function
    /// of its input and concurrent audits on this (singleton) auditor cannot
    /// cross-contaminate each other's roots. Default: null.
    /// </summary>
    protected virtual Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => Task.FromResult<string?>(null);

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(context);

        var tool = ExternalToolNames.Validate(ToolName, nameof(ToolName));
        var options = OptionsAccessor() ?? new ExternalToolAuditorOptions();
        var contextArguments = await ResolveContextArgumentsAsync(
            sandbox, workingDirectory, context, options, ct).ConfigureAwait(false) ?? [];
        var argv = BuildArgv(tool, options, contextArguments);

        await ThrowIfBinaryMissingAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
        await VerifyToolVersionPinAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
        await VerifyToolAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
        var scanRoot = await ResolveScanRootAsync(sandbox, workingDirectory, context, options, ct)
            .ConfigureAwait(false);
        var result = await ExecToolAsync(sandbox, workingDirectory, tool, argv, options, ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw Unavailable(tool, "could not execute: the sandbox exec transport was unavailable", result);
        if (result.ExitCode is CommandCannotExecuteExitCode or CommandNotFoundExitCode)
            throw Unavailable(tool, "could not execute (exit 127/126 — binary missing or not executable in the sandbox)", result);
        if (!options.FindingsExitCodes.Contains(result.ExitCode))
            throw Unavailable(
                tool,
                $"could not run (exit {result.ExitCode}). Only exits [{string.Join(", ", options.FindingsExitCodes.Order())}] are declared as findings-producing; declare this tool's convention via {nameof(ExternalToolAuditorOptions.FindingsExitCodes)}.",
                result);

        var parsed = ParseOutput(tool, result, scanRoot, workingDirectory);
        var findings = ToFindings(tool, parsed, options);
        var truncated = findings.Count < parsed.Count;
        var passed = findings.All(f => f.Severity < AuditSeverity.Error);
        return new AuditResult(passed, findings, RawOutput: BuildRawOutput(result, options, parsed.Count - findings.Count, truncated));
    }

    private IReadOnlyList<string> BuildArgv(
        string tool,
        ExternalToolAuditorOptions options,
        IReadOnlyList<string> contextArguments)
    {
        var built = BuildToolArguments(options) ?? [];
        if (built.Count + contextArguments.Count > MaxBuiltArguments)
            throw new InvalidOperationException(
                $"Auditor '{Name}' built {built.Count + contextArguments.Count} tool arguments, exceeding the bound of {MaxBuiltArguments}.");
        if (options.ExtraArguments.Count > ExternalToolAuditorOptions.MaxExtraArguments)
            throw new InvalidOperationException(
                $"Auditor '{Name}' was configured with {options.ExtraArguments.Count} extra arguments, exceeding the bound of {ExternalToolAuditorOptions.MaxExtraArguments}.");

        var argv = new List<string>(1 + built.Count + contextArguments.Count + options.ExtraArguments.Count) { tool };
        argv.AddRange(built);
        argv.AddRange(contextArguments);
        argv.AddRange(options.ExtraArguments);
        return argv;
    }

    /// <summary>
    /// Bounded presence probe for a binary the auditor invokes — the
    /// declared <see cref="ToolName"/> itself (<see cref="RunAsync"/> calls
    /// this) or an auxiliary binary a <see cref="VerifyToolAsync"/> override
    /// needs (e.g. the toolchain a scanner shells out to). Fails closed: an
    /// exec-transport failure, a timeout, or any non-zero probe exit throws
    /// <see cref="AuditUnavailableException"/> naming the binary — "could
    /// not confirm presence" is never treated as "present".
    /// <paramref name="purpose"/> optionally states why the binary is
    /// required (e.g. which tool shells out to it); keep it an
    /// author-chosen constant.
    /// </summary>
    protected static async Task ThrowIfBinaryMissingAsync(
        ISandbox sandbox,
        string workingDirectory,
        string binary,
        ExternalToolAuditorOptions options,
        CancellationToken ct,
        string? purpose = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        ExternalToolNames.Validate(binary, nameof(binary));

        var reason = purpose is null ? string.Empty : $" ({SingleLine(purpose)})";
        var probe = await RunBoundedProbeAsync(
            sandbox,
            binary,
            $"presence check{reason}",
            ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", binary],
            workingDirectory,
            options,
            ct).ConfigureAwait(false);

        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: required binary '{binary}' is not installed in the audit sandbox."
                + $"{reason} Install it in the sandbox baseline; the check did not run, so this is "
                + "infrastructure, not a verdict on the diff.");
    }

    private Task<SandboxExecResult> ExecToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> argv,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var maxBytes = Math.Clamp(
            options.MaxOutputBytesPerStream,
            ExternalToolAuditorOptions.MinCapturedOutputBytes,
            ExternalToolAuditorOptions.MaxCapturedOutputBytes);
        return ExecToolBoundedAsync(
            sandbox,
            tool,
            "scan",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = maxBytes,
                MaxStderrBytes = maxBytes,
                KillOnOutputLimit = false,
                ExtraEnvironment = BuildToolEnvironment(options),
            },
            EffectiveTimeout(options),
            ct);
    }

    /// <summary>
    /// Effective per-invocation timeout: the configured value, or the default
    /// when it is unset or non-positive.
    /// </summary>
    protected static TimeSpan EffectiveTimeout(ExternalToolAuditorOptions options)
        => options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(ExternalToolAuditorOptions.DefaultTimeoutSeconds)
            : options.Timeout;

    /// <summary>
    /// Executes one bounded invocation on behalf of <paramref name="tool"/>:
    /// enforces <paramref name="timeout"/> and classifies a timeout or exec
    /// transport failure as <see cref="AuditUnavailableException"/> naming the
    /// tool — infrastructure, never a pass. Cooperative cancellation and
    /// sandbox-provisioning deferrals propagate unwrapped. The scan path uses
    /// this; <see cref="VerifyToolAsync"/> overrides use it for precondition
    /// probes. <paramref name="operation"/> names the invocation in failure
    /// messages (e.g. "scan", "version check"). <paramref name="timeout"/>
    /// must be a positive finite duration — <see cref="TimeSpan.Zero"/> would
    /// fire immediately and <see cref="Timeout.InfiniteTimeSpan"/> would
    /// silently disable the bound.
    /// </summary>
    protected static async Task<SandboxExecResult> ExecToolBoundedAsync(
        ISandbox sandbox,
        string tool,
        string operation,
        SandboxExec exec,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await sandbox.ExecAsync(exec, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' {operation} timed out after {FormatTimeout(timeout)}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' {operation} could not run: {SingleLine(ex.Message)}",
                ex);
        }
    }

    /// <summary>
    /// Runs one bounded precondition probe on behalf of
    /// <paramref name="tool"/>: builds the <see cref="SandboxExec"/> with the
    /// shared probe envelope — <see cref="ProbeMaxOutputBytes"/> per stream
    /// (or <paramref name="maxStdoutBytes"/> for probes that legitimately
    /// emit more), kill-on-limit, and the capped <see cref="ProbeTimeout"/>
    /// — and classifies an exec-transport failure as
    /// <see cref="AuditUnavailableException"/> naming the tool. Exit-code
    /// interpretation stays with the caller: what a completed probe's exit
    /// code means is probe-specific. <paramref name="operation"/> names the
    /// invocation in failure messages (e.g. "presence check", "version
    /// check"); keep it an author-chosen constant.
    /// </summary>
    protected static async Task<SandboxExecResult> RunBoundedProbeAsync(
        ISandbox sandbox,
        string tool,
        string operation,
        IReadOnlyList<string> argv,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct,
        int maxStdoutBytes = ProbeMaxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(options);

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            operation,
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = maxStdoutBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' {operation} could not run: the sandbox exec "
                + "transport was unavailable.");
        return result;
    }

    private async Task VerifyToolVersionPinAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (VersionPin is not { } pin)
            return;

        var configured = pin.ConfiguredExpectedVersion();
        var expected = ExtractToolVersion(
            string.IsNullOrWhiteSpace(configured) ? pin.DefaultExpectedVersion : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{pin.PluginId}:{ToolVersionPin.ExpectedVersionKey} "
                + $"to a {tool} release such as '{pin.DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var probeArguments = pin.VersionProbeArguments.Count > 0
            ? pin.VersionProbeArguments
            : ["--version"];
        var result = await RunBoundedProbeAsync(
            sandbox,
            tool,
            "version check",
            [tool, .. probeArguments],
            workingDirectory,
            options,
            ct).ConfigureAwait(false);

        var reported = (pin.VersionExtractor ?? ExtractToolVersion)(result.Stdout);
        if (result.ExitCode != 0
            || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{tool}' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different release changes the tool's checks and its "
                + $"findings; provision the pinned release or set {ToolVersionPin.ExpectedVersionKey} "
                + "to the version you provisioned.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Bounded presence probe for repository-controlled files at the audited
    /// worktree root — e.g. suppression files the audit subject could use to
    /// hide findings from the tool. Returns the subset of
    /// <paramref name="relativePaths"/> that exist (regular files and
    /// symlinks). Fails closed: an exec-transport failure or any non-zero
    /// probe exit throws <see cref="AuditUnavailableException"/> — "could not
    /// confirm absence" is never treated as "absent". Path entries must be
    /// relative; absolute paths, <c>..</c> segments, and embedded newlines are
    /// rejected so the probe can never escape the worktree or corrupt its
    /// one-name-per-line protocol.
    /// </summary>
    protected static Task<IReadOnlyList<string>> ProbeRepositoryFilesPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> relativePaths,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ProbeRepositoryPathsAsync(
            sandbox,
            workingDirectory,
            tool,
            relativePaths,
            options,
            RepositoryFilePresenceScript,
            "suppression check",
            "confirm repository-file absence",
            ct);

    /// <summary>
    /// Bounded presence probe for repository files an audit tool will OPEN —
    /// stricter than <see cref="ProbeRepositoryFilesPresentAsync"/>: a path
    /// counts as present only when it is a regular file AND no component of
    /// it (leaf or ancestor directory) is a symlink. A name passing that
    /// check resolves canonically inside the worktree, so a repo-committed
    /// symlink cannot redirect the tool's read to a file outside the audited
    /// tree — use this variant whenever the probed names will be handed to a
    /// tool as file arguments rather than merely checked for existence (a
    /// suppression-file gate wants the loose variant: a symlinked config
    /// file IS a suppression surface). Same fail-closed contract: transport
    /// failure or non-zero probe exit is <see cref="AuditUnavailableException"/>,
    /// never evidence about the files.
    /// </summary>
    protected static Task<IReadOnlyList<string>> ProbeRepositoryRegularFilesPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> relativePaths,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ProbeRepositoryPathsAsync(
            sandbox,
            workingDirectory,
            tool,
            relativePaths,
            options,
            RepositoryRegularFilePresenceScript,
            "file probe",
            "confirm the probed paths are regular files inside the worktree",
            ct);

    private static async Task<IReadOnlyList<string>> ProbeRepositoryPathsAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> relativePaths,
        ExternalToolAuditorOptions options,
        string script,
        string operation,
        string failureGoal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var requested = new HashSet<string>(StringComparer.Ordinal);
        var argv = new List<string>(relativePaths.Count + 4)
        {
            "sh", "-c", script, "sh",
        };
        foreach (var path in relativePaths)
        {
            var normalized = NormalizeProbePath(path);
            if (requested.Add(normalized))
                argv.Add(normalized);
        }
        if (requested.Count == 0)
            return [];

        var result = await RunBoundedProbeAsync(
            sandbox, tool, operation, argv, workingDirectory, options, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' {operation} could not {failureGoal} "
                + $"(exit {result.ExitCode}) — a failed probe is infrastructure, not evidence "
                + "about the probed files.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        // The probe echoes each accepted path, one per line; intersect with
        // the requested set — output beyond it is not trusted.
        var present = new List<string>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (requested.Contains(line))
                present.Add(line);
        }
        return present;
    }

    /// <summary>
    /// Resolves the merge-base of <c>HEAD</c> and the work item's
    /// <see cref="AuditContext.BaseBranch"/> — the
    /// <c>origin/&lt;base&gt;...HEAD</c> three-dot semantics the pipeline's own
    /// diff auditors use, so upstream state added to the base after the
    /// branch point is not misread as removed. Probes <c>origin/&lt;base&gt;</c>
    /// first, then the bare branch name, via bounded <c>git</c> executions;
    /// the <see cref="Validation.ValidateBranchName"/>-validated value reaches
    /// git only as argv entries, never through a shell. Every failure — an
    /// empty or invalid base branch, an unresolvable ref, no common ancestor —
    /// is a deterministic <see cref="AuditUnavailableException"/>:
    /// infrastructure, never a pass. <paramref name="baselineConfigHint"/> is
    /// an author-chosen constant appended to those failures so operators can
    /// find the auditor's explicit-baseline configuration knobs; keep it a
    /// fixed string, never untrusted data.
    /// </summary>
    protected async Task<string> ResolveMergeBaseAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        string baselineConfigHint,
        CancellationToken ct)
    {
        var baseBranch = context.BaseBranch?.Trim();
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has no baseline to compare against: no baseline "
                + "is configured and the work item carries no usable base branch for merge-base "
                + $"resolution. {baselineConfigHint}")
            { IsDeterministic = true };

        try
        {
            Validation.ValidateBranchName(baseBranch, nameof(context.BaseBranch));
        }
        catch (ArgumentException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' cannot resolve a baseline: {SingleLine(ex.Message)}. "
                + baselineConfigHint, ex)
            { IsDeterministic = true };
        }

        var baseBranchDisplay = SingleLine(baseBranch);

        // origin/<base> is the sandbox clone's canonical ref (the pipeline's
        // own diff auditors use origin/<base>...HEAD); the bare name covers
        // layouts that only carry a local branch.
        string? baseSha = null;
        foreach (var candidate in new[] { $"origin/{baseBranch}", baseBranch })
        {
            var probe = await GitProbeAsync(
                sandbox,
                workingDirectory,
                options,
                ["rev-parse", "--verify", $"{candidate}^{{commit}}"],
                ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                baseSha = ReadCommitSha(probe.Stdout);
                if (baseSha is not null)
                    break;
            }
        }

        if (baseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve base branch "
                + $"'{baseBranchDisplay}' (tried 'origin/{baseBranchDisplay}' and "
                + $"'{baseBranchDisplay}') in the audited repository — git must be available "
                + "and the base ref present in the sandbox clone. " + baselineConfigHint)
            { IsDeterministic = true };

        // Merge-base semantics match the pipeline's three-dot work diff: the
        // state the change actually diverged from.
        var mergeBase = await GitProbeAsync(
            sandbox,
            workingDirectory,
            options,
            ["merge-base", "HEAD", baseSha],
            ct).ConfigureAwait(false);
        var mergeBaseSha = mergeBase.ExitCode == 0 ? ReadCommitSha(mergeBase.Stdout) : null;
        if (mergeBaseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found no merge base between HEAD and "
                + $"base branch '{baseBranchDisplay}' — the audited history must share an "
                + "ancestor with the base ref. " + baselineConfigHint)
            { IsDeterministic = true };

        return mergeBaseSha;
    }

    private async Task<SandboxExecResult> GitProbeAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var argv = new List<string>(args.Count + 1) { "git" };
        argv.AddRange(args);
        return await RunBoundedProbeAsync(
            sandbox,
            ToolName,
            "baseline resolution",
            argv,
            workingDirectory,
            options,
            ct).ConfigureAwait(false);
    }

    private static string? ReadCommitSha(string stdout)
    {
        var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine is null)
            return null;
        try
        {
            Validation.ValidateCommitSha(firstLine, "git output");
        }
        catch (ArgumentException)
        {
            return null;
        }
        return firstLine;
    }

    /// <summary>
    /// True when the operator's
    /// <see cref="ExternalToolAuditorOptions.ExtraArguments"/> already supplies
    /// any of <paramref name="flags"/>: the separated "<c>--flag</c>" form, the
    /// attached "<c>--flag=value</c>" form, or — for a single-dash short
    /// option that takes a value — the joined "<c>-fvalue</c>"/"<c>-f=value</c>"
    /// form. Check this before emitting a flag whose repetition would conflict
    /// with or silently override the operator's own setting.
    /// </summary>
    protected static bool ExtraArgumentsSupplyFlag(
        ExternalToolAuditorOptions options,
        params string[] flags)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flags);
        foreach (var flag in flags)
        {
            if (string.IsNullOrEmpty(flag))
                continue;
            var attachedPrefix = flag + "=";
            var shortJoined = flag.Length == 2 && flag[0] == '-' && flag[1] != '-';
            foreach (var arg in options.ExtraArguments)
            {
                if (string.Equals(arg, flag, StringComparison.Ordinal)
                    || arg.StartsWith(attachedPrefix, StringComparison.Ordinal)
                    || (shortJoined
                        && arg.Length > flag.Length
                        && arg.StartsWith(flag, StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Appends "<paramref name="flag"/> <paramref name="value"/>" as two argv
    /// entries when the configured value is non-blank; blank values emit
    /// nothing. The value is passed as its own argv entry — it is never
    /// concatenated into a flag token or shell string.
    /// </summary>
    protected static void AddValueFlag(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(flag);
        args.Add(value.Trim());
    }

    /// <summary>
    /// Normalizes an <see cref="ExternalToolAuditorOptions.ExcludePaths"/>
    /// entry to its repository-relative form — the single source of truth for
    /// the finding-level filter and for auditors translating entries into
    /// tool flags. Returns null for blank entries; a preserved trailing
    /// <c>/</c> marks a directory-prefix exclusion.
    /// </summary>
    protected static string? NormalizeExcludePathEntry(string? entry)
    {
        var normalized = ExternalToolJsonHelpers.NormalizePath(entry).TrimStart('/');
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// The <see cref="ExternalToolAuditorOptions.ExcludePaths"/> matching
    /// contract — the single source of truth for both the finding-level
    /// filter below and auditors that must apply the same scope decision
    /// BEFORE the scan runs (e.g. when narrowing which files are passed as
    /// tool arguments). <paramref name="normalizedRepoRelativePath"/> must
    /// already be in repo-relative normalized form (<c>\</c>→<c>/</c>, no
    /// leading <c>/</c> or <c>./</c> — e.g.
    /// <c>ExternalToolJsonHelpers.NormalizePath</c> output); a raw or
    /// un-normalized path silently produces wrong decisions.
    /// An entry ending in <c>/</c> is a directory-prefix exclusion;
    /// any other entry excludes exactly that path.
    /// </summary>
    protected static bool IsNormalizedPathExcluded(
        string normalizedRepoRelativePath,
        ExternalToolAuditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(normalizedRepoRelativePath);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var entry in options.ExcludePaths)
        {
            var normalized = NormalizeExcludePathEntry(entry);
            if (normalized is null)
                continue;
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                if (normalizedRepoRelativePath.StartsWith(normalized, StringComparison.Ordinal))
                    return true;
            }
            else if (normalizedRepoRelativePath.Equals(normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Bounded timeout for precondition probes: shares the configured scan
    /// timeout below a fixed cap — a probe is a liveness check, not the scan.
    /// </summary>
    protected static TimeSpan ProbeTimeout(ExternalToolAuditorOptions options)
    {
        var timeout = EffectiveTimeout(options);
        return timeout > ProbeTimeoutCap ? ProbeTimeoutCap : timeout;
    }

    /// <summary>
    /// Extracts the first <c>major.minor.patch</c> version token from tool
    /// version output; null when the output carries none. A trailing sentence
    /// period (as in <c>CodeQL command-line toolchain release 2.27.1.</c>) is
    /// not part of the version and is stripped — a version never ends with a
    /// dot, while the suffix character class would otherwise absorb it and
    /// make an installed release compare unequal to its pin.
    /// </summary>
    protected static string? ExtractToolVersion(string output)
    {
        var match = ToolVersionPattern.Match(output);
        if (!match.Success)
            return null;
        var version = match.Value.TrimEnd('.');
        return version.Length == 0 ? null : version;
    }

    private static string NormalizeProbePath(string? path)
    {
        // Probe paths are baked into the sh script's "$@" list: refuse
        // anything that could escape the worktree or break the
        // one-name-per-line protocol.
        var normalized = ExternalToolJsonHelpers.NormalizePath(path);
        if (normalized.Length == 0
            || normalized[0] == '/'
            || normalized.IndexOf('\n') >= 0
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new ArgumentException(
                "Repository probe paths must be relative paths inside the worktree.",
                nameof(path));
        return normalized;
    }

    /// <summary>
    /// Single-lines a (possibly configured) value for an exception message
    /// and bounds it to <see cref="MessageValueMaxChars"/> chars — operator
    /// values echoed into failure reasons never run unbounded. Blank or
    /// null values render as <c>(empty)</c>.
    /// </summary>
    protected static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }

    private IReadOnlyList<ExternalToolFinding> ParseOutput(
        string tool, SandboxExecResult result, string? scanRoot, string? workingDirectory)
    {
        try
        {
            return OutputParser.Parse(
                new ExternalToolParseInput(
                    tool, result.Stdout, result.Stderr, result.ExitCode,
                    ScanRoot: scanRoot, WorkingDirectory: workingDirectory)) ?? [];
        }
        catch (ExternalToolParseException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' ran but its output could not be parsed: {SingleLine(ex.Message)}",
                ex);
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' ran but its output parser failed: {SingleLine(ex.Message)}",
                ex);
        }
    }

    private List<AuditFinding> ToFindings(
        string tool,
        IReadOnlyList<ExternalToolFinding> parsed,
        ExternalToolAuditorOptions options)
    {
        var mapping = SeverityMapping ?? ExternalToolSeverityMapping.Default;
        var maxFindings = Math.Max(1, options.MaxFindings);
        var findings = new List<AuditFinding>(Math.Min(parsed.Count, maxFindings));

        foreach (var item in parsed)
        {
            if (findings.Count >= maxFindings)
                break;
            if (item is null)
                continue;
            if (IsRuleFiltered(item, options) || IsPathExcluded(item, options))
                continue;

            var severity = mapping.Map(item.SeverityLevel);
            if (severity < options.MinimumSeverity)
                continue;

            findings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: severity,
                Title: BuildTitle(item),
                Description: BuildDescription(tool, item),
                Location: BuildLocation(item)));
        }

        return findings;
    }

    private static bool IsRuleFiltered(ExternalToolFinding item, ExternalToolAuditorOptions options)
    {
        if (options.ExcludedRules.Count > 0
            && !string.IsNullOrWhiteSpace(item.RuleId)
            && options.ExcludedRules.Contains(item.RuleId))
            return true;
        return options.IncludedRules.Count > 0
            && (string.IsNullOrWhiteSpace(item.RuleId) || !options.IncludedRules.Contains(item.RuleId));
    }

    private static bool IsPathExcluded(ExternalToolFinding item, ExternalToolAuditorOptions options)
    {
        if (options.ExcludePaths.Count == 0 || string.IsNullOrWhiteSpace(item.Path))
            return false;
        return IsNormalizedPathExcluded(NormalizeFindingPath(item.Path), options);
    }

    private static string BuildTitle(ExternalToolFinding item)
    {
        var firstLine = FirstLine(item.Message);
        if (string.IsNullOrWhiteSpace(item.RuleId))
            return Truncate(firstLine.Length == 0 ? "tool finding" : firstLine, TitleMaxChars);
        return firstLine.Length == 0
            ? SanitizeSingleLine(item.RuleId, TitleMaxChars)
            : Truncate($"{SanitizeSingleLine(item.RuleId, 80)}: {firstLine}", TitleMaxChars);
    }

    private string BuildDescription(string tool, ExternalToolFinding item)
    {
        var builder = new StringBuilder();
        builder.Append("Tool: ").Append(tool).Append('\n');
        builder.Append("Rule: ").Append(string.IsNullOrWhiteSpace(item.RuleId) ? "(none)" : SanitizeSingleLine(item.RuleId, 160)).Append('\n');
        builder.Append("Severity (tool): ").Append(string.IsNullOrWhiteSpace(item.SeverityLevel) ? "(none)" : SanitizeSingleLine(item.SeverityLevel, 80)).Append('\n');
        var location = BuildLocation(item);
        if (location is not null)
            builder.Append("Location: ").Append(location).Append('\n');
        builder.Append('\n').Append(StripEscapes(item.Message).Trim());
        return builder.ToString().TrimEnd();
    }

    private static string? BuildLocation(ExternalToolFinding item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
            return null;
        var path = SanitizeSingleLine(NormalizeFindingPath(item.Path), 512);
        return item.Line is > 0 ? $"{path}:{item.Line}" : path;
    }

    private static string NormalizeFindingPath(string? path)
        => ExternalToolJsonHelpers.NormalizePath(path).TrimStart('/');

    private static string BuildRawOutput(
        SandboxExecResult result,
        ExternalToolAuditorOptions options,
        int droppedFindings,
        bool findingsTruncated)
    {
        var maxBytes = Math.Clamp(
            options.MaxOutputBytesPerStream,
            ExternalToolAuditorOptions.MinCapturedOutputBytes,
            ExternalToolAuditorOptions.MaxCapturedOutputBytes);
        var stdout = result.Stdout;
        if (result.StdoutLimitExceeded)
            stdout += $"\n[stdout truncated after {maxBytes} bytes]";
        var stderr = result.Stderr;
        if (result.StderrLimitExceeded)
            stderr += $"\n[stderr truncated after {maxBytes} bytes]";

        string combined;
        if (string.IsNullOrEmpty(stderr))
            combined = stdout;
        else if (string.IsNullOrEmpty(stdout))
            combined = stderr;
        else
            combined = stdout + "\n" + stderr;

        if (findingsTruncated)
            combined += $"\n[findings truncated: {droppedFindings} finding(s) beyond MaxFindings {Math.Max(1, options.MaxFindings)} were dropped]";
        return combined;
    }

    private static AuditUnavailableException Unavailable(string tool, string reason, SandboxExecResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr + "\n" + result.Stdout;
        var detail = string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : $": {Tail(SingleLine(output), UnavailableOutputTailMaxChars)}";
        return new AuditUnavailableException(
            $"could-not-verify: audit tool '{tool}' {reason}{detail}",
            result.ExitCode,
            output);
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;
        var stripped = StripEscapes(message);
        var newline = stripped.IndexOf('\n');
        var first = (newline < 0 ? stripped : stripped[..newline]).Trim().Replace('\r', ' ');
        return first.Trim();
    }

    private static string SanitizeSingleLine(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var stripped = StripEscapes(value).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return Truncate(stripped, maxChars);
    }

    // Tool output is untrusted input that ends up in rendered findings:
    // strip terminal escape bytes and every other control character (C1
    // CSI, BEL, stray CR, …) so a scanner cannot inject control sequences
    // into operator-facing output. The newline separating message lines
    // survives — descriptions are multi-line.
    private static string StripEscapes(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\n' || !char.IsControl(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static string Truncate(string value, int maxChars)
        => ExternalToolJsonHelpers.Truncate(value, maxChars);

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    /// <summary>
    /// Flattens tool output to a single line for failure messages. Every
    /// control character — newlines, tabs, ESC and other terminal escape
    /// bytes — becomes a space so untrusted tool output cannot inject
    /// sequences into logged messages or persisted failure reasons.
    /// </summary>
    protected static string SingleLine(string message)
        => ExternalToolJsonHelpers.SingleLine(message);

    private static string FormatTimeout(TimeSpan timeout)
        => timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:0.##} minutes"
            : $"{timeout.TotalSeconds:0.##} seconds";
}
