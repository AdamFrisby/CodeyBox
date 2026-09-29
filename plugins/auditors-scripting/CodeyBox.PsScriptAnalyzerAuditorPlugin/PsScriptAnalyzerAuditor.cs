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
/// plugin README: pin <c>$ErrorActionPreference = 'Stop'</c> so a mid-scan
/// non-terminating error cannot yield a silently partial report, forward
/// every argument to the cmdlet, project each diagnostic record to
/// <c>{RuleName, Severity, Message, File, Line}</c>, serialize as a JSON
/// array on stdout, and exit <c>2</c> when any diagnostics were emitted
/// (<c>0</c> on a clean run); a bare <c>--version</c> invocation prints
/// the installed PSScriptAnalyzer module version. Provisioning the module
/// alone is not enough — the shim is the declared tool, and its absence is
/// the same infrastructure failure as a missing module.</para>
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
/// <para><b>Repository-controlled config surface — settings auto-discovery.</b>
/// Invoke-ScriptAnalyzer DOES auto-load a settings file from the
/// repository: when <c>-Settings</c> is absent, the cmdlet enters
/// <c>SettingsMode.Auto</c> and loads <c>PSScriptAnalyzerSettings.psd1</c>
/// from the resolved <c>-Path</c> directory (or its directory when
/// <c>-Path</c> is a file) — i.e. a file the diff author could commit to
/// inject <c>ExcludeRules</c>/<c>Severity</c> filters or a
/// <c>CustomRulePath</c> module, emptying or weaponizing the report while
/// the audit reports a clean pass. The auditor therefore ALWAYS passes
/// <c>-Settings</c>: the operator's preset name or validated
/// outside-worktree <c>.psd1</c>, or — when <c>SettingsPath</c> is unset —
/// a generated empty settings file (<c>@{}</c>) in the per-run scratch
/// directory, which pins the default rule set and disables auto-discovery.
/// A configured path and the scan cwd are canonicalized in the sandbox
/// with <c>realpath -m</c> before containment, so a relative path, a
/// <c>..</c> segment, or a symlinked component cannot smuggle an in-tree
/// file past the guard. Name-shaped values are accepted only when they
/// exactly match a preset shipped by the pinned module AND no same-named
/// file sits at the worktree root — the cmdlet's preset check enumerates
/// the INSTALLED module's shipped presets at run time, so a compiled-in
/// list cannot prove the name resolves inside the module, and a name it
/// lacks falls through to cwd-relative file resolution where a committed
/// same-named file would be parsed as settings. Every other
/// string resolves through the cmdlet's provider-path resolver (cwd-
/// relative, with wildcard expansion), so wildcards are rejected and all
/// remaining values go through the containment check. Because the resolver
/// globs the argv value it is handed, the metacharacter rejection applies
/// to the canonicalized string too: realpath resolves through symlinked
/// components whose literal names can introduce <c>[</c> <c>]</c> <c>*</c>
/// <c>?</c> the configured path never carried.</para>
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
/// opaque binding failure. PowerShell binds parameters case-insensitively,
/// by unambiguous prefix (<c>-Set</c> binds <c>-Settings</c>), by alias
/// (<c>Path</c>→<c>PSPath</c>, <c>CustomRulePath</c>→
/// <c>CustomizedRulePath</c>, <c>WhatIf</c>→<c>wi</c>,
/// <c>Confirm</c>→<c>cf</c>), and accepts the Unicode dashes
/// U+2013/U+2014/U+2015 as parameter markers — so the check normalizes the
/// leading dash and matches prefixes case-insensitively on both
/// <c>-Flag</c>/<c>-Flag:value</c>/<c>-Flag=value</c> spellings, with the
/// aliases reserved alongside their parameter names.</para>
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
    /// verbatim to <c>-Settings</c>: a built-in preset name (one of
    /// <see cref="BuiltinSettingsPresets"/> — exact match, the pinned
    /// module's own check; there is no comma-list form at this version —
    /// rejected deterministically when a same-named file sits at the
    /// worktree root, since the cmdlet falls back to cwd-relative file
    /// resolution whenever the installed module lacks the preset) or
    /// a path to a <c>.psd1</c> settings file. A path must resolve OUTSIDE
    /// the audited worktree — settings carry rule selection, severity
    /// filtering, and custom rule paths, and take precedence over
    /// conflicting command-line parameters, so an in-tree file would hand
    /// gate control to the audit subject; in-tree paths (including via
    /// <c>..</c> segments and symlinked components) are rejected
    /// deterministically before the scan runs. Unset → a generated empty
    /// settings file that pins the default rule set and keeps the cmdlet
    /// from auto-discovering a repository-committed
    /// <c>PSScriptAnalyzerSettings.psd1</c>.
    /// </summary>
    public const string SettingsPathKey = "SettingsPath";

    /// <summary>
    /// Name of the generated empty settings file written under the per-run
    /// scratch directory and passed to <c>-Settings</c> when
    /// <see cref="SettingsPathKey"/> is unset — see the class docstring's
    /// settings auto-discovery paragraph.
    /// </summary>
    internal const string EmptySettingsFileName = "codeybox-empty-psscriptanalyzer-settings.psd1";

    // Fixed script — no configuration-derived text: the per-run directory
    // arrives as $1, never spliced into the script. Creates the scratch dir
    // (mode 700 — the generated settings file steers the gate, so it must
    // not be writable by other users of the shared temp area) and writes
    // the empty settings file the -Settings argv names.
    private const string SettingsPreparationScript =
        "d=\"$1\""
        + " && mkdir -m 700 -p \"$d\""
        + " && printf '%s\\n' '@{}' > \"$d/" + EmptySettingsFileName + "\"";

    /// <summary>
    /// The built-in settings presets shipped by the pinned PSScriptAnalyzer
    /// module — the <c>*.psd1</c> names under its <c>Settings/</c>
    /// directory. The cmdlet's own preset check is exact ordinal-ignore-case
    /// membership in this list; anything else name-shaped resolves as a
    /// provider path (cwd-relative, with wildcard expansion) rather than as
    /// a preset, so only exact matches may skip canonicalization — and even
    /// those only after
    /// <see cref="ThrowIfPresetShadowedByRepositoryFileAsync"/> clears the
    /// worktree of a same-named file: the installed module decides
    /// preset-vs-path against its OWN shipped list, not this snapshot, and a
    /// name it lacks resolves cwd-relative where a committed file would be
    /// parsed as settings. The list travels with
    /// <see cref="DefaultExpectedVersion"/> — an operator who re-pins
    /// <c>ExpectedVersion</c> to a release shipping a different preset set
    /// must update it here too (a missing entry fails closed: the name
    /// resolves as an in-tree path and is rejected).
    /// </summary>
    private static readonly HashSet<string> BuiltinSettingsPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "CmdletDesign",
        "CodeFormatting",
        "CodeFormattingAllman",
        "CodeFormattingOTBS",
        "CodeFormattingStroustrup",
        "DSC",
        "PSGallery",
        "ScriptFunctions",
        "ScriptingStyle",
        "ScriptSecurity",
    };

    // The cmdlet resolves a non-preset -Settings through
    // GetResolvedProviderPathFromPSPath — a globbing resolver — so these
    // characters in a configured path are rejected outright: a wildcard
    // could expand to an in-tree file the canonicalization check never
    // sees.
    private static readonly char[] WildcardMetacharacters = ['*', '?', '[', ']'];

    // Flags whose presence in ExtraArguments would retarget the scan
    // (Path and its PSPath alias, ScriptDefinition) or narrow it
    // (Recurse — an operator '-Recurse:$false' would shrink a directory
    // -Path to top-level files and silently drop subdirectory findings),
    // corrupt the JSON report contract (ReportSummary — host text on
    // stdout), change the declared exit convention (EnableExit), mutate the
    // audited tree
    // (Fix), invert the report vocabulary (SuppressedOnly, IncludeSuppressed),
    // fetch modules mid-scan (SaveDscDependency), load repository-controlled
    // module code or rule selections (CustomRulePath and its
    // CustomizedRulePath alias, RecurseCustomRulePath, IncludeDefaultRules
    // — the same threat the SettingsPath containment guard exists for;
    // operators needing custom rules point SettingsPath at an
    // outside-worktree .psd1 carrying them), duplicate the scoped config
    // surface (Settings/Profile), degrade the shim's fail-on-error contract
    // (ErrorAction: a 'Continue' override would turn mid-scan failures into
    // a silently partial report), or skip analysis via the ShouldProcess
    // gate (WhatIf/wi, Confirm/cf). Matching is case-insensitive prefix
    // matching: PowerShell binds '-Set' to -Settings and the aliases
    // verbatim.
    private static readonly string[] ReservedFlags =
    [
        "-Path",
        "-PSPath",
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
        "-wi",
        "-Confirm",
        "-cf",
        "-CustomRulePath",
        "-CustomizedRulePath",
        "-RecurseCustomRulePath",
        "-IncludeDefaultRules",
        "-ErrorAction",
        "-ea",
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
    /// bounded sandbox probe to compute — ALWAYS: with no <c>-Settings</c>
    /// the cmdlet's <c>SettingsMode.Auto</c> auto-discovers
    /// <c>PSScriptAnalyzerSettings.psd1</c> in the resolved <c>-Path</c>
    /// directory, handing gate configuration to a file the diff author can
    /// commit. Unset <c>SettingsPath</c> → the generated empty settings
    /// file (<c>@{}</c> → default rule set), prepared by
    /// <see cref="VerifyToolAsync"/> in the per-run scratch directory. A
    /// configured built-in preset name passes verbatim only after a bounded
    /// probe confirms no same-named file sits at the worktree root: the
    /// cmdlet's preset check is dynamic — it enumerates the INSTALLED
    /// module's shipped <c>Settings/*.psd1</c> at run time, and a name that
    /// list does not carry (a module provisioned without its Settings tree,
    /// or an <c>ExpectedVersion</c> re-pin to a release shipping a
    /// different preset set) falls through to cwd-relative FILE
    /// resolution — so <see cref="BuiltinSettingsPresets"/> being a
    /// compiled-in snapshot can never prove the name resolves inside the
    /// module, and a committed same-named file would be parsed as the
    /// settings file. Every other value is a file path the
    /// cmdlet resolves through a globbing, cwd-relative provider-path
    /// resolver: wildcard metacharacters are rejected outright (a wildcard
    /// could expand to an in-tree file the canonicalization check never
    /// sees), the surviving path must canonicalize outside the worktree,
    /// and the canonical result is metacharacter-checked again — realpath
    /// can introduce <c>[</c>/<c>]</c>/<c>*</c>/<c>?</c> through a glob-named
    /// symlinked component the configured spelling lacked. The value
    /// validated is exactly the value argv carries — a mid-run
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
        if (configured is null)
            return ["-Settings",
                RejectGlobExpandableSettingsValue(
                    Path.Combine(PerRunTempDirectoryPath, EmptySettingsFileName),
                    "generated settings path")];
        if (BuiltinSettingsPresets.TryGetValue(configured, out var preset))
        {
            await ThrowIfPresetShadowedByRepositoryFileAsync(
                sandbox, workingDirectory, configured, preset, options, ct).ConfigureAwait(false);
            return ["-Settings", configured];
        }
        var wildcardChecked = RejectGlobExpandableSettingsValue(configured, "configured value");

        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, wildcardChecked, SettingsPathKey, options, ct).ConfigureAwait(false);
        return ["-Settings", RejectGlobExpandableSettingsValue(canonical, "canonicalized path")];
    }

    // A preset name may pass verbatim only when it cannot resolve to a
    // repository file: the cmdlet's own preset check is dynamic — it
    // enumerates the INSTALLED module's Settings/*.psd1 at run time, and
    // when the name is absent there (a module provisioned without its
    // Settings tree, or an ExpectedVersion re-pin to a release shipping a
    // different preset set — the --version pin sees only the module's
    // self-reported version string, not its preset fileset) the cmdlet
    // enters file mode and resolves the name cwd-relative, parsing a
    // same-named committed file as settings — ExcludeRules/Severity
    // filters or a CustomRulePath module in the diff author's hands. Both
    // spellings are probed (the configured value and the canonical preset
    // name differ only in case): the cmdlet prefers a real preset over a
    // same-named file, so rejecting on presence is conservative and loses
    // nothing; a name the module lacks with no shadowing file still fails
    // closed — the cmdlet errors on the missing settings file, surfacing
    // as infrastructure.
    private async Task ThrowIfPresetShadowedByRepositoryFileAsync(
        ISandbox sandbox,
        string workingDirectory,
        string configured,
        string preset,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        string[] candidates = string.Equals(configured, preset, StringComparison.Ordinal)
            ? [configured]
            : [configured, preset];
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, ToolName, candidates, options, ct).ConfigureAwait(false);
        if (present.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' {SettingsPathKey} preset "
            + $"'{TruncateForMessage(configured)}' is shadowed by repository file(s) "
            + $"'{string.Join("', '", present)}' — when the installed module does not ship a preset "
            + "under that name the cmdlet resolves it as a cwd-relative settings file, so a "
            + "same-named committed file would be parsed as gate configuration. Remove or rename "
            + $"the file, or point CodeyBox:Plugins:{PluginId}:{SettingsPathKey} at a .psd1 outside "
            + "the worktree.")
        { IsDeterministic = true };
    }

    // The -Settings value the argv actually carries is the string the
    // cmdlet's globbing provider-path resolver expands — so every spelling
    // of it needs the metacharacter rejection, with 'origin' naming which
    // one failed: the configured value (a wildcard could expand
    // to an in-tree file the canonicalization check never sees), the
    // canonicalized path (realpath resolves THROUGH symlinked components,
    // and a component literally named like 'pol[ic]y' puts '[' ']' into the
    // canonical string the configured value never carried), or the
    // generated settings path (the host temp root itself could carry glob
    // characters). Glob expansion of that argv value could land on an
    // in-tree file the containment check judged only by its literal
    // spelling.
    private string RejectGlobExpandableSettingsValue(string value, string origin)
    {
        if (value.IndexOfAny(WildcardMetacharacters) < 0)
            return value;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' -Settings {origin} "
            + $"'{TruncateForMessage(value)}' carries wildcard metacharacters — the cmdlet resolves "
            + "-Settings through a globbing provider-path resolver, so the value argv carries could "
            + "expand to a repository-controlled file the containment check never judged. The "
            + "-Settings value's resolved form must contain no '*', '?', '[', ']'.")
        { IsDeterministic = true };
    }

    /// <summary>
    /// Prepares the generated <c>-Settings</c> file the scan argv names: a
    /// fresh per-run directory (outside the worktree) holding an empty
    /// settings file that pins the default rule set and keeps the cmdlet's
    /// <c>SettingsMode.Auto</c> from discovering a repository-committed
    /// <c>PSScriptAnalyzerSettings.psd1</c>. A preparation failure fails
    /// closed as infrastructure naming the tool — the scan must not run
    /// with <c>-Settings</c> pointing at a file that does not exist (the
    /// cmdlet would error — the wrong failure shape) nor without the flag.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "settings file preparation",
            new SandboxExec
            {
                Argv = ["sh", "-c", SettingsPreparationScript, "sh", PerRunTempDirectoryPath],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' settings file preparation could not run: "
                + "the sandbox exec transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not prepare its generated settings file "
                + $"(exit {result.ExitCode}) — the -Settings argument must not name a missing file, "
                + "so this is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
    }

    /// <summary>
    /// Resolves the absolute scan directory as the tool sees it, so the
    /// parser can relativize the absolute <c>File</c> values the analyzer
    /// emits for resolved targets. Sandbox providers may translate the
    /// audit's working directory, so it is probed rather than assumed —
    /// through the shared <c>pwd</c> probe.
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
        _targetPath = () => scoped[TargetPathKey];
        _settingsPath = () => scoped[SettingsPathKey];
        context.Logger.LogInformation(
            "PsScriptAnalyzerAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
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
                    offenders.Add(TruncateForMessage(arg));
                    break;
                }
            }
        }
        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders.Distinct(StringComparer.Ordinal))}' — they would retarget the scan, "
            + "corrupt the JSON report on stdout, change the declared exit convention, mutate the "
            + "audited tree, fetch modules mid-scan, load in-tree rule modules, or re-open the "
            + $"scoped-config surface. Use the scoped keys under CodeyBox:Plugins:{PluginId} "
            + "(TargetPath, SettingsPath) or the shared knobs; ExtraArguments is for everything else "
            + "(e.g. -IncludeRule, -ExcludeRule, -Severity).")
        { IsDeterministic = true };
    }

    // The flag token of an extra argument: text up to the first ':' or '='
    // (PowerShell named arguments are '-Name value', '-Name:value', or
    // '-Name=value'), or null when the entry is not flag-shaped (a bare
    // positional value — the cmdlet rejects a second positional -Path, so
    // it cannot retarget the scan). The PowerShell parameter-token 'dash'
    // production accepts U+2013/U+2014/U+2015 as well as '-', so a leading
    // Unicode dash is normalized before matching — otherwise '–Settings'
    // (en dash) would still bind -Settings while dodging the reserved list.
    private static string? ExtraArgumentFlagToken(string arg)
    {
        var trimmed = arg.Trim();
        if (trimmed.Length == 0 || !IsParameterDash(trimmed[0]))
            return null;
        var normalized = '-' + trimmed[1..];
        var stop = normalized.IndexOfAny(FlagValueSeparators);

        var token = stop < 0 ? normalized : normalized[..stop];
        return token.Length > 1 ? token : null;
    }
}
