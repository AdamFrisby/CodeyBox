using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BufAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>buf breaking</c> (Protobuf
/// breaking-change detection) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the <c>--error-format=json</c> report
/// parser (<see cref="BufBreakingJsonParser"/> — one JSON object per line,
/// each carrying the rule <c>type</c>, <c>message</c>, and an optional
/// <c>path</c>/<c>start_line</c> location), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and
/// the baseline resolution below.
///
/// <para><b>Gate behaviour: blocking.</b> <c>buf breaking</c> has no
/// severity vocabulary — every violation it emits is a Protobuf
/// compatibility break (deleted file, removed field, changed wire type),
/// so every finding maps to <see cref="AuditSeverity.Error"/> and fails
/// the audit. There is no advisory mode; narrow scope with
/// <c>IncludedRules</c>/<c>ExcludedRules</c>, <c>ExcludePaths</c>, or
/// <c>MinimumSeverity</c> instead. Note the consequence: raising
/// <c>MinimumSeverity</c> above <c>error</c> drops every finding.</para>
///
/// <para><b>Exit-code convention (verified against buf 1.73.0 — not the
/// common 0/1/2 convention).</b> <c>0</c> = the check completed and no
/// breaking change exists (empty stdout); <c>100</c> = the check completed
/// and breaking changes exist (one JSON violation object per stdout line);
/// <c>1</c> = the check could not complete (missing <c>--against</c>,
/// unresolvable baseline, no <c>.proto</c> files, usage error) with a
/// plain-text <c>Failure: …</c> diagnostic and no report. Only <c>0</c>
/// and <c>100</c> are findings-producing exits. A <c>100</c> exit whose
/// stdout carries no parseable violation line contradicts the output
/// contract and fails closed as infrastructure through the parser, as does
/// any other non-declared exit. <c>126</c>/<c>127</c> are cannot-execute /
/// not-found — infrastructure.</para>
///
/// <para><b>Baseline resolution.</b> A breaking-change check is meaningless
/// without a "previous schema" to compare against. Unless the operator
/// pins a baseline explicitly (the scoped <c>Against</c> key, the scoped
/// <c>AgainstRegistry</c> flag, or an <c>--against</c> /
/// <c>--against-registry</c> flag in <c>ExtraArguments</c>), the auditor
/// resolves the merge-base of <c>HEAD</c> and the work item's
/// <see cref="AuditContext.BaseBranch"/> — the same
/// <c>origin/&lt;base&gt;...HEAD</c> semantics the pipeline's own diff
/// auditors use — and passes it as <c>--against .git#ref=&lt;sha&gt;</c>.
/// Resolution probes <c>origin/&lt;base&gt;</c> first, then the bare branch
/// name (the <see cref="Validation.ValidateBranchName"/>-validated value
/// reaches git only as an argv entry, never through a shell). An
/// empty/invalid base branch, an unresolvable ref, or no common ancestor
/// is a deterministic infrastructure failure pointing at the baseline
/// knobs — never a pass. The resolution needs the <see cref="AuditContext"/>
/// that <c>BuildToolArguments</c> does not receive, so it runs inside the
/// base's <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync"/>
/// seam and returns the <c>--against</c> pair as context arguments.</para>
///
/// <para><b>Version pin.</b> The breaking-rule set and the JSON report
/// shape change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>buf --version</c> before every scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled rule configuration.</b> <c>buf
/// breaking</c> reads its rule category and per-rule <c>except</c> /
/// <c>ignore</c> / <c>ignore_only</c> tuning from the audited
/// repository's own <c>buf.yaml</c> (or the file <c>ConfigPath</c>
/// points at), so a change set can soften the check the audit applies to
/// it. The auditor does not second-guess that file — the workspace
/// configuration is what the project's own CI enforces too — but
/// operators who need the gate to be independent of repo-authored tuning
/// should pin the category and rule set in an operator-owned config file
/// and point <c>ConfigPath</c> at it. Rule ids in findings (e.g.
/// <c>FIELD_SAME_TYPE</c>) additionally stay filterable after the fact
/// through the shared <c>IncludedRules</c>/<c>ExcludedRules</c> knobs,
/// which the audit host applies — outside the subject's reach.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>buf breaking</c> at the
/// repository root with the current directory as the input: buf's own
/// workspace discovery decides scope (every module in <c>buf.yaml</c>, or
/// the <c>.proto</c> files under the root when no config exists). No
/// positional input is passed, so findings describe only the workspace's
/// Protobuf surface — never vendored trees, which the default
/// <c>ExcludePaths</c> drops on top. A repository without <c>.proto</c>
/// files fails closed with buf's own diagnostic (exit 1) surfaced as
/// infrastructure — enabling this auditor on a non-Protobuf project is a
/// misconfiguration, and a loud one, not a silent skip.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Buf Protobuf Breaking Changes",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "buf",
    InstallHint = "provision the pinned buf release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "a pinned buf — by fetching the versioned upstream GitHub release tarball via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "git",
    InstallHint = "git ships in the stock sandbox baseline; declared here because the default "
        + "baseline resolution runs git rev-parse/merge-base inside the audited clone")]
public sealed class BufAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.buf";

    /// <summary>
    /// buf release the invocation, its exit convention, and its JSON report
    /// shape were verified against. Operators running a different pinned
    /// build set <c>ExpectedVersion</c> in the plugin's scoped config to
    /// match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.73.0";

    /// <summary>
    /// Exit code buf uses for "check completed, breaking changes exist".
    /// Disjoint from <c>0</c> (clean) and <c>1</c> (could not run), so it
    /// and 0 are the only findings-producing exits.
    /// </summary>
    internal const int BreakingFindingsExitCode = 100;

    /// <summary>
    /// Scoped-config key for an explicit baseline input
    /// (<c>--against</c>): any buf input — a <c>.git#ref=…</c> fragment, a
    /// directory, a BSR module, an archive, or an image.
    /// </summary>
    public const string AgainstKey = "Against";

    /// <summary>
    /// Scoped-config boolean for <c>--against-registry</c>: compare every
    /// named workspace module against its latest BSR commit instead of a
    /// single <c>--against</c> input.
    /// </summary>
    public const string AgainstRegistryKey = "AgainstRegistry";

    /// <summary>
    /// Scoped-config key for an operator-owned buf configuration file
    /// (<c>--config</c>) when the workspace configuration under audit
    /// must not come from the repository itself.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    private const string AgainstFlag = "--against";
    private const string AgainstRegistryFlag = "--against-registry";
    private const string ConfigFlag = "--config";
    private const string ErrorFormatFlag = "--error-format";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = check completed, no breaking changes; 100 = check completed,
        // breaking changes exist. Both match the JSON report contract —
        // both are verdicts. 1 (could not run), 2, and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, BreakingFindingsExitCode },
        // Findings under vendored/dependency trees describe upstream
        // .proto copies, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config. Findings without a
        // path (e.g. FILE_NO_DELETE) cannot be excluded this way and
        // always surface.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _against = static () => null;
    private Func<bool> _againstRegistry = static () => false;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:buf";

    /// <inheritdoc />
    protected override string ToolName => "buf";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } =
        new BufBreakingJsonParser();

    /// <summary>
    /// Declared mapping from buf's severity vocabulary to
    /// <see cref="AuditSeverity"/>. buf breaking reports no severity per
    /// violation — every emitted line is a compatibility break by
    /// construction — so the map carries only the <c>error</c> label the
    /// human-readable formats print, and the default resolves everything
    /// (including the JSON format's absent level) to
    /// <see cref="AuditSeverity.Error"/>. Raw tool levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <summary>
    /// Baseline selection — the arguments that need the
    /// <see cref="AuditContext"/>. Exactly one source wins, in precedence
    /// order: the scoped <c>Against</c> key, the scoped
    /// <c>AgainstRegistry</c> flag, an operator-supplied
    /// <c>--against</c> / <c>--against-registry</c> flag in
    /// <c>ExtraArguments</c>, or the merge-base of <c>HEAD</c> and
    /// <see cref="AuditContext.BaseBranch"/> resolved via bounded git
    /// probes (<c>origin/&lt;base&gt;</c> first, then the bare branch
    /// name). Two sources at once is a deterministic configuration
    /// failure — the tool would reject the duplicated flag with a generic
    /// usage error — and no resolvable baseline at all is a deterministic
    /// infrastructure failure pointing at the baseline knobs.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var scopedAgainst = ValidatedScopedValue(_against(), AgainstKey);
        var scopedRegistry = _againstRegistry();
        var operatorAgainst = ExtraArgumentsSupplyFlag(options, AgainstFlag);
        var operatorRegistry = ExtraArgumentsSupplyFlag(options, AgainstRegistryFlag);
        var sources = (scopedAgainst is not null ? 1 : 0)
            + (scopedRegistry ? 1 : 0)
            + (operatorAgainst || operatorRegistry ? 1 : 0);
        if (sources > 1)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has more than one baseline configured "
                + "(scoped Against / AgainstRegistry plus an ExtraArguments --against* flag) — "
                + "set exactly one baseline source.")
            { IsDeterministic = true };
        if (scopedAgainst is not null)
            return [AgainstFlag, scopedAgainst];
        if (scopedRegistry)
            return [AgainstRegistryFlag];
        if (operatorAgainst || operatorRegistry)
            return [];

        var mergeBaseSha = await ResolveDefaultBaselineRefAsync(
            sandbox, workingDirectory, context, options, ct).ConfigureAwait(false);
        return [AgainstFlag, $".git#ref={mergeBaseSha}"];
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string> { "breaking" };

        // The parser only reads --error-format=json. An operator-owned
        // format flag takes precedence when supplied; anything but JSON
        // then fails closed through the parser as infrastructure.
        if (!ExtraArgumentsSupplyFlag(options, ErrorFormatFlag))
        {
            args.Add(ErrorFormatFlag);
            args.Add("json");
        }

        // The scan must use one configuration: an ExtraArguments --config
        // is appended verbatim by the base, so only the scoped key's value
        // is emitted here.
        if (EffectiveConfigPath(options, out var fromScopedKey) is { } configPath
            && fromScopedKey)
        {
            args.Add(ConfigFlag);
            args.Add(configPath);
        }

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _against = () => scoped[AgainstKey];
        _againstRegistry = () =>
            bool.TryParse(scoped[AgainstRegistryKey], out var registry) && registry;
        _configPath = () => scoped[ConfigPathKey] ?? scoped["Config"];
        context.Logger.LogInformation(
            "BufAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private async Task<string> ResolveDefaultBaselineRefAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var baseBranch = context.BaseBranch?.Trim();
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has no baseline to compare against: "
                + "no baseline is configured and the work item carries no usable base branch for "
                + "merge-base resolution. " + BaselineConfigHint)
            { IsDeterministic = true };

        try
        {
            Validation.ValidateBranchName(baseBranch, nameof(context.BaseBranch));
        }
        catch (ArgumentException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' cannot resolve a baseline: "
                + $"{ExternalToolJsonHelpers.SingleLine(ex.Message)}. " + BaselineConfigHint, ex)
            { IsDeterministic = true };
        }

        var baseBranchDisplay = ExternalToolJsonHelpers.SingleLine(baseBranch);

        // The work item's base branch is the previous public schema state.
        // origin/<base> is the sandbox clone's canonical ref (the pipeline's
        // own diff auditors use origin/<base>...HEAD); the bare name covers
        // layouts that only carry a local branch.
        string? baseSha = null;
        foreach (var candidate in new[] { $"origin/{baseBranch}", baseBranch })
        {
            var probe = await GitProbeAsync(
                sandbox,
                workingDirectory,
                options,
                ["rev-parse", "--verify", $"{candidate}^{{commit}}"],
                ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                baseSha = ReadCommitSha(probe.Stdout);
                if (baseSha is not null)
                    break;
            }
        }

        if (baseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve base branch "
                + $"'{baseBranchDisplay}' (tried 'origin/{baseBranchDisplay}' and "
                + $"'{baseBranchDisplay}') in the audited repository — git must be available "
                + "and the base ref present in the sandbox clone. " + BaselineConfigHint)
            { IsDeterministic = true };

        // Merge-base semantics match the pipeline's three-dot work diff:
        // the schema state the change actually diverged from, so schema
        // added to the base after the branch point is not misread as
        // removed.
        var mergeBase = await GitProbeAsync(
            sandbox,
            workingDirectory,
            options,
            ["merge-base", "HEAD", baseSha],
            ct).ConfigureAwait(false);
        var mergeBaseSha = mergeBase.ExitCode == 0 ? ReadCommitSha(mergeBase.Stdout) : null;
        if (mergeBaseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found no merge base between HEAD and "
                + $"base branch '{baseBranchDisplay}' — the audited history must share an "
                + "ancestor with the base ref. " + BaselineConfigHint)
            { IsDeterministic = true };

        return mergeBaseSha;
    }

    private async Task<SandboxExecResult> GitProbeAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var argv = new List<string>(args.Count + 1) { "git" };
        argv.AddRange(args);
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "baseline resolution",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' baseline resolution could not run: "
                + "the sandbox exec transport was unavailable.");
        return result;
    }

    /// <summary>
    /// The config file the scan will actually consult: the scoped
    /// <c>ConfigPath</c> value, or an operator-supplied <c>--config</c> in
    /// <c>ExtraArguments</c>. Both channels at once are a deterministic
    /// configuration failure (a second flag would silently override at the
    /// flag layer); an ExtraArguments flag with no usable value fails the
    /// same way.
    /// </summary>
    /// <param name="fromScopedKey">
    /// True when the returned value came from the scoped key — callers
    /// emitting the flag themselves must not re-emit an
    /// ExtraArguments-supplied one (the base appends those verbatim).
    /// </param>
    private string? EffectiveConfigPath(ExternalToolAuditorOptions options, out bool fromScopedKey)
    {
        var scoped = ValidatedScopedValue(_configPath(), ConfigPathKey);
        var extraSupplied = TryGetExtraArgumentsFlagValue(options, ConfigFlag, out var extra);
        fromScopedKey = scoped is not null;
        if (scoped is not null && extraSupplied)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has --config configured in both "
                + $"CodeyBox:Plugins:{PluginId}:{ConfigPathKey} and ExtraArguments — set it "
                + "in exactly one place.")
            { IsDeterministic = true };
        if (scoped is not null || !extraSupplied)
            return scoped;
        if (extra is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' ExtraArguments supplies '{ConfigFlag}' "
                + "with no following value — pass it as '--config <path>' or "
                + "'--config=<path>'.")
            { IsDeterministic = true };
        return ValidatedArgumentValue(extra, $"ExtraArguments '{ConfigFlag}'");
    }

    /// <summary>
    /// Extracts the value an operator's <c>ExtraArguments</c> supplies for a
    /// long-form flag — the entry following a bare <c>--flag</c>, or the
    /// text after <c>--flag=</c>, the same spellings
    /// <see cref="ExtraArgumentsSupplyFlag"/> recognizes. The last
    /// occurrence wins; a bare trailing flag yields a null value (the tool
    /// would reject it — callers validate).
    /// </summary>
    private static bool TryGetExtraArgumentsFlagValue(
        ExternalToolAuditorOptions options,
        string flag,
        out string? value)
    {
        value = null;
        var supplied = false;
        var attachedPrefix = flag + "=";
        var extraArguments = options.ExtraArguments;
        for (var i = 0; i < extraArguments.Count; i++)
        {
            var arg = extraArguments[i];
            if (string.Equals(arg, flag, StringComparison.Ordinal))
            {
                supplied = true;
                value = i + 1 < extraArguments.Count ? extraArguments[i + 1] : null;
            }
            else if (arg.StartsWith(attachedPrefix, StringComparison.Ordinal))
            {
                supplied = true;
                value = arg[attachedPrefix.Length..];
            }
        }
        return supplied;
    }

    private static string BaselineConfigHint
        => $"Set CodeyBox:Plugins:{PluginId}:{AgainstKey} to a buf input (e.g. '.git#branch=main'), "
            + $"set {AgainstRegistryKey} to true for BSR comparison, or pass --against* via "
            + "ExtraArguments, to pin the baseline explicitly.";

    private static string? ValidatedScopedValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ValidatedArgumentValue(value, key);
    }

    /// <summary>
    /// Validates a configured value that travels to the tool as an argv
    /// entry: bounded length, no leading dash (it would be read as another
    /// flag), no control characters. <paramref name="source"/> names the
    /// knob that supplied the value for the failure message. Values are
    /// never concatenated into a shell string — this only guards the argv
    /// contract.
    /// </summary>
    private static string ValidatedArgumentValue(string value, string source)
    {
        var trimmed = value.Trim();
        const int maxChars = 1024;
        if (trimmed.Length == 0 || trimmed.Length > maxChars
            || trimmed[0] == '-'
            || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' is not a usable argument value "
                + "(empty, overlong, leading '-', or contains control characters).")
            { IsDeterministic = true };
        return trimmed;
    }

    private static string? ReadCommitSha(string stdout)
    {
        var token = stdout.Split(
            [' ', '\n', '\r', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
