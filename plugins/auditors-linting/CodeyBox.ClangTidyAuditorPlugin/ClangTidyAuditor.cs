using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ClangTidyAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>clang-tidy</c> (C and C++ analysis) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (clang-tidy's
/// <c>--export-fixes</c> YAML report pointed at <c>/dev/stdout</c>, parsed by
/// <see cref="ClangTidyYamlOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// translation-unit discovery below, and the default include-set / exclusion
/// posture.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Diagnostics the tool reports at level <c>Error</c> (broken
/// code, e.g. <c>clang-diagnostic-error</c>) map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; level
/// <c>Warning</c> maps to <see cref="AuditSeverity.Warning"/> and is
/// advisory. <c>MinimumSeverity</c> only drops findings, it never raises
/// them: there is no mode in which a warning fails the audit.</para>
///
/// <para><b>Exit-code convention (verified against clang-tidy 18.1.3 — not
/// assumed from the common table).</b> <c>0</c> = ran: clean (no YAML — the
/// tool emits no report when no diagnostic fired), or warnings with a YAML
/// report on stdout. <c>1</c> = ran with errors (YAML report with
/// <c>Level: Error</c> entries, e.g. a file that does not compile) or could
/// not run (bad flags, unreadable config, no checks enabled, missing input —
/// stdout is usage text or driver errors, never a YAML report). Both
/// <c>0</c> and <c>1</c> are findings-producing verdicts; <c>1</c> without a
/// YAML report fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> = cannot execute / not found — infrastructure.
/// Anything else is an unknown convention and fails loudly as infrastructure
/// rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>clang-tidy --version</c> before the scan;
/// a missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> clang-tidy honors a
/// <c>.clang-tidy</c> configuration file and <c>// NOLINT</c> comments
/// authored inside the audited repository — and the audit subject writes that
/// repository. The configuration surface is honored (its check selection is
/// the project's own analysis contract, and changes to it are visible in the
/// audited diff); operators who need a fully operator-owned gate pin one via
/// <c>Checks</c> or <c>ConfigFile</c>. <c>NOLINT</c> comments have no
/// command-line off switch, so they are always honored — a blind spot the
/// plugin README calls out. The auditor runs with
/// <see cref="AuditCapabilities.None"/>.</para>
///
/// <para><b>Scope and defaults.</b> clang-tidy analyses translation units
/// named on its command line, so each run first enumerates <c>.c</c>,
/// <c>.C</c>, <c>.cc</c>, <c>.cpp</c>, <c>.cxx</c>, <c>.cp</c> and
/// <c>.c++</c> files with a fixed discovery script (sorted, vendored and
/// generated directories pruned — see the plugin README for the exact set)
/// and passes them as positional arguments. Headers are analysed as included
/// by those translation units, never as units of their own. On top of that,
/// the finding-level <c>ExcludePaths</c> backstop drops findings under
/// vendored and generated prefixes. An empty discovery (no C/C++ translation
/// units in scope) is infrastructure, not a pass: the tool cannot produce a
/// verdict without input, and a vacuous pass would claim coverage that never
/// happened.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Clang-Tidy C/C++ Analyzer",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "clang-tidy",
    AptPackage = "clang-tidy",
    InstallHint = "provision clang-tidy into the sandbox baseline via apt (AptPackage clang-tidy) — "
        + "the Debian metapackage tracks the distro default, so pin the exact release your baseline "
        + "installs (see ExpectedVersion, default " + DefaultExpectedVersion + ") and keep the two in "
        + "step through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ClangTidyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.clang-tidy";

    /// <summary>
    /// clang-tidy release the invocation and its findings are verified against
    /// (Ubuntu's 18.1.3 build; <c>clang-tidy --version</c> reports
    /// <c>Ubuntu LLVM version 18.1.3</c>). Operators running a different
    /// pinned build set <c>ExpectedVersion</c> in the plugin's scoped config
    /// to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "18.1.3";

    /// <summary>Scoped-config key for the check selection passed to <c>--checks</c>.</summary>
    public const string ChecksKey = "Checks";

    /// <summary>Scoped-config key for an explicit configuration file path passed to <c>--config-file</c>.</summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>Scoped-config key for extra compiler flags, each passed through as <c>--extra-arg</c>.</summary>
    public const string CompileFlagsKey = "CompileFlags";

    // clang-tidy takes translation units, not directories: the discovery
    // script enumerates them. Fixed author-owned text — never built from
    // configuration — run via sh -c with the head limit as $1 (structured
    // argv, never a constructed command string). Sorting keeps argv order
    // deterministic; the head cap is one past the largest file list the scan
    // argv can hold, so hitting it means scope overflow rather than a
    // truncated scan.
    private const string DiscoveryScript =
        "find . -type d \\( -name .git -o -name vendor -o -name third_party -o -name node_modules"
        + " -o -name dist -o -name build -o -name out -o -name coverage"
        + " -o -name .venv -o -name venv -o -name __pycache__ \\) -prune"
        + " -o -type f \\( -name '*.c' -o -name '*.C' -o -name '*.cc' -o -name '*.cpp'"
        + " -o -name '*.cxx' -o -name '*.cp' -o -name '*.c++' \\) -print | sort | head -n \"$1\"";

    // Bound mirror of the shared base's built-argument ceiling: the scan argv
    // holds built entries plus one entry per file, so 257 discovered files
    // can never fit a scan that always carries at least --export-fixes.
    private const int DiscoveryHeadLimit = 257;

    // Conservative per-entry bound so a single overlong discovery line can
    // neither bloat the scan argv nor the failure message that quotes it.
    private const int MaxDiscoveredPathLength = 1024;

    // Single source of truth for the translation-unit policy on the C# side —
    // keep in sync with the -name globs in DiscoveryScript above.
    private static readonly string[] SourceExtensions = [".c", ".C", ".cc", ".cpp", ".cxx", ".cp", ".c++"];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran (clean, or warnings with a report). 1 = ran with errors
        // (report with Error entries) or could not run (usage/config error —
        // no report, so the YAML parser fails closed as infrastructure).
        // Everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/", ".venv/", "venv/", "__pycache__/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _checks = static () => null;
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _compileFlags = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:clang-tidy";

    /// <inheritdoc />
    protected override string ToolName => "clang-tidy";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ClangTidyYamlOutputParser();

    /// <summary>
    /// Declared mapping from clang-tidy's <c>Level</c> vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. Only <c>Error</c> (and
    /// <c>Fatal</c>) fail the audit; <c>Warning</c> is advisory by design.
    /// Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["remark"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // The YAML report is the verdict the parser reads: always sink it to
        // stdout. An operator --export-fixes would redirect the report away
        // from stdout; LLVM keeps the last occurrence, so the parser then
        // sees text diagnostics without a report and fails closed as
        // infrastructure rather than passing silently.
        var args = new List<string> { "--export-fixes=/dev/stdout" };

        var checks = _checks();
        if (!string.IsNullOrWhiteSpace(checks)
            && !ExtraArgumentsSupplyFlag(options, "--checks"))
        {
            args.Add("--checks=" + checks.Trim());
        }

        // --config and --config-file are mutually exclusive in the tool; an
        // operator supplying either takes over configuration entirely.
        var configFile = _configFile();
        if (!string.IsNullOrWhiteSpace(configFile)
            && !ExtraArgumentsSupplyFlag(options, "--config", "--config-file"))
        {
            args.Add("--config-file=" + configFile.Trim());
        }

        // Compiler flags need no "--" separator: --extra-arg appends to the
        // compiler command line from anywhere in argv.
        foreach (var flag in _compileFlags())
        {
            if (!string.IsNullOrWhiteSpace(flag))
                args.Add("--extra-arg=" + flag.Trim());
        }

        return args;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var files = await DiscoverSourcesAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false);
        if (files.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' discovered no C/C++ translation units in the "
                + $"audited repository. Disable CodeyBox:Plugins:{PluginId} for projects without C/C++ "
                + "sources — without input the tool cannot produce a verdict, so this is "
                + "infrastructure, not a pass.")
            { IsDeterministic = true };

        var built = BuildToolArguments(options)?.Count ?? 0;
        if (built + files.Count > DiscoveryHeadLimit - 1)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' discovered {files.Count} C/C++ translation units, "
                + "more than one scan invocation holds. Move generated sources under an excluded prefix "
                + "(build/, out/, vendor/ and friends) so discovery stays in scope.")
            { IsDeterministic = true };

        return files;
    }

    private async Task<IReadOnlyList<string>> DiscoverSourcesAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "source discovery",
            new SandboxExec
            {
                Argv = ["sh", "-c", DiscoveryScript, "sh", DiscoveryHeadLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' source discovery could not run: the sandbox exec "
                + "transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' source discovery failed (exit {result.ExitCode}) — "
                + "without the file list there is nothing to analyze, so this is infrastructure, not a "
                + "verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        var files = new List<string>();
        var stdout = result.Stdout ?? string.Empty;
        if (stdout.Length == 0)
            return files;

        // A complete discovery always ends in a newline (find -print emits
        // one per file); a missing terminator means the probe output cap cut
        // the list, and proceeding would silently narrow the scan.
        if (!stdout.EndsWith('\n'))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' source discovery output was truncated — too many "
                + "C/C++ files for one scan. Move generated sources under an excluded prefix (build/, out/, "
                + "vendor/ and friends) so discovery stays in scope.");

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            files.Add(NormalizeDiscoveredPath(line));
            if (files.Count >= DiscoveryHeadLimit)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' discovered more than "
                    + $"{DiscoveryHeadLimit - 1} C/C++ translation units — more than one scan invocation "
                    + "holds. Move generated sources under an excluded prefix (build/, out/, vendor/ and "
                    + "friends) so discovery stays in scope.")
                { IsDeterministic = true };
        }

        return files;
    }

    // Discovery output is repository file content — untrusted. Every entry
    // must be a repository-relative source path, or the scan scope cannot be
    // trusted and the run fails closed instead of scanning a narrowed set.
    // In particular an entry starting with '-' after the protective "./"
    // prefix is stripped would be option-parsed by the tool (LLVM keeps the
    // last occurrence of flags such as --checks/--config-file/--export-fixes,
    // and ExtraArguments are appended after the file list), so such entries
    // fail closed here instead of reaching the scan argv.
    private static string NormalizeDiscoveredPath(string entry)
    {
        var normalized = entry.Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0
            || normalized.Length > MaxDiscoveredPathLength
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("-", StringComparison.Ordinal)
            || normalized.Contains('\n', StringComparison.Ordinal)
            || normalized.Contains('\r', StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal)
            || !HasSourceExtension(normalized))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool 'clang-tidy' source discovery returned an entry outside the "
                + $"expected repository-relative shape ('{TruncateForMessage(entry)}'). The scan scope cannot "
                + "be trusted, so this is infrastructure, not a verdict on the diff.")
            { IsDeterministic = true };
        return normalized;
    }

    private static bool HasSourceExtension(string path)
    {
        foreach (var extension in SourceExtensions)
        {
            if (path.EndsWith(extension, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _checks = () => scoped[ChecksKey];
        _configFile = () => scoped[ConfigFileKey];
        _compileFlags = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[CompileFlagsKey]);
        context.Logger.LogInformation(
            "ClangTidyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
