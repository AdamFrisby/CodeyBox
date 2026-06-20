using CodeyBox.Projects;
using Microsoft.Extensions.Options;

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
/// <para>Default exemptions cover framework-internal sections bound outside
/// the typed root (e.g. <c>BuildScriptAudit</c>, <c>Plugins</c>, <c>Mutation</c>).
/// Operator extension namespaces add to
/// <c>CodeyBox:ConfigValidation:UnboundKeys:AdditionalExemptPathPrefixes</c>.</para>
/// </summary>
internal sealed class UnboundConfigKeyHostedValidator : IHostedService
{
    /// <summary>
    /// Sections under <c>CodeyBox:</c> that are deliberately bound to typed
    /// option classes outside <see cref="CodeyBoxOptions"/> /
    /// <see cref="ProjectsOptions"/>. Excluded so a vanilla config does not
    /// flag them as unbound.
    /// </summary>
    internal static readonly string[] DefaultExemptPaths =
    {
        "CodeyBox:BuildScriptAudit",
        "CodeyBox:PromptPreprocessing",
        "CodeyBox:Presets",
        "CodeyBox:Mutation",
        "CodeyBox:CheckAndActCompletion",
        "CodeyBox:Plugins",
        // Read directly via IConfiguration.GetValue<bool>(ApiKeyAuth.DisableConfigKey)
        // rather than through the typed options graph.
        "CodeyBox:DangerouslyDisableAuth",
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

    private readonly IConfiguration _config;
    private readonly IOptions<CodeyBoxOptions> _options;
    private readonly ILogger<UnboundConfigKeyHostedValidator> _log;

    public UnboundConfigKeyHostedValidator(
        IConfiguration config,
        IOptions<CodeyBoxOptions> options,
        ILogger<UnboundConfigKeyHostedValidator> log)
    {
        _config = config;
        _options = options;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var knobs = _options.Value.ConfigValidation.UnboundKeys;
        if (!knobs.Enabled)
            return Task.CompletedTask;

        var reports = Inspect(_config, knobs.AdditionalExemptPathPrefixes);
        if (reports.Count == 0)
            return Task.CompletedTask;

        var summary = BuildSummary(reports);
        if (string.Equals(knobs.Mode, "warn", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Unbound CodeyBox configuration keys detected: {Reports}", summary);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "Unbound CodeyBox configuration keys detected (no matching CodeyBoxOptions / " +
            "ProjectsOptions property). Fix or remove these keys, or downgrade to a warning " +
            "via CodeyBox:ConfigValidation:UnboundKeys:Mode=\"warn\":" +
            Environment.NewLine + summary);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

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
            exempt);
    }

    private static string BuildSummary(IReadOnlyList<UnboundConfigKeyReport> reports)
        => UnboundConfigKeyInspector.FormatReports(reports);
}
