using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ReuseAuditorPlugin;

/// <summary>
/// Licensing auditor wrapping <c>reuse</c> (REUSE specification licence
/// metadata compliance) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the <c>reuse lint --json</c> report parser
/// (<see cref="ReuseJsonOutputParser"/>), the pinned tool version via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the operator-owned
/// configuration selection below, and the hermetic default posture.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — NOT blocking by
/// default.</b> Criteria that break REUSE compliance (files without
/// copyright or licensing information, bad or missing licenses, invalid
/// SPDX expressions, unreadable files) map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; deprecation and
/// hygiene criteria (deprecated licenses, license texts without a file
/// extension, unused licenses) map to <see cref="AuditSeverity.Warning"/>
/// and <see cref="AuditSeverity.Info"/> and are advisory. A run that
/// reports only an unused license text passes. Operators who want every
/// criterion to block lower <c>MinimumSeverity</c> handling by treating
/// warnings as errors at the gate rather than asking this auditor to
/// reinterpret advisory levels.</para>
///
/// <para><b>Exit-code convention (verified against reuse v6.2.0 —
/// <c>src/reuse/cli/lint.py</c>: <c>sys.exit(0 if report.is_compliant else
/// 1)</c> — NOT assumed from the common convention).</b> <c>0</c> = the
/// project is compliant (the <c>non_compliant</c> section is empty);
/// <c>1</c> = the project is not compliant (at least one criterion
/// non-empty). Both carry the JSON report, so both are verdicts; anything
/// else (click usage errors exit <c>2</c>, missing binary <c>127</c>) means
/// the tool could not run and is infrastructure. A verdict-class exit
/// without a reuse JSON report on stdout still fails closed as
/// infrastructure through the parser.</para>
///
/// <para><b>Version pin.</b> The report shape and the checked criteria
/// change between releases, so findings are only meaningful from the build
/// the auditor was verified against. The auditor probes
/// <c>reuse --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Configuration: the audited repository's REUSE metadata, unless
/// the operator says otherwise.</b> The tool reads <c>REUSE.toml</c> (and
/// the deprecated <c>.reuse/dep5</c>), adjacent <c>.license</c> files, and
/// in-file SPDX tags from the repository itself — that metadata <i>is</i>
/// the check being run, so by default the scan uses reuse's own root
/// discovery (the VCS repository root of the working directory, else the
/// working directory). An operator-owned <c>--root</c> in
/// <c>ExtraArguments</c> takes precedence and replaces repository discovery
/// for that run.</para>
///
/// <para><b>Scope and defaults.</b> The scan covers the whole project tree
/// the tool discovers. Findings under vendored (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>, <c>.venv/</c>,
/// <c>venv/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>, <c>bin/</c>, <c>obj/</c>,
/// <c>target/</c>) prefixes are dropped at finding level via the shared
/// <c>ExcludePaths</c> backstop: licensing metadata there belongs to
/// upstream packages or build output, not the change under audit —
/// reporting it trains operators to ignore the auditor. The tool offers no
/// crawl-time exclusion flag of its own (its own ignores live in the
/// repository-authored <c>REUSE.toml</c>), so excluded trees are still
/// walked; operators who need them out of the walk narrow the scan with
/// <c>--root</c> in <c>ExtraArguments</c>. <c>.git/</c> stays excluded:
/// history internals carry no licensing metadata of their own.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: REUSE Licence Compliance",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "reuse",
    InstallHint = "provision the pinned reuse release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Python package "
        + "(pipx install reuse==" + DefaultExpectedVersion
        + ", or pip install reuse==" + DefaultExpectedVersion + " — no distro apt package "
        + "carries a version pin) through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ReuseAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.reuse";

    /// <summary>
    /// Reuse release the invocation and its report shape are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "6.2.0";

    private static ExternalToolAuditorOptions CreateDefaults() => new()
    {
        // 0 = compliant (empty non_compliant section); 1 = at least one
        // criterion non-empty. Both emit the JSON report. Anything else
        // (usage errors, missing binary) is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in VCS internals, vendored/dependency trees, and generated
        // build output do not describe the change under audit — noise that
        // trains operators to ignore the auditor. The tool has no crawl-time
        // exclusion flag of its own, so this list applies at finding level
        // only; pass --root in ExtraArguments to keep such trees out of the
        // walk itself. Operators re-include a path by overriding
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

    /// <inheritdoc />
    public override string Name => "codeybox:reuse";

    /// <inheritdoc />
    protected override string ToolName => "reuse";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ReuseJsonOutputParser();

    /// <summary>
    /// Declared mapping from reuse's compliance criteria to
    /// <see cref="AuditSeverity"/>: criteria that break REUSE compliance —
    /// files without licensing or copyright information, bad or missing
    /// licenses, invalid SPDX expressions, unreadable files — are
    /// <see cref="AuditSeverity.Error"/> (blocking); deprecation and
    /// hygiene criteria — deprecated licenses, license texts without a file
    /// extension, unused license texts — are <see cref="AuditSeverity.Warning"/>
    /// and <see cref="AuditSeverity.Info"/> (advisory). The tool itself
    /// reports no severity levels, only criteria, so this mapping <i>is</i>
    /// the severity vocabulary: raw criterion tokens never reach findings
    /// except in the description's tool-severity line. Anything the parser
    /// cannot classify keeps the fail-closed <see cref="AuditSeverity.Error"/>
    /// default.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            [ReuseJsonOutputParser.MissingLicensingInfoLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.MissingCopyrightInfoLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.BadLicensesLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.MissingLicensesLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.InvalidSpdxExpressionLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.ReadErrorsLevel] = AuditSeverity.Error,
            [ReuseJsonOutputParser.DeprecatedLicensesLevel] = AuditSeverity.Warning,
            [ReuseJsonOutputParser.LicensesWithoutExtensionLevel] = AuditSeverity.Warning,
            [ReuseJsonOutputParser.UnusedLicensesLevel] = AuditSeverity.Info,
        }, AuditSeverity.Error);

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
            "lint",
        };

        if (!ExtraArgumentsSupplyFlag(
            options,
            "--json", "-j",
            "--plain", "-p",
            "--lines", "-l",
            "--quiet", "-q"))
        {
            // The JSON report on stdout is the verdict. An operator output
            // flag would replace the JSON the parser expects and break the
            // run into infrastructure failure; let that surface loudly.
            args.Add("--json");
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
        context.Logger.LogInformation(
            "ReuseAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
