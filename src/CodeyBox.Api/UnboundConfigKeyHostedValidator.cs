using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that walks the operator-provided <c>CodeyBox:*</c>
/// configuration at host start and refuses to come up when any key fails to
/// bind to a known property on <see cref="CodeyBoxOptions"/> /
/// <see cref="ProjectsOptions"/>.
///
/// <para>Catches the silent no-op shape — e.g. an operator who writes
/// <c>CodeyBox:AgentStreams:RootDirectory</c> when the typed property is
/// <c>Path</c> would otherwise see the default keep applying with no signal
/// that the configured value was dropped on the floor.</para>
///
/// <para>Gating: <c>CodeyBox:ConfigValidation:UnboundKeys:Enabled</c>
/// (default <c>true</c>) turns the check on/off. When the check fires,
/// <c>Mode = "strict"</c> (default) throws and <c>"warn"</c> emits one
/// warning log per unbound key.</para>
///
/// <para>Framework-internal sections bound outside the typed root (e.g.
/// <c>BuildScriptAudit</c>, <c>Plugins</c>, <c>Mutation</c>) are walked
/// against their own typed root POCO so typos inside them still surface;
/// only genuine operator-keyed extension subtrees (e.g. <c>CodeyBox:Plugins:&lt;plugin-id&gt;</c>)
/// are silenced. Operator extension namespaces add to
/// <c>CodeyBox:ConfigValidation:UnboundKeys:AdditionalExemptPaths</c>.</para>
/// </summary>
internal sealed class UnboundConfigKeyHostedValidator : IHostedService
{
    /// <summary>
    /// Leaf-shaped <c>CodeyBox:*</c> keys read directly via
    /// <see cref="IConfiguration"/> with no matching property on the typed
    /// options graph. Excluded by exact path so a vanilla config does not
    /// flag them as unbound.
    ///
    /// <para>POCO-shaped sections bound to a typed root outside
    /// <see cref="CodeyBoxOptions"/> / <see cref="ProjectsOptions"/> are NOT
    /// listed here — they live in <see cref="DefaultExternalBindings"/> so the
    /// walker still descends into them with their own property graph and
    /// surfaces typos like <c>CodeyBox:BuildScriptAudit:TimoutSeconds</c>.</para>
    /// </summary>
    internal static readonly string[] DefaultExemptPaths =
    {
        // Read directly via IConfiguration.GetValue<bool>(ApiKeyAuth.DisableConfigKey)
        // rather than through the typed options graph.
        "CodeyBox:DangerouslyDisableAuth",
        // Read directly via CredentialFileWatcherSettings.IsEnabled against the
        // raw configuration value. Documented operator knob with no matching
        // typed property.
        "CodeyBox:CredentialFileWatchers",
        // Direct-config leaf keys for per-agent credential file paths and
        // OAuth client secrets. Read via builder.Configuration["CodeyBox:…"]
        // when the matching CODEYBOX_…_FILE env var is unset; no matching
        // property exists on CodeyBoxOptions / ProjectsOptions.
        "CodeyBox:ClaudeOAuthFile",
        "CodeyBox:CodexOAuthFile",
        "CodeyBox:GeminiOAuthFile",
        "CodeyBox:GeminiSettingsFile",
        "CodeyBox:CursorAuthFile",
        "CodeyBox:OpencodeAuthFile",
        "CodeyBox:OpencodeAuthDestPath",
        "CodeyBox:GeminiOauthClientId",
        "CodeyBox:GeminiOauthClientSecret",
    };

    /// <summary>
    /// Configuration sub-paths whose typed root binds outside the
    /// <see cref="CodeyBoxOptions"/> / <see cref="ProjectsOptions"/> graph. The
    /// inspector still walks the sub-tree — using the mapped POCO — so a typo
    /// like <c>CodeyBox:BuildScriptAudit:TimoutSeconds</c> surfaces instead of
    /// being lost to a blanket subtree exemption.
    ///
    /// <para><c>AllowsExtensionKeys</c> is set on <c>CodeyBox:Plugins</c>
    /// because its sub-tree mixes typed
    /// <see cref="PluginOptions.AssemblyPaths"/>/<see cref="PluginOptions.PackageDirectories"/>/
    /// <see cref="PluginOptions.Allowlist"/> properties with operator-defined
    /// <c>&lt;plugin-id&gt;</c> sub-trees that plugins read via
    /// <c>IPluginHost.ScopedConfig</c>. Non-matching keys at that level are
    /// treated as opaque plugin ids rather than flagged. The trade-off is
    /// that a typo of a typed property name (e.g. <c>Allwlist</c>) at the
    /// <c>CodeyBox:Plugins</c> level cannot be distinguished from a plugin
    /// id and stays silent; typos under the typed properties themselves
    /// (e.g. inside <c>AssemblyPaths</c>) are still validated.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, ExternalSectionBinding> DefaultExternalBindings =
        new Dictionary<string, ExternalSectionBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["CodeyBox:BuildScriptAudit"] = new(typeof(BuildScriptAuditorOptions), AllowsExtensionKeys: false),
            ["CodeyBox:PromptPreprocessing"] = new(typeof(AgentPromptPreprocessingOptions), AllowsExtensionKeys: false),
            ["CodeyBox:Presets"] = new(typeof(PresetCatalogOptions), AllowsExtensionKeys: false),
            ["CodeyBox:Mutation"] = new(typeof(MutationTestingAuditorOptions), AllowsExtensionKeys: false),
            ["CodeyBox:CheckAndActCompletion"] = new(typeof(CheckAndActCompletionOptions), AllowsExtensionKeys: false),
            ["CodeyBox:Plugins"] = new(typeof(PluginOptions), AllowsExtensionKeys: true),
        };

    private const string KnobsSectionPath = "CodeyBox:ConfigValidation:UnboundKeys";

    private readonly IConfiguration _config;
    private readonly ILogger<UnboundConfigKeyHostedValidator> _log;

    public UnboundConfigKeyHostedValidator(
        IConfiguration config,
        ILogger<UnboundConfigKeyHostedValidator> log)
    {
        _config = config;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Read the knobs directly from IConfiguration rather than via
        // IOptions&lt;CodeyBoxOptions&gt;.Value: resolving the bound options
        // would force every registered IValidateOptions&lt;CodeyBoxOptions&gt;
        // to run first, so an unrelated validator failure (or a typo that
        // ALSO trips a downstream typed validator — the common shape) would
        // swallow this report before it could surface. Tying this validator
        // to live config keeps it independent of the rest of the typed
        // options graph.
        var knobs = _config.GetSection(KnobsSectionPath);
        var enabled = GetBool(knobs, "Enabled", defaultValue: true);
        if (!enabled)
            return Task.CompletedTask;

        var mode = knobs["Mode"];
        var additionalExempt = knobs
            .GetSection("AdditionalExemptPaths")
            .GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();

        var reports = Inspect(_config, additionalExempt);
        if (reports.Count == 0)
            return Task.CompletedTask;

        var summary = UnboundConfigKeyInspector.FormatReports(reports);
        if (IsWarnMode(mode))
        {
            _log.LogWarning("Unbound CodeyBox configuration keys detected: {Reports}", summary);
            return Task.CompletedTask;
        }

        if (!IsStrictMode(mode))
        {
            // Don't silently fall through to strict — operators who typo
            // "warning"/"loose"/"off" need to see the rejected value, not
            // discover it via a startup throw they did not expect.
            _log.LogWarning(
                "Unknown CodeyBox:ConfigValidation:UnboundKeys:Mode value '{Mode}'. " +
                "Expected 'strict' or 'warn'; treating as 'strict'.",
                mode);
        }

        throw new InvalidOperationException(
            "Unbound CodeyBox configuration keys detected (no matching CodeyBoxOptions / " +
            "ProjectsOptions property). Fix or remove these keys, or downgrade to a warning " +
            "via CodeyBox:ConfigValidation:UnboundKeys:Mode=\"warn\":" +
            Environment.NewLine + summary);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static bool GetBool(IConfiguration section, string key, bool defaultValue)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static bool IsWarnMode(string? mode) =>
        string.Equals(mode, "warn", StringComparison.OrdinalIgnoreCase);

    private static bool IsStrictMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode)
        || string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<UnboundConfigKeyReport> Inspect(
        IConfiguration config,
        IReadOnlyCollection<string>? additionalExemptPaths = null)
    {
        var exempt = new HashSet<string>(DefaultExemptPaths, StringComparer.OrdinalIgnoreCase);
        if (additionalExemptPaths is not null)
        {
            foreach (var path in additionalExemptPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    exempt.Add(path.Trim());
            }
        }

        return UnboundConfigKeyInspector.Inspect(
            config.GetSection("CodeyBox"),
            new[] { typeof(CodeyBoxOptions), typeof(ProjectsOptions) },
            exempt,
            DefaultExternalBindings);
    }
}
