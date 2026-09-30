using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ValeAuditorPlugin;

/// <summary>
/// Documentation auditor wrapping <c>vale</c> (prose and documentation
/// linting) on the shared <see cref="ExternalToolAuditorBase"/>: the base
/// supplies sandboxed invocation with a bounded timeout, per-stream output
/// caps, exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the vale JSON report parser
/// (<see cref="ValeJsonOutputParser"/>), the pinned tool version via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the operator-owned
/// configuration selection below, and the hermetic default posture.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — NOT blocking by
/// default.</b> Alerts at vale level <c>error</c> map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; <c>warning</c> maps
/// to <see cref="AuditSeverity.Warning"/> and <c>suggestion</c> to
/// <see cref="AuditSeverity.Info"/>, both advisory. A run that reports only
/// style suggestions passes. Operators who want every alert to block raise
/// their rules to <c>error</c> in the vale configuration (the tool's own
/// level is the gate's vocabulary) rather than asking this auditor to
/// reinterpret advisory levels.</para>
///
/// <para><b>Exit-code convention (verified against vale v3.23.0 source —
/// <c>cmd/vale/main.go</c>, <c>cmd/vale/json.go</c> — NOT the common "exit 1
/// = findings" assumption).</b> <c>0</c> = the run completed with no
/// <c>error</c>-level alert — warnings and suggestions may still be present
/// in the JSON report, so <c>0</c> is a verdict, not a clean bill;
/// <c>1</c> = the run completed with at least one <c>error</c>-level alert
/// (also a verdict); <c>2</c> = vale itself could not run (missing
/// configuration, a rule that failed to load, an unknown argument —
/// <c>handleError</c> exits <c>2</c>). Only <c>{0, 1}</c> are declared
/// findings-producing; everything else is infrastructure. A verdict-class
/// exit without a JSON report on stdout still fails closed as infrastructure
/// through the parser (vale prints at least <c>{}</c> on every completed
/// run).</para>
///
/// <para><b>Version pin.</b> A checker's rule implementations, defaults, and
/// report shape change between releases, so findings are only meaningful from
/// the build the auditor was verified against. The auditor probes
/// <c>vale --version</c> (which prints <c>vale version X.Y.Z</c>) before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Configuration: the audited repository's ruleset, unless the
/// operator says otherwise.</b> Vale cannot run without a configuration file
/// (no config is exit <c>2</c>), and the configuration — styles, rule levels,
/// vocabularies — <i>is</i> the check being run, so by default the scan uses
/// vale's own discovery: the file named by <c>--config</c>, then
/// <c>VALE_CONFIG_PATH</c>, then the first of <c>.vale.ini</c>,
/// <c>_vale.ini</c>, <c>vale.ini</c>, <c>.vale</c>, <c>_vale</c> walking up
/// from the working directory. An operator-owned <c>ConfigPath</c> (or
/// <c>--config</c> in <c>ExtraArguments</c>) takes precedence and replaces
/// repository discovery for that run. <c>--no-global</c> is always passed so
/// ambient user-level configuration outside the worktree cannot steer the
/// verdict. A repository with no discoverable configuration fails closed as
/// infrastructure: silently passing a tree vale never checked would be the
/// worst outcome available.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>vale .</c>: the worktree is
/// walked and every file the resolved configuration has a section for is
/// checked. Findings under vendored (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>, <c>.venv/</c>, <c>venv/</c>) and generated
/// (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>, <c>bin/</c>,
/// <c>obj/</c>, <c>target/</c>) prefixes are dropped at finding level via the
/// shared <c>ExcludePaths</c> backstop: prose problems there belong to
/// upstream packages or build output, not the change under audit — reporting
/// them trains operators to ignore the auditor. Vale offers no crawl-time
/// exclusion flag (its <c>--glob</c> only narrows by inclusion), so excluded
/// trees are still walked; operators who need them out of the walk narrow
/// <c>Inputs</c> or pass <c>--glob</c> in <c>ExtraArguments</c>.
/// <c>.git/</c> stays excluded: history internals are not prose.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Vale Prose Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "vale",
    InstallHint = "provision the pinned vale release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries a "
        + "version pin — e.g. unpack the vale_" + DefaultExpectedVersion
        + "_Linux_64-bit.tar.gz release asset from errata-ai/vale (now vale-cli/vale) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ValeAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.vale";

    /// <summary>
    /// Vale release the invocation and its report shape are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "3.23.0";

    /// <summary>Scoped-config key for an explicit vale configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for the vale inputs (comma-separated files or
    /// directories). Replaces the default whole-tree <c>.</c> input.
    /// </summary>
    public const string InputsKey = "Inputs";

    private static ExternalToolAuditorOptions CreateDefaults() => new()
    {
        // 0 = ran with no error-level alert (warnings/suggestions may still
        // be reported — 0 is a verdict, not a clean bill); 1 = ran with at
        // least one error-level alert. Both emit the JSON report. 2 (could
        // not run: missing config, bad rule, bad argument) and everything
        // else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in VCS internals, vendored/dependency trees, and generated
        // build output do not describe the change under audit — noise that
        // trains operators to ignore the auditor. Vale has no crawl-time
        // exclusion flag, so this list applies at finding level only; narrow
        // Inputs or pass --glob in ExtraArguments to keep such trees out of
        // the walk itself. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
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

    /// <inheritdoc />
    public override string Name => "codeybox:vale";

    /// <inheritdoc />
    protected override string ToolName => "vale";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ValeJsonOutputParser();

    /// <summary>
    /// Declared mapping from vale's alert levels to
    /// <see cref="AuditSeverity"/>: <c>error</c> is a violated must-fix rule
    /// (<see cref="AuditSeverity.Error"/>, blocking); <c>warning</c> is
    /// advisory (<see cref="AuditSeverity.Warning"/>); <c>suggestion</c> is
    /// informational (<see cref="AuditSeverity.Info"/>). Anything the parser
    /// cannot classify keeps the fail-closed <see cref="AuditSeverity.Error"/>
    /// default. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["suggestion"] = AuditSeverity.Info,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>();

        if (!ExtraArgumentsSupplyFlag(options, "--output"))
        {
            // The JSON report on stdout is the verdict. An operator --output
            // would replace the JSON the parser expects and break the run
            // into infrastructure failure; let that surface loudly.
            args.Add("--output");
            args.Add("JSON");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--no-global"))
        {
            // Never consult ambient user-level configuration outside the
            // worktree: the verdict must be a function of the operator and
            // repository configuration only.
            args.Add("--no-global");
        }

        // Config precedence: explicit operator config, else vale's own
        // repository discovery. The value is validated as an argv entry;
        // the operator's ExtraArguments --config wins by coming later.
        var configPath = ValidatedScopedValue(_configPath(), ConfigPathKey);
        if (configPath is not null && !ExtraArgumentsSupplyFlag(options, "--config"))
        {
            args.Add("--config");
            args.Add(configPath);
        }

        // Scan targets stay inside the audited worktree: a rooted path or a
        // ".." segment would point the tool outside the tree and produce
        // report paths the finding-location contract cannot express.
        var inputs = _inputs();
        if (inputs.Count > 0)
        {
            foreach (var input in inputs)
                args.Add(ValidatedRepoRelativeTarget(input, PluginId + ":" + InputsKey));
        }
        else
        {
            args.Add(".");
        }

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
        context.Logger.LogInformation(
            "ValeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
