using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ApiCompatAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>apicompat</c> (Microsoft.DotNet.ApiCompat.Tool —
/// .NET public API breaking-change detection) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed invocation with a
/// bounded timeout, per-stream output caps, exit-code classification, severity mapping,
/// finding identity, and per-auditor configuration. This class adds the text report
/// parser (<see cref="ApiCompatReportParser"/> — the tool exposes no JSON mode; its
/// verdict is <c>CPnnnn:</c> diagnostic lines on stderr plus the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>), the assembly /
/// package mode selection below, and the side-effect guard over operator arguments.
///
/// <para><b>Gate behaviour: blocking.</b> <c>apicompat</c> reports no severity per
/// diagnostic — every <c>CPnnnn</c> line it emits is a public API compatibility break
/// (removed type/member, changed signature) — so every finding maps to
/// <see cref="AuditSeverity.Error"/> and fails the audit. There is no advisory mode;
/// narrow scope with <c>IncludedRules</c>/<c>ExcludedRules</c>, <c>NoWarn</c>, or an
/// operator-owned <c>SuppressionFile</c> instead. Note the consequence: raising
/// <c>MinimumSeverity</c> above <c>error</c> drops every finding.</para>
///
/// <para><b>Exit-code convention (verified against 10.0.401 live runs — not the common
/// 0/1/2 convention).</b> <c>0</c> = the comparison completed and no breaking change
/// exists (the clean-run marker on stdout); <c>1</c> = ambiguous: breaking changes
/// (diagnostic lines on stderr plus the <c>API breaking changes found.</c> trailer) or
/// the run failed (missing assembly → <c>Unhandled exception: …</c> with a stack trace;
/// bad invocation → usage text) — the parser tells them apart by content. A <c>1</c>
/// exit whose output carries no parseable <c>CPnnnn</c> line contradicts the output
/// contract and fails closed as infrastructure through the parser, as does any other
/// non-declared exit. <c>126</c>/<c>127</c> are cannot-execute / not-found —
/// infrastructure.</para>
///
/// <para><b>Mode selection.</b> Assembly mode is the default:
/// <c>apicompat -l &lt;baseline&gt; -r &lt;current&gt;</c> from the scoped
/// <c>LeftAssembly</c> / <c>RightAssembly</c> keys (comma-separated for multi-assembly
/// comparisons). Package mode — <c>apicompat package &lt;pkg&gt; --baseline-package
/// &lt;base&gt;</c> — applies when <c>Package</c> or <c>BaselinePackage</c> is
/// configured or <c>ExtraArguments</c> carries the <c>package</c> subcommand. Both modes
/// at once (assembly keys plus package keys) is a deterministic configuration failure,
/// and no resolvable baseline at all is a deterministic infrastructure failure pointing
/// at the baseline knobs — a compatibility check without a "previous API" is
/// meaningless, and enabling this auditor without configuring it is a loud
/// misconfiguration, never a silent skip. An operator-supplied flag in
/// <c>ExtraArguments</c> takes precedence over the matching scoped key: the auditor
/// emits nothing for that slot and the base appends the operator's argv verbatim.</para>
///
/// <para><b>Version pin.</b> The diagnostic catalogue and the report shape change between
/// releases, so findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>apicompat --version</c> before every scan; a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool — never a pass,
/// never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> <c>apicompat</c> reads its
/// compatibility suppressions only from the file <c>SuppressionFile</c> points at (or a
/// <c>--suppression-file</c> in <c>ExtraArguments</c>); it consults no repository file
/// implicitly. A suppression file that lives inside the audited repository therefore
/// lets a change set soften the check applied to it. The auditor does not second-guess
/// the configured file — but operators who need the gate to be independent of
/// repo-authored tuning should point <c>SuppressionFile</c> at an operator-owned file
/// outside the repository. Rule ids in findings (e.g. <c>CP0002</c>) additionally stay
/// filterable after the fact through the shared <c>IncludedRules</c>/<c>ExcludedRules</c>
/// knobs, which the audit host applies — outside the subject's reach.</para>
///
/// <para><b>Scope and defaults.</b> The scan compares exactly the configured
/// assemblies/packages: the tool never walks the repository, so vendored or generated
/// trees cannot produce findings and the auditor ships no <c>ExcludePaths</c> default.
/// Findings carry member identities, never file locations, so path exclusion has
/// nothing to match — rule selection (<c>IncludedRules</c>/<c>ExcludedRules</c>,
/// <c>NoWarn</c>) is the narrowing mechanism.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ApiCompat .NET API Compatibility",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "apicompat",
    InstallHint = "provision the pinned apicompat release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "it — with 'dotnet tool install --global Microsoft.DotNet.ApiCompat.Tool --version "
        + DefaultExpectedVersion + "' (which needs the .NET SDK on PATH) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ApiCompatAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.apicompat";

    /// <summary>
    /// apicompat release the invocation, its exit convention, and its report
    /// shape were verified against. Operators running a different pinned
    /// build set <c>ExpectedVersion</c> in the plugin's scoped config to
    /// match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "10.0.401";

    /// <summary>Scoped-config key for baseline assemblies (<c>-l/--left-assembly</c>, comma-separated).</summary>
    public const string LeftAssemblyKey = "LeftAssembly";

    /// <summary>Scoped-config key for current assemblies (<c>-r/--right-assembly</c>, comma-separated).</summary>
    public const string RightAssemblyKey = "RightAssembly";

    /// <summary>Scoped-config key for the current package (<c>package &lt;pkg&gt;</c>).</summary>
    public const string PackageKey = "Package";

    /// <summary>Scoped-config key for the baseline package (<c>--baseline-package</c>).</summary>
    public const string BaselinePackageKey = "BaselinePackage";

    /// <summary>
    /// Scoped-config key for compatibility suppression files (<c>--suppression-file</c>,
    /// comma-separated). Prefer an operator-owned file outside the audited repository.
    /// </summary>
    public const string SuppressionFileKey = "SuppressionFile";

    /// <summary>Scoped-config key for assembly-mode strict checks (<c>--strict-mode</c>).</summary>
    public const string StrictModeKey = "StrictMode";

    /// <summary>Scoped-config key for checking internal APIs too (<c>--respect-internals</c>).</summary>
    public const string RespectInternalsKey = "RespectInternals";

    /// <summary>Scoped-config key for the parameter-name rule (<c>--enable-rule-cannot-change-parameter-name</c>).</summary>
    public const string EnableRuleCannotChangeParameterNameKey = "EnableRuleCannotChangeParameterName";

    /// <summary>Scoped-config key for the attributes rule (<c>--enable-rule-attributes-must-match</c>).</summary>
    public const string EnableRuleAttributesMustMatchKey = "EnableRuleAttributesMustMatch";

    /// <summary>Scoped-config key for disabling diagnostics (<c>--noWarn &lt;ids&gt;</c>).</summary>
    public const string NoWarnKey = "NoWarn";

    private const string LeftFlag = "--left-assembly";
    private const string LeftShortFlag = "-l";
    private const string RightFlag = "--right-assembly";
    private const string RightShortFlag = "-r";
    private const string BaselinePackageFlag = "--baseline-package";
    private const string SuppressionFileFlag = "--suppression-file";
    private const string NoWarnFlag = "--noWarn";
    private const string StrictModeFlag = "--strict-mode";
    private const string RespectInternalsFlag = "--respect-internals";
    private const string ParameterNameRuleFlag = "--enable-rule-cannot-change-parameter-name";
    private const string AttributesRuleFlag = "--enable-rule-attributes-must-match";
    private const string GenerateSuppressionFileFlag = "--generate-suppression-file";
    private const string SuppressionOutputFileFlag = "--suppression-output-file";
    private const string PackageSubcommand = "package";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean (clean-run marker); 1 = breaking changes found OR
        // could not run — the parser tells them apart by content (CPnnnn
        // diagnostic lines mean findings, their absence means
        // infrastructure). Everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // No default: the tool compares exactly the configured
        // assemblies/packages and never walks the tree, so vendored or
        // generated code cannot produce findings to exclude. Findings carry
        // member identities, never file paths, so path exclusion has
        // nothing to match — narrow with IncludedRules/ExcludedRules/NoWarn.
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _leftAssembly = static () => null;
    private Func<string?> _rightAssembly = static () => null;
    private Func<string?> _package = static () => null;
    private Func<string?> _baselinePackage = static () => null;
    private Func<string?> _suppressionFile = static () => null;
    private Func<string?> _noWarn = static () => null;
    private Func<bool> _strictMode = static () => false;
    private Func<bool> _respectInternals = static () => false;
    private Func<bool> _enableRuleCannotChangeParameterName = static () => false;
    private Func<bool> _enableRuleAttributesMustMatch = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:apicompat";

    /// <inheritdoc />
    protected override string ToolName => "apicompat";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } =
        new ApiCompatReportParser();

    /// <summary>
    /// Declared mapping from apicompat's severity vocabulary to
    /// <see cref="AuditSeverity"/>. The tool prints every diagnostic under its
    /// "API compatibility errors" header with no per-diagnostic level, so the
    /// parser labels each finding <c>error</c> and the default resolves
    /// everything (including any future level the map does not name) to
    /// <see cref="AuditSeverity.Error"/>. Raw tool levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["breaking"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["dangerous"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["non-breaking"] = AuditSeverity.Info,
            ["safe"] = AuditSeverity.Info,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <summary>
    /// Builds the scan argv. Exactly one mode wins: package mode when a package-side
    /// scoped key is set or <c>ExtraArguments</c> carries the <c>package</c> subcommand,
    /// otherwise assembly mode. Assembly keys combined with package keys are a
    /// deterministic configuration failure, as is a mode with no resolvable baseline
    /// (missing key and no operator-supplied flag). For any slot the operator supplies
    /// via <c>ExtraArguments</c>, the auditor emits nothing — the base appends the
    /// operator's argv verbatim. Values travel as their own argv entries, never through
    /// a shell.
    /// </summary>
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lefts = SplitScopedList(_leftAssembly(), LeftAssemblyKey);
        var rights = SplitScopedList(_rightAssembly(), RightAssemblyKey);
        var package = ValidatedScopedValue(_package(), PackageKey);
        var baselinePackage = ValidatedScopedValue(_baselinePackage(), BaselinePackageKey);
        var packageMode = package is not null
            || baselinePackage is not null
            || OptionsCarrySubcommand(options, PackageSubcommand);

        if (packageMode && (lefts.Count > 0 || rights.Count > 0))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has both assembly baselines "
                + $"({LeftAssemblyKey}/{RightAssemblyKey}) and package baselines "
                + $"({PackageKey}/{BaselinePackageKey}) configured — set exactly one comparison mode.")
            { IsDeterministic = true };

        return packageMode
            ? BuildPackageArguments(options, package, baselinePackage)
            : BuildAssemblyArguments(options, lefts, rights);
    }

    private List<string> BuildPackageArguments(
        ExternalToolAuditorOptions options, string? package, string? baselinePackage)
    {
        if (OptionsCarrySubcommand(options, PackageSubcommand))
            return [];

        if (package is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' is in package mode ({BaselinePackageKey} is set) "
                + $"but scoped '{PackageKey}' names no current package to validate. Configure "
                + $"CodeyBox:Plugins:{PluginId}:{PackageKey} (and :{BaselinePackageKey} for the "
                + "baseline), or drive the whole invocation through ExtraArguments.")
            { IsDeterministic = true };

        var args = new List<string> { PackageSubcommand, package };
        if (!ExtraArgumentsSupplyFlag(options, BaselinePackageFlag))
        {
            if (baselinePackage is null)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has no baseline to compare against: scoped "
                    + $"'{PackageKey}' names the current package but neither scoped '{BaselinePackageKey}' "
                    + $"nor an ExtraArguments '{BaselinePackageFlag}' flag names the baseline package. "
                    + "A compatibility check without a previous API is meaningless — configure one.")
                { IsDeterministic = true };
            args.Add(BaselinePackageFlag);
            args.Add(baselinePackage);
        }

        AddSharedFlags(args, options);
        return args;
    }

    private List<string> BuildAssemblyArguments(
        ExternalToolAuditorOptions options, IReadOnlyList<string> lefts, IReadOnlyList<string> rights)
    {
        var args = new List<string>();
        var operatorLeft = ExtraArgumentsSupplyFlag(options, LeftFlag, "--left", LeftShortFlag);
        var operatorRight = ExtraArgumentsSupplyFlag(options, RightFlag, "--right", RightShortFlag);

        if (!operatorLeft)
        {
            if (lefts.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has no baseline to compare against: scoped "
                    + $"'{LeftAssemblyKey}' names no baseline assembly and no ExtraArguments "
                    + "'-l/--left-assembly' flag does either. A compatibility check without a "
                    + "previous API is meaningless — configure "
                    + $"CodeyBox:Plugins:{PluginId}:{LeftAssemblyKey} (and :{RightAssemblyKey} for the "
                    + "current assemblies), or drive the whole invocation through ExtraArguments.")
                { IsDeterministic = true };
            foreach (var left in lefts)
            {
                args.Add(LeftFlag);
                args.Add(left);
            }
        }

        if (!operatorRight)
        {
            if (rights.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has no current API to compare: scoped "
                    + $"'{RightAssemblyKey}' names no current assembly and no ExtraArguments "
                    + "'-r/--right-assembly' flag does either. Configure "
                    + $"CodeyBox:Plugins:{PluginId}:{RightAssemblyKey}, or drive the whole invocation "
                    + "through ExtraArguments.")
                { IsDeterministic = true };
            foreach (var right in rights)
            {
                args.Add(RightFlag);
                args.Add(right);
            }
        }

        if (_strictMode() && !ExtraArgumentsSupplyFlag(options, StrictModeFlag))
            args.Add(StrictModeFlag);

        AddSharedFlags(args, options);
        return args;
    }

    private void AddSharedFlags(List<string> args, ExternalToolAuditorOptions options)
    {
        // StrictMode is assembly-mode only (package validation has its own
        // per-framework strict flags, reachable through ExtraArguments), so
        // only the callers that accept it emit it.

        if (!ExtraArgumentsSupplyFlag(options, SuppressionFileFlag))
        {
            foreach (var suppression in SplitScopedList(_suppressionFile(), SuppressionFileKey))
            {
                args.Add(SuppressionFileFlag);
                args.Add(suppression);
            }
        }

        if (!ExtraArgumentsSupplyFlag(options, NoWarnFlag))
            AddValueFlag(args, NoWarnFlag, ValidatedScopedValue(_noWarn(), NoWarnKey));

        if (_respectInternals() && !ExtraArgumentsSupplyFlag(options, RespectInternalsFlag))
            args.Add(RespectInternalsFlag);
        if (_enableRuleCannotChangeParameterName()
            && !ExtraArgumentsSupplyFlag(options, ParameterNameRuleFlag))
            args.Add(ParameterNameRuleFlag);
        if (_enableRuleAttributesMustMatch()
            && !ExtraArgumentsSupplyFlag(options, AttributesRuleFlag))
            args.Add(AttributesRuleFlag);
    }

    /// <summary>
    /// Side-effect guard on the live path: <c>--generate-suppression-file</c> and
    /// <c>--suppression-output-file</c> make the scan write files into the sandbox —
    /// the audited worktree — mutating the tree under audit and letting a run mint a
    /// suppression file a later run could mistake for operator configuration. Both
    /// spellings (bare and <c>--flag=value</c>) fail closed as deterministic
    /// infrastructure before the scan runs. Assembly paths themselves need no guard:
    /// the tool opens them through Roslyn metadata readers — it never executes them —
    /// and every value travels as its own argv entry, never through a shell.
    /// </summary>
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var extra in options.ExtraArguments)
        {
            if (IsSuppressionWriteFlag(extra))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' rejects ExtraArguments entry '{extra}': "
                    + "suppression-file generation writes into the audited tree and would let a scan "
                    + "mint the configuration a later scan enforces. Generate suppression files "
                    + "out-of-band instead.")
                { IsDeterministic = true };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _leftAssembly = () => scoped[LeftAssemblyKey];
        _rightAssembly = () => scoped[RightAssemblyKey];
        _package = () => scoped[PackageKey];
        _baselinePackage = () => scoped[BaselinePackageKey];
        _suppressionFile = () => scoped[SuppressionFileKey];
        _noWarn = () => scoped[NoWarnKey];
        _strictMode = () => bool.TryParse(scoped[StrictModeKey], out var strict) && strict;
        _respectInternals = () => bool.TryParse(scoped[RespectInternalsKey], out var internals) && internals;
        _enableRuleCannotChangeParameterName = () =>
            bool.TryParse(scoped[EnableRuleCannotChangeParameterNameKey], out var rule) && rule;
        _enableRuleAttributesMustMatch = () =>
            bool.TryParse(scoped[EnableRuleAttributesMustMatchKey], out var rule) && rule;
        context.Logger.LogInformation(
            "ApiCompatAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static List<string> SplitScopedList(string? configured, string key)
    {
        var values = ExternalToolAuditorOptions.SplitCommaSeparatedList(configured);
        var validated = new List<string>(values.Count);
        foreach (var value in values)
        {
            var entry = ValidatedScopedValue(value, key);
            if (entry is not null)
                validated.Add(entry);
        }

        return validated;
    }

    private static bool OptionsCarrySubcommand(ExternalToolAuditorOptions options, string subcommand)
    {
        foreach (var arg in options.ExtraArguments)
        {
            if (string.Equals(arg?.Trim(), subcommand, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsSuppressionWriteFlag(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return false;
        var value = argument.Trim();
        return value.Equals(GenerateSuppressionFileFlag, StringComparison.Ordinal)
            || value.StartsWith(GenerateSuppressionFileFlag + "=", StringComparison.Ordinal)
            || value.Equals(SuppressionOutputFileFlag, StringComparison.Ordinal)
            || value.StartsWith(SuppressionOutputFileFlag + "=", StringComparison.Ordinal);
    }
}
