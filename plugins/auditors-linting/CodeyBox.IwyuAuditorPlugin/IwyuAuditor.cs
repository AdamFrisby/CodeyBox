using System.Text.Json;
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
/// analysed". <c>iwyu_tool</c> aggregates the worst child exit code, so
/// <c>0</c> = every selected translation unit produced a verdict
/// (findings-producing), and any other exit means at least one unit failed
/// or the driver itself failed — infrastructure, never a partial pass.
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
/// operator-owned absolute path outside the tree. The file is read through a
/// bounded sandbox read, parsed host-side, and every entry must be a
/// well-formed object with string <c>file</c> and <c>directory</c>
/// fields — iwyu_tool reads both unconditionally, so a missing field
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
/// deterministic infrastructure failure (never a vacuous pass), and the
/// out-of-tree entry count is logged per run. With violations a passing
/// result still means "every in-scope translation unit produced a verdict"
/// because any per-unit failure is exit <c>1</c>.</para>
///
/// <para><b>Version pin.</b> The analysis engine — not the driver — is
/// pinned: <c>iwyu_tool</c> is a launcher script, so the auditor probes
/// <c>include-what-you-use --version</c> in <see cref="VerifyToolAsync"/>
/// after the base's driver presence check. IWYU releases carry two-part
/// versions (<c>0.21</c>) whose banner reads
/// <c>include-what-you-use 0.21 based on clang version …</c>; the reported
/// token is normalized to the three-part pin convention
/// (<c>0.21.0</c>). Each IWYU release builds against exactly one LLVM/Clang,
/// so the IWYU pin pins the toolchain pair. A missing binary, an
/// unrecognised banner, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a finding.
/// The <c>IWYU_BINARY</c> environment variable is stripped from the scan and
/// probe environments: it would let the baseline redirect iwyu_tool at a
/// different engine binary than the one this pin verified.</para>
///
/// <para><b>Scope and defaults.</b> Audit subject controls the database
/// content (its compile flags reach the clang front-end verbatim — the same
/// trust posture as clang-tidy honoring the repository's
/// <c>compile_commands.json</c>); the auditor controls which entries run
/// (worktree containment) and bounds the database size
/// (<c>MaxOutputBytesPerStream</c> caps the read) and entry count. Findings
/// under vendored/generated prefixes are dropped by the default
/// <c>ExcludePaths</c> backstop. The auditor runs with
/// <see cref="AuditCapabilities.None"/>.</para>
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

    // Per-field bound on untrusted database strings (file/directory): enough
    // for the deepest real path, small enough that a hostile entry cannot
    // bloat argv or failure text.
    private const int MaxDatabaseFieldChars = 1024;

    /// <summary>The analysis engine iwyu_tool execs per translation unit.</summary>
    private const string EngineBinary = "include-what-you-use";

    // Canonicalization + file-type probe for the configured database path.
    // Two realpath lines (the configured path resolved in the sandbox's own
    // path space — providers may translate the exec working directory — and
    // the canonical worktree root), then a marker line: dir (contains a
    // compile_commands.json), dir-no-db, file, or missing. Structured argv:
    // the configured path travels as "$1", never inside the script text.
    private const string DatabasePathProbeScript =
        "realpath -m -- \"$1\" . || exit 1\n"
        + "if [ -d \"$1\" ]; then\n"
        + "  if [ -f \"$1/compile_commands.json\" ]; then echo dir; else echo dir-no-db; fi\n"
        + "elif [ -f \"$1\" ]; then\n"
        + "  echo file\n"
        + "else\n"
        + "  echo missing\n"
        + "fi";

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
    /// The engine locate hook <c>IWYU_BINARY</c> must never reach the tool
    /// process — a baseline-set value would make iwyu_tool execute a binary
    /// other than the verified <c>include-what-you-use</c>, silently
    /// bypassing the version pin and the presence check.
    /// </summary>
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(ExternalToolAuditorOptions options)
        => ["IWYU_BINARY"];

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
        ReportCoverage(AnalyzeCompilationDatabase(databaseJson, worktreeRoot));

        // Selecting the worktree root asks iwyu_tool for exactly the
        // canonicalized-in-tree entries — the source-containment boundary.
        return ["-p", databaseFile, "."];
    }

    /// <summary>
    /// Rejects operator ExtraArguments that would silently change the audit
    /// contract: a second <c>-p</c> overrides the validated database (the
    /// tool takes the last occurrence) and bypasses containment checks, and
    /// post-<c>--</c> <c>--error</c>/<c>--error_always</c> renumbers the exit
    /// convention so violations read as tool failure.
    /// </summary>
    private void RejectUnsupportedExtraArguments(ExternalToolAuditorOptions options)
    {
        if (ExtraArgumentsSupplyFlag(options, "-p"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a compilation-database flag "
                + $"('-p') in ExtraArguments. Set CodeyBox:Plugins:{PluginId}:{CompilationDatabaseKey} "
                + "instead — the database path is validated and contained before every run.")
            { IsDeterministic = true };

        var forwarded = false;
        foreach (var arg in options.ExtraArguments)
        {
            if (!forwarded)
            {
                forwarded = arg == "--";
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
                Argv = ["sh", "-c", DatabasePathProbeScript, "sh", candidate],
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
        if (probe.ExitCode != 0 || lines.Length != 3
            || !Path.IsPathRooted(lines[0]) || !Path.IsPathRooted(lines[1]))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not canonicalize "
                + $"{CompilationDatabaseKey} '{TruncateForMessage(configured)}' (exit {probe.ExitCode}) — "
                + "an unchecked database path is never trusted.",
                probe.ExitCode,
                probe.Stderr);

        var canonicalPath = lines[0];
        var worktreeRoot = lines[1];
        var marker = lines[2];

        if (!operatorAbsolute && !HostPathPolicy.IsWithinDirectory(canonicalPath, worktreeRoot))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                + $"'{TruncateForMessage(configured)}' resolves to '{TruncateForMessage(canonicalPath)}' "
                + "outside the audited worktree — a repository-controlled path must not redirect the "
                + "database read. Set an absolute operator-owned path, or a repository-relative path "
                + "that stays inside the tree.")
            { IsDeterministic = true };

        return marker switch
        {
            "file" => (canonicalPath, worktreeRoot),
            "dir" => (canonicalPath + "/compile_commands.json", worktreeRoot),
            "dir-no-db" => throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                + $"'{TruncateForMessage(configured)}' is a directory with no compile_commands.json — "
                + "generate the database (e.g. cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON) into that "
                + "location, or point the key at the file directly.")
            { IsDeterministic = true },
            _ => throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {CompilationDatabaseKey} "
                + $"'{TruncateForMessage(configured)}' does not exist in the audit sandbox — the "
                + "database must be generated or committed before the audit runs.")
            { IsDeterministic = true },
        };
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

    /// <summary>
    /// Counts database entries whose canonicalized <c>file</c> resolves
    /// inside the audited worktree — the same set <c>iwyu_tool … .</c>
    /// selects — and returns (inScope, outOfScope). The JSON is repository
    /// content: a malformed document, a malformed entry, more than the
    /// configured entry bound, or zero in-worktree translation units are all
    /// deterministic infrastructure failures — a vacuous or
    /// unaccountable-coverage run is never a pass.
    /// </summary>
    private (int InScope, int OutOfScope) AnalyzeCompilationDatabase(string json, string worktreeRoot)
    {
        var maxEntries = _maxDatabaseEntries();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' read a compilation database that is not "
                + $"valid JSON: {SingleLine(ex.Message)}",
                ex)
            { IsDeterministic = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw MalformedDatabase("the root is not a JSON array");

            var inScope = 0;
            var outOfScope = 0;
            var entryIndex = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (++entryIndex > maxEntries)
                    throw new AuditUnavailableException(
                        $"could-not-verify: audit tool '{ToolName}' compilation database holds more "
                        + $"than {maxEntries} entries — the per-run bound keeps "
                        + "coverage accounting finite. Narrow the build or raise "
                        + $"CodeyBox:Plugins:{PluginId}:{MaxCompilationDatabaseEntriesKey}.")
                    { IsDeterministic = true };

                if (entry.ValueKind != JsonValueKind.Object)
                    throw MalformedDatabase($"entry #{entryIndex} is not an object");

                var file = GetEntryString(entry, "file");
                if (file is null || file.Length == 0)
                    throw MalformedDatabase($"entry #{entryIndex} has no usable 'file' path");
                if (file.Length > MaxDatabaseFieldChars || file.Any(char.IsControl))
                    throw MalformedDatabase($"entry #{entryIndex} has an overlong or control-carrying 'file' path");

                var directory = GetEntryString(entry, "directory");
                if (directory is null)
                    // iwyu_tool reads entry['directory'] unconditionally —
                    // a missing field crashes the driver at run time, so it
                    // fails deterministically here instead.
                    throw MalformedDatabase($"entry #{entryIndex} has no usable 'directory' path");
                if (directory.Length == 0 || directory.Length > MaxDatabaseFieldChars
                    || directory.Any(char.IsControl))
                    throw MalformedDatabase(
                        $"entry #{entryIndex} has an overlong or control-carrying 'directory' path");

                if (CanonicalEntryContained(file, directory, worktreeRoot))
                    inScope++;
                else
                    outOfScope++;
            }

            if (inScope == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' compilation database lists no "
                    + "translation units inside the audited worktree — nothing can be analysed, so "
                    + "this is infrastructure, not a pass. Check that the database was generated "
                    + "against this tree.")
                { IsDeterministic = true };

            return (inScope, outOfScope);
        }
    }

    /// <summary>
    /// Canonicalizes a database entry's <c>file</c> the way iwyu_tool's
    /// <c>fixup_compilation_db</c> does — relative <c>file</c> joins
    /// <c>directory</c> (which iwyu_tool requires on every entry) — then
    /// applies the lexical containment check against the canonical worktree
    /// root.
    /// </summary>
    private bool CanonicalEntryContained(string file, string directory, string worktreeRoot)
    {
        string combined;
        if (file.StartsWith("/", StringComparison.Ordinal))
        {
            combined = file;
        }
        else if (!directory.StartsWith("/", StringComparison.Ordinal))
        {
            // A relative directory resolves against the driver's cwd — the
            // worktree.
            combined = worktreeRoot + "/" + directory + "/" + file;
        }
        else
        {
            combined = directory + "/" + file;
        }

        var canonical = ExternalToolJsonHelpers.CollapseDotSegments(
            ExternalToolJsonHelpers.NormalizePath(combined));
        return canonical.Length > worktreeRoot.Length + 1
            && canonical.StartsWith(worktreeRoot + "/", StringComparison.Ordinal);
    }

    private void ReportCoverage((int InScope, int OutOfScope) coverage)
    {
        if (coverage.OutOfScope > 0)
        {
            _logger.LogWarning(
                "IwyuAuditor: {OutOfScope} compilation-database entries resolve outside the audited "
                    + "worktree and were not analysed (in-scope: {InScope}).",
                coverage.OutOfScope,
                coverage.InScope);
        }
        else
        {
            _logger.LogInformation(
                "IwyuAuditor: {InScope} in-worktree translation units selected for analysis.",
                coverage.InScope);
        }
    }

    private AuditUnavailableException MalformedDatabase(string detail)
        => new(
            $"could-not-verify: audit tool '{ToolName}' read a malformed compilation database "
            + $"({detail}). Regenerate it with a conforming build tool.")
        { IsDeterministic = true };

    /// <summary>
    /// Post-presence precondition: iwyu_tool is only the driver — the engine
    /// it shells out to (<c>include-what-you-use</c>) must exist and match
    /// the pinned release. Mirrors the shared <see cref="ToolVersionPin"/>
    /// semantics, which cannot be used directly because the probe must exec
    /// a binary other than <see cref="ToolName"/>.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        await ThrowIfBinaryMissingAsync(
            sandbox, workingDirectory, EngineBinary, options, ct,
            "iwyu_tool execs it once per translation unit").ConfigureAwait(false);

        var configured = _expectedVersion();
        var expected = NormalizeVersionToken(
            string.IsNullOrWhiteSpace(configured) ? DefaultExpectedVersion : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{TruncateForMessage(configured)}'); set "
                + $"CodeyBox:Plugins:{PluginId}:{ToolVersionPin.ExpectedVersionKey} to the IWYU release "
                + $"you provisioned, e.g. '{DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "engine version check",
            new SandboxExec
            {
                Argv = [EngineBinary, "--version"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
                EnvironmentVariablesToUnset = BuildToolEnvironmentRemovals(options),
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);

        var reported = result.ExitCode == 0
            ? NormalizeVersionToken(ExtractReportedVersion(result.Stdout ?? string.Empty))
            : null;
        if (reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{EngineBinary}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + "a missing or foreign engine is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{EngineBinary}' is version {reported}, but this auditor "
                + $"is pinned to {expected}. A different release changes the tool's analysis and its "
                + $"findings; provision the pinned release or set {ToolVersionPin.ExpectedVersionKey} "
                + "to the version you provisioned.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Resolves the directory the aggregated scan actually runs in, so the
    /// parser can relativize absolute reported paths onto the worktree.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

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

    private static string? GetEntryString(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
