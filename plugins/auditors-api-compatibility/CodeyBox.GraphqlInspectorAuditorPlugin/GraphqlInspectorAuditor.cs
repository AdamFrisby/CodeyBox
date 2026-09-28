using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GraphqlInspectorAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>graphql-inspector diff</c> (GraphQL
/// schema breaking-change detection) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the diff report parser
/// (<see cref="GraphqlInspectorReportParser"/> — the tool exposes no JSON
/// output mode; its verdict is the logger's symbol-prefixed line report),
/// the schema-pointer and diff-rule preconditions below, and the default
/// scope.
///
/// <para><b>Gate behaviour: blocking on breaking changes.</b> Every change
/// the tool reports at <c>✖</c> (criticality <c>BREAKING</c> — removed
/// fields, types, enum values, widened inputs) maps to
/// <see cref="AuditSeverity.Error"/> and fails the audit; <c>⚠</c>
/// (<c>DANGEROUS</c>) maps to <see cref="AuditSeverity.Warning"/> and
/// <c>✔</c> (<c>NON_BREAKING</c>) maps to <see cref="AuditSeverity.Info"/>,
/// both advisory. Raw tool levels never reach findings.</para>
///
/// <para><b>Exit-code convention (verified against
/// <c>@graphql-inspector/cli</c> 7.0.0 source and live runs — not the common
/// 0/1/2 convention).</b> <c>0</c> = the diff completed and no breaking
/// change exists (dangerous/non-breaking changes may still be listed);
/// <c>1</c> = ambiguous: <c>failOnBreakingChanges</c> calls
/// <c>process.exit(1)</c> when breaking changes exist, but a thrown load
/// error (unresolvable pointer, invalid SDL) also exits <c>1</c> through
/// yargs. Both are findings-producing <em>candidates</em>; the parser tells
/// them apart by content — change lines mean findings, their absence means
/// infrastructure. <c>126</c>/<c>127</c> are cannot-execute / not-found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> The change catalogue, message templates, and
/// exit convention move between releases, so findings are only meaningful
/// from the build the auditor was verified against: <c>@graphql-inspector/cli</c>
/// <c>7.0.0</c>, provisioned through the declared tool requirement below.
/// There is deliberately no runtime version probe: <c>graphql-inspector
/// --version</c> prints <c>unknown</c> on every invocation (verified), so a
/// probe could never confirm the pin and would fail closed on the genuine
/// binary. The pin lives at provisioning time (exact <c>npm install -g
/// @graphql-inspector/cli@7.0.0</c> in the install hint) instead of
/// pretending to verify at scan time.</para>
///
/// <para><b>Repository-controlled input.</b> <c>diff</c> reads no repository
/// config file — its only inputs are the two schema pointers and the flags
/// this auditor builds. The code-loading surfaces are <c>--rule</c> with
/// a module path (plus <c>--onComplete</c>, <c>--onUsage</c> and
/// <c>--require</c>, which load sandbox-side JavaScript and, for
/// <c>--onComplete</c>, replace the exit-1-on-breaking contract the parser
/// relies on). Operator <c>ExtraArguments</c> carrying any of those flags
/// are a deterministic infrastructure failure; the side-effect-free builtin
/// rules are offered instead through the scoped <c>DiffRules</c> key, which
/// accepts only the exact allowlist below. The allowlist alone is not
/// sufficient: the tool resolves a <c>--rule</c> name against the sandbox
/// working directory (the audited repository root) <em>before</em> its
/// builtin table, so a repository-root file shadowing a configured builtin
/// name would be <c>require</c>d as code in-process — able to silence
/// findings. <see cref="VerifyToolAsync"/> therefore probes for such
/// shadowing files and fails closed when any is present. The audit subject
/// can therefore neither silence this auditor nor execute code through
/// it.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>graphql-inspector diff &lt;OldSchema&gt; &lt;NewSchema&gt;</c> and
/// reads exactly those two pointers — the tool never walks the repository,
/// so vendored or generated trees cannot produce findings and the auditor
/// ships no <c>ExcludePaths</c> default. Both pointers are required with no
/// guessed default: schema layout varies per repository (SDL files,
/// code-first, federation, URLs), and any invented default risks a silent
/// no-op diff. An unset pointer is a deterministic infrastructure failure
/// naming the key — enabling this auditor without configuring it is a loud
/// misconfiguration, not a silent skip.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: GraphQL Inspector Schema Compatibility",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "graphql-inspector",
    InstallHint = "provision the pinned graphql-inspector release (7.0.0) into the sandbox baseline via npm (npm install -g @graphql-inspector/cli@"
        + DefaultExpectedVersion + " graphql) — no distro apt package carries it — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class GraphqlInspectorAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.graphql-inspector";

    /// <summary>
    /// graphql-inspector release the invocation and its findings are verified
    /// against. There is no runtime probe (the binary reports its version as
    /// <c>unknown</c>); operators pin this exact release at provisioning time
    /// via the declared tool requirement above.
    /// </summary>
    public const string DefaultExpectedVersion = "7.0.0";

    /// <summary>Scoped-config key for the baseline (old) schema pointer.</summary>
    public const string OldSchemaKey = "OldSchema";

    /// <summary>Scoped-config key for the current (new) schema pointer.</summary>
    public const string NewSchemaKey = "NewSchema";

    /// <summary>Scoped-config key for builtin diff rules (comma-separated).</summary>
    public const string DiffRulesKey = "DiffRules";

    /// <summary>
    /// Side-effect-free builtin <c>--rule</c> names the auditor passes
    /// through. <c>considerUsage</c> is excluded: it requires an
    /// <c>--onUsage</c> JavaScript module. Custom rule paths are excluded:
    /// they load sandbox-side code.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedDiffRules =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "dangerousBreaking",
            "suppressRemovalOfDeprecatedField",
            "ignoreDescriptionChanges",
            "safeUnreachable",
        };

    private const int MaxPointerChars = 1024;

    private static readonly string[] CodeLoadingFlags =
    [
        "-r", "--require",
        "--rule",
        "--onComplete", "--onUsage",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran, no breaking changes (advisory changes may be listed);
        // 1 = breaking changes found OR could not run — the parser tells
        // them apart by content. Everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // No default: the tool reads exactly the two configured schema
        // pointers and never walks the tree, so vendored or generated code
        // cannot produce findings to exclude.
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _oldSchema = static () => null;
    private Func<string?> _newSchema = static () => null;
    private Func<IReadOnlyList<string>> _diffRules = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:graphql-inspector";

    /// <inheritdoc />
    protected override string ToolName => "graphql-inspector";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser =>
        // Bound per invocation so the finding path echoes the current
        // scoped NewSchema pointer; operator edits apply without a restart.
        new GraphqlInspectorReportParser(_newSchema);

    /// <summary>
    /// Declared mapping from graphql-inspector's criticality vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. The parser supplies the
    /// canonical level tokens; raw symbols never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["breaking"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["dangerous"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["non-breaking"] = AuditSeverity.Info,
            ["nonbreaking"] = AuditSeverity.Info,
            ["safe"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var oldSchema = _oldSchema()?.Trim();
        var newSchema = _newSchema()?.Trim();
        var args = new List<string> { "diff" };
        if (!string.IsNullOrWhiteSpace(oldSchema))
            args.Add(oldSchema);
        if (!string.IsNullOrWhiteSpace(newSchema))
            args.Add(newSchema);

        foreach (var rule in _diffRules())
        {
            args.Add("--rule");
            args.Add(rule);
        }

        return args;
    }

    /// <inheritdoc />
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var oldSchema = _oldSchema()?.Trim();
        var newSchema = _newSchema()?.Trim();
        if (string.IsNullOrWhiteSpace(oldSchema) || oldSchema.Length > MaxPointerChars)
            throw new AuditUnavailableException(
                $"Audit tool '{tool}' is misconfigured: scoped '{OldSchemaKey}' must name the baseline "
                + $"schema pointer (file, git:ref:path, github: pointer, or URL) of at most {MaxPointerChars} "
                + $"characters. Configure CodeyBox:Plugins:{PluginId}:{OldSchemaKey}.");
        if (string.IsNullOrWhiteSpace(newSchema) || newSchema.Length > MaxPointerChars)
            throw new AuditUnavailableException(
                $"Audit tool '{tool}' is misconfigured: scoped '{NewSchemaKey}' must name the current "
                + $"schema pointer (file, git:ref:path, github: pointer, or URL) of at most {MaxPointerChars} "
                + $"characters. Configure CodeyBox:Plugins:{PluginId}:{NewSchemaKey}.");

        var rules = _diffRules();
        foreach (var rule in rules)
        {
            if (!AllowedDiffRules.Contains(rule))
                throw new AuditUnavailableException(
                    $"Audit tool '{tool}' is misconfigured: scoped '{DiffRulesKey}' entry '{rule}' is not "
                    + $"a supported builtin diff rule (supported: {string.Join(", ", AllowedDiffRules.Order())}). "
                    + "Custom rule modules are not accepted because they load sandbox-side JavaScript.");
        }

        if (rules.Count > 0)
            await ThrowIfRuleShadowedByRepositoryFileAsync(
                sandbox, workingDirectory, tool, rules, options, ct).ConfigureAwait(false);

        foreach (var extra in options.ExtraArguments)
        {
            if (IsCodeLoadingFlag(extra))
                throw new AuditUnavailableException(
                    $"Audit tool '{tool}' is misconfigured: ExtraArguments entry '{extra}' loads sandbox-side "
                    + $"code or replaces the verified exit/output contract (--rule/--onComplete/--onUsage/--require). "
                    + $"Use scoped '{DiffRulesKey}' for builtin diff rules.");
        }
    }

    /// <summary>
    /// Fails closed when a repository-controlled file shadows a configured
    /// builtin <c>--rule</c> name. The tool resolves each rule name against
    /// its working directory (the audited repository root) and
    /// <c>require</c>s a hit <em>before</em> consulting its builtin rule
    /// table, so a repository-root file named e.g.
    /// <c>dangerousBreaking</c> would execute audit-subject code in-process
    /// with the CLI — able to suppress breaking changes. A bounded sandbox
    /// presence probe keeps this check at the sink: even a future caller
    /// passing a new allowlisted name through <c>--rule</c> is covered,
    /// because the probe runs over the entries actually emitted as argv.
    /// </summary>
    private static async Task ThrowIfRuleShadowedByRepositoryFileAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> rules,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, rules, options, ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' at the audited repository root shadowing configured "
                + $"builtin diff rule(s) from scoped '{DiffRulesKey}' — graphql-inspector resolves '--rule' "
                + "names against its working directory and loads a hit as code before its builtin rule "
                + "table, so the audit subject could execute code with the scan and suppress findings. "
                + "Remove the file(s) or drop the shadowed rule(s) from "
                + $"CodeyBox:Plugins:{PluginId}:{DiffRulesKey}.")
            { IsDeterministic = true };
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _oldSchema = () => scoped[OldSchemaKey];
        _newSchema = () => scoped[NewSchemaKey];
        _diffRules = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[DiffRulesKey]);
        context.Logger.LogInformation(
            "GraphqlInspectorAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static bool IsCodeLoadingFlag(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return false;
        var value = argument.Trim();
        foreach (var flag in CodeLoadingFlags)
        {
            if (value.Equals(flag, StringComparison.Ordinal)
                || value.StartsWith(flag + "=", StringComparison.Ordinal))
                return true;
            // Joined short form (e.g. "-r<module>"): the base documents and
            // handles this shape for single-dash value options, so the guard
            // matches it too.
            if (flag.Length == 2 && flag[0] == '-' && flag[1] != '-'
                && value.Length > flag.Length
                && value.StartsWith(flag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
