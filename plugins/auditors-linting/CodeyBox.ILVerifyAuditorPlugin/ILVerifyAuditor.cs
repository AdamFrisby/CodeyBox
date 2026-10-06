using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ILVerifyAuditorPlugin;

/// <summary>
/// Compiled-assembly auditor wrapping <c>ilverify</c> (IL verification per
/// ECMA-335, from the <c>dotnet-ilverify</c> NuGet package) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the invocation shape (explicit
/// candidate-produced assembly positionals plus one <c>-r</c> entry per
/// configured reference assembly, parsed by <see
/// cref="ILVerifyOutputParser"/>), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the required-closure
/// probing below, and the default exclusion posture.
///
/// <para><b>Gate behaviour: blocking on any verification failure.</b>
/// ILVerify has no severity vocabulary — every <c>[IL]: Error</c> line is a
/// proven IL defect (bad stack, type mismatch, invalid token) — so each maps
/// to <see cref="AuditSeverity.Error"/> and fails the audit. A clean run
/// (exit <c>0</c> with the tool's <c>Verified.</c> marker) passes with zero
/// findings. <c>MinimumSeverity</c> only drops findings, it never raises
/// them.</para>
///
/// <para><b>Exit-code convention (verified against dotnet-ilverify 10.0.12 —
/// not assumed from the common table).</b> <c>0</c> = every input verified
/// (one <c>All Classes and Methods in &lt;assembly&gt; Verified.</c> line
/// per input on stdout). <c>2</c> = verification failures (one <c>[IL]:
/// Error …</c> line per failure plus a summary, on stdout). Both <c>0</c>
/// and <c>2</c> are findings-producing verdicts. <c>1</c> = the verifier
/// could not start (e.g. <c>Error: Assembly or module not found:
/// mscorlib</c> — an incomplete reference closure) and <c>134</c> = an
/// unhandled tool exception (missing input file, unknown flag); both are
/// infrastructure. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed. Exit <c>0</c> without the
/// <c>Verified.</c> marker, or exit <c>2</c> without <c>[IL]: Error</c>
/// lines, fails closed through the parser — a hijacked or truncated
/// invocation is never a pass.</para>
///
/// <para><b>Version pin.</b> Verification rules change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>ilverify --version</c> before the scan
/// and pins the NuGet package release line (<c>10.0.12</c> out of the
/// <c>10.0.12-servicing…</c> banner — the servicing suffix is stripped,
/// so rollups of the same release do not flap the gate); a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Reference closure is operator-owned and mandatory.</b> ILVerify
/// resolves dependencies only from assemblies named with <c>-r</c>; it
/// never infers a closure from the host runtime. Both lists are explicit
/// scoped config: <c>Assemblies</c> names the candidate-produced assemblies
/// under audit (presence-probed, missing entries fail closed), and
/// <c>ReferenceAssemblies</c> names the complete reference set matching the
/// target framework (each literal entry presence-probed, each glob entry
/// required to match at least one file — a configured reference that
/// resolves to nothing fails closed). A loader failure at scan time (exit
/// <c>1</c>, or a <c>FileLoadErrorGeneric</c> / failed-to-load line on exit
/// <c>2</c>) is an incomplete closure, i.e. infrastructure — never a pass
/// and never a code defect. There is no repository-controlled suppression
/// surface for IL verification: suppression files, attributes, and config
/// cannot hide a verification failure from the tool.</para>
///
/// <para><b>Scope and defaults.</b> The scan verifies exactly the configured
/// <c>Assemblies</c> — there is no discovery, so an unconfigured assembly
/// is out of scope rather than silently covered. Findings carry the
/// repository-relative assembly path the scan was given; the finding-level
/// <c>ExcludePaths</c> backstop drops findings under dependency prefixes
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) by default.
/// Build-output prefixes are deliberately NOT excluded: candidate
/// assemblies live in build output (<c>bin/</c>, <c>artifacts/</c>), so
/// excluding them would drop every finding the auditor exists to report.
/// The scan reads the inputs and writes nothing into the audited tree.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ILVerify Compiled-Assembly Auditor",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "ilverify",
    InstallHint = "provision the pinned dotnet-ilverify release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via dotnet tool (dotnet tool install -g "
        + "dotnet-ilverify --version " + DefaultExpectedVersion + ") — no distro apt package carries a "
        + "version pin — through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "dotnet",
    InstallHint = "ilverify is a framework-dependent .NET tool and the reference closure normally comes "
        + "from a .NET shared framework or SDK, so the sandbox baseline needs a .NET runtime (9 or later) "
        + "alongside ilverify")]
public sealed class ILVerifyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.ilverify";

    /// <summary>
    /// dotnet-ilverify release line the invocation and its findings are
    /// verified against, as the NuGet package version: the
    /// <c>dotnet-ilverify 10.0.12</c> package reports
    /// <c>10.0.12-servicing…</c> via <c>ilverify --version</c>, pinned here
    /// as <c>10.0.12</c> (the servicing suffix is stripped before
    /// comparison). Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "10.0.12";

    /// <summary>
    /// Scoped-config key listing the candidate-produced assemblies under
    /// audit: comma-separated repository-relative paths (e.g.
    /// <c>artifacts/publish/App.dll</c>). Explicit paths only — no globs,
    /// no discovery. Every entry must exist; a missing entry fails closed
    /// as deterministic infrastructure.
    /// </summary>
    public const string AssembliesKey = "Assemblies";

    /// <summary>
    /// Scoped-config key listing the complete reference-assembly closure
    /// matching the target framework: comma-separated repository-relative
    /// paths or <c>*</c>/<c>?</c> globs (e.g.
    /// <c>artifacts/publish/*.dll, refs/net10.0/System.Runtime.dll</c>).
    /// Each entry is passed to the tool as its own <c>-r</c> argument.
    /// Literal entries must exist; glob entries must match at least one
    /// file — otherwise the run fails closed as deterministic
    /// infrastructure. Do not infer the closure from the host runtime.
    /// </summary>
    public const string ReferenceAssembliesKey = "ReferenceAssemblies";

    private const string ReferenceFlag = "-r";

    // One scan invocation holds the built entries plus one entry per
    // assembly plus two entries per reference (-r + value): 64 assemblies
    // and 32 references fit comfortably under the shared 256-argument
    // ceiling while keeping per-entry glob probes bounded.
    private const int MaxAssemblyInputs = 64;
    private const int MaxReferenceEntries = 32;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = every input verified (Verified. marker per input). 2 =
        // verification failures ([IL]: Error lines plus a summary). Exit 1
        // (loader could not start) and everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Findings under dependency prefixes describe code that is not the
        // change under audit. Build-output prefixes are NOT excluded here:
        // candidate assemblies live in build output, so excluding them
        // would silence the auditor entirely. Operators re-include a path
        // by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _assemblies = static () => null;
    private Func<string?> _referenceAssemblies = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:ilverify";

    /// <inheritdoc />
    protected override string ToolName => "ilverify";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ILVerifyOutputParser();

    /// <summary>
    /// Declared mapping for ILVerify's single severity: every verification
    /// failure the parser reports carries level <c>error</c> and fails the
    /// audit. Raw levels never reach findings; anything unrecognised falls
    /// back to <see cref="AuditSeverity.Warning"/>.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"], ExtractPackageVersion);

    // The banner appends a servicing suffix the NuGet package version does
    // not carry ("10.0.12-servicing.26422.108+…"): pin the package release
    // line (first major.minor.patch token) so servicing rollups of the same
    // release do not flap the gate, while any other release line still
    // fails closed. A configured ExpectedVersion keeps the shared
    // extraction — an operator pinning a suffixed build fails closed
    // against the suffix-stripped report rather than passing loosely.
    private static readonly Regex PackageVersionPattern = new(
        @"\d+\.\d+\.\d+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? ExtractPackageVersion(string output)
    {
        var match = PackageVersionPattern.Match(output ?? string.Empty);
        return match.Success ? match.Value : null;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // The scan positionals (-r values and input assemblies) are
        // per-run context resolved in ResolveContextArgumentsAsync (they
        // need sandbox probes); operator ExtraArguments ride after them via
        // the base. Nothing tool-wide is needed here.
        return [];
    }

    /// <inheritdoc />
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // ilverify ships as a framework-dependent .NET global tool: without
        // a runtime beside it the scan cannot start. Probe early so the
        // failure names dotnet instead of surfacing as a bare exec error.
        await ThrowIfBinaryMissingAsync(
            sandbox, workingDirectory, "dotnet", options, ct,
            "ILVerify is a framework-dependent .NET tool").ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var assemblies = ResolveAssemblyInputs();
        var references = ResolveReferenceEntries();
        await ProbeAssemblyInputsPresentAsync(sandbox, workingDirectory, assemblies, options, ct)
            .ConfigureAwait(false);
        var referenceValues = await ProbeReferenceEntriesPresentAsync(
            sandbox, workingDirectory, references, options, ct).ConfigureAwait(false);

        var argv = new List<string>(assemblies.Count + (2 * referenceValues.Count));
        foreach (var assembly in assemblies)
            argv.Add(ToSafePositional(assembly));
        foreach (var reference in referenceValues)
        {
            argv.Add(ReferenceFlag);
            argv.Add(reference);
        }

        return argv;
    }

    /// <inheritdoc />
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
        _assemblies = () => scoped[AssembliesKey];
        _referenceAssemblies = () => scoped[ReferenceAssembliesKey];
        context.Logger.LogInformation(
            "ILVerifyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> ResolveAssemblyInputs()
    {
        var entries = ExternalToolAuditorOptions.SplitCommaSeparatedList(_assemblies());
        if (entries.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' has no '{AssembliesKey}' configured. Set "
                + $"CodeyBox:Plugins:{PluginId}:{AssembliesKey} to the comma-separated "
                + "repository-relative paths of the candidate-produced assemblies under audit (e.g. "
                + "'artifacts/publish/App.dll'). Without explicit inputs the tool has nothing to "
                + "verify, so this is infrastructure, not a pass.")
            { IsDeterministic = true };
        if (entries.Count > MaxAssemblyInputs)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with {entries.Count} "
                + $"'{AssembliesKey}' entries, more than the {MaxAssemblyInputs} one scan invocation "
                + "holds. Verify fewer assemblies per audit or split the scope.")
            { IsDeterministic = true };

        var resolved = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IndexOfAny(['*', '?', '[', ']']) >= 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{PluginId}' '{AssembliesKey}' entry "
                    + $"('{TruncateForMessage(entry)}') must be an explicit assembly path, not a glob. "
                    + "Candidate assemblies are always named exactly; globs are only accepted in "
                    + $"'{ReferenceAssembliesKey}'.")
                { IsDeterministic = true };
            var relative = ValidatedRepoRelativeTarget(entry, $"'{AssembliesKey}' entry");
            if (!IsAssemblyFile(relative))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{PluginId}' '{AssembliesKey}' entry "
                    + $"('{TruncateForMessage(entry)}') must name a .dll or .exe assembly.")
                { IsDeterministic = true };
            if (!resolved.Contains(relative, StringComparer.Ordinal))
                resolved.Add(relative);
        }

        return resolved;
    }

    private IReadOnlyList<string> ResolveReferenceEntries()
    {
        var entries = ExternalToolAuditorOptions.SplitCommaSeparatedList(_referenceAssemblies());
        if (entries.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' has no '{ReferenceAssembliesKey}' configured. "
                + $"Set CodeyBox:Plugins:{PluginId}:{ReferenceAssembliesKey} to the comma-separated "
                + "repository-relative reference assemblies matching the target framework (e.g. "
                + "'artifacts/publish/*.dll'). ILVerify resolves dependencies only from explicitly "
                + "named references — without a complete closure nothing can be verified, so this is "
                + "infrastructure, not a pass.")
            { IsDeterministic = true };
        if (entries.Count > MaxReferenceEntries)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with {entries.Count} "
                + $"'{ReferenceAssembliesKey}' entries, more than the {MaxReferenceEntries} one scan "
                + "invocation probes. Collapse framework directories into globs (e.g. 'refs/*.dll').")
            { IsDeterministic = true };

        var resolved = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var normalized = NormalizeReferenceEntry(entry);
            if (!resolved.Contains(normalized, StringComparer.Ordinal))
                resolved.Add(normalized);
        }

        return resolved;
    }

    // Reference entries travel to the tool as -r argv values (which the
    // tool glob-expands itself in the sandbox), so they pass the argv guard
    // plus containment: repository-relative, no ".." escape, no bracket
    // classes (the shared probe globber has no bracket semantics), and
    // naming assembly files. A leading "./" is accepted and folded away so
    // "./refs/*.dll" and "refs/*.dll" probe and verify identically.
    private static string NormalizeReferenceEntry(string entry)
    {
        string normalized;
        try
        {
            normalized = NormalizeProbePathGlob(entry);
        }
        catch (ArgumentException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' '{ReferenceAssembliesKey}' entry "
                + $"('{TruncateForMessage(entry)}') is not a usable repository-relative reference path "
                + "or '*'/'?' glob.",
                ex)
            { IsDeterministic = true };
        }

        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Split('/').Contains("..", StringComparer.Ordinal)
            || !IsAssemblyFile(normalized))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' '{ReferenceAssembliesKey}' entry "
                + $"('{TruncateForMessage(entry)}') must be a repository-relative .dll or .exe path or "
                + "'*'/'?' glob without '..' segments.")
            { IsDeterministic = true };
        return ToSafePositional(normalized);
    }

    private async Task ProbeAssemblyInputsPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> assemblies,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, ToolName, assemblies, options, ct).ConfigureAwait(false);
        var missing = assemblies
            .Where(candidate => !present.Contains(candidate, StringComparer.Ordinal))
            .ToList();
        if (missing.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found no candidate assembly "
                + $"'{TruncateForMessage(missing[0])}' in the audited repository. Build the candidate "
                + "before the audit so the configured "
                + $"CodeyBox:Plugins:{PluginId}:{AssembliesKey} paths exist — a missing binary is "
                + "infrastructure, not a verdict on the diff.")
            { IsDeterministic = true };
    }

    // Literal references are presence-probed in one batch; each glob entry
    // is expanded with its own bounded probe and must match at least one
    // file — a configured reference that resolves to nothing would silently
    // narrow the closure the verifier sees. The original (glob) entries
    // travel to the scan: the tool expands them in the sandbox at scan
    // time, after the probes just proved them non-empty.
    private async Task<IReadOnlyList<string>> ProbeReferenceEntriesPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> references,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var literals = references
            .Where(entry => entry.IndexOfAny(['*', '?']) < 0)
            .Select(entry => entry.StartsWith("./", StringComparison.Ordinal) ? entry[2..] : entry)
            .ToList();
        if (literals.Count > 0)
        {
            var present = await ProbeRepositoryFilesPresentAsync(
                sandbox, workingDirectory, ToolName, literals, options, ct).ConfigureAwait(false);
            var missing = literals
                .Where(candidate => !present.Contains(candidate, StringComparer.Ordinal))
                .ToList();
            if (missing.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' found no reference assembly "
                    + $"'{TruncateForMessage(missing[0])}' in the audited repository. Supply a complete "
                    + "reference closure matching the target framework via "
                    + $"CodeyBox:Plugins:{PluginId}:{ReferenceAssembliesKey} — a missing reference is "
                    + "infrastructure, not a verdict on the diff.")
                { IsDeterministic = true };
        }

        foreach (var entry in references)
        {
            var probeEntry = entry.StartsWith("./", StringComparison.Ordinal) ? entry[2..] : entry;
            if (probeEntry.IndexOfAny(['*', '?']) < 0)
                continue;
            IReadOnlyList<string> matched;
            try
            {
                matched = await ProbeRepositoryPathGlobsPresentAsync(
                    sandbox, workingDirectory, ToolName, [probeEntry], options, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{PluginId}' '{ReferenceAssembliesKey}' entry "
                    + $"('{TruncateForMessage(entry)}') is not a usable repository-relative glob.",
                    ex)
                { IsDeterministic = true };
            }

            if (matched.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' reference glob "
                    + $"'{TruncateForMessage(entry)}' matched no files in the audited repository. A "
                    + "reference entry that resolves to nothing silently narrows the closure the "
                    + "verifier sees, so this is infrastructure, not a verdict on the diff.")
                { IsDeterministic = true };
        }

        return references;
    }

    private static bool IsAssemblyFile(string path)
        => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    // A configured name comes from operator config — it is passed as one
    // argv entry (never through a shell), and the "./" prefix keeps a
    // leading-dash name from being read as a tool flag.
    private static string ToSafePositional(string relativePath)
        => relativePath.StartsWith("./", StringComparison.Ordinal)
            ? relativePath
            : "./" + relativePath;
}
