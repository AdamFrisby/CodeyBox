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
    private const int MessageValueMaxChars = ToolOutputText.MessageValueMaxChars;

    /// <summary>Per-stream capture cap for precondition probes (version checks, repository-suppression gates).</summary>
    protected const int ProbeMaxOutputBytes = 16 * 1024;

    // Precondition probes are liveness checks, not the scan: they never need
    // more than this and share the operator-configured timeout below it.
    private static readonly TimeSpan ProbeTimeoutCap = TimeSpan.FromSeconds(30);

    private static readonly Regex ToolVersionPattern = new(
        @"\d+\.\d+\.\d+[\w.\-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Per-run scratch directory minted by RunAsync itself before any hook or
    // argv build, and consumed through PerRunTempDirectoryPath by the hooks
    // that need it. AsyncLocal — not a field — because auditor instances are
    // shared singletons: concurrent audits must not see each other's paths.
    // The base owns the lifecycle (mint before ResolveContextArgumentsAsync,
    // clear when the run finishes) so plugins can never mint at the wrong
    // point in the call order — e.g. after the argv that names the path was
    // already frozen.
    private readonly AsyncLocal<string?> _perRunTempDirectoryPath = new();

    // Each candidate is probed with -e (exists) and -L (symlink — catches a
    // dangling symlink that -e would miss) and echoed when present; the
    // script always exits 0 once it completes, so the exit code carries only
    // probe health while stdout carries the verdict. The "./" prefix keeps a
    // leading-dash name from being read as a test operator.
    private const string RepositoryFilePresenceScript =
        "for f in \"$@\"; do if [ -e \"./$f\" ] || [ -L \"./$f\" ]; then printf '%s\\n' \"$f\"; fi; done; exit 0";

    // Each glob is a `find -path` operand matched against the whole
    // "./"-relative worktree path — `*`/`?` there match '/' too, so
    // "*gitleaks.toml*" covers the name at any depth. The "./" prefix keeps
    // a non-wildcard-led glob anchored the same way the C#-side re-filter
    // sees it (it strips "./" before matching). `.git` is pruned: object
    // storage is transport metadata, not audited source. A find failure —
    // an unreadable directory, a resource error — truncates the walk, so
    // the script accumulates find's exit status rather than reporting a
    // partial enumeration as "no matches": the exit code carries probe
    // health, stdout the matched paths.
    private const string RepositoryPathGlobPresenceScript =
        "rc=0; for g in \"$@\"; do find . -name .git -prune -o -path \"./$g\" -print || rc=1; done; exit $rc";

    // Ambient GIT_* variables would re-point or re-configure any git an
    // exec spawns: GIT_DIR/GIT_WORK_TREE redirect the repository,
    // GIT_CONFIG_PARAMETERS and the GIT_CONFIG_COUNT/GIT_CONFIG_KEY_*/
    // GIT_CONFIG_VALUE_* family inject config (e.g. diff.<name>.textconv,
    // which a committed .gitattributes `diff=<name>` re-combines with to
    // substitute patch content), GIT_CONFIG_GLOBAL/GIT_CONFIG_SYSTEM take
    // config file paths from the baseline environment, GIT_SSH_COMMAND a
    // command line, and the GIT_*_PATHSPECS variables rewrite how probe
    // pathspecs match (GIT_LITERAL_PATHSPECS would read `*` literally,
    // GIT_GLOB_PATHSPECS would stop it crossing '/'). None has a
    // legitimate role inside an audit sandbox, so every exec that can
    // reach git — scan, version check, or precondition probe — unsets
    // them.
    protected static readonly IReadOnlyList<string> GitEnvironmentRemovals =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_SYSTEM",
        "GIT_SSH_COMMAND",
        "GIT_LITERAL_PATHSPECS",
        "GIT_GLOB_PATHSPECS",
        "GIT_NOGLOB_PATHSPECS",
        "GIT_ICASE_PATHSPECS",
    ];

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
    /// Environment variable names that must not reach the tool process even
    /// when the sandbox baseline exports them — e.g. variables carrying a
    /// config file path the tool would honor above the auditor's own pinned
    /// configuration, bypassing whatever guard covers the equivalent argv
    /// flag. Providers apply removals after the baseline and
    /// <see cref="BuildToolEnvironment"/> merges, so a removal wins over
    /// both. Author-chosen constants only. Default: none.
    /// </summary>
    protected virtual IReadOnlyList<string> BuildToolEnvironmentRemovals(ExternalToolAuditorOptions options)
        => [];

    /// <summary>
    /// Optional pinned-version declaration. Non-null makes
    /// <see cref="RunAsync"/> probe the pin's binary — the tool itself, or
    /// <see cref="ToolVersionPin.ProbedBinary"/> when the version-pinned
    /// component is a separate binary the driver execs — with the pin's
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
    /// and before the scan executes. The scan argv and the per-run scratch
    /// directory (<see cref="PerRunTempDirectoryPath"/>) are already built
    /// when this runs. Override for tool requirements the base
    /// cannot express — e.g. a repository-state gate — and throw
    /// <see cref="AuditUnavailableException"/> to fail closed: a failed
    /// precondition is infrastructure, never a pass.
    /// Use <see cref="ExecToolBoundedAsync"/> for precondition probes so they
    /// get the same timeout bounding and failure classification as the scan;
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
    /// <see cref="ExecToolBoundedAsync"/> for probes so they share the
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
    /// scan exits with a declared findings-producing code and before
    /// <see cref="OutputParser"/> runs — the seam for tools whose report
    /// must not ride the captured scan streams. The default feeds the parser
    /// the captured stdout/stderr verbatim. Override when routing the report
    /// over captured output would carry tool-controlled bytes into
    /// <see cref="AuditResult.RawOutput"/>, unavailability messages, and
    /// webhooks — e.g. a report format that embeds matched source snippets
    /// (literal secrets). Fetch it instead through a separate bounded
    /// sandbox read (a file the tool wrote, under
    /// <see cref="PerRunTempDirectoryPath"/> or elsewhere) and return the
    /// <see cref="ExternalToolParseInput"/> the parser should see; the
    /// report bytes then reach only the parser, never the persisted raw
    /// output or failure text.
    /// </summary>
    /// <param name="argv">
    /// The argv the scan was actually invoked with (argv[0] is the tool
    /// name) — hooks deciding where the report lives should read it from
    /// here so the decision is exactly what the run produced, not a second
    /// read of scoped config that a hot reload could have flipped since
    /// <see cref="ResolveContextArgumentsAsync"/> built the arguments.
    /// </param>
    protected virtual Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
        => Task.FromResult(new ExternalToolParseInput(
            tool, result.Stdout, result.Stderr, result.ExitCode,
            ScanRoot: scanRoot, WorkingDirectory: workingDirectory));

    /// <summary>
    /// Optional per-run hook invoked inside <see cref="RunAsync"/> after the
    /// tool-presence, version, and <see cref="VerifyToolAsync"/> checks and
    /// immediately before the scan executes — for tools whose reports carry
    /// absolute paths but embed no working directory. Return the absolute
    /// directory the scan will actually run in: sandbox providers may
    /// translate <paramref name="workingDirectory"/> (the process provider
    /// maps it onto a host path), so resolve it with a bounded probe such as
    /// <c>pwd</c> through <see cref="ExecToolBoundedAsync"/>. The value is
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

    /// <summary>
    /// The per-run scratch directory — <c>&lt;temp&gt;/codeybox-&lt;tool&gt;-&lt;guid&gt;</c> —
    /// that <see cref="RunAsync"/> mints for the current invocation before
    /// <see cref="ResolveContextArgumentsAsync"/> and <see cref="BuildToolArguments"/>
    /// run, so the same path can be named in the scan argv and prepared by
    /// <see cref="VerifyToolAsync"/>. The directory is NOT created by the
    /// mint — hooks create it inside the sandbox when they need it. The temp
    /// root is computed on the host but interpreted in the sandbox's path
    /// space; on the supported Linux layout both resolve under <c>/tmp</c>.
    /// VM sandboxes discard the directory with their temp area; on
    /// process-provider hosts it may accumulate — sweep it in a preparation
    /// step when that matters. Throws a deterministic
    /// <see cref="AuditUnavailableException"/> when read outside a run —
    /// there is no correct way to recover by inventing another path, because
    /// the argv would still name this one.
    /// </summary>
    protected string PerRunTempDirectoryPath
        => _perRunTempDirectoryPath.Value is { Length: > 0 } path
            ? path
            : throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' per-run scratch directory is only "
                + "available while a run is in progress.")
            { IsDeterministic = true };

    private static string MintPerRunTempDirectoryPath(string tool)
        => Path.Combine(Path.GetTempPath(), "codeybox-" + tool + "-" + Guid.NewGuid().ToString("N"));

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
        _perRunTempDirectoryPath.Value = MintPerRunTempDirectoryPath(tool);
        try
        {
            var contextArguments = await ResolveContextArgumentsAsync(
                sandbox, workingDirectory, context, options, ct).ConfigureAwait(false) ?? [];
            var argv = BuildArgv(tool, options, contextArguments);

            await ThrowIfBinaryMissingAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
            await VerifyToolVersionPinAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
            await VerifyToolAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
            var scanRoot = await ResolveScanRootAsync(sandbox, workingDirectory, context, options, ct)
                .ConfigureAwait(false);
            var result = await ExecToolAsync(sandbox, workingDirectory, tool, argv, options, ct).ConfigureAwait(false);

            // A dead exec transport is infrastructure loss, not audit
            // unavailability: propagate so the pipeline parks the item for
            // retry instead of terminal-failing it.
            if (result.ExecutionUnavailable)
                throw new SandboxExecutionUnavailableException(result.ExitCode);
            if (result.ExitCode is CommandCannotExecuteExitCode or CommandNotFoundExitCode)
                throw Unavailable(tool, "could not execute (exit 127/126 — binary missing or not executable in the sandbox)", result);
            if (!options.FindingsExitCodes.Contains(result.ExitCode))
                throw Unavailable(
                    tool,
                    $"could not run (exit {result.ExitCode}). Only exits [{string.Join(", ", options.FindingsExitCodes.Order())}] are declared as findings-producing; declare this tool's convention via {nameof(ExternalToolAuditorOptions.FindingsExitCodes)}.",
                    result);

            var parseInput = await ResolveParserInputAsync(
                    sandbox, workingDirectory, tool, options, result, argv, scanRoot, ct)
                .ConfigureAwait(false);
            var parsed = ParseOutput(tool, parseInput);
            var findings = ToFindings(tool, parsed, options);
            var truncated = findings.Count < parsed.Count;
            var passed = findings.All(f => f.Severity < AuditSeverity.Error);
            return new AuditResult(passed, findings, RawOutput: BuildRawOutput(result, options, parsed.Count - findings.Count, truncated));
        }
        finally
        {
            _perRunTempDirectoryPath.Value = null;
        }
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

        var probe = await ExecToolBoundedAsync(
            sandbox,
            binary,
            "presence check",
            new SandboxExec
            {
                Argv = ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", binary],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reason = purpose is null ? string.Empty : $" ({SingleLine(purpose)})";
        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
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
        var maxBytes = CapturedOutputLimit(options);
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
                EnvironmentVariablesToUnset = BuildToolEnvironmentRemovals(options) ?? [],
            },
            EffectiveTimeout(options),
            ct);
    }

    /// <summary>
    /// Reads a report file the tool wrote during its scan through a separate
    /// bounded sandbox read — the single implementation of the read-back
    /// <see cref="ResolveParserInputAsync"/> overrides perform for tools
    /// whose report must not ride the captured scan streams (it may embed
    /// matched source snippets, or the tool simply cannot stream it). The
    /// path travels to <c>cat</c> as its own argv entry, never through a
    /// shell; stdout is capped at the configured per-stream bound.
    /// Fail-closed: a dead exec transport, an oversized report (a clipped
    /// document is never parsed), or a missing/unreadable file is
    /// infrastructure — never a pass. The read's stdout is never copied
    /// into a failure message — only <c>cat</c>'s stderr is carried.
    /// </summary>
    /// <param name="reportPath">
    /// Report path as the sandbox sees it — typically under
    /// <see cref="PerRunTempDirectoryPath"/>.
    /// </param>
    /// <param name="oversizedScopeHint">
    /// Clause appended to the oversized-report failure telling the operator
    /// how to shrink the report — e.g. <c>"(Targets, Platforms)"</c>.
    /// Defaults to the <c>ExcludePaths</c> wording.
    /// </param>
    protected static async Task<ExternalToolParseInput> ReadReportFileParseInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        string reportPath,
        string? scanRoot,
        CancellationToken ct,
        string oversizedScopeHint = "with ExcludePaths")
    {
        var read = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "report read",
            new SandboxExec
            {
                // '--' terminates option parsing so a dash-leading report
                // path can never be read as a cat flag (and 'cat' alone can
                // never fall back to stdin).
                Argv = ["cat", "--", reportPath],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = CapturedOutputLimit(options),
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (read.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' report read could not run: the sandbox exec "
                + "transport was unavailable.");
        if (read.StdoutLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' wrote a report exceeding the "
                + $"{CapturedOutputLimit(options)}-byte capture bound — the report is fetched "
                + "through a bounded read, so an oversized one is infrastructure, never a partial "
                + $"parse. Raise MaxOutputBytesPerStream or narrow the scan {oversizedScopeHint}.")
            { IsDeterministic = true };
        if (read.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' completed its scan but produced no readable "
                + $"report file (exit {read.ExitCode}) — a completed scan must leave a report, so "
                + "this is infrastructure, not a verdict on the diff.",
                read.ExitCode,
                read.Stderr);

        return new ExternalToolParseInput(
            tool, read.Stdout, result.Stderr, result.ExitCode,
            ScanRoot: scanRoot, WorkingDirectory: workingDirectory);
    }

    /// <summary>
    /// The configured per-stream capture bound clamped to the SDK floor and
    /// ceiling — the single computation shared by the scan exec, report-file
    /// reads, and raw-output truncation labelling.
    /// </summary>
    protected static int CapturedOutputLimit(ExternalToolAuditorOptions options)
        => Math.Clamp(
            options.MaxOutputBytesPerStream,
            ExternalToolAuditorOptions.MinCapturedOutputBytes,
            ExternalToolAuditorOptions.MaxCapturedOutputBytes);

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

    private async Task VerifyToolVersionPinAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (VersionPin is not { } pin)
            return;

        // The pin's subject is normally the tool itself, but a driver/engine
        // split (iwyu_tool → include-what-you-use) pins the binary the driver
        // execs. A different probed binary gets the same presence check the
        // tool received — a missing engine is "not installed", not merely an
        // unparseable banner.
        var probedBinary = pin.ProbedBinary is { Length: > 0 } probed
            ? ExternalToolNames.Validate(probed, nameof(pin.ProbedBinary))
            : tool;
        if (!string.Equals(probedBinary, tool, StringComparison.Ordinal))
            await ThrowIfBinaryMissingAsync(
                sandbox, workingDirectory, probedBinary, options, ct,
                $"audit tool '{tool}' execs it during the scan").ConfigureAwait(false);

        var configured = pin.ConfiguredExpectedVersion();
        var expected = ExtractToolVersion(
            string.IsNullOrWhiteSpace(configured) ? pin.DefaultExpectedVersion : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{pin.PluginId}:{ToolVersionPin.ExpectedVersionKey} "
                + $"to a {probedBinary} release such as '{pin.DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var probeArguments = pin.VersionProbeArguments.Count > 0
            ? pin.VersionProbeArguments
            : ["--version"];
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [probedBinary, .. probeArguments],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
                // The removals hook's contract is "must not reach the tool
                // process" — the version check execs the tool too.
                EnvironmentVariablesToUnset = BuildToolEnvironmentRemovals(options) ?? [],
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reported = (pin.VersionExtractor ?? ExtractToolVersion)(result.Stdout);
        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);
        if (result.ExitCode != 0
            || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{probedBinary}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{probedBinary}' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{probedBinary}' is version {reported}, but this auditor is "
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
    /// symlinks). Fails closed: an exec-transport failure throws
    /// <see cref="SandboxExecutionUnavailableException"/> (retriable
    /// infrastructure loss) and any non-zero probe exit throws
    /// <see cref="AuditUnavailableException"/> — "could not
    /// confirm absence" is never treated as "absent". Path entries must be
    /// relative; absolute paths, <c>..</c> segments, and embedded newlines are
    /// rejected so the probe can never escape the worktree or corrupt its
    /// one-name-per-line protocol.
    /// </summary>
    protected static async Task<IReadOnlyList<string>> ProbeRepositoryFilesPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> relativePaths,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (await ExecRepositoryProbeAsync(
                sandbox, workingDirectory, tool,
                RepositoryFilePresenceScript, relativePaths, NormalizeProbePath,
                "repository-file", options, ct).ConfigureAwait(false)
            is not { } probe)
            return [];

        // The probe echoes each present path, one per line; intersect with
        // the requested set — output beyond it is not trusted.
        var present = new List<string>();
        foreach (var line in SplitProbeLines(probe.Result.Stdout))
        {
            if (probe.Requested.Contains(line))
                present.Add(line);
        }
        return present;
    }

    /// <summary>
    /// Bounded probe for repository-controlled paths matching an
    /// author-declared glob at ANY worktree depth — e.g. a filename family
    /// the audited tool exempts from its scan wherever it appears
    /// (<c>*gitleaks.toml*</c>), which the root-only
    /// <see cref="ProbeRepositoryFilesPresentAsync"/> cannot see. Globs are
    /// author-declared constants in <c>find -path</c>/git-pathspec syntax —
    /// <c>*</c> and <c>?</c> match across directory separators — validated
    /// by <see cref="NormalizeProbePathGlob"/>. The repository's
    /// <c>.git</c> storage is pruned: it is transport metadata, not audited
    /// source. Returns the deduplicated repository-relative paths that
    /// matched; each returned line is re-verified against the declared
    /// globs because the output bytes are repository filenames — a line
    /// that matches no glob is chatter, not evidence. Fails closed like
    /// the sibling probe: a transport failure is retriable infrastructure
    /// loss, and a non-zero exit — including a <c>find</c> traversal error
    /// the script propagates — is audit unavailability, never "absent".
    /// </summary>
    protected static async Task<IReadOnlyList<string>> ProbeRepositoryPathGlobsPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> pathGlobs,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(pathGlobs);

        if (await ExecRepositoryProbeAsync(
                sandbox, workingDirectory, tool,
                RepositoryPathGlobPresenceScript, pathGlobs, NormalizeProbePathGlob,
                "repository-path", options, ct).ConfigureAwait(false)
            is not { } probe)
            return [];

        var matchers = probe.Requested.Select(PathGlobMatcher).ToList();
        var present = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in SplitProbeLines(probe.Result.Stdout))
        {
            // find emits "./"-prefixed paths; the declared globs are written
            // against plain repository-relative paths.
            var path = line.StartsWith("./", StringComparison.Ordinal) ? line[2..] : line;
            if (path.Length == 0 || !seen.Add(path))
                continue;
            foreach (var matcher in matchers)
            {
                if (matcher.IsMatch(path))
                {
                    present.Add(path);
                    break;
                }
            }
        }
        return present;
    }

    /// <summary>
    /// The bounded-exec/fail-closed skeleton the repository-presence probes
    /// share: dedup the normalized <paramref name="entries"/> into the
    /// script's <c>$@</c> argv, run one bounded exec, and classify a
    /// transport loss or non-zero exit as infrastructure — "could not
    /// confirm absence" is never evidence of absence. Returns null when no
    /// entry survives normalization; the caller owns the per-line verdict
    /// on the probe output. <paramref name="absentNoun"/> names what the
    /// probe lists (e.g. "repository-file") in the failure message.
    /// </summary>
    private static async Task<(HashSet<string> Requested, SandboxExecResult Result)?> ExecRepositoryProbeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string script,
        IReadOnlyList<string> entries,
        Func<string?, string> normalizeEntry,
        string absentNoun,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var argv = new List<string>(entries.Count + 4)
        {
            "sh", "-c", script, "sh",
        };
        foreach (var entry in entries)
        {
            var normalized = normalizeEntry(entry);
            if (requested.Add(normalized))
                argv.Add(normalized);
        }
        if (requested.Count == 0)
            return null;

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "suppression check",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not confirm {absentNoun} "
                + $"absence (exit {result.ExitCode}) — a failed probe is infrastructure, not evidence "
                + "of absence.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        return (requested, result);
    }

    /// <summary>
    /// Bounded <c>pwd</c> probe resolving the directory the tool will
    /// actually run in — the single implementation of the scan-root probe
    /// for auditors whose reports carry absolute paths but no embedded cwd
    /// (override <see cref="ResolveScanRootAsync"/> to call it). Sandbox
    /// providers may translate <paramref name="workingDirectory"/>, so the
    /// value is probed rather than assumed: <c>pwd</c> is a shell builtin —
    /// the audited repository cannot shadow it via PATH — and prints the
    /// process's own logical cwd, exactly the prefix the tool embeds in its
    /// absolute reported paths. The root is returned VERBATIM (no
    /// whitespace trimming — a canonical directory name may legitimately
    /// end in whitespace) so downstream relativization compares the same
    /// bytes the tool reports. Fails closed: an exec-transport failure
    /// throws <see cref="SandboxExecutionUnavailableException"/> (retriable
    /// infrastructure loss, not a verdict); a non-zero exit or output that
    /// is not exactly one absolute path line throws
    /// <see cref="AuditUnavailableException"/> — a missing or
    /// mis-derived scan root would let absolute paths survive normalization
    /// and silently defeat repo-relative exclusion filters.
    /// </summary>
    protected async Task<string> ProbeSandboxWorkingDirectoryAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "scan-root probe",
            new SandboxExec
            {
                Argv = ["sh", "-c", "pwd", "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);

        // The scan root is a path verbatim, not text: it is compared
        // byte-for-byte against reported paths downstream, so it must not
        // be trimmed — a canonical directory name may legitimately end in
        // whitespace (a legal POSIX leaf). Exactly one line is required:
        // extra output means the transport prepended chatter or the cwd
        // name itself carries a newline, and taking the first line would
        // mis-derive the relativization prefix rather than fail closed.
        var lines = SplitProbeLines(result.Stdout);
        if (result.ExitCode != 0 || lines.Length != 1 || !Path.IsPathRooted(lines[0]))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve the scan root (exit "
                + $"{result.ExitCode}) — reported paths could not be trusted relative to the worktree, "
                + "so this is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
        return lines[0];
    }

    /// <summary>
    /// Canonicalizes an operator-configured file path inside the sandbox and
    /// fails closed when it resolves inside the audited worktree — the
    /// shared policy for scoped-config file knobs (tool config, settings
    /// files) whose content steers what the gate measures. A tool resolves
    /// a relative configured path against its cwd — the worktree — so the
    /// probe canonicalizes <paramref name="configuredPath"/> and the probe
    /// cwd with one <c>realpath -m</c> call: relative paths, <c>..</c>
    /// segments, and symlinked components all collapse to the path the tool
    /// would actually open, and containment is judged against the same
    /// canonicalized scan root. Fails closed on a transport failure, a
    /// probe error, or a non-absolute (non-canonical) result — an
    /// unchecked configured path is never trusted.
    /// </summary>
    /// <param name="configuredKey">Scoped-config key that supplied the value; named in failure messages.</param>
    protected async Task<string> CanonicalizeOutsideWorktreeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string configuredPath,
        string configuredKey,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "out-of-worktree path check",
            new SandboxExec
            {
                // Two operands, two output lines: the configured path and
                // "." — the exec working directory canonicalized in the
                // sandbox's own path space (providers may translate the
                // host-side workingDirectory). realpath resolves through
                // PATH like the other probe binaries (sh, cat) rather than
                // an assumed FHS location; a missing realpath exits
                // non-zero and fails closed as infrastructure below.
                Argv = ["realpath", "-m", "--", configuredPath, "."],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);

        // The canonical bytes realpath emitted are compared verbatim —
        // trimming a path-bearing line would mis-derive a path or root
        // whose name legitimately ends in whitespace and could judge an
        // in-tree file "outside".
        var lines = SplitProbeLines(probe.Stdout);
        // `realpath -m` always emits absolute paths; a non-rooted line means
        // the output is not a canonicalization at all — treat it as a probe
        // failure rather than letting a relative path slip past containment.
        if (probe.ExitCode != 0 || lines.Length != 2
            || !Path.IsPathRooted(lines[0]) || !Path.IsPathRooted(lines[1]))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not canonicalize {configuredKey} "
                + $"'{TruncateForMessage(configuredPath)}' (exit {probe.ExitCode}) — an unchecked "
                + "configured path is never trusted, so this is infrastructure, not a verdict on the "
                + "diff.",
                probe.ExitCode,
                probe.Stderr);

        var canonicalPath = ValidatedArgumentValue(lines[0], configuredKey);
        var canonicalWorktree = lines[1];
        if (HostPathPolicy.IsWithinDirectory(canonicalPath, canonicalWorktree))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {configuredKey} "
                + $"'{TruncateForMessage(configuredPath)}' resolves to "
                + $"'{TruncateForMessage(canonicalPath)}' inside the audited worktree — a "
                + "repository-controlled file handed to the tool through operator configuration "
                + "would let the diff author reshape the gate. Set an absolute path outside the "
                + "repository, or unset the key for the auditor's default.")
            { IsDeterministic = true };

        return canonicalPath;
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
    /// Bounded timeout for precondition probes: shares the configured scan
    /// timeout below a fixed cap — a probe is a liveness check, not the scan.
    /// </summary>
    protected static TimeSpan ProbeTimeout(ExternalToolAuditorOptions options)
    {
        var timeout = EffectiveTimeout(options);
        return timeout > ProbeTimeoutCap ? ProbeTimeoutCap : timeout;
    }

    /// <summary>
    /// Bounded <c>git</c> probe for baseline resolution: runs
    /// <paramref name="args"/> as <c>git</c> argv entries (never a shell
    /// string) with the shared per-stream output caps and the probe timeout.
    /// A dead exec transport throws
    /// <see cref="SandboxExecutionUnavailableException"/> — retriable
    /// infrastructure loss, not a verdict — and ambient GIT_* variables are
    /// unset (<see cref="GitEnvironmentRemovals"/>) so the baseline
    /// environment can neither redirect the repository nor re-configure
    /// git's reads. The single seam for "run git to resolve a baseline SHA"
    /// so API-compatibility auditors cannot fork the policy.
    /// </summary>
    protected async Task<SandboxExecResult> GitProbeAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var argv = new List<string>(args.Count + 1) { "git" };
        argv.AddRange(args);
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "baseline resolution",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
                EnvironmentVariablesToUnset = GitEnvironmentRemovals,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);
        return result;
    }

    /// <summary>
    /// Reads the first line of <c>git</c> probe stdout as a commit SHA,
    /// failing closed (null) when it is absent or not a valid SHA. The
    /// single definition of "a usable baseline SHA" shared by
    /// API-compatibility auditors.
    /// </summary>
    protected static string? ReadCommitSha(string stdout)
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
    /// The characters that open a PowerShell parameter token: ASCII '-'
    /// plus the Unicode dashes U+2013/U+2014/U+2015 pwsh's binder also
    /// accepts as parameter markers. Shared so every guard that recognizes
    /// parameter-shaped tokens applies the same dash set.
    /// </summary>
    protected static bool IsParameterDash(char c)
        => c is '-' or '\u2013' or '\u2014' or '\u2015';

    /// <summary>
    /// Validates a configured value that travels to the tool as an argv
    /// entry: bounded length, no leading dash (it would be read as another
    /// flag), no control characters. <paramref name="source"/> names the
    /// knob that supplied the value for the failure message. Values are
    /// never concatenated into a shell string — this only guards the argv
    /// contract.
    /// </summary>
    protected static string ValidatedArgumentValue(string value, string source)
    {
        var trimmed = value.Trim();
        const int maxChars = 1024;
        if (trimmed.Length == 0 || trimmed.Length > maxChars
            // A leading parameter dash would be read as another flag. The
            // check applies the full parameter-dash set (see IsParameterDash)
            // rather than ASCII '-' alone: for tools that bind Unicode
            // dashes it closes a bypass, and for every other tool a
            // dash-led value is still an argument-shape mistake.
            || IsParameterDash(trimmed[0])
            || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' is not a usable argument value "
                + "(empty, overlong, has a leading parameter dash, or contains control "
                + "characters).")
            { IsDeterministic = true };
        return trimmed;
    }

    /// <summary>
    /// Validates a scoped-config value that travels to the tool as an argv
    /// entry; blank values mean "unset" (null). The single seam so scoped
    /// path-like knobs share one policy.
    /// </summary>
    protected static string? ValidatedScopedValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ValidatedArgumentValue(value, key);
    }

    /// <summary>
    /// Validates a configured scan-target entry that must stay inside the
    /// audited worktree: the argv guard of <see cref="ValidatedArgumentValue"/>
    /// plus containment — a rooted path or a <c>..</c> segment would point the
    /// tool at files outside the tree under audit and produce report paths the
    /// repo-relative finding-location contract (and the <c>ExcludePaths</c>
    /// prefix filter) cannot express. <paramref name="source"/> names the knob
    /// that supplied the value for the failure message.
    /// </summary>
    protected static string ValidatedRepoRelativeTarget(string value, string source)
    {
        var validated = ValidatedArgumentValue(value, source);
        var normalized = validated.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(validated)}') "
                + "must be a repo-relative path inside the worktree.")
            { IsDeterministic = true };
        return validated;
    }

    /// <summary>
    /// Extracts the value an operator's <c>ExtraArguments</c> supplies for a
    /// long-form flag — the entry following a bare <c>--flag</c>, or the
    /// text after <c>--flag=</c>, the same spellings
    /// <see cref="ExtraArgumentsSupplyFlag"/> recognizes. The last
    /// occurrence wins; a bare trailing flag yields a null value (the tool
    /// would reject it — callers validate). Returns false when the flag is
    /// absent.
    /// </summary>
    protected static bool TryGetExtraArgumentsFlagValue(
        ExternalToolAuditorOptions options,
        string flag,
        out string? value)
        => TryGetExtraArgumentsFlagValue(options, flag, shortFlag: null, out value);

    /// <summary>
    /// <see cref="TryGetExtraArgumentsFlagValue(ExternalToolAuditorOptions, string, out string?)"/>
    /// extended with a pflag-style shorthand: <paramref name="shortFlag"/>
    /// (<c>-f</c>, exactly one dash and one letter) additionally matches a
    /// bare <c>-f</c>, the attached <c>-f=value</c>, the joined
    /// <c>-fvalue</c>, and the shorthand inside a single-dash cluster —
    /// pflag reads a value-taking shorthand's value as the rest of its
    /// token (<c>-fcfg.toml</c>) or, when the shorthand is the cluster's
    /// last letter, the NEXT argv entry (<c>-vc cfg.toml</c> binds
    /// <c>cfg.toml</c> to <c>-c</c>). A cluster letter that was really part
    /// of an earlier shorthand's value over-matches into a conservative
    /// extra check — harmless for callers guarding a path value, since the
    /// checked value only ever fails closed.
    /// </summary>
    protected static bool TryGetExtraArgumentsFlagValue(
        ExternalToolAuditorOptions options,
        string flag,
        string? shortFlag,
        out string? value)
    {
        value = null;
        var supplied = false;
        var attachedPrefix = flag + "=";
        var shortChar = shortFlag is { Length: 2 } && shortFlag[0] == '-' && shortFlag[1] != '-'
            ? shortFlag[1]
            : (char?)null;
        var extraArguments = options.ExtraArguments;
        for (var i = 0; i < extraArguments.Count; i++)
        {
            var arg = extraArguments[i];
            if (string.Equals(arg, flag, StringComparison.Ordinal)
                || (shortFlag is not null
                    && string.Equals(arg, shortFlag, StringComparison.Ordinal)))
            {
                supplied = true;
                value = i + 1 < extraArguments.Count ? extraArguments[i + 1] : null;
            }
            else if (arg.StartsWith(attachedPrefix, StringComparison.Ordinal))
            {
                supplied = true;
                value = arg[attachedPrefix.Length..];
            }
            else if (shortChar is { } shorthand
                && arg.Length > 2
                && arg[0] == '-'
                && arg[1] != '-')
            {
                // A single-dash multi-letter token is a joined "-fvalue" or
                // a pflag cluster ("-vc"): the shorthand's value is the rest
                // of the token — or, when the letter ends the cluster, the
                // next argv entry.
                var index = arg.IndexOf(shorthand, 1);
                if (index < 0)
                    continue;
                var rest = arg[(index + 1)..];
                supplied = true;
                if (rest.Length == 0)
                    value = i + 1 < extraArguments.Count ? extraArguments[i + 1] : null;
                else
                    value = rest.StartsWith("=", StringComparison.Ordinal) ? rest[1..] : rest;
            }
        }
        return supplied;
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

    /// <summary>
    /// Splits bounded probe stdout into lines for PATH comparison: entries
    /// are kept VERBATIM — never whitespace-trimmed, no carriage-return
    /// stripping — because a canonical path or worktree root may
    /// legitimately end in whitespace or a '\r' (legal POSIX leaf bytes).
    /// Rewriting any byte would compare a different string than the one
    /// <c>realpath</c>/<c>pwd</c>/<c>printf</c> emitted and could judge an
    /// in-tree file "outside" the worktree. Those emitters terminate lines
    /// with LF only; a '\r' in the output is data, not a line ending.
    /// </summary>
    private static string[] SplitProbeLines(string stdout)
        => stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

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

    // Probe globs become `find -path` operands and git pathspec argv:
    // refuse bytes that would corrupt the one-name-per-line protocol or
    // turn into find predicates / git pathspec magic instead of a pattern.
    // Only `*` and `?` wildcards are supported — bracket classes would
    // need a second matcher in PathGlobMatcher for no author-facing gain.
    // '\' is rejected on the authored glob BEFORE NormalizePath folds it
    // into '/': in find and git patterns the backslash is the escape
    // character, so an author writing one almost certainly meant an escape
    // the C# matcher cannot express — not a separator. ('**' needs no
    // special case: none of the three matchers gives it meaning beyond two
    // '*'s — find -path, git's non-`glob` pathspec matching, and
    // PathGlobMatcher all let `*` cross '/'.)
    protected static string NormalizeProbePathGlob(string? glob)
    {
        if (glob?.Contains('\\', StringComparison.Ordinal) == true)
            throw new ArgumentException(
                "Repository probe globs must be non-empty relative `*`/`?` patterns without escapes.",
                nameof(glob));
        var normalized = ExternalToolJsonHelpers.NormalizePath(glob);
        if (normalized.Length == 0
            || normalized[0] is '-' or '!' or ':' or '/'
            || normalized.Any(c => c is '\n' or '[' or ']'))
            throw new ArgumentException(
                "Repository probe globs must be non-empty relative `*`/`?` patterns.",
                nameof(glob));
        return normalized;
    }

    // Compiles a declared probe glob to a matcher with the same semantics
    // the sandbox-side tools apply: `*` and `?` cross directory separators
    // (find -path, git's default — non-`glob`-magic — pathspec matching),
    // everything else is literal and case-sensitive.
    private static Regex PathGlobMatcher(string glob)
        => new(
            "^"
                + Regex.Escape(glob)
                    .Replace("\\*", ".*", StringComparison.Ordinal)
                    .Replace("\\?", ".", StringComparison.Ordinal)
                + "$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        string tool, ExternalToolParseInput parseInput)
    {
        try
        {
            return OutputParser.Parse(parseInput) ?? [];
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
        var path = NormalizeFindingPath(item.Path);
        foreach (var entry in options.ExcludePaths)
        {
            var normalized = NormalizeExcludePathEntry(entry);
            if (normalized is null)
                continue;
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                if (path.StartsWith(normalized, StringComparison.Ordinal))
                    return true;
            }
            else if (path.Equals(normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        var maxBytes = CapturedOutputLimit(options);
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
