using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.EslintAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>eslint</c> (JavaScript and TypeScript analysis)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the ESLint JSON output parser
/// (<see cref="EslintJsonOutputParser"/> — ESLint's built-in
/// <c>--format json-with-metadata</c>, which embeds the process cwd so the
/// parser can relativize the absolute <c>filePath</c> values ESLint emits;
/// the SARIF formatter is a second unpinned npm package, so the native format
/// is used instead), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> ESLint rule violations at severity <c>2</c> (its "error")
/// and fatal parse errors map to <see cref="AuditSeverity.Error"/> and fail
/// the audit; violations at severity <c>1</c> (its "warn") map to
/// <see cref="AuditSeverity.Warning"/> and are advisory. Whether a rule is
/// error or warn is decided by the ruleset in force — the audited
/// repository's <c>eslint.config.*</c> by default, or an operator-pinned
/// config via <c>ConfigPath</c>/<c>--config</c>. To make every violation
/// blocking, set the rules to <c>"error"</c> in the ruleset;
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against ESLint v10.x).</b>
/// <c>0</c> = linted clean or warnings only; <c>1</c> = linted with
/// error-level violations (or a <c>--max-warnings</c> breach). Both emit the
/// JSON report on stdout, so both are findings-producing verdicts.
/// <c>2</c> = could not run: configuration problem (including the absence of
/// an <c>eslint.config.*</c> file in the audited repository) or internal
/// error — infrastructure. Exit <c>1</c>/<c>2</c> with no JSON on stdout
/// (usage errors print text, not a report) fails closed as infrastructure
/// through the parser. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly as
/// infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A linter's rule implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>eslint --version</c> before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> ESLint honors inline
/// configuration comments (<c>/* eslint-disable … */</c>, <c>/* global …
/// */</c>, per-line rule/severity overrides) authored inside the audited
/// repository — and the audit subject writes that repository. An auditor its
/// subject can silence is not a gate, so by default the scan passes
/// <c>--no-inline-config</c>, making every <c>eslint</c> comment inert;
/// findings then surface for code the comments would have suppressed.
/// Operators who deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c> in scoped config. Note the separate,
/// larger surface: <c>eslint.config.*</c> itself is repo-authored (and
/// executable JavaScript — the auditor runs with
/// <see cref="AuditCapabilities.None"/>, so it executes with no credentials
/// or network). The repo's ruleset is honored because linting against the
/// project's own lint contract is the meaningful check; operators who need a
/// fully operator-owned ruleset pin one outside the repository via
/// <c>ConfigPath</c> or <c>--config</c> + <c>--no-config-lookup</c> in
/// <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>eslint .</c>: the audited
/// repository's flat config decides which files are linted through its
/// <c>files</c>/<c>ignores</c> entries — the project's own declaration of
/// lintable scope. On top of that, findings under vendored
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) and generated
/// (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes are
/// dropped by default: problems there belong to upstream packages or build
/// output, not the change under audit, and reporting them trains operators
/// to ignore the auditor. <c>--no-warn-ignored</c> keeps ESLint's
/// "file ignored" notices for explicitly-passed patterns out of findings.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ESLint JavaScript/TypeScript Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "eslint",
    InstallHint = "provision the pinned eslint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g eslint@"
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class EslintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.eslint";

    /// <summary>
    /// ESLint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "10.10.0";

    /// <summary>Scoped-config key for an explicit ESLint configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// ESLint's inline configuration comments (<c>/* eslint-disable */</c>
    /// and friends). Default false: the audited repo must not be able to
    /// silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean or warnings only; 1 = error-level findings (or a
        // --max-warnings breach). Both emit the JSON report — both are
        // verdicts. 2 (config/internal error) and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:eslint";

    /// <inheritdoc />
    protected override string ToolName => "eslint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new EslintJsonOutputParser();

    /// <summary>
    /// Declared mapping from ESLint's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. ESLint reports numeric levels —
    /// <c>1</c> (warn) and <c>2</c> (error) — plus <c>fatal</c> for parse
    /// failures; the parser keeps those raw tokens and they are translated
    /// here, never passed through.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["2"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["1"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>
        {
            // json-with-metadata: the report carries metadata.cwd, which the
            // parser uses to relativize ESLint's absolute filePath values.
            "--format", "json-with-metadata",
            // Explicit file args an operator adds via ExtraArguments must not
            // turn "file is ignored" notices into findings.
            "--no-warn-ignored",
        };

        // The audit subject authors inline eslint-disable comments; keep them
        // inert unless the operator opts in to repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--no-inline-config");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // The repository's flat config declares lintable scope through its
        // own files/ignores entries.
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
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "EslintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
