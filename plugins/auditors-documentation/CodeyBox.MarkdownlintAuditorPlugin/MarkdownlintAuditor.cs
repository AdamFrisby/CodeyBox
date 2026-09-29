using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.MarkdownlintAuditorPlugin;

/// <summary>
/// Documentation auditor wrapping <c>markdownlint</c> (Markdown diagnostics)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the <c>--json</c> report
/// parser (<see cref="MarkdownlintJsonOutputParser"/> — the report is read
/// from stderr, where markdownlint-cli writes it), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// suppression posture and default scope below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Diagnostics the tool reports at severity <c>error</c> map
/// to <see cref="AuditSeverity.Error"/> and fail the audit; diagnostics at
/// severity <c>warning</c> map to <see cref="AuditSeverity.Warning"/> and
/// are advisory. Whether a rule reports as error or warning is decided by
/// the configuration in force — the audited repository's markdownlint
/// config files by default, or an operator-pinned config via
/// <c>ConfigPath</c>. <c>MinimumSeverity</c> only drops findings, it never
/// raises them.</para>
///
/// <para><b>Exit-code convention (verified against markdownlint-cli 0.49.1 —
/// not assumed from the common table).</b> <c>0</c> = linted clean, or
/// linted with warning-severity diagnostics only (the JSON report is still
/// emitted on stderr — both are findings-producing verdicts).
/// <c>1</c> = linted with error-severity diagnostics (the JSON report is
/// emitted on stderr — a verdict). <c>2</c> = could not write the
/// <c>--output</c> file; <c>3</c> = could not load a custom <c>--rules</c>
/// module; <c>4</c> = unexpected problem (malformed config, bad flags) — all
/// infrastructure. A verdict-class exit without a JSON report on stderr
/// still fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> = cannot execute / not found — infrastructure.
/// Anything else is an unknown convention and fails loudly as
/// infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A linter's rule implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>markdownlint --version</c>
/// before the scan; a missing binary, an unrecognised version string, or a
/// version other than <c>ExpectedVersion</c> is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> The audit subject writes
/// the repository, and markdownlint-cli honors a suppression surface
/// authored inside it: a <c>.markdownlintignore</c> file in the working
/// directory is loaded unless <c>--ignore-path</c> redirects it. By default
/// the scan pins <c>--ignore-path /dev/null</c> (an empty ignore list), so
/// the repository's ignore file is inert and cannot hide committed
/// documentation from the scan; <c>TrustRepositorySuppression</c> opts back
/// in to the repository's own ignore file. The larger surface is the
/// markdownlint configuration itself (<c>.markdownlint.jsonc</c>,
/// <c>.markdownlint.json</c>, <c>.markdownlint.yaml</c>,
/// <c>.markdownlint.yml</c>, <c>.markdownlintrc</c>): it is repo-authored,
/// it merges underneath an operator <c>--config</c> file, and its rule
/// selection (including per-rule <c>severity</c>) decides what is reported —
/// changes to it are visible in the audited diff. The auditor runs with
/// <see cref="AuditCapabilities.None"/> (no agent credentials, no network).
/// Operators who need a fully operator-owned ruleset pin one outside the
/// repository via <c>ConfigPath</c> and narrow the effective ruleset there.
/// Inline <c>markdownlint-disable</c> comments inside Markdown files are
/// always honored — the CLI offers no flag to disable them — and are
/// documented in the plugin README as a known blind spot.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>markdownlint --json --dot
/// .</c>: markdownlint-cli has no default exclusion list (vendored trees
/// such as <c>node_modules/</c> are walked unless ignored), so each
/// <c>ExcludePaths</c> entry is also passed as <c>--ignore</c> to keep
/// excluded trees out of the walk entirely — findings inside them would
/// never be checked before the finding-level filter could drop them.
/// Dot-directories are included (<c>--dot</c>) because documentation
/// legitimately lives under paths such as <c>.github/</c>. On top of that,
/// the finding-level <c>ExcludePaths</c> backstop drops findings under
/// vendored and generated prefixes: violations there belong to upstream
/// packages or build output, not the change under audit — reporting them
/// produces noise that trains operators to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Markdownlint Markdown Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "markdownlint",
    InstallHint = "provision the pinned markdownlint-cli release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g markdownlint-cli@"
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class MarkdownlintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.markdownlint";

    /// <summary>
    /// markdownlint-cli release the invocation and its report shape are
    /// verified against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.49.1";

    /// <summary>Scoped-config key for an explicit markdownlint configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for the scan inputs (comma-separated files, globs,
    /// or directories). Replaces the default whole-tree <c>.</c> input.
    /// </summary>
    public const string InputsKey = "Inputs";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression — the
    /// working-directory <c>.markdownlintignore</c> file. Default false: the
    /// audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Exit code markdownlint-cli returns for a run that produced no
    /// error-severity diagnostics (clean, or warnings only). A warnings-only
    /// run still emits the JSON report, so this exit is a verdict, and an
    /// empty report on this exit is the clean pass.
    /// </summary>
    internal const int CleanExitCode = 0;

    // Passing --ignore-path replaces the working-directory
    // .markdownlintignore the CLI would otherwise load. /dev/null reads as
    // an empty ignore list, so the repository's ignore file cannot steer the
    // audit. An operator --ignore-path or TrustRepositorySuppression
    // outranks the pin.
    private const string PinnedEmptyIgnorePath = "/dev/null";

    private static ExternalToolAuditorOptions CreateDefaults() => new()
    {
        // 0 = clean or warnings only (the warnings-only report is still
        // emitted — a verdict); 1 = error-severity diagnostics found (a
        // verdict). 2 (output-file write failure), 3 (custom-rule load
        // failure), 4 (config/usage error), and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in VCS internals, vendored/dependency trees, and
        // generated build output do not describe the change under audit —
        // noise that trains operators to ignore the auditor. Each entry is
        // also passed to markdownlint-cli as --ignore so those files are
        // never walked (the tool has no default exclusion list);
        // operators re-include a path by overriding ExcludePaths in
        // scoped config.
        ExcludePaths =
        [
            ".git/",
            "vendor/",
            "third_party/",
            "node_modules/",
            ".venv/",
            "venv/",
            "dist/",
            "build/",
            "out/",
            "coverage/",
            "bin/",
            "obj/",
            "target/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = CreateDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<IReadOnlyList<string>> _inputs = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:markdownlint";

    /// <inheritdoc />
    protected override string ToolName => "markdownlint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new MarkdownlintJsonOutputParser();

    /// <summary>
    /// Declared mapping from markdownlint's severity vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>: <c>error</c> diagnostics are
    /// violated rules that fail the audit; <c>warning</c> diagnostics are
    /// advisory. The tool reports no other levels; anything unrecognised
    /// (including a missing severity) stays advisory rather than blocking
    /// on an unknown dialect. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var operatorSuppliesConfig = ExtraArgumentsSupplyFlag(options, "--config", "-c");
        var operatorSuppliesIgnorePath = ExtraArgumentsSupplyFlag(options, "--ignore-path", "-p");

        var args = new List<string>
        {
            // The JSON issue array on stderr is the verdict; the parser
            // reads it from the captured stderr stream.
            "--json",
            // Docs legitimately live under dot-directories (.github/).
            // .git/ stays out via the ExcludePaths-derived --ignore below.
            "--dot",
        };

        // The audit subject authors .markdownlintignore; keep it inert
        // unless the operator opts in to repo-controlled suppression.
        // --ignore-path is single-valued, so defer to the operator's own
        // setting when present.
        if (!_trustRepositorySuppression() && !operatorSuppliesIgnorePath)
        {
            args.Add("--ignore-path");
            args.Add(PinnedEmptyIgnorePath);
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath) && !operatorSuppliesConfig)
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // Keep excluded trees out of the walk entirely — markdownlint-cli
        // has no default exclusion list, so without these every vendored
        // Markdown file would be linted (and reported) before the
        // findings-level ExcludePaths filter could drop it. --ignore is
        // repeatable, so these compose with any operator --ignore entries.
        foreach (var entry in options.ExcludePaths)
        {
            var normalized = NormalizeExcludePathEntry(entry);
            if (normalized is not null)
            {
                args.Add("--ignore");
                args.Add(normalized);
            }
        }

        var inputs = _inputs();
        if (inputs.Count > 0)
            args.AddRange(inputs);
        else
            args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, CreateDefaults());
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _inputs = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[InputsKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "MarkdownlintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
