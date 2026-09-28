using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PsScriptAnalyzerAuditorPlugin;

/// <summary>
/// Scripting auditor wrapping <c>Invoke-ScriptAnalyzer</c> — the
/// PSScriptAnalyzer engine cmdlet — on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the JSON report parser
/// (<see cref="PsScriptAnalyzerJsonOutputParser"/>), the pinned
/// module-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// PSScriptAnalyzer-specific plumbing below.
///
/// <para><b>The declared tool is a shim.</b> <c>Invoke-ScriptAnalyzer</c> is a
/// PowerShell cmdlet inside the PSScriptAnalyzer module, not a standalone
/// binary, so the executable on PATH is an operator-provisioned wrapper
/// whose contract is owned by this auditor and shipped verbatim in the
/// plugin README: forward every argument to the cmdlet, project each
/// diagnostic record to <c>{RuleName, Severity, Message, File, Line}</c>,
/// serialize as a JSON array on stdout, and exit <c>2</c> when any
/// diagnostics were emitted (<c>0</c> on a clean run); a bare
/// <c>--version</c> invocation prints the installed PSScriptAnalyzer module
/// version. Provisioning the module alone is not enough — the shim is the
/// declared tool, and its absence is the same infrastructure failure as a
/// missing module.</para>
///
/// <para><b>Gate behaviour: blocking on Error/ParseError only.</b>
/// PSScriptAnalyzer severities go through the declared map, never raw:
/// <c>Error</c> and <c>ParseError</c> (a file that cannot even be parsed)
/// → <see cref="AuditSeverity.Error"/> (fails the audit),
/// <c>Warning</c> → <see cref="AuditSeverity.Warning"/>,
/// <c>Information</c> → <see cref="AuditSeverity.Info"/>; anything
/// unrecognised → <see cref="AuditSeverity.Warning"/>. The auditor is a
/// merge gate for error-level script defects, not a blocker on every
/// style note; narrow with <c>MinimumSeverity</c>,
/// <c>IncludedRules</c>/<c>ExcludedRules</c> (post-scan), or
/// <c>ExcludePaths</c>.</para>
///
/// <para><b>Exit-code convention — verified, not assumed.</b>
/// <c>Invoke-ScriptAnalyzer</c> is a cmdlet: it returns diagnostic objects
/// and has no process exit convention of its own. Its <c>-EnableExit</c>
/// switch exits with the error-record count — deliberately not used: the
/// count collides with exec-failure codes (126/127 are hardwired to
/// "cannot execute / not found" before the findings-exit check) and wraps
/// modulo 256, so a large error count could exit <c>0</c>. Instead the
/// provisioned shim defines the contract: <c>0</c> = analysis ran clean,
/// <c>2</c> = analysis ran and emitted diagnostics (the JSON array is the
/// verdict), and anything else — a missing module
/// (<c>CommandNotFoundException</c> exits 1), bad parameters, an
/// interrupted run — is infrastructure. A findings exit without a
/// parseable report still fails closed through the parser.</para>
///
/// <para><b>Repository-controlled config surface.</b>
/// Invoke-ScriptAnalyzer does NOT auto-load a settings file from the
/// repository: configuration applies only through an explicit
/// <c>-Settings</c> argument, so the audited tree cannot inject a profile
/// on its own. An operator <c>SettingsPath</c> that resolves inside the
/// audited worktree is still rejected deterministically — a settings file
/// is executable gate configuration (it can select rules, severities, and
/// custom rule paths, and its values take precedence over conflicting
/// parameters), so the audit subject must not supply it. The configured
/// path and the scan cwd are canonicalized in the sandbox with
/// <c>realpath -m</c> before containment, so a relative path, a
/// <c>..</c> segment, or a symlinked component cannot smuggle an in-tree
/// file past the guard. Built-in preset names (no path separator, no
/// <c>.psd1</c> suffix) resolve inside the pinned module and are passed
/// verbatim — they are operator-controlled content.</para>
///
/// <para><b>Version pin.</b> The rule corpus changes between module
/// releases, so findings are only meaningful from the build the auditor
/// was verified against. The shim's <c>--version</c> is probed before
/// every run; a missing shim, an unrecognised version string, or a module
/// version other than <c>ExpectedVersion</c> is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Reserved flags.</b> <c>ExtraArguments</c> is appended to the
/// scan argv by the shared base, so entries that would retarget the scan,
/// corrupt the JSON contract, change the exit convention, mutate the
/// audited tree, or fetch modules mid-scan are rejected deterministically
/// (naming the scoped key to use instead) rather than failing later as an
/// opaque binding failure. PowerShell binds parameters case-insensitively
/// and by unambiguous prefix (<c>-Set</c> binds <c>-Settings</c>), so the
/// rejection matches prefixes case-insensitively on both
/// <c>-Flag</c>/<c>-Flag:value</c>/<c>-Flag=value</c> spellings.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is the whole work
/// tree (<c>-Path . -Recurse</c>); PSScriptAnalyzer only inspects
/// <c>.ps1</c>/<c>.psm1</c>/<c>.psd1</c> files, so other languages are
/// out of scope by construction. Findings under vendored and dependency
/// trees (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) are
/// dropped by default — problems there describe upstream packages, not
/// the change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: PSScriptAnalyzer PowerShell Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "Invoke-ScriptAnalyzer",
    InstallHint = "provision the pinned PSScriptAnalyzer module (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — pwsh -Command \"Install-Module "
        + "PSScriptAnalyzer -RequiredVersion <version> -Force\" — then install the "
        + "Invoke-ScriptAnalyzer wrapper shim (verbatim in this plugin's README) as a PATH "
        + "executable via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; the shim is the declared tool — the bare cmdlet is not "
        + "invocable as a process")]
[CodeyBoxPluginRequiresTool(
    "pwsh",
    InstallHint = "the Invoke-ScriptAnalyzer shim runs under the PowerShell 7 host — provision "
        + "pwsh into the sandbox baseline (Microsoft's packages.microsoft.com apt feed or the "
        + "release tarball) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions")]
public sealed class PsScriptAnalyzerAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.psscriptanalyzer";

    /// <summary>
    /// PSScriptAnalyzer module release the invocation and its report shape
    /// are verified against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.25.0";

    /// <summary>
    /// Scoped-config key for the scan root (a single <c>-Path</c> value —
    /// repository-relative keeps finding locations repository-relative).
    /// Unset → <c>.</c> (the whole work tree, scanned recursively).
    /// </summary>
    public const string TargetPathKey = "TargetPath";

    /// <summary>
    /// Scoped-config key for a PSScriptAnalyzer settings source passed
    /// verbatim to <c>-Settings</c>: a built-in preset name (e.g.
    /// <c>CodeFormattingOTBS</c>) or a comma-separated preset list, or an
    /// absolute path to a <c>.psd1</c> settings file. A path must resolve
    /// OUTSIDE the audited worktree — settings carry rule selection,
    /// severity filtering, and custom rule paths, and take precedence over
    /// conflicting command-line parameters, so an in-tree file would hand
    /// gate control to the audit subject; in-tree paths (including via
    /// <c>..</c> segments and symlinked components) are rejected
    /// deterministically before the scan runs.
    /// </summary>
    public const string SettingsPathKey = "SettingsPath";

    /// <summary>Binary the shim's interpreter — declared so the scan fails closed with a named tool when PowerShell is absent.</summary>
    private const string PwshBinaryName = "pwsh";

    // Flags whose presence in ExtraArguments would retarget the scan
    // (Path, ScriptDefinition), corrupt the JSON report contract
    // (ReportSummary — host text on stdout), change the declared exit
    // convention (EnableExit), mutate the audited tree (Fix), invert the
    // report vocabulary (SuppressedOnly, IncludeSuppressed), fetch modules
    // from the gallery mid-scan (SaveDscDependency), duplicate the scoped
    // config surface (Settings/Profile), or skip analysis via the
    // ShouldProcess gate (WhatIf, Confirm). Matching is case-insensitive
    // prefix matching: PowerShell binds '-Set' to -Settings.
    private static readonly string[] ReservedFlags =
    [
        "-Path",
        "-ScriptDefinition",
        "-Recurse",
        "-Settings",
        "-Profile",
        "-Fix",
        "-SuppressedOnly",
        "-IncludeSuppressed",
        "-ReportSummary",
        "-SaveDscDependency",
        "-EnableExit",
        "-WhatIf",
        "-Confirm",
    ];

    // Separators between a PowerShell parameter name and its attached value:
    // '-Name value', '-Name:value', and '-Name=value' are all valid spellings.
    private static readonly char[] FlagValueSeparators = [':', '='];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // The provisioned shim defines the exit convention (the cmdlet has
        // none of its own): 0 = analysis ran clean, 2 = analysis ran and
        // emitted diagnostics. Everything else — 1 on a terminating error
        // (missing module, bad parameters), 126/127 exec failures, any
        // other value — is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Findings inside vendored/dependency trees describe upstream
        // packages, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _targetPath = static () => null;
    private Func<string?> _settingsPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:psscriptanalyzer";

    /// <inheritdoc />
    protected override string ToolName => "Invoke-ScriptAnalyzer";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PsScriptAnalyzerJsonOutputParser();

    /// <summary>
    /// Declared mapping from PSScriptAnalyzer's severity vocabulary
    /// (<see cref="PsScriptAnalyzerAuditor"/>) to <see cref="AuditSeverity"/>:
    /// <c>Error</c>/<c>ParseError</c> → Error (blocking),
    /// <c>Warning</c> → Warning, <c>Information</c> → Info, unrecognised →
    /// Warning. Raw tool tokens never reach findings, so "Error" means the
    /// same thing as in every other auditor.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        BuildSeverityMapping();

    // Derived from the shared default so vocabulary additions there
    // propagate; PSScriptAnalyzer emits 'Information' (the shared default
    // maps 'info'/'informational', not 'information') and 'ParseError',
    // which is a defect — a file that cannot be parsed at all — so it
    // blocks like Error.
    private static ExternalToolSeverityMapping BuildSeverityMapping()
    {
        var levels = new Dictionary<string, AuditSeverity>(
            ExternalToolSeverityMapping.Default.Levels, StringComparer.OrdinalIgnoreCase);
        levels["information"] = AuditSeverity.Info;
        levels["parseerror"] = AuditSeverity.Error;
        return new ExternalToolSeverityMapping(levels, AuditSeverity.Warning);
    }

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        RejectReservedExtraArguments(options);

        // Invoke-ScriptAnalyzer only inspects .ps1/.psm1/.psd1 files; -Recurse
        // makes the directory target cover the tree (a directory -Path
        // without it scans top-level files only).
        var configured = _targetPath();
        var target = string.IsNullOrWhiteSpace(configured) ? "." : configured;
        return ["-Path", ValidatedArgumentValue(target, TargetPathKey), "-Recurse"];
    }

    /// <summary>
    /// Emits the <c>-Settings</c> pair — the one argv element that needs a
    /// bounded sandbox probe to compute. Invoke-ScriptAnalyzer never
    /// auto-loads repository config, so the audited tree cannot influence
    /// the scan unless the operator points <c>SettingsPath</c> into it —
    /// and a .psd1 settings file can carry ExcludeRules, Severity, and
    /// CustomRulePath (arbitrary module code), which is why an in-tree
    /// settings path is rejected deterministically after canonicalization.
    /// A configured preset name (no path separator, no .psd1 suffix)
    /// resolves inside the pinned module and passes through verbatim. The
    /// value validated is exactly the value argv carries — a mid-run
    /// scoped-config reload cannot split the guard from the flag.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = ValidatedScopedValue(_settingsPath(), SettingsPathKey);
        if (configured is null || !LooksLikeSettingsPath(configured))
            return configured is null ? [] : ["-Settings", configured];

        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured, options, ct).ConfigureAwait(false);
        return ["-Settings", canonical];
    }

    // -Settings accepts preset names, hashtables, or a .psd1 file path.
    // Only a file path can point at repository-controlled content; presets
    // are name-shaped (no separators, no .psd1 suffix — a bare "x.psd1" is
    // always a path, never a preset).
    private static bool LooksLikeSettingsPath(string value)
        => value.Contains('/')
            || value.Contains('\\')
            || value.EndsWith(".psd1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonicalizes an operator-configured <c>SettingsPath</c> inside the
    /// sandbox and fails closed when it resolves inside the audited
    /// worktree. The cmdlet resolves a relative <c>-Settings</c> against its
    /// cwd — the worktree — so the probe canonicalizes the configured path
    /// and the probe cwd with one <c>realpath -m</c> call: relative paths,
    /// <c>..</c> segments, and symlinked components all collapse to the
    /// path the cmdlet would actually open, and containment is judged
    /// against the same canonicalized scan root. From inside the tree the
    /// diff author could set rule selection, severity filters, or custom
    /// rule paths and silently empty — or weaponize — the report.
    /// </summary>
    private async Task<string> CanonicalizeOutsideWorktreeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string configured,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "settings-file check",
            new SandboxExec
            {
                // Two operands, two output lines: the configured path and
                // "." — the exec working directory canonicalized in the
                // sandbox's own path space (providers may translate the
                // host-side workingDirectory). realpath resolves through
                // PATH like the other probe binaries (sh, cat) rather than
                // an assumed FHS location; a missing realpath exits
                // non-zero and fails closed as infrastructure below.
                Argv = ["realpath", "-m", "--", configured, "."],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' {SettingsPathKey} canonicalization could "
                + "not run: the sandbox exec transport was unavailable.");

        var lines = probe.Stdout.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (probe.ExitCode != 0 || lines.Length != 2)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not canonicalize {SettingsPathKey} "
                + $"'{TruncateForMessage(configured)}' (exit {probe.ExitCode}) — an unchecked settings "
                + "path is never trusted, so this is infrastructure, not a verdict on the diff.",
                probe.ExitCode,
                probe.Stderr);

        var canonicalSettings = ValidatedArgumentValue(lines[0], SettingsPathKey);
        var canonicalWorktree = lines[1];
        if (HostPathPolicy.IsWithinDirectory(canonicalSettings, canonicalWorktree))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' {SettingsPathKey} "
                + $"'{TruncateForMessage(configured)}' resolves to '{TruncateForMessage(canonicalSettings)}' "
                + "inside the audited worktree — a repository-controlled settings file selects rules, "
                + "severities, and custom rule paths and can empty the report. Set an absolute path "
                + "outside the repository, a built-in preset name, or unset it for the default rule set.")
            { IsDeterministic = true };

        return canonicalSettings;
    }

    /// <summary>
    /// Confirms the shim's interpreter: <c>Invoke-ScriptAnalyzer</c> is a
    /// PowerShell cmdlet executed through a provisioned wrapper, so the
    /// <c>pwsh</c> host must also be present — a missing one fails closed
    /// naming the binary (otherwise the shim fails exec with an opaque
    /// 127 at scan time).
    /// </summary>
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ThrowIfBinaryMissingAsync(
            sandbox, workingDirectory, PwshBinaryName, options, ct,
            purpose: "the Invoke-ScriptAnalyzer shim executes under the PowerShell 7 host");

    /// <summary>
    /// Resolves the absolute scan directory as the tool sees it, so the
    /// parser can relativize the absolute <c>File</c> values the analyzer
    /// emits for resolved targets. Sandbox providers may translate the
    /// audit's working directory, so it is probed rather than assumed.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "scan-root probe",
            new SandboxExec
            {
                Argv = ["sh", "-c", "pwd", "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' scan-root probe could not run: the sandbox "
                + "exec transport was unavailable.");

        var root = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (result.ExitCode != 0 || string.IsNullOrEmpty(root))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve the scan root (exit "
                + $"{result.ExitCode}) — reported paths could not be trusted relative to the worktree, "
                + "so this is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
        return root;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targetPath = () => scoped[TargetPathKey];
        _settingsPath = () => scoped[SettingsPathKey];
        context.Logger.LogInformation(
            "PsScriptAnalyzerAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = new List<string>();
        foreach (var arg in options.ExtraArguments)
        {
            var token = ExtraArgumentFlagToken(arg);
            if (token is null)
                continue;
            foreach (var reserved in ReservedFlags)
            {
                // PowerShell's binder accepts any unambiguous parameter-name
                // prefix and is case-insensitive, so a token reserves a flag
                // when the reserved name starts with it — "-Set" would bind
                // -Settings just as "-Settings" does.
                if (reserved.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(arg.Trim());
                    break;
                }
            }
        }
        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor 'codeybox:psscriptanalyzer' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would retarget the scan, "
            + "corrupt the JSON report on stdout, change the declared exit convention, mutate the "
            + "audited tree, fetch modules mid-scan, or re-open the scoped-config surface. Use the "
            + $"scoped keys under CodeyBox:Plugins:{PluginId} (TargetPath, SettingsPath) or the "
            + "shared knobs; ExtraArguments is for everything else (e.g. -IncludeRule, -ExcludeRule, "
            + "-Severity, -CustomRulePath).")
        { IsDeterministic = true };
    }

    // The flag token of an extra argument: text up to the first ':' or '='
    // (PowerShell named arguments are '-Name value', '-Name:value', or
    // '-Name=value'), or null when the entry is not flag-shaped (a bare
    // positional value — the cmdlet rejects a second positional -Path, so
    // it cannot retarget the scan).
    private static string? ExtraArgumentFlagToken(string arg)
    {
        var trimmed = arg.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '-')
            return null;
        var stop = trimmed.IndexOfAny(FlagValueSeparators);

        var token = stop < 0 ? trimmed : trimmed[..stop];
        return token.Length > 1 ? token : null;
    }
}
