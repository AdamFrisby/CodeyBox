using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ApiCompatAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>apicompat</c>
/// (Microsoft.DotNet.ApiCompat.Tool — .NET public-API breaking-change
/// detection) on the shared <see cref="ExternalToolAuditorBase"/>: the base
/// supplies sandboxed invocation with a bounded timeout, per-stream output
/// caps, exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the apicompat report parser
/// (<see cref="ApiCompatReportParser"/> — the tool has no SARIF/JSON mode;
/// findings are <c>CP####</c>/<c>PKV###</c> diagnostic lines whose stream
/// carries the severity), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the contract
/// (left) / implementation (right) operand wiring, and the suppression-file
/// posture below.
///
/// <para><b>Gate behaviour: blocking on breaking changes.</b> Every
/// unsuppressed apicompat difference is logged on its error channel and maps
/// to <see cref="AuditSeverity.Error"/>, so a breaking change fails the
/// audit; warning- and info-channel diagnostics (e.g. unresolved reference
/// search directories) are advisory. Suppressed differences — via a
/// <c>--suppression-file</c> or <c>--noWarn</c> — are invisible to this
/// auditor by the tool's design (they are never logged); whether
/// repository-authored suppressions are honored is governed by
/// <c>TrustRepositorySuppression</c> below.</para>
///
/// <para><b>Exit-code convention (verified against
/// Microsoft.DotNet.ApiCompat.Tool 10.0.401 — the common 0/1/2 convention
/// does NOT hold here).</b> <c>0</c> = ran, no unsuppressed error
/// differences; <c>1</c> = ran with unsuppressed error differences —
/// <em>but 1 is also the exit for every failure</em>: System.CommandLine
/// usage errors, a missing assembly operand, an unparseable suppression
/// file all exit 1. So exit 1 is a verdict only when stderr carries at
/// least one <c>CP####</c>/<c>PKV###</c> diagnostic line; a diagnostic-free
/// exit 1 contradicts the contract and fails closed as infrastructure
/// through the parser. Note the history: the tool exited 0 even with
/// findings before the .NET 10 line (dotnet/sdk#50989) — hence the
/// <see cref="ToolVersionPin"/> to a release with the fixed convention.
/// <c>126</c>/<c>127</c> (cannot-execute/not-found) and any other exit are
/// infrastructure.</para>
///
/// <para><b>Version pin.</b> The rule set, message templates, and the
/// exit-code contract change between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>apicompat --version</c> before every scan; a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Operands — the thing an operator must configure.</b> apicompat
/// compares two artifact sets: the <em>contract</em> (left — the baseline
/// API, e.g. the previous release's build output or a published package)
/// and the <em>implementation</em> (right — the current build output). The
/// auditor does not build anything: both sides must already exist as
/// <c>.dll</c>/<c>.nupkg</c> files, directories of assemblies, or
/// <c>*</c>-globs when the audit runs. Assembly mode uses
/// <c>-l/--left</c> + <c>-r/--right</c> from the <c>Left</c>/<c>Right</c>
/// scoped keys or from ExtraArguments; package mode uses
/// <c>apicompat package &lt;nupkg&gt; [--baseline-package &lt;nupkg&gt;]
/// </c> from the <c>Package</c>/<c>BaselinePackage</c> keys (or a bare
/// <c>package</c> token in ExtraArguments). Mixing channels or modes, and
/// naming one side without the other, are deterministic configuration
/// failures. Literal repository-relative operands are probed for presence
/// before the scan — a missing operand is a loud deterministic
/// infrastructure failure, not a crash mid-scan.</para>
///
/// <para><b>Repository-controlled suppression.</b> apicompat reads no
/// config file automatically — but a suppression file honored via
/// <c>--suppression-file</c> can erase every finding, and when that file
/// lives inside the audited repository the change under audit can edit it.
/// An auditor its subject can silence is not a gate, so a
/// repository-relative suppression path (scoped <c>SuppressionFile</c> or
/// an ExtraArguments <c>--suppression-file</c> value) is a deterministic
/// failure unless <c>TrustRepositorySuppression</c> is set; absolute paths
/// (operator-provisioned, outside the worktree) are always allowed. For
/// the same reason <c>--generate-suppression-file</c> is refused outright:
/// it routes differences into a file instead of stderr, turning the audit
/// into a silent pass.</para>
///
/// <para><b>Scope and defaults.</b> There is no default scope: which
/// assemblies constitute the public API is an operator decision, so the
/// operands are required configuration and their absence is a deterministic
/// failure rather than a guess. No <c>ExcludePaths</c> default either —
/// findings locate to the compared assemblies the operator named, never to
/// vendored or generated trees the tool was not pointed at. The comparison
/// is pure metadata analysis (Roslyn loads the assemblies; no restore, no
/// build), so the auditor runs in the most restrictive sandbox with no
/// network.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ApiCompat .NET API Compatibility",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "apicompat",
    InstallHint = "provision the pinned Microsoft.DotNet.ApiCompat.Tool release (see ExpectedVersion, "
        + "default " + DefaultExpectedVersion + ") into the sandbox baseline so apicompat is on PATH — "
        + "dotnet tool install --global Microsoft.DotNet.ApiCompat.Tool --version "
        + DefaultExpectedVersion + " via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions (the shim needs a .NET runtime, so a dotnet SDK install is a "
        + "prerequisite); no distro apt package carries it")]
public sealed class ApiCompatAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.apicompat";

    /// <summary>
    /// Microsoft.DotNet.ApiCompat.Tool release the invocation, its exit
    /// convention, and its report shape were verified against. Operators
    /// running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "10.0.401";

    /// <summary>
    /// Exit code apicompat returns both for "unsuppressed error
    /// differences" (<c>HasLoggedErrorSuppressions</c>) and for every
    /// failure — usage errors and crashes share it, so the parser
    /// cross-checks stderr for diagnostics before calling it a verdict.
    /// </summary>
    internal const int FindingsExitCode = 1;

    /// <summary>Scoped-config key for the contract (baseline) side — assemblies, directories, or globs (<c>--left</c>).</summary>
    public const string LeftKey = "Left";

    /// <summary>Scoped-config key for the implementation (current) side — assemblies, directories, or globs (<c>--right</c>).</summary>
    public const string RightKey = "Right";

    /// <summary>Scoped-config key for package mode: the <c>.nupkg</c> to validate (<c>apicompat package</c>).</summary>
    public const string PackageKey = "Package";

    /// <summary>Scoped-config key for the baseline package in package mode (<c>--baseline-package</c>).</summary>
    public const string BaselinePackageKey = "BaselinePackage";

    /// <summary>
    /// Scoped-config key for a suppression file (<c>--suppression-file</c>).
    /// Repository-relative paths require <see cref="TrustRepositorySuppressionKey"/>.
    /// </summary>
    public const string SuppressionFileKey = "SuppressionFile";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression files.
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private const string GenerateSuppressionFileFlag = "--generate-suppression-file";
    private const string SuppressionFileFlag = "--suppression-file";
    private const string BaselinePackageFlag = "--baseline-package";
    private const string PackageSubcommand = "package";

    private static readonly string[] LeftFlags = ["--left", "-l", "--left-assembly"];
    private static readonly string[] RightFlags = ["--right", "-r", "--right-assembly"];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean; 1 = findings OR failure — the parser disambiguates
        // by requiring CP####/PKV### diagnostics on stderr. Every other exit
        // is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, FindingsExitCode },
        // The comparison loads two assembly sets into Roslyn symbols —
        // seconds for a pair of assemblies, minutes for a multi-TFM package.
        // Bounded; tune via TimeoutSeconds.
        Timeout = TimeSpan.FromMinutes(10),
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _left = static () => null;
    private Func<string?> _right = static () => null;
    private Func<string?> _package = static () => null;
    private Func<string?> _baselinePackage = static () => null;
    private Func<string?> _suppressionFile = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:apicompat";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "apicompat";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser =>
        new ApiCompatReportParser(RightOperands);

    /// <summary>
    /// apicompat's severity vocabulary is the output channel each diagnostic
    /// is logged on: differences on the error channel (<c>error</c>) map to
    /// <see cref="AuditSeverity.Error"/> and block; warning-channel
    /// diagnostics (<c>warning</c>) map to <see cref="AuditSeverity.Warning"/>;
    /// <c>info</c>-prefixed differences map to <see
    /// cref="AuditSeverity.Info"/>. Unknown tokens default to Warning; raw
    /// tool channels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            [ApiCompatReportParser.ErrorLevel] = AuditSeverity.Error,
            [ApiCompatReportParser.WarningLevel] = AuditSeverity.Warning,
            [ApiCompatReportParser.InfoLevel] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _left = () => scoped[LeftKey];
        _right = () => scoped[RightKey];
        _package = () => scoped[PackageKey];
        _baselinePackage = () => scoped[BaselinePackageKey];
        _suppressionFile = () => scoped[SuppressionFileKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "ApiCompatAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the invocation shape from the scoped keys and ExtraArguments.
    /// Package mode (scoped <c>Package</c>, a bare <c>package</c> token, or
    /// any <c>--baseline-package</c> in ExtraArguments) emits
    /// <c>package &lt;nupkg&gt; [--baseline-package &lt;nupkg&gt;]</c> and is
    /// exclusive with the assembly operands. Assembly mode emits
    /// <c>--left &lt;left&gt; --right &lt;right&gt;</c>; both sides must come
    /// from one channel — the scoped keys, or both flag families in
    /// ExtraArguments — and a missing side is a deterministic configuration
    /// failure, since silently omitting one would let the tool crash or,
    /// worse, compare nothing.
    /// </summary>
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var scopedLeft = ValidatedOperand(_left(), LeftKey);
        var scopedRight = ValidatedOperand(_right(), RightKey);
        var scopedPackage = ValidatedOperand(_package(), PackageKey);
        var scopedBaselinePackage = ValidatedOperand(_baselinePackage(), BaselinePackageKey);
        var scopedSuppressionFile = ValidatedOperand(_suppressionFile(), SuppressionFileKey);

        var extraLeft = ExtraArgumentsSupplyFlag(options, LeftFlags);
        var extraRight = ExtraArgumentsSupplyFlag(options, RightFlags);
        var extraPackage = options.ExtraArguments.Contains(PackageSubcommand, StringComparer.Ordinal);
        var extraBaselinePackage = ExtraArgumentsSupplyFlag(options, BaselinePackageFlag);
        var extraSuppressionFile = ExtraArgumentsSupplyFlag(options, SuppressionFileFlag);

        if (ExtraArgumentsSupplyFlag(options, GenerateSuppressionFileFlag))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' refuses ExtraArguments "
                + $"'{GenerateSuppressionFileFlag}': the flag routes differences into a suppression "
                + "file instead of the report, so the audit would pass while hiding the findings it "
                + "exists to surface.")
            { IsDeterministic = true };

        var packageMode = scopedPackage is not null
            || extraPackage
            || scopedBaselinePackage is not null
            || extraBaselinePackage;

        var args = new List<string>();
        if (packageMode)
        {
            if (scopedLeft is not null || scopedRight is not null || extraLeft || extraRight)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' was configured for package mode but also "
                    + "received assembly operands — set CodeyBox:Plugins:" + PluginId + ":"
                    + $"{PackageKey}/{BaselinePackageKey} (or 'package'/'--baseline-package' in "
                    + $"ExtraArguments) OR {LeftKey}/{RightKey} (--left/--right), not both.")
                { IsDeterministic = true };
            if (scopedPackage is not null && extraPackage)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has the package operand configured twice — "
                    + $"CodeyBox:Plugins:{PluginId}:{PackageKey} and a 'package' token in "
                    + "ExtraArguments. Set it in exactly one place.")
                { IsDeterministic = true };
            if (scopedBaselinePackage is not null && extraBaselinePackage)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has the baseline package configured twice — "
                    + $"CodeyBox:Plugins:{PluginId}:{BaselinePackageKey} and a --baseline-package "
                    + "flag in ExtraArguments. Set it in exactly one place.")
                { IsDeterministic = true };
            if (extraBaselinePackage && scopedPackage is null && !extraPackage)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' received --baseline-package without a package "
                    + "to validate — pass 'package <nupkg>' via ExtraArguments or set "
                    + $"CodeyBox:Plugins:{PluginId}:{PackageKey}.")
                { IsDeterministic = true };
            if (scopedPackage is null && scopedBaselinePackage is not null && !extraPackage)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has {BaselinePackageKey} set but no package "
                    + $"to validate — set CodeyBox:Plugins:{PluginId}:{PackageKey} or pass 'package "
                    + "<nupkg>' via ExtraArguments.")
                { IsDeterministic = true };
            if (scopedBaselinePackage is not null && extraPackage)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' cannot combine an ExtraArguments 'package' "
                    + "operand with a scoped baseline package — the baseline flag would precede the "
                    + "subcommand and fail parsing. Set " + BaselinePackageKey + " via the scoped key "
                    + "together with " + PackageKey + ", or pass --baseline-package in ExtraArguments.")
                { IsDeterministic = true };

            if (scopedPackage is not null)
                args.AddRange([PackageSubcommand, scopedPackage]);
            if (scopedBaselinePackage is not null)
                args.AddRange([BaselinePackageFlag, scopedBaselinePackage]);
        }
        else
        {
            var anyOperand = scopedLeft is not null
                || scopedRight is not null
                || extraLeft
                || extraRight;
            if (!anyOperand)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has nothing to compare — apicompat needs a "
                    + "contract (baseline) side and an implementation side. Set "
                    + $"CodeyBox:Plugins:{PluginId}:{LeftKey} and {RightKey} to repository-relative "
                    + "assembly paths, directories, or globs (or pass --left/--right via "
                    + "ExtraArguments); for package validation set " + PackageKey + " instead.")
                { IsDeterministic = true };
            if ((scopedLeft is null) != (scopedRight is null)
                || ((scopedLeft is not null) && (extraLeft || extraRight))
                || (scopedLeft is null && extraLeft != extraRight))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' needs both sides of the comparison from one "
                    + $"channel — set {LeftKey} and {RightKey} together, or pass both "
                    + "--left/--right via ExtraArguments; mixing channels or naming one side is a "
                    + "configuration error.")
                { IsDeterministic = true };
            if (scopedLeft is not null)
                args.AddRange(["--left", scopedLeft, "--right", scopedRight!]);
        }

        EmitSuppressionFile(args, scopedSuppressionFile, extraSuppressionFile, options);
        return args;
    }

    /// <summary>
    /// Emits the suppression-file flag for the configured channel and
    /// enforces the repository-suppression posture on whichever channel
    /// supplied it: a repository-relative suppression path is writable by
    /// the audited change and therefore requires
    /// <c>TrustRepositorySuppression</c>; absolute paths are
    /// operator-provisioned and always allowed.
    /// </summary>
    private void EmitSuppressionFile(
        List<string> args,
        string? scopedSuppressionFile,
        bool extraSuppressionFile,
        ExternalToolAuditorOptions options)
    {
        if (scopedSuppressionFile is not null && extraSuppressionFile)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has the suppression file configured twice — "
                + $"CodeyBox:Plugins:{PluginId}:{SuppressionFileKey} and a --suppression-file flag "
                + "in ExtraArguments. Set it in exactly one place.")
            { IsDeterministic = true };

        if (scopedSuppressionFile is not null)
            args.AddRange([SuppressionFileFlag, scopedSuppressionFile]);

        if (_trustRepositorySuppression())
            return;

        IReadOnlyList<string> configured = scopedSuppressionFile is not null
            ? [scopedSuppressionFile]
            : CollectExtraArgumentsValues(options, SuppressionFileFlag);
        foreach (var value in configured)
        {
            if (!IsRepositoryRelativePath(value))
                continue;
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was given suppression file '{TruncateForMessage(value)}' "
                + "inside the audited repository — the change under audit can edit it and suppress its "
                + "own breaking changes. Provision the file outside the worktree and pass an absolute "
                + $"path, or set CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-controlled suppression.")
            { IsDeterministic = true };
        }
    }

    /// <summary>
    /// Pre-scan precondition: literal repository-relative operands — the
    /// contract/implementation sides, the package operands, any
    /// repository-relative suppression file — must exist before the scan so
    /// a typo'd path is a loud deterministic configuration failure instead
    /// of a mid-scan crash buried in an ambiguous exit 1. Glob members and
    /// absolute paths cannot be probed through the worktree-relative
    /// presence check; the tool fails closed on those itself.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probeTargets = ProbeableOperands(options).ToList();
        if (probeTargets.Count == 0)
            return;

        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            probeTargets,
            options,
            ct).ConfigureAwait(false);
        var missing = probeTargets.FirstOrDefault(p => !present.Contains(p, StringComparer.Ordinal));
        if (missing is not null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' operand '{missing}' does not exist in the "
                + $"audited repository — fix the CodeyBox:Plugins:{PluginId} operand configuration "
                + "so the comparison has real inputs.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// The right-side (implementation) operand members the scan was invoked
    /// with — scoped <c>Right</c> or an ExtraArguments right flag — split on
    /// the commas apicompat itself accepts, normalized to forward slashes.
    /// The parser matches message-embedded paths against them. Package mode
    /// contributes the package operand so the audited artifact can still be
    /// attributed.
    /// </summary>
    private IReadOnlyList<string> RightOperands()
    {
        var scoped = ValidatedOperand(_right(), RightKey) ?? ValidatedOperand(_package(), PackageKey);
        if (scoped is not null)
            return SplitOperandMembers(scoped);
        var options = _optionsAccessor();
        var members = new List<string>();
        foreach (var flag in RightFlags)
        {
            // Assembly operands honor apicompat's own comma-splitting.
            foreach (var value in CollectExtraArgumentsValues(options, flag))
                members.AddRange(SplitOperandMembers(value));
        }
        return members;
    }

    /// <summary>
    /// The literal repository-relative members of every configured operand,
    /// for the presence probe: both sides of the comparison (or the package
    /// operands) and repository-relative suppression files. Glob members and
    /// absolute paths are excluded — neither can be existence-checked
    /// through the worktree-relative probe.
    /// </summary>
    private IEnumerable<string> ProbeableOperands(ExternalToolAuditorOptions options)
    {
        var operands = new List<string?>();
        if (ValidatedOperand(_package(), PackageKey) is { } package)
        {
            operands.Add(package);
            operands.Add(ValidatedOperand(_baselinePackage(), BaselinePackageKey));
        }
        else
        {
            operands.Add(ValidatedOperand(_left(), LeftKey)
                ?? LastExtraArgumentsValue(options, LeftFlags));
            operands.Add(ValidatedOperand(_right(), RightKey)
                ?? LastExtraArgumentsValue(options, RightFlags));
        }

        foreach (var operand in operands)
        {
            foreach (var member in SplitOperandMembers(operand))
            {
                if (IsProbeableMember(member))
                    yield return member;
            }
        }

        // Suppression files are one path per flag — apicompat's custom
        // comma-splitting applies only to the assembly operands.
        var suppressionFile = ValidatedOperand(_suppressionFile(), SuppressionFileKey);
        var suppressionPaths = suppressionFile is not null
            ? [suppressionFile]
            : CollectExtraArgumentsValues(options, SuppressionFileFlag);
        foreach (var path in suppressionPaths)
        {
            if (IsProbeableMember(path))
                yield return path;
        }
    }

    /// <summary>
    /// True when an operand member can be existence-checked through the
    /// worktree-relative presence probe: repository-relative, not a glob,
    /// and free of characters or segments that would escape the worktree or
    /// break the probe's one-path-per-line protocol (<c>..</c>, newlines).
    /// Anything else is left for the tool itself to fail closed on.
    /// </summary>
    private static bool IsProbeableMember(string member)
        => IsRepositoryRelativePath(member)
            && member.IndexOfAny(['*', '?']) < 0
            && member.IndexOfAny('\n', '\r') < 0
            && !member.Split('/').Contains("..", StringComparer.Ordinal);

    /// <summary>
    /// Collects every value an option takes in <c>ExtraArguments</c>: the
    /// tokens following each bare <c>--flag</c> until the next flag-looking
    /// token (the option is multi-argument), and the suffix of each
    /// <c>--flag=value</c> spelling. Returned values are validated to the
    /// argv contract.
    /// </summary>
    private static IReadOnlyList<string> CollectExtraArgumentsValues(
        ExternalToolAuditorOptions options,
        string flag)
    {
        var values = new List<string>();
        var attachedPrefix = flag + "=";
        // The same spellings ExtraArgumentsSupplyFlag recognizes, including
        // the joined "-fvalue"/"-f=value" short-option form.
        var shortJoined = flag.Length == 2 && flag[0] == '-' && flag[1] != '-';
        var extraArguments = options.ExtraArguments;
        for (var i = 0; i < extraArguments.Count; i++)
        {
            var arg = extraArguments[i];
            if (string.Equals(arg, flag, StringComparison.Ordinal))
            {
                while (i + 1 < extraArguments.Count
                    && !extraArguments[i + 1].StartsWith('-'))
                    values.Add(extraArguments[++i]);
            }
            else if (arg.StartsWith(attachedPrefix, StringComparison.Ordinal))
            {
                values.Add(arg[attachedPrefix.Length..]);
            }
            else if (shortJoined
                && arg.Length > flag.Length
                && arg.StartsWith(flag, StringComparison.Ordinal)
                && arg[flag.Length] != '-')
            {
                values.Add(arg[flag.Length..].TrimStart('='));
            }
        }
        return values;
    }

    private static string? LastExtraArgumentsValue(ExternalToolAuditorOptions options, string[] flags)
    {
        string? last = null;
        foreach (var flag in flags)
        {
            var values = CollectExtraArgumentsValues(options, flag);
            if (values.Count > 0)
                last = values[^1];
        }
        return last;
    }

    /// <summary>
    /// Splits one operand value into its members on the commas apicompat's
    /// own option parser honors.
    /// </summary>
    private static List<string> SplitOperandMembers(string? operand)
    {
        if (string.IsNullOrWhiteSpace(operand))
            return [];
        return operand!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// True when a configured path resolves inside the audited worktree —
    /// i.e. is not absolute on either filesystem convention (<c>/…</c> or a
    /// drive-letter path) and is not a <c>~</c> home expansion. Relative
    /// paths are the ones the audited repository controls.
    /// </summary>
    private static bool IsRepositoryRelativePath(string value)
        => !value.StartsWith('/')
            && !value.StartsWith('~')
            && !(value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');

    /// <summary>
    /// Validates a configured value that travels to the tool as an argv
    /// entry: bounded length, no leading dash (it would be read as a flag),
    /// no control characters. Values are never concatenated into a shell
    /// string — this only guards the argv contract. Returns null for blank.
    /// </summary>
    private static string? ValidatedOperand(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        const int maxChars = 1024;
        if (trimmed.Length > maxChars || trimmed[0] == '-' || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{key}' is not a usable argument value "
                + "(overlong, leading '-', or contains control characters).")
            { IsDeterministic = true };
        return trimmed;
    }
}
