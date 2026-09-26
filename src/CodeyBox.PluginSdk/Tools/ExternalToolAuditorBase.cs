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
    /// Use <see cref="ExecToolBoundedAsync"/> for precondition probes so they
    /// get the same timeout bounding and failure classification as the scan;
    /// <see cref="ProbeRepositoryFilesPresentAsync"/> covers the common
    /// "does a repository-controlled file exist" gate. The default imposes no
    /// extra preconditions.
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

        await ThrowIfToolMissingAsync(sandbox, workingDirectory, tool, ct).ConfigureAwait(false);
        await VerifyToolVersionPinAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
        await VerifyToolAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
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

        var parsed = ParseOutput(tool, result);
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

    private static async Task ThrowIfToolMissingAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        CancellationToken ct)
    {
        SandboxExecResult probe;
        try
        {
            probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", tool],
                WorkingDirectory = workingDirectory,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
            throw new AuditUnavailableException(
                $"could-not-verify: auditor tool '{tool}' presence check could not run: {SingleLine(ex.Message)}",
                ex);
        }

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor tool '{tool}' presence check could not run: the sandbox exec transport was unavailable.");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is not installed in the audit sandbox. Install it in the sandbox baseline; the check did not run, so this is infrastructure, not a verdict on the diff.");
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
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [tool, .. probeArguments],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reported = ExtractToolVersion(result.Stdout);
        if (result.ExecutionUnavailable
            || result.ExitCode != 0
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

        var requested = new HashSet<string>(StringComparer.Ordinal);
        var argv = new List<string>(relativePaths.Count + 4)
        {
            "sh", "-c", RepositoryFilePresenceScript, "sh",
        };
        foreach (var path in relativePaths)
        {
            var normalized = NormalizeProbePath(path);
            if (requested.Add(normalized))
                argv.Add(normalized);
        }
        if (requested.Count == 0)
            return [];

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
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not run: the sandbox exec "
                + "transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not confirm repository-file "
                + $"absence (exit {result.ExitCode}) — a failed probe is infrastructure, not evidence "
                + "that the files are absent.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        // The probe echoes each present path, one per line; intersect with
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
    /// Normalizes an <see cref="ExternalToolAuditorOptions.ExcludePaths"/>
    /// entry to its repository-relative form — the single source of truth for
    /// the finding-level filter and for auditors translating entries into
    /// tool flags. Returns null for blank entries; a preserved trailing
    /// <c>/</c> marks a directory-prefix exclusion.
    /// </summary>
    protected static string? NormalizeExcludePathEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;
        var normalized = entry.Replace('\\', '/').Trim().TrimStart('/');
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
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.Length == 0
            || normalized[0] == '/'
            || normalized.IndexOf('\n') >= 0
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new ArgumentException(
                "Repository probe paths must be relative paths inside the worktree.",
                nameof(path));
        return normalized;
    }

    private static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }

    private IReadOnlyList<ExternalToolFinding> ParseOutput(string tool, SandboxExecResult result)
    {
        try
        {
            return OutputParser.Parse(new ExternalToolParseInput(tool, result.Stdout, result.Stderr, result.ExitCode)) ?? [];
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
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

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

    // Tool output is untrusted input that ends up in rendered findings: strip
    // terminal escape sequences so a scanner cannot inject control sequences
    // into operator-facing output.
    private static string StripEscapes(string value)
        => value.Replace("\x1b", string.Empty, StringComparison.Ordinal);

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    /// <summary>
    /// Flattens tool output to a single line for failure messages. Every
    /// control character — newlines, tabs, ESC and other terminal escape
    /// bytes — becomes a space so untrusted tool output cannot inject
    /// sequences into logged messages or persisted failure reasons.
    /// </summary>
    protected static string SingleLine(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    private static string FormatTimeout(TimeSpan timeout)
        => timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:0.##} minutes"
            : $"{timeout.TotalSeconds:0.##} seconds";
}
