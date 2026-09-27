using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CppcheckAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>cppcheck</c> (C and C++ analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (cppcheck's built-in
/// XML emitter on stderr, parsed by <see cref="CppcheckXmlOutputParser"/>),
/// the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Diagnostics at cppcheck severity <c>error</c> (definite
/// defects such as null-pointer dereference or uninitialized variables) map
/// to <see cref="AuditSeverity.Error"/> and fail the audit; <c>warning</c>,
/// <c>style</c>, <c>performance</c> and <c>portability</c> map to
/// <see cref="AuditSeverity.Warning"/> and are advisory, and
/// <c>information</c> (e.g. <c>missingInclude</c>) maps to
/// <see cref="AuditSeverity.Info"/>. <c>MinimumSeverity</c> only drops
/// findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against cppcheck 2.13.0 — not
/// assumed from the common table).</b> cppcheck exits <c>0</c> even when it
/// finds defects, so the scan passes <c>--error-exitcode=1</c> explicitly:
/// <c>0</c> = checked clean (empty <c>&lt;errors&gt;</c>); <c>1</c> = checked
/// with defects (XML report on stderr) <i>or</i> could not run (usage errors
/// and "no input files" print text to stdout with no XML). Both <c>0</c> and
/// <c>1</c> are findings-producing verdicts; exit <c>1</c> without XML on
/// stderr fails closed as infrastructure through the parser — except the
/// exact "could not find or open any of the paths given." diagnostic, which
/// means the tree has no checkable C/C++ files and is a clean pass.
/// <c>126</c>/<c>127</c> = cannot execute / not found — infrastructure.
/// Anything else is an unknown convention and fails loudly as infrastructure
/// rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>cppcheck --version</c> before the scan; a
/// missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> cppcheck honors inline
/// <c>// cppcheck-suppress &lt;id&gt;</c> comments only when
/// <c>--inline-suppr</c> is passed, and suppression-list files only when
/// <c>--suppressions-list</c> names them — the scan passes neither, so the
/// audited repository cannot silence the audit by default (verified: a
/// suppression comment without <c>--inline-suppr</c> still reports). No
/// cppcheck configuration is auto-read from the repository: there is no
/// repo-local config file format, so the only repo-authored inputs are the
/// sources themselves. Operators who deliberately trust repo-authored
/// suppression pass <c>--inline-suppr</c> in <c>ExtraArguments</c>; an
/// operator-owned suppressions file is pinned via <c>SuppressionsPath</c> in
/// scoped config (it must live outside the audited tree).</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>cppcheck --xml
/// --xml-version=2 --enable=all --error-exitcode=1 --quiet .</c>:
/// <c>--enable=all</c> turns on every check category (the default run covers
/// only a subset — verified: style checks such as <c>unusedFunction</c> need
/// it), and the severity map keeps the extra categories advisory rather than
/// blocking. cppcheck reports worktree-relative paths, so the finding-level
/// <c>ExcludePaths</c> backstop drops findings under vendored and generated
/// prefixes. The <c>checkersReport</c> meta-diagnostic (emitted on every
/// <c>--enable=all</c> run, with no file position) is excluded by default
/// through the shared rule-exclusion mechanism. The scan writes nothing into
/// the audited tree (<c>--output-file</c> is never passed; the XML report
/// stays on the captured stderr stream).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Cppcheck C/C++ Analyser",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cppcheck",
    AptPackage = "cppcheck",
    InstallHint = "install the pinned cppcheck release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") — the declared apt package installs it into the sandbox baseline "
        + "automatically when this plugin is enabled; on baselines whose distro cppcheck differs, set "
        + "ExpectedVersion to the provisioned release")]
public sealed class CppcheckAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cppcheck";

    /// <summary>
    /// cppcheck release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.13.0";

    /// <summary>
    /// Scoped-config key for an operator-owned cppcheck suppressions file,
    /// passed as <c>--suppressions-list</c>. It must live outside the audited
    /// repository: a repo-authored file would let the audit subject silence
    /// the gate. Ignored when <c>ExtraArguments</c> already supplies
    /// <c>--suppressions-list</c>.
    /// </summary>
    public const string SuppressionsPathKey = "SuppressionsPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = checked clean; 1 = defects found (XML on stderr) or could not
        // run (text, no XML — fails closed in the parser). Anything else is
        // infrastructure. cppcheck's default exit-0-always is overridden by
        // the --error-exitcode=1 the scan always passes.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // checkersReport is a tool-configuration meta message emitted on
        // every --enable=all run (no file position), not a finding about
        // code. Excluded through the shared exact rule-id mechanism;
        // overriding ExcludedRules in scoped config replaces this default.
        ExcludedRules = new HashSet<string>(StringComparer.Ordinal) { "checkersReport" },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config. cppcheck reports
        // worktree-relative paths, so these prefixes match.
        ExcludePaths = ["vendor/", "third_party/", "external/", "node_modules/", "build/", "out/", "dist/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _suppressionsPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:cppcheck";

    /// <inheritdoc />
    protected override string ToolName => "cppcheck";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new CppcheckXmlOutputParser();

    /// <summary>
    /// Declared mapping from cppcheck's severity vocabulary (the closed set
    /// <c>--errorlist</c> reports for 2.13.0: error, warning, style,
    /// performance, portability, information) to CodeyBox's
    /// <see cref="AuditSeverity"/>. Definite defects block the gate; every
    /// other category is advisory. Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["style"] = AuditSeverity.Warning,
            ["performance"] = AuditSeverity.Warning,
            ["portability"] = AuditSeverity.Warning,
            ["information"] = AuditSeverity.Info,
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
        var args = new List<string>();

        // Machine-readable report on stderr for the XML parser. cppcheck
        // accepts repeats of these flags, but an operator override would
        // replace the report the parser expects (--xml-version) or the exit
        // convention the base classifies on (--error-exitcode) — defer to
        // the operator's own setting and let the mismatch surface loudly.
        args.Add("--xml");
        if (!ExtraArgumentsSupplyFlag(options, "--xml-version"))
        {
            args.Add("--xml-version");
            args.Add("2");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--error-exitcode"))
        {
            // Without this cppcheck exits 0 even with defects; the base
            // could not tell "ran and found problems" from "clean".
            args.Add("--error-exitcode");
            args.Add("1");
        }

        // Progress lines ("Checking ...") go to stdout; keep them out of the
        // captured output. Verified repeat-safe and orthogonal to the XML
        // report and the no-input-files diagnostic.
        args.Add("--quiet");

        if (!ExtraArgumentsSupplyFlag(options, "--enable"))
        {
            // Full check coverage: the default run omits style, performance,
            // portability and information categories. cppcheck --enable is
            // additive, so an operator --enable replaces this wholesale;
            // subtract from "all" with --disable=<id> instead.
            args.Add("--enable");
            args.Add("all");
        }

        // Never pass --inline-suppr: it would honor suppression comments
        // authored in the audited tree. Operators opt in via ExtraArguments.
        var suppressionsPath = _suppressionsPath();
        if (!string.IsNullOrWhiteSpace(suppressionsPath)
            && !ExtraArgumentsSupplyFlag(options, "--suppressions-list"))
        {
            args.Add("--suppressions-list");
            args.Add(suppressionsPath.Trim());
        }

        args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _suppressionsPath = () => scoped[SuppressionsPathKey];
        context.Logger.LogInformation(
            "CppcheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
