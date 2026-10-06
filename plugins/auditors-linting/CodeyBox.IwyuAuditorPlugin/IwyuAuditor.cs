using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.IwyuAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>include-what-you-use</c> (IWYU — the clang-based
/// analyzer that reports which <c>#include</c>/forward-declaration lines a
/// C/C++ file must add and which it can drop) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the driver invocation
/// (<c>iwyu_tool -p &lt;compile_commands.json&gt; .</c>, whose per-file blocks
/// are parsed by <see cref="IwyuTextOutputParser"/>), the engine version pin,
/// the compilation-database gate, and the source-containment posture below.
///
/// <para><b>Read-only by design.</b> The auditor never runs
/// <c>fix_includes</c>/<c>fix_include</c> and never passes
/// <c>-Xiwyu --update_comments</c> or any rewriting flag: IWYU's output is a
/// verdict stream, not a patch channel. Apply-style edits belong to the work
/// phase, not the audit gate.</para>
///
/// <para><b>Gate behaviour: advisory — not blocking.</b> IWYU has no severity
/// vocabulary of its own; every add/remove suggestion is reported at level
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/>, so findings are
/// advisory and a run with violations still passes. The single
/// <c>iwyu-coverage</c> record is emitted at <c>note</c> →
/// <see cref="AuditSeverity.Info"/>. <c>MinimumSeverity</c> only drops
/// findings, it never raises them — there is no mode in which the audit
/// fails because includes were untidy; failures are always infrastructure
/// (the check could not be verified).</para>
///
/// <para><b>Exit-code convention (verified against IWYU 0.21's
/// <c>iwyu.cc</c>/<c>iwyu_globals.cc</c> and <c>iwyu_tool.py</c> — not assumed
/// from the common table).</b> Modern IWYU (0.17+) exits <c>0</c> whenever the
/// translation unit analysed — clean or with violations — and exits
/// <c>1</c> on an unrecoverable frontend error (a file that does not compile,
/// bad arguments, no compiler instance); the old
/// <c>2 + edits</c> convention died with IWYU 0.16. The auditor does NOT pass
/// <c>-Xiwyu --error[=N]</c>: leaving violations at exit <c>0</c> keeps
/// "findings exist" distinguishable from "a translation unit could not be
/// analysed". <c>iwyu_tool</c> aggregates the worst child exit code via
/// <c>max()</c> — a signal-killed unit's negative returncode cannot raise
/// the aggregate above <c>0</c>, so the exit code alone cannot prove full
/// coverage. The report is therefore reconciled against the coverage plan
/// the parsed database yielded: <c>0</c> where a selected in-worktree
/// unit's file carries no verdict record (a unit that produced no verdict
/// at all) fails closed as
/// infrastructure, and any other exit means at least one unit failed or
/// the driver itself failed — infrastructure, never a partial pass.
/// <c>126</c>/<c>127</c> = cannot execute / not found. A literal
/// <c>--error</c>/<c>--error_always</c> in operator <c>ExtraArguments</c>
/// (after <c>--</c>) is rejected deterministically: it renumbers the
/// convention so violations would read as tool failures.</para>
///
/// <para><b>Compilation database — required and explicit.</b> IWYU needs real
/// compile flags; there is no usable default analysis without them. The
/// auditor requires the operator to name a Clang JSON compilation database
/// via <see cref="CompilationDatabaseKey"/> — a repository-relative path to
/// <c>compile_commands.json</c> or its containing directory, canonicalized in
/// the sandbox and required to stay inside the audited worktree, or an
/// operator-owned absolute path outside the tree. The directory form
/// canonicalizes the <c>compile_commands.json</c> leaf itself, so a
/// repo-controlled leaf symlink cannot redirect the read. The file is read
/// through a bounded sandbox read, parsed host-side, and every entry must be a
/// well-formed object with string <c>file</c> and <c>directory</c>
/// fields plus a usable <c>command</c> string or <c>arguments</c> string
/// array — iwyu_tool reads all three unconditionally, so a missing field
/// fails deterministically here instead of crashing the driver mid-run.
/// Entries are bounded by
/// <see cref="MaxCompilationDatabaseEntriesKey"/>.</para>
///
/// <para><b>Source containment and explicit coverage.</b> The scan selects
/// the worktree root (<c>.</c>) as iwyu_tool's only source selector, so a
/// translation unit runs only when its canonicalized <c>file</c> resolves
/// inside the audited tree — entries pointing outside (generated build
/// output, system trees) are never analysed. The host-side entry scan makes
/// the coverage accounting explicit: zero in-worktree entries is a
/// deterministic infrastructure failure (never a vacuous pass), the
/// out-of-tree entry count is logged per run, and the parsed report's
/// verdict records are reconciled against the selected in-worktree files
/// by path identity, so a unit
/// that exited without producing a verdict (e.g. signal-killed — iwyu_tool's
/// <c>max()</c> aggregation folds that into exit <c>0</c>) is an
/// infrastructure failure, not a silent partial pass.</para>
///
/// <para><b>Version pin.</b> The analysis engine — not the driver — is
/// pinned: <c>iwyu_tool</c> is a launcher script, so the declared
/// <see cref="VersionPin"/> probes the separate
/// <c>include-what-you-use</c> binary via
/// <see cref="ToolVersionPin.ProbedBinary"/>. IWYU releases carry two-part
/// versions (<c>0.21</c>) whose banner reads
/// <c>include-what-you-use 0.21 based on clang version …</c>; the reported
/// token is normalized to the three-part pin convention
/// (<c>0.21.0</c>). Each IWYU release builds against exactly one LLVM/Clang,
/// so the IWYU pin pins the toolchain pair. A missing binary, an
/// unrecognised banner, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a finding.
/// The <c>IWYU_BINARY</c> and <c>IWYU_VERBOSE</c> environment variables are
/// stripped from the scan and probe environments: the first would let the
/// baseline redirect iwyu_tool at a different engine binary than the one
/// this pin verified, and the second can silence the add/remove sections of
/// every verdict (<c>IWYU_VERBOSE=0</c> suppresses them below verbosity 1)
/// — a run that passes while reporting nothing.</para>
///
/// <para><b>Scope and defaults.</b> Audit subject controls the database
/// content — its <c>command</c>/<c>arguments</c> entries reach the clang
/// front-end verbatim, so enabling this auditor lets the audited repository
/// supply arbitrary clang driver flags (including plugin-loading and
/// file-inclusion surfaces) inside the audit sandbox; execution itself is
/// contained to the sandbox (<see cref="AuditCapabilities.None"/>). The
/// auditor controls which entries run
/// (worktree containment) and bounds the database size
/// (<c>MaxOutputBytesPerStream</c> caps the read) and entry count. Findings
/// under vendored/generated prefixes are dropped by the default
/// <c>ExcludePaths</c> backstop.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Include-What-You-Use C/C++ Analyzer",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "iwyu_tool",
    AptPackage = "iwyu",
    InstallHint = "provision the iwyu package into the sandbox baseline via apt (AptPackage iwyu ships "
        + "both iwyu_tool and include-what-you-use) — the Debian metapackage tracks the distro "
        + "default, so pin the exact upstream release your baseline installs (see ExpectedVersion, "
        + "default " + DefaultExpectedVersion + " for iwyu 8.21 → upstream 0.21) and keep the two in "
        + "step through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "include-what-you-use",
    AptPackage = "iwyu",
    InstallHint = "iwyu_tool shells out to include-what-you-use once per translation unit — the same "
        + "apt package 'iwyu' provides it; provision the release pinned by ExpectedVersion")]
public sealed class IwyuAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.iwyu";

    /// <summary>
    /// IWYU release the invocation and its report grammar are verified
    /// against — upstream <c>0.21</c> (the iwyu 8.21 package on Ubuntu noble,
    /// the baseline the companion clang-tidy pin targets), written in the
    /// three-part convention the pin compares: <c>0.21.0</c>. Operators
    /// running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.21.0";

    /// <summary>
    /// Scoped-config key naming the Clang JSON compilation database to
    /// audit: a repository-relative path to <c>compile_commands.json</c> or
    /// the directory containing it (canonicalized inside the worktree), or
    /// an operator-owned absolute path outside the tree. Required — there is
    /// no useful IWYU verdict without real compile flags, and an absent key
    /// is a deterministic infrastructure failure, never a pass.
    /// </summary>
    public const string CompilationDatabaseKey = "CompilationDatabase";

    /// <summary>
    /// Scoped-config key bounding the number of entries the compilation
    /// database may hold. Larger databases are a deterministic
    /// infrastructure failure — split the build or raise the cap
    /// deliberately. Default <see cref="DefaultMaxCompilationDatabaseEntries"/>.
    /// </summary>
    public const string MaxCompilationDatabaseEntriesKey = "MaxCompilationDatabaseEntries";

    /// <summary>Default entry bound for <see cref="MaxCompilationDatabaseEntriesKey"/>.</summary>
    internal const int DefaultMaxCompilationDatabaseEntries = 4096;

    // Upper clamp on the configured entry bound: a JSON array can declare
    // arbitrarily many entries, and per-entry work must stay bounded.
    private const int MaxCompilationDatabaseEntriesCeiling = 65_536;

    /// <summary>The analysis engine iwyu_tool execs per translation unit.</summary>
    private const string EngineBinary = "include-what-you-use";

    // IWYU's version banner: "include-what-you-use 0.21 based on Ubuntu
    // clang version 18.1.3". The IWYU token — never the clang token — is the
    // pin's subject (the same way the PMD pin anchors on "PMD").
    private static readonly Regex IwyuVersionPattern = new(
        @"include-what-you-use\s+(?<version>\d+\.\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Configured pin values may be written "0.21" or "0.21.0"; normalize to
    // the three-part form for comparison.
    private static readonly Regex ExpectedVersionPattern = new(
        @"^(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = every selected translation unit analysed (clean or with
        // violations — the auditor deliberately does not pass
        // -Xiwyu --error, so violations do not renumber the exit). 1 =
        // a translation unit failed unrecoverably or the driver failed —
        // partial coverage, reported as infrastructure. Anything else is an
        // unknown convention and fails loudly.
        FindingsExitCodes = new HashSet<int> { 0 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit. Operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/", ".venv/", "venv/", "__pycache__/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _compilationDatabase = static () => null;
    private Func<int> _maxDatabaseEntries = static () => DefaultMaxCompilationDatabaseEntries;
    private ILogger _logger = NullLogger.Instance;

    // The run's coverage plan rides the base's PerRunState slot from
    // ResolveContextArgumentsAsync to ResolveParserInputAsync. The pre-scan
    // analysis is the faithful plan: entry compile flags reach the clang
    // driver verbatim and can rewrite the database file mid-run, so a
    // post-scan re-read could describe a different database than the one
    // iwyu_tool selected from.
    private sealed record PerRunPlan(string DatabaseFile, CompilationDatabasePlan Analysis);

    /// <inheritdoc />
    public override string Name => "codeybox:iwyu";

    /// <inheritdoc />
    protected override string ToolName => "iwyu_tool";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new IwyuTextOutputParser();

    /// <summary>
    /// IWYU emits no severity vocabulary: add/remove suggestions are
    /// <c>warning</c> (advisory), the coverage record is <c>note</c>.
    /// Unknown levels fall back to Warning — raw tool text never reaches
    /// findings as a severity.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <summary>
    /// Pins the analysis engine, not the driver: <c>iwyu_tool</c> is a
    /// launcher script, so <see cref="ToolVersionPin.ProbedBinary"/> names
    /// <c>include-what-you-use</c> — the base presence-checks it and probes
    /// <c>include-what-you-use --version</c> before every scan. The banner's
    /// IWYU token is extracted and normalized to the three-part pin
    /// convention; the configured expectation is normalized the same way so
    /// operators may write <c>0.21</c> or <c>0.21.0</c>.
    /// </summary>
    protected override ToolVersionPin? VersionPin => new(
        PluginId,
        ConfiguredExpectedVersion: ConfiguredExpectedVersion,
        DefaultExpectedVersion,
        VersionProbeArguments: ["--version"],
        VersionExtractor: ExtractEngineVersion,
        ProbedBinary: EngineBinary);

    /// <summary>
    /// The environment hooks <c>IWYU_BINARY</c> and <c>IWYU_VERBOSE</c> must
    /// never reach the tool process — a baseline-set <c>IWYU_BINARY</c>
    /// would make iwyu_tool execute a binary other than the verified
    /// <c>include-what-you-use</c>, silently bypassing the version pin and
    /// the presence check, and <c>IWYU_VERBOSE=0</c> suppresses the "should
    /// add"/"should remove" sections of every verdict block (IWYU 0.21's
    /// iwyu_output.cc prints them only at verbosity ≥ 1) — a run that passes
    /// while reporting nothing.
    /// </summary>
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(ExternalToolAuditorOptions options)
        => ["IWYU_BINARY", "IWYU_VERBOSE"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
        => [];

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // -p/--error guards run before any probe: a malformed invocation
        // shape fails deterministically without touching the sandbox.
        RejectUnsupportedExtraArguments(options);

        var configured = _compilationDatabase();
        if (string.IsNullOrWhiteSpace(configured))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' requires an explicit compilation database — "
                + $"set CodeyBox:Plugins:{PluginId}:{CompilationDatabaseKey} to the repository-relative "
                + "path of the generated compile_commands.json (or its directory). Without real compile "
                + "flags the tool cannot produce a verdict, so this is infrastructure, not a pass.")
            { IsDeterministic = true };

        var (databaseFile, worktreeRoot) = await ResolveDatabaseFileAsync(
            sandbox, workingDirectory, configured.Trim(), options, ct).ConfigureAwait(false);
        var databaseJson = await ReadDatabaseAsync(sandbox, workingDirectory, databaseFile, options, ct)
            .ConfigureAwait(false);
        var analysis = IwyuCompilationDatabase.Analyze(
            databaseJson, worktreeRoot, _maxDatabaseEntries(), ToolName);
        ReportCoverage(analysis);
        PerRunState = new PerRunPlan(databaseFile, analysis);

        // Selecting the worktree root asks iwyu_tool for exactly the
        // canonicalized-in-tree entries — the source-containment boundary.
        return ["-p", databaseFile, "."];
    }

    /// <summary>
    /// Rejects operator ExtraArguments that would silently change the audit
    /// contract: a second <c>-p</c> — bare, attached, or smuggled inside an
    /// argparse single-dash cluster like <c>-vp</c> — overrides the
    /// validated database (the tool takes the last occurrence) and bypasses
    /// containment checks, and post-<c>--</c>
    /// <c>--error</c>/<c>--error_always</c> renumbers the exit
    /// convention so violations read as tool failure.
    /// </summary>
    private void RejectUnsupportedExtraArguments(ExternalToolAuditorOptions options)
    {
        if (ExtraArgumentsSupplyFlag(options, "-p"))
            throw DatabaseFlagRejected();

        var forwarded = false;
        foreach (var arg in options.ExtraArguments)
        {
            if (!forwarded)
            {
                if (arg == "--")
                {
                    forwarded = true;
                    continue;
                }

                // argparse splits a single-dash cluster, so "-vp /path"
                // parses as -v followed by -p /path — smuggling the database
                // flag past the exact-token check. Reject a 'p' reachable
                // through the only flag-style letter iwyu_tool defines
                // ('v'); a 'p' deeper in a token belongs to a value-taking
                // option's joined value (-j4-style) and never reaches the
                // parser as an option.
                var clusterP = arg.IndexOf('p', 1);
                if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-'
                    && clusterP > 0 && arg[1..clusterP].All(static c => c == 'v'))
                    throw DatabaseFlagRejected();
                continue;
            }
            if (arg.StartsWith("--error", StringComparison.Ordinal))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' was configured with '{TruncateForMessage(arg)}' "
                    + "after '--' in ExtraArguments — the IWYU exit-code convention is part of the audit "
                    + "contract (exit 1 must mean 'a translation unit could not be analysed', never "
                    + "'violations found'). Remove the flag; findings are advisory by design.")
                { IsDeterministic = true };
        }
    }

    private AuditUnavailableException DatabaseFlagRejected()
        => new(
            $"could-not-verify: auditor '{Name}' was configured with a compilation-database flag "
            + $"('-p') in ExtraArguments. Set CodeyBox:Plugins:{PluginId}:{CompilationDatabaseKey} "
            + "instead — the database path is validated and contained before every run.")
        { IsDeterministic = true };

    /// <summary>
    /// Resolves the configured database path to the canonical absolute
    /// compile_commands.json in the sandbox's path space, plus the canonical
    /// worktree root the same probe reports. A repository-relative value
    /// must canonicalize inside the worktree (a repo-controlled symlink must
    /// not redirect the read); an absolute value is operator-owned and may
    /// live anywhere. Missing paths and directories lacking
    /// compile_commands.json are deterministic infrastructure failures.
    /// </summary>
    private async Task<(string DatabaseFile, string WorktreeRoot)> ResolveDatabaseFileAsync(
        ISandbox sandbox,
        string workingDirectory,
        string configured,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var operatorAbsolute = configured.StartsWith("/", StringComparison.Ordinal);
        var candidate = operatorAbsolute
            ? ValidatedArgumentValue(configured, CompilationDatabaseKey)
            : ValidatedRepoRelativeTarget(configured, CompilationDatabaseKey);

        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "compilation-database resolution",
            new SandboxExec
            {
                Argv = ["sh", "-c", IwyuCompilationDatabase.PathProbeScript, "sh", candidate],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);

        var lines = (probe.Stdout ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (probe.ExitCode != 0 || lines.Length is < 3 or > 4
            || !Path.IsPathRooted(lines[0]) || !Path.IsPathRooted(lines[1]))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not canonicalize "
                + $"{CompilationDatabaseKey} '{TruncateForMessage(configured)}' (exit {probe.ExitCode}) — "
                + "an unchecked database path is never trusted.",
                probe.ExitCode,
                probe.Stderr);

        var canonicalPath = lines[0];
        var worktreeRoot = lines[1];
        var marker = lines[^1];

        string databaseFile;
        switch (marker)
        {
            case "file" when lines.Length == 3:
                databaseFile = canonicalPath;
                break;
            case "dir" when lines.Length == 4 && Path.IsPathRooted(lines[2]):
                // The probe canonicalized the leaf itself: a repo-controlled
                // compile_commands.json symlink resolves to its target here,
                // so the containment check below judges the file actually
                // read — not the directory containing the symlink.
                databaseFile = lines[2];
                break;
            case "dir-no-db" when lines.Length == 3:
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                    + $"'{TruncateForMessage(configured)}' is a directory with no compile_commands.json — "
                    + "generate the database (e.g. cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON) into that "
                    + "location, or point the key at the file directly.")
                { IsDeterministic = true };
            case "missing" when lines.Length == 3:
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                    + $"'{TruncateForMessage(configured)}' does not exist in the audit sandbox — the "
                    + "database must be generated or committed before the audit runs.")
                { IsDeterministic = true };
            default:
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' could not canonicalize "
                    + $"{CompilationDatabaseKey} '{TruncateForMessage(configured)}' — the path probe "
                    + "returned an unrecognised shape, so the database path is never trusted.")
                { IsDeterministic = true };
        }

        if (!operatorAbsolute && !HostPathPolicy.IsStrictlyWithinDirectory(databaseFile, worktreeRoot))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                + $"'{TruncateForMessage(configured)}' resolves to '{TruncateForMessage(databaseFile)}' "
                + "outside the audited worktree — a repository-controlled path must not redirect the "
                + "database read. Set an absolute operator-owned path, or a repository-relative path "
                + "that stays inside the tree.")
            { IsDeterministic = true };

        return (databaseFile, worktreeRoot);
    }

    /// <summary>
    /// Reads the resolved database file through one bounded <c>cat</c> —
    /// the path travels as its own argv entry after <c>--</c>. An oversized
    /// database (output cap exceeded), an unreadable file, or a dead exec
    /// transport all fail closed as infrastructure: a clipped database is
    /// never parsed.
    /// </summary>
    private async Task<string> ReadDatabaseAsync(
        ISandbox sandbox,
        string workingDirectory,
        string databaseFile,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var read = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "compilation-database read",
            new SandboxExec
            {
                Argv = ["cat", "--", databaseFile],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = CapturedOutputLimit(options),
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (read.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(read.ExitCode);
        if (read.StdoutLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found a compilation database exceeding the "
                + $"{CapturedOutputLimit(options)}-byte read bound — a clipped database is never "
                + "parsed, so this is infrastructure, not a verdict. Raise MaxOutputBytesPerStream or "
                + "generate a narrower database.")
            { IsDeterministic = true };
        if (read.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' resolved its compilation database but could "
                + $"not read it (exit {read.ExitCode}).",
                read.ExitCode,
                read.Stderr);

        return read.Stdout ?? string.Empty;
    }

    private void ReportCoverage(CompilationDatabasePlan analysis)
    {
        if (analysis.OutOfScopeCount > 0)
        {
            _logger.LogWarning(
                "IwyuAuditor: {OutOfScope} compilation-database entries resolve outside the audited "
                    + "worktree and were not analysed (in-scope: {InScope}).",
                analysis.OutOfScopeCount,
                analysis.InScopeFiles.Count);
        }
        else
        {
            _logger.LogInformation(
                "IwyuAuditor: {InScope} in-worktree translation units selected for analysis.",
                analysis.InScopeFiles.Count);
        }
    }

    /// <summary>
    /// Resolves the canonical directory the aggregated scan runs in.
    /// iwyu_tool canonicalizes both the selection and every entry's
    /// <c>file</c> with <c>os.path.realpath</c>, so the parser must
    /// relativize against the realpath'd root — under a symlinked/aliased
    /// exec cwd a logical <c>pwd</c> root would diverge from the prefix
    /// reported paths carry and leave them absolute (defeating
    /// ExcludePaths and repo-relative locations).
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "scan-root probe",
            new SandboxExec
            {
                Argv = ["realpath", "-m", "--", "."],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);

        // realpath -m emits exactly one absolute canonical path; anything
        // else — chatter, a multi-line result, a relative path — is a probe
        // failure, not a root to relativize against.
        var lines = (probe.Stdout ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (probe.ExitCode != 0 || lines.Length != 1 || !Path.IsPathRooted(lines[0]))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve the canonical scan root "
                + $"(exit {probe.ExitCode}) — reported paths could not be trusted relative to the "
                + "worktree, so this is infrastructure, not a verdict on the diff.",
                probe.ExitCode,
                (probe.Stdout ?? string.Empty) + "\n" + probe.Stderr);
        return lines[0];
    }

    /// <summary>
    /// Stamps the run's coverage plan — recorded by
    /// <see cref="ResolveContextArgumentsAsync"/> from the database the scan
    /// ran against — onto the parse input: the repository-relative files the
    /// selection covers (each must carry a verdict record; iwyu_tool's
    /// <c>max()</c> exit aggregation can hide a unit that produced none) and
    /// the relative-path anchors resolving spellings reported against an
    /// entry's <c>directory</c>. The plan rides the <see cref="PerRunState"/>
    /// slot rather than being re-derived post-scan: the scan's own compile
    /// flags can
    /// rewrite the database file mid-run, so a re-read could describe a
    /// different database than the one iwyu_tool parsed. A run that reaches
    /// here with no recorded plan, or whose argv lost the
    /// <c>-p &lt;database&gt;</c> operand that names the analysed file, is a
    /// defect — reconciliation fails closed instead of silently disabling.
    /// </summary>
    protected override Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
    {
        var perRun = PerRunState as PerRunPlan;
        PerRunState = null;
        if (perRun is null || !ArgvCarriesDatabaseOperand(argv, perRun.DatabaseFile))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' produced a report whose coverage cannot "
                + "be reconciled — the run's compilation-database plan is missing, or the scan argv "
                + "lost its '-p <database>' operand — so this is infrastructure, not a pass.")
            { IsDeterministic = true };

        return Task.FromResult(new ExternalToolParseInput(
            tool, result.Stdout, result.Stderr, result.ExitCode,
            ScanRoot: scanRoot, WorkingDirectory: workingDirectory,
            ExpectedVerdictFiles: perRun.Analysis.InScopeFiles,
            RelativePathAnchors: perRun.Analysis.RelativePathAnchors));
    }

    // The scan argv must name the database the plan analysed: "-p" followed
    // by the resolved file. Structural search rather than a fixed index —
    // author-built and operator arguments can precede or follow the context
    // arguments, so position alone is not the contract.
    private static bool ArgvCarriesDatabaseOperand(IReadOnlyList<string> argv, string databaseFile)
    {
        for (var i = 1; i + 1 < argv.Count; i++)
        {
            if (argv[i] == "-p" && argv[i + 1] == databaseFile)
                return true;
        }
        return false;
    }

    // The pin's expected side: the configured spelling normalized to the
    // three-part convention the shared extractor compares — "0.21" becomes
    // "0.21.0". An unparseable value passes through so the shared pin
    // reports it verbatim as an unparseable ExpectedVersion.
    private string? ConfiguredExpectedVersion()
    {
        var configured = _expectedVersion();
        return string.IsNullOrWhiteSpace(configured)
            ? configured
            : NormalizeVersionToken(configured) ?? configured;
    }

    // The pin's reported side: the IWYU token from the banner (never the
    // trailing clang version) normalized to the three-part convention.
    private static string? ExtractEngineVersion(string banner)
        => NormalizeVersionToken(ExtractReportedVersion(banner));

    private static string? ExtractReportedVersion(string banner)
        => IwyuVersionPattern.Match(banner) is { Success: true } match
            ? match.Groups["version"].Value
            : null;

    /// <summary>
    /// Normalizes an IWYU version token to the pinned three-part form:
    /// <c>0.21</c> and <c>0.21.0</c> both normalize to <c>0.21.0</c>; any
    /// other shape yields null (unparseable → fail closed).
    /// </summary>
    private static string? NormalizeVersionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = ExpectedVersionPattern.Match(value.Trim());
        if (!match.Success)
            return null;
        var patch = match.Groups["patch"];
        return string.Concat(
            match.Groups["major"].Value, ".",
            match.Groups["minor"].Value, ".",
            patch.Success ? patch.Value : "0");
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _compilationDatabase = () => scoped[CompilationDatabaseKey];
        _maxDatabaseEntries = () =>
            int.TryParse(scoped[MaxCompilationDatabaseEntriesKey], out var configured) && configured > 0
                ? Math.Min(configured, MaxCompilationDatabaseEntriesCeiling)
                : DefaultMaxCompilationDatabaseEntries;
        _logger = context.Logger;
        context.Logger.LogInformation(
            "IwyuAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
