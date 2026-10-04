using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ClippyAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>cargo-clippy</c> (Rust analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the clippy NDJSON output parser
/// (<see cref="ClippyJsonOutputParser"/> — the <c>--message-format=json</c>
/// report, one JSON object per line on stdout with the lint in
/// <c>message.code.code</c> and the location in the first primary
/// <c>message.spans[]</c> entry), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: severity-driven — not blocking by default.</b> A
/// diagnostic's <c>level</c> goes through the declared map, never raw:
/// <c>error</c> maps to <see cref="AuditSeverity.Error"/> and fails the
/// audit; <c>warning</c> maps to <see cref="AuditSeverity.Warning"/> and is
/// advisory. Clippy lints default to warn, so a lint-clean gate needs no
/// configuration while a deny-level or type-error gate fails. To make every
/// lint blocking, deny warnings in the crate (<c>[lints.clippy]</c>) or pass
/// <c>-- --deny warnings</c> in <c>ExtraArguments</c>;
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against clippy 0.1.99 / cargo
/// 1.99.0 — not assumed from the common table).</b> <c>0</c> = ran:
/// warnings included — clippy warnings do not fail the build, so lint
/// findings arrive with exit 0 and the JSON report on stdout. <c>101</c> =
/// ran with error-level diagnostics (denied lints, type errors) with the
/// JSON report on stdout — or could not run (missing <c>Cargo.toml</c>,
/// toolchain failure) with empty stdout. The discriminator is the report,
/// enforced by the parser: a non-zero exit that yields no diagnostics fails
/// closed as infrastructure. <c>1</c> = could not run (cargo usage error
/// such as an unknown flag — no JSON). <c>126</c>/<c>127</c> = cannot
/// execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly as infrastructure rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> Clippy's lints change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>cargo-clippy --version</c> before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Clippy honors suppression
/// authored inside the audited repository — <c>#[allow(...)]</c> attributes,
/// <c>clippy.toml</c>, and <c>Cargo.toml [lints]</c> tables (which can weaken
/// or disable lints) — and the audit subject writes that repository. Clippy
/// offers no flag that makes <c>#[allow]</c> attributes inert, so the base
/// cannot express that gate and this auditor does not hand-roll one;
/// suppressions are honored and documented as a limitation (see the plugin
/// README). The repo's manifest and lint configuration are honored because
/// analysis against the project's own contract is the meaningful check;
/// operators who need an operator-owned gate pin one outside the repository
/// via <c>ManifestPath</c> and treat a warnings-clean local run that
/// disagrees with the audit as a signal to inspect the diff's attributes.
/// </para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>cargo-clippy clippy --message-format=json --all-targets</c>: the whole
/// audited crate graph including tests, benches, and examples, with the
/// repository's own <c>[lints]</c> tables deciding which lints run. Set
/// <c>AllTargets</c> to <c>false</c> for lib/bins only. Findings under
/// vendored (<c>vendor/</c>, <c>third_party/</c>) and generated
/// (<c>target/</c>) prefixes are dropped by default: problems there belong
/// to upstream packages or build output, not the change under audit.</para>
///
/// <para><b>Network.</b> Resolving and downloading crate dependencies needs
/// registry access, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> unless <c>Offline</c> is set —
/// the hosts must be in the deployment's <c>AuditToolAllowedHosts</c> egress
/// list. Fully offline deployments pre-seed the cargo cache into the
/// baseline and set <c>Offline</c>; the declared capability permits egress,
/// it does not force it.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Clippy Rust Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cargo-clippy",
    InstallHint = "provision the pinned clippy release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — install the matching Rust "
        + "toolchain and add the component (`rustup toolchain install <toolchain> --component clippy`) "
        + "or `rustup component add clippy`; no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ClippyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.clippy";

    /// <summary>
    /// Clippy release the invocation and its findings are verified against
    /// (the <c>clippy 0.1.x</c> banner from <c>cargo-clippy --version</c>).
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.1.99";

    /// <summary>Scoped-config key for <c>--manifest-path</c> — the <c>Cargo.toml</c> the scan is rooted at.</summary>
    public const string ManifestPathKey = "ManifestPath";

    /// <summary>Scoped-config boolean for <c>--offline</c>: no network access of any kind.</summary>
    public const string OfflineKey = "Offline";

    /// <summary>
    /// Scoped-config boolean selecting <c>--all-targets</c> (lib, bins, tests,
    /// benches, examples). Default true: the whole crate graph is the audited
    /// scope. Set to <c>false</c> for lib/bins only.
    /// </summary>
    public const string AllTargetsKey = "AllTargets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran (clean or warnings — warnings do not fail the build, so
        // lint findings arrive with exit 0). 101 = error-level diagnostics
        // with the JSON report, or could-not-run with empty stdout — the
        // parser is the discriminator, so both are findings-producing exits
        // here and anything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 101 },
        // Findings in vendored trees and cargo build output describe code
        // that is not the change under audit — noise that trains operators
        // to ignore the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "target/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _manifestPath = static () => null;
    private Func<bool> _offline = static () => false;
    private Func<bool> _allTargets = static () => true;

    /// <inheritdoc />
    public override string Name => "codeybox:clippy";

    /// <inheritdoc />
    public override AuditCapabilities Required => _offline() ? AuditCapabilities.None : AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "cargo-clippy";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ClippyJsonOutputParser();

    /// <summary>
    /// Declared mapping from clippy/rustc diagnostic levels to CodeyBox's
    /// <see cref="AuditSeverity"/>. Clippy lints default to
    /// <c>warning</c> — advisory; <c>error</c> (denied lints, type errors)
    /// fails the audit; <c>note</c>/<c>help</c> context maps to informational.
    /// Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["bug"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["failure-note"] = AuditSeverity.Info,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["help"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // cargo-clippy follows the cargo subcommand protocol: argv[1] names
        // the subcommand it was dispatched as, so the scan must lead with
        // "clippy" (verified: without it the binary behaves as cargo check
        // and emits no JSON report).
        var args = new List<string> { "clippy" };

        if (!ExtraArgumentsSupplyFlag(options, "--message-format"))
        {
            // Machine-readable NDJSON report on stdout. An operator
            // --message-format would replace the JSON the parser expects
            // and break the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--message-format=json");
        }

        if (_allTargets()
            && !ExtraArgumentsSupplyFlag(options, "--all-targets", "--lib", "--bins", "--tests", "--benches", "--examples"))
            args.Add("--all-targets");

        if (_offline()
            && !ExtraArgumentsSupplyFlag(options, "--offline", "--frozen"))
            args.Add("--offline");

        var manifestPath = _manifestPath();
        if (!string.IsNullOrWhiteSpace(manifestPath)
            && !ExtraArgumentsSupplyFlag(options, "--manifest-path"))
        {
            args.Add("--manifest-path");
            args.Add(manifestPath.Trim());
        }

        return args;
    }

    /// <summary>
    /// Clippy's spans are worktree-relative but may arrive absolute when the
    /// manifest lives in a subdirectory, so the parser relativizes against
    /// the directory the tool actually ran in — resolved through the shared
    /// <c>pwd</c> probe, since sandbox providers may translate
    /// <paramref name="workingDirectory"/>.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _manifestPath = () => scoped[ManifestPathKey];
        _offline = () => bool.TryParse(scoped[OfflineKey], out var offline) && offline;
        // Unset or unparseable keeps the historical default: the whole crate
        // graph (lib, bins, tests, benches, examples) is the audited scope.
        _allTargets = () =>
            !bool.TryParse(scoped[AllTargetsKey], out var allTargets) || allTargets;
        context.Logger.LogInformation(
            "ClippyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
