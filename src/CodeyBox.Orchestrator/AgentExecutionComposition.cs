using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeyBox.Agents;
using CodeyBox.Agents.Aider;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Autohand;
using CodeyBox.Agents.CavemanCode;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Cline;
using CodeyBox.Agents.Cmd;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Continue;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Crock;
using CodeyBox.Agents.Crush;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Devin;
using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Goose;
using CodeyBox.Agents.Kilo;
using CodeyBox.Agents.Omp;
using CodeyBox.Agents.Opencode;
using CodeyBox.Agents.Pi;
using CodeyBox.Agents.Prime;
using CodeyBox.Agents.Qwen;
using CodeyBox.Agents.Unreal;
using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Inputs for <see cref="AgentExecutionComposition"/>. The API host passes its
/// discovered credential plugins; the executor host leaves plugins empty.
/// </summary>
public sealed class AgentExecutionCompositionOptions
{
    /// <summary>
    /// Host configuration. Per-agent option sections bind straight from the
    /// <c>CodeyBox:</c> subtree, so both hosts read identical values without
    /// sharing the API host's aggregate options shape.
    /// </summary>
    public required IConfiguration Configuration { get; init; }

    /// <summary>
    /// Credential plugins discovered by the caller. Evaluated lazily at first
    /// credential resolution (after the caller's plugin loader ran), so the
    /// composition call itself stays ordering-independent.
    /// </summary>
    public Func<IReadOnlyList<LoadedPlugin>> PluginsProvider { get; set; } = static () =>
        Array.Empty<LoadedPlugin>();

    /// <summary>
    /// Test-only extension point standing in for a catalog edit: runners added
    /// here join the same <see cref="IAgentRegistry"/> the built-in catalog
    /// feeds. Production agents are added to
    /// <see cref="AgentExecutionComposition.AddAgentExecution"/> itself — the
    /// single catalog both hosts share — never here and never host-side.
    /// </summary>
    public IList<Func<IServiceProvider, IAgentRunner>> AdditionalRunners { get; } =
        new List<Func<IServiceProvider, IAgentRunner>>();
}

/// <summary>
/// Single composition site for the agent-execution stack both the API host and
/// the executor host run: per-agent options, shared snapshots, quota-failure
/// detectors, credential file sources plus the chained credential provider,
/// every <see cref="IAgentRunner"/>, the <see cref="IAgentRegistry"/>, and
/// stream capture (store, analysis guard, parsers, tool-call counters).
///
/// <para>Adding an agent means editing this file only: append the runner (and,
/// when the CLI has its own failure/stream shape, its detector and parser
/// rows) and both hosts serve it. Neither <c>Program.cs</c> owns a copy.</para>
///
/// <para>Values are identical to the former API-host inline registrations:
/// each per-agent section binds from the same <c>CodeyBox:</c> configuration
/// keys the aggregate options shape used, so in-process orchestration behaviour
/// is unchanged and an executor with the same sections behaves the same.</para>
/// </summary>
public static class AgentExecutionComposition
{
    /// <summary>
    /// Registers the whole agent-execution stack. Call once per host;
    /// <paramref name="configure"/> only supplies plugins and test runners.
    /// </summary>
    public static IServiceCollection AddAgentExecution(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AgentExecutionCompositionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AgentExecutionCompositionOptions { Configuration = configuration };
        configure?.Invoke(options);

        // Bare test containers have no framework-registered configuration;
        // real hosts already provide IConfiguration, which TryAdd preserves.
        services.TryAddSingleton(configuration);

        AddAgentOptions(services, configuration);
        AddSnapshots(services, configuration);
        AddQuotaFailureDetectors(services, configuration);
        AddCredentialChain(services, configuration, options);
        AddAgentRunners(services, options);
        AddStreamCapture(services);
        return services;
    }

    // Per-agent hot-reloadable options, bound straight from their sections.
    // Reading through IOptionsMonitor keeps the no-restart edits operators
    // rely on; the runner factories below resolve the monitor, never a copy.
    private static void AddAgentOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GooseOptions>().Bind(configuration.GetSection("CodeyBox:Goose"));
        services.AddOptions<VibeOptions>().Bind(configuration.GetSection("CodeyBox:Vibe"));
        services.AddOptions<AutohandOptions>().Bind(configuration.GetSection("CodeyBox:Autohand"));
        services.AddOptions<ClineOptions>().Bind(configuration.GetSection("CodeyBox:Cline"));
        services.AddOptions<KiloOptions>().Bind(configuration.GetSection("CodeyBox:Kilo"));
        services.AddOptions<ContinueOptions>().Bind(configuration.GetSection("CodeyBox:Continue"));
        services.AddOptions<CopilotOptions>().Bind(configuration.GetSection("CodeyBox:Copilot"));
        services.AddOptions<AntigravitySectionOptions>().Bind(configuration.GetSection("CodeyBox:Antigravity"));
        services.AddOptions<PrimeSectionOptions>().Bind(configuration.GetSection("CodeyBox:Prime"));
        services.AddOptions<CrockSandboxOptions>().Bind(configuration.GetSection("CodeyBox:Crock"));
        services.AddOptions<AgentStreamsOptions>().Bind(configuration.GetSection("CodeyBox:AgentStreams"));
        services.AddOptions<AgentStreamParserOptions>().Bind(configuration.GetSection("CodeyBox:AgentStreamAnalysis"));
    }

    // Shared holders every runner reads through, so an edit lands on the next
    // dispatched run. Dictionaries are rebuilt case-insensitive to match the
    // aggregate-options binding the API host used before extraction.
    private static void AddSnapshots(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp =>
        {
            var bound = configuration.GetSection("CodeyBox:AgentDefaults").Get<Dictionary<string, string?>>();
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (bound is not null)
            {
                foreach (var kvp in bound)
                    dict[kvp.Key] = kvp.Value;
            }
            return new AgentDefaultsSnapshot(dict);
        });
        services.AddSingleton(sp =>
        {
            var bound = configuration.GetSection("CodeyBox:AgentNetworkTolerance")
                .Get<Dictionary<string, AgentNetworkToleranceOptions?>>();
            return new AgentNetworkToleranceSnapshot(
                bound ?? new Dictionary<string, AgentNetworkToleranceOptions?>());
        });
        services.AddSingleton(sp =>
        {
            var bound = configuration.GetSection("CodeyBox:ClaudeThinkingBlockSanitizer")
                .Get<ClaudeThinkingBlockSanitizerBridge>() ?? new ClaudeThinkingBlockSanitizerBridge();
            return new ClaudeThinkingBlockSanitizerConfig { Enabled = bound.Enabled };
        });
    }

    // Structural twin of the API host's former pattern-options element: the
    // aggregate shape lives in the API project, which this project must not
    // reference, so extras bind through this bridge instead. Same keys, same
    // binder, same values.
    private sealed class QuotaPatternRow
    {
        public string Pattern { get; set; } = string.Empty;
        public QuotaFailureKind Kind { get; set; } = QuotaFailureKind.LimitReached;
    }

    private sealed class ClaudeThinkingBlockSanitizerBridge
    {
        public bool Enabled { get; set; } = true;
    }

    private static QuotaFailurePattern[]? ExtrasFor(IConfiguration configuration, params string[] agentKeys)
    {
        var table = configuration.GetSection("CodeyBox:QuotaFailurePatterns")
            .Get<Dictionary<string, List<QuotaPatternRow>>>();
        if (table is null)
            return null;
        var extras = new List<QuotaFailurePattern>();
        foreach (var kvp in table)
        {
            var matches = false;
            foreach (var key in agentKeys)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    break;
                }
            }
            if (!matches)
                continue;
            foreach (var row in kvp.Value ?? new List<QuotaPatternRow>())
            {
                if (!string.IsNullOrEmpty(row.Pattern))
                    extras.Add(new QuotaFailurePattern(row.Pattern, row.Kind));
            }
        }
        return extras.ToArray();
    }

    // Per-provider quota-failure detectors. Each agent library owns its
    // patterns; the orchestrator dispatches by AgentKind. Operator extras
    // append to the built-ins from CodeyBox:QuotaFailurePatterns:<kind>.
    private static void AddQuotaFailureDetectors(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentQuotaFailureDetector, ClaudeQuotaFailureDetector>();
        services.AddSingleton<IAgentQuotaFailureDetector, CodexQuotaFailureDetector>();
        services.AddSingleton<IAgentQuotaFailureDetector, GeminiQuotaFailureDetector>();
        AddExtrasDetector(services, configuration, static extras => new CursorQuotaFailureDetector(extras), AgentKind.Cursor.Value);
        services.AddSingleton<IAgentQuotaFailureDetector, OpencodeQuotaFailureDetector>();
        AddExtrasDetector(services, configuration, static extras => new DevinQuotaFailureDetector(extras), AgentKind.Devin.Value);
        AddExtrasDetector(services, configuration, static extras => new PiQuotaFailureDetector(extras), AgentKind.Pi.Value);
        AddExtrasDetector(services, configuration, static extras => new AiderQuotaFailureDetector(extras), AgentKind.Aider.Value);
        AddExtrasDetector(services, configuration, static extras => new GooseQuotaFailureDetector(extras), AgentKind.Goose.Value);
        AddExtrasDetector(services, configuration, static extras => new VibeQuotaFailureDetector(extras), AgentKind.Vibe.Value);
        AddExtrasDetector(services, configuration, static extras => new PrimeQuotaFailureDetector(extras), AgentKind.Prime.Value);
        AddExtrasDetector(services, configuration, static extras => new AutohandQuotaFailureDetector(extras), AgentKind.Autohand.Value);
        AddExtrasDetector(services, configuration, static extras => new ClineQuotaFailureDetector(extras), AgentKind.Cline.Value);
        AddExtrasDetector(services, configuration, static extras => new KiloQuotaFailureDetector(extras), AgentKind.Kilo.Value);
        AddExtrasDetector(services, configuration, static extras => new OmpQuotaFailureDetector(extras), AgentKind.Omp.Value);
        // Continue intentionally also honours the cmd key: preserved verbatim
        // from the pre-extraction registration (see suggestions log).
        AddExtrasDetector(
            services, configuration, static extras => new ContinueQuotaFailureDetector(extras),
            AgentKind.Continue.Value, AgentKind.Cmd.Value);
        AddExtrasDetector(services, configuration, static extras => new QwenQuotaFailureDetector(extras), AgentKind.Qwen.Value);
        AddExtrasDetector(services, configuration, static extras => new CmdQuotaFailureDetector(extras), AgentKind.Cmd.Value);
        AddExtrasDetector(
            services, configuration, static extras => new DotNetOpencodeQuotaFailureDetector(extras),
            AgentKind.DotNetOpencode.Value);
        AddExtrasDetector(services, configuration, static extras => new CrushQuotaFailureDetector(extras), AgentKind.Crush.Value);
        AddExtrasDetector(services, configuration, static extras => new UnrealQuotaFailureDetector(extras), AgentKind.Unreal.Value);
        services.AddSingleton<IAgentQuotaFailureDetector, CavemanCodeQuotaFailureDetector>();
        services.AddSingleton<IAgentQuotaFailureDetector, AntigravityQuotaFailureDetector>();
        services.AddSingleton<IAgentQuotaFailureDetector, CopilotQuotaFailureDetector>();
        services.AddSingleton<IQuotaFailureClassifier>(sp =>
            new CompositeQuotaFailureClassifier(sp.GetServices<IAgentQuotaFailureDetector>()));
    }

    private static void AddExtrasDetector(
        IServiceCollection services,
        IConfiguration configuration,
        Func<QuotaFailurePattern[]?, IAgentQuotaFailureDetector> factory,
        params string[] agentKeys)
    {
        services.AddSingleton<IAgentQuotaFailureDetector>(
            _ => factory(ExtrasFor(configuration, agentKeys)));
    }

    // Credential file sources (one shared watcher per file) plus the rotation
    // pusher, then the chain both hosts resolve identically: OAuth-file
    // providers first, caller plugins in the middle, env-var providers last.
    // A credential that works on one host works on the other because the
    // resolution code path is this one in both processes.
    private static void AddCredentialChain(
        IServiceCollection services,
        IConfiguration configuration,
        AgentExecutionCompositionOptions options)
    {
        var claudeOAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_CLAUDE_OAUTH_FILE", "CodeyBox:ClaudeOAuthFile",
            ".claude", ".credentials.json");
        var claudeOAuthProviderConfigured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_OAUTH_FILE")
            ?? configuration["CodeyBox:ClaudeOAuthFile"]);
        var codexOAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_CODEX_OAUTH_FILE", "CodeyBox:CodexOAuthFile",
            ".codex", "auth.json");
        var geminiHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        var geminiOAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_GEMINI_OAUTH_FILE", "CodeyBox:GeminiOAuthFile",
            geminiHome, "oauth_creds.json");
        var geminiSettingsFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_GEMINI_SETTINGS_FILE", "CodeyBox:GeminiSettingsFile",
            geminiHome, "settings.json");
        var cursorAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_CURSOR_AUTH_FILE", "CodeyBox:CursorAuthFile",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "cursor"),
            "auth.json");
        var devinAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_DEVIN_AUTH_FILE", "CodeyBox:DevinAuthFile",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "devin"),
            "credentials.toml");
        var opencodeAuthFilePath = ResolveCredentialPath(
            configuration, "CODEYBOX_OPENCODE_AUTH_FILE", "CodeyBox:OpencodeAuthFile",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode"),
            "auth.json");
        var antigravityOAuthFilePath = ResolveCredentialPath(
            configuration,
            "CODEYBOX_ANTIGRAVITY_OAUTH_FILE",
            "CodeyBox:Antigravity:OAuthTokenFile",
            Path.Combine(geminiHome, "antigravity-cli"),
            "antigravity-oauth-token");
        var opencodeAuthDestPath =
            Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_AUTH_DEST")
            ?? configuration["CodeyBox:OpencodeAuthDestPath"];

        services.AddSingleton(sp => new ClaudeCredentialFileSource(
            claudeOAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new CodexCredentialFileSource(
            codexOAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new GeminiOAuthCredentialFileSource(
            geminiOAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new GeminiSettingsCredentialFileSource(
            geminiSettingsFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new CursorCredentialFileSource(
            cursorAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new DevinCredentialFileSource(
            devinAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new OpencodeCredentialFileSource(
            opencodeAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));
        services.AddSingleton(sp => new AntigravityCredentialFileSource(
            antigravityOAuthFilePath,
            sp.GetService<ILogger<CredentialFileSource>>(),
            watch: CredentialFileWatchersEnabled(configuration)));

        services.AddSingleton<ClaudeTokenRotationPusher>(sp => new ClaudeTokenRotationPusher(
            sp.GetRequiredService<ClaudeCredentialFileSource>(),
            sp.GetService<ILogger<ClaudeTokenRotationPusher>>()));
        services.AddSingleton<IClaudeTokenRotationPusher>(sp =>
            sp.GetRequiredService<ClaudeTokenRotationPusher>());

        services.AddSingleton<ChainedCredentialProvider>(sp =>
        {
            var builtInFirst = new List<ICredentialProvider>();
            var namedPlugins = new List<(string Id, ICredentialProvider Provider)>();
            var builtInLast = new List<ICredentialProvider>();

            if (claudeOAuthProviderConfigured)
            {
                builtInFirst.Add(new ClaudeOAuthFileCredentialProvider(
                    sp.GetRequiredService<ClaudeCredentialFileSource>(),
                    sandboxEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
                    sp.GetService<ILogger<ClaudeOAuthFileCredentialProvider>>()));
            }

            builtInFirst.Add(new CodexAuthJsonEnvironmentCredentialProvider(
                sp.GetService<ILogger<CodexAuthJsonEnvironmentCredentialProvider>>()));
            builtInFirst.Add(new CodexOAuthFileCredentialProvider(
                sp.GetRequiredService<CodexCredentialFileSource>(),
                sp.GetService<ILogger<CodexOAuthFileCredentialProvider>>()));
            builtInFirst.Add(new GeminiOAuthFileCredentialProvider(
                sp.GetRequiredService<GeminiOAuthCredentialFileSource>(),
                sp.GetRequiredService<GeminiSettingsCredentialFileSource>(),
                sp.GetService<ILogger<GeminiOAuthFileCredentialProvider>>()));
            builtInFirst.Add(new CursorOAuthFileCredentialProvider(
                sp.GetRequiredService<CursorCredentialFileSource>(),
                sp.GetService<ILogger<CursorOAuthFileCredentialProvider>>()));
            builtInFirst.Add(new DevinCliCredentialsFileCredentialProvider(
                sp.GetRequiredService<DevinCredentialFileSource>(),
                sp.GetService<ILogger<DevinCliCredentialsFileCredentialProvider>>()));
            builtInFirst.Add(new OpencodeOAuthFileCredentialProvider(
                sp.GetRequiredService<OpencodeCredentialFileSource>(),
                destinationPath: opencodeAuthDestPath,
                sp.GetService<ILogger<OpencodeOAuthFileCredentialProvider>>()));

            var credentialProviderType = typeof(ICredentialProvider);
            foreach (var plugin in options.PluginsProvider() ?? [])
            {
                foreach (var type in plugin.RegisteredTypes)
                {
                    if (credentialProviderType.IsAssignableFrom(type))
                        namedPlugins.Add((plugin.PluginId, (ICredentialProvider)sp.GetRequiredService(type)));
                }
            }

            builtInLast.Add(new ClaudeEnvironmentCredentialProvider());
            builtInLast.Add(new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Copilot, "CODEYBOX_COPILOT_TOKEN", "GH_TOKEN"),
                new AgentCredentialMapping(
                    AgentKind.Copilot,
                    "CODEYBOX_COPILOT_PROVIDER_API_KEY",
                    CopilotAgentRunner.ProviderApiKeyEnvironmentVariable),
                new AgentCredentialMapping(
                    AgentKind.Copilot,
                    "CODEYBOX_COPILOT_PROVIDER_BEARER_TOKEN",
                    CopilotAgentRunner.ProviderBearerTokenEnvironmentVariable),
                new AgentCredentialMapping(AgentKind.Codex, "CODEYBOX_CODEX_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "CODEYBOX_CAVEMAN_ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "CODEYBOX_CAVEMAN_OPENAI_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "CODEYBOX_CAVEMAN_GEMINI_API_KEY", "GEMINI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "CODEYBOX_CAVEMAN_OPENROUTER_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Cursor, "CODEYBOX_CURSOR_AUTH_JSON", "CODEYBOX_CURSOR_AUTH_JSON"),
                new AgentCredentialMapping(AgentKind.Devin, "CODEYBOX_DEVIN_AUTH_TOML", "CODEYBOX_DEVIN_AUTH_TOML"),
                new AgentCredentialMapping(AgentKind.Pi, "CODEYBOX_PI_API_KEY", "ANTHROPIC_API_KEY"),
                new AgentCredentialMapping(AgentKind.Aider, "CODEYBOX_AIDER_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Goose, "CODEYBOX_GOOSE_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Vibe, "CODEYBOX_VIBE_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Prime, "CODEYBOX_PRIME_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Autohand, "CODEYBOX_AUTOHAND_API_KEY", "AUTOHAND_API_KEY"),
                new AgentCredentialMapping(AgentKind.Cline, "CODEYBOX_CLINE_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Kilo, "CODEYBOX_KILO_API_KEY", "KILO_API_KEY"),
                new AgentCredentialMapping(AgentKind.Omp, "CODEYBOX_OMP_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Continue, "CODEYBOX_CONTINUE_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Qwen, "CODEYBOX_QWEN_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.Qwen, "CODEYBOX_QWEN_BASE_URL", "OPENAI_BASE_URL"),
                new AgentCredentialMapping(AgentKind.Qwen, "CODEYBOX_QWEN_MODEL", "OPENAI_MODEL"),
                new AgentCredentialMapping(AgentKind.Cmd, "CODEYBOX_CMD_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Crush, "CODEYBOX_CRUSH_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.DotNetOpencode, "CODEYBOX_DOTNETOPENCODE_CONFIG_JSON", "DOTNETOPENCODE_CONFIG_JSON"),
                new AgentCredentialMapping(AgentKind.Unreal, "CODEYBOX_UNREAL_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Unreal, "CODEYBOX_UNREAL_PROVIDER", "UNREAL_HARNESS_LLM_PROVIDER"),
                new AgentCredentialMapping(AgentKind.Unreal, "CODEYBOX_UNREAL_OPENAI_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.Unreal, "CODEYBOX_UNREAL_FIREWORKS_API_KEY", "FIREWORKS_API_KEY"),
            }));
            builtInLast.Add(new AntigravityEnvironmentCredentialProvider(
                sp.GetService<ILogger<AntigravityEnvironmentCredentialProvider>>()));
            builtInLast.Add(new CrockEnvironmentCredentialProvider(
                sandboxOptions: () => sp.GetRequiredService<IOptionsMonitor<CrockSandboxOptions>>().CurrentValue,
                sp.GetService<ILogger<CrockEnvironmentCredentialProvider>>()));
            builtInLast.Add(new EnvironmentCredentialProvider(new[]
            {
                new AgentCredentialMapping(AgentKind.Codex, "OPENAI_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "OPENAI_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "GEMINI_API_KEY", "GEMINI_API_KEY"),
                new AgentCredentialMapping(AgentKind.CavemanCode, "OPENROUTER_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Unreal, "OPENROUTER_API_KEY", "OPENROUTER_API_KEY"),
                new AgentCredentialMapping(AgentKind.Unreal, "OPENAI_API_KEY", "OPENAI_API_KEY"),
                new AgentCredentialMapping(AgentKind.Unreal, "FIREWORKS_API_KEY", "FIREWORKS_API_KEY"),
            }));

            return new ChainedCredentialProvider(
                builtInFirst,
                namedPlugins,
                builtInLast,
                log: sp.GetService<ILogger<ChainedCredentialProvider>>());
        });
        services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<ChainedCredentialProvider>());
        services.AddSingleton<IProjectAwareCredentialProvider>(sp => sp.GetRequiredService<ChainedCredentialProvider>());
    }

    // Same kill-switch the API host used (env CODEYBOX_CREDENTIAL_FILE_WATCHERS
    // or CodeyBox:CredentialFileWatchers, default on): file watching needs an
    // inotify-capable fs, which some executor mounts lack.
    private static bool CredentialFileWatchersEnabled(IConfiguration configuration)
    {
        var raw = Environment.GetEnvironmentVariable("CODEYBOX_CREDENTIAL_FILE_WATCHERS")
            ?? configuration["CodeyBox:CredentialFileWatchers"];
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        var value = raw.Trim();
        if (bool.TryParse(value, out var enabled))
            return enabled;
        return value switch
        {
            "0" => false,
            "1" => true,
            var v when v.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ => throw new InvalidOperationException(
                "CODEYBOX_CREDENTIAL_FILE_WATCHERS or CodeyBox:CredentialFileWatchers must be a boolean-like value: true/false, 1/0, yes/no, or on/off."),
        };
    }

    private static string ResolveCredentialPath(
        IConfiguration configuration,
        string envVar,
        string configKey,
        string defaultDirectory,
        string fileName)
    {
        var configured =
            Environment.GetEnvironmentVariable(envVar) ?? configuration[configKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            var baseDir = defaultDirectory.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    defaultDirectory[2..])
                : defaultDirectory.StartsWith("/", StringComparison.Ordinal)
                    ? defaultDirectory
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        defaultDirectory);
            return Path.Combine(baseDir, fileName);
        }
        var path = configured.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return path;
    }

    // Every agent runner both hosts serve. Copilot's BYOK key travels through
    // the credential chain (never config); Antigravity's print budget and the
    // per-provider option accessors below stay hot-reloadable.
    private static void AddAgentRunners(
        IServiceCollection services,
        AgentExecutionCompositionOptions options)
    {
        services.AddSingleton<IAgentRunner>(sp => new ClaudeAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            sp.GetRequiredService<IClaudeTokenRotationPusher>(),
            sp.GetRequiredService<ClaudeThinkingBlockSanitizerConfig>(),
            sp.GetRequiredService<AgentNetworkToleranceSnapshot>(),
            sp.GetRequiredService<IQuotaFailureClassifier>()));
        services.AddSingleton<IAgentRunner>(sp => new CopilotAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>())
        {
            Options = sp.GetRequiredService<IOptionsMonitor<CopilotOptions>>().CurrentValue,
        });
        services.AddSingleton<IAgentRunner>(sp => new CodexAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            sp.GetRequiredService<AgentNetworkToleranceSnapshot>(),
            sp.GetRequiredService<IQuotaFailureClassifier>()));
        services.AddSingleton<IAgentRunner>(sp => new GeminiAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner, CursorAgentRunner>();
        services.AddSingleton<IAgentRunner>(sp => new DevinAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner, OpencodeAgentRunner>();
        services.AddSingleton<IAgentRunner>(sp => new CavemanCodeAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new AntigravityAgentRunner
        {
            PrintTimeout = TimeSpan.FromMinutes(
                sp.GetRequiredService<IOptionsMonitor<AntigravitySectionOptions>>().CurrentValue.PrintTimeoutMinutes),
        });
        services.AddSingleton<IAgentRunner>(sp => new CrockAgentRunner
        {
            SandboxOptions = () => sp.GetRequiredService<IOptionsMonitor<CrockSandboxOptions>>().CurrentValue,
        });
        services.AddSingleton<IAgentRunner>(sp => new PiAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new AiderAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new GooseAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<GooseOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new VibeAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<VibeOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new PrimeAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>())
        {
            PrimeOptions = () => sp.GetRequiredService<IOptionsMonitor<PrimeSectionOptions>>().CurrentValue,
        });
        services.AddSingleton<IAgentRunner>(sp => new AutohandAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<AutohandOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new ClineAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<ClineOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new KiloAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<KiloOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new OmpAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new ContinueAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>(),
            () => sp.GetRequiredService<IOptionsMonitor<ContinueOptions>>().CurrentValue));
        services.AddSingleton<IAgentRunner>(sp => new QwenAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new CmdAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new CrushAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new DotNetOpencodeAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        services.AddSingleton<IAgentRunner>(sp => new UnrealAgentRunner(
            sp.GetRequiredService<AgentDefaultsSnapshot>()));
        foreach (var factory in options.AdditionalRunners)
            services.AddSingleton<IAgentRunner>(factory);
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
    }

    // Stream capture both hosts write: the file store (hot-reloadable caps),
    // the parser set that attributes stream lines to agents, and the
    // per-provider tool-call counters for runs without structured capture.
    private static void AddStreamCapture(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentStreamsOptions>>().Value;
            AgentStreamsOptions.ValidateAtStartup(
                opts,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentStreams"));
            return opts;
        });
        services.AddSingleton<IAgentStreamStore>(sp =>
        {
            _ = sp.GetRequiredService<AgentStreamsOptions>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<AgentStreamsOptions>>();
            return new AgentStreamStore(
                () => monitor.CurrentValue,
                sp.GetRequiredService<ILogger<AgentStreamStore>>());
        });
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentStreamParserOptions>>().Value;
            if (opts.StallThreshold < TimeSpan.Zero)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:StallThreshold must be non-negative");
            if (opts.MaxLineBytes < 1024)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxLineBytes must be >= 1024");
            if (opts.MaxJsonDepth < 1)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxJsonDepth must be >= 1");
            if (opts.MaxEvents < 1)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxEvents must be >= 1");
            if (opts.MaxToolCalls < 0)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxToolCalls must be non-negative");
            if (opts.MaxStalls < 0)
                throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxStalls must be non-negative");
            return opts;
        });
        services.AddSingleton<IAgentStreamParser, AntigravityStreamParser>();
        services.AddSingleton<IAgentStreamParser, ClaudeStreamParser>();
        services.AddSingleton<IAgentStreamParser, CodexStreamParser>();
        services.AddSingleton<IAgentStreamParser, CopilotStreamParser>();
        services.AddSingleton<IAgentStreamParser, CursorStreamParser>();
        services.AddSingleton<IAgentStreamParser, DevinStreamParser>();
        services.AddSingleton<IAgentStreamParser, GeminiStreamParser>();
        services.AddSingleton<IAgentStreamParser, OpencodeStreamParser>();
        services.AddSingleton<IAgentStreamParser, PiStreamParser>();
        services.AddSingleton<IAgentStreamParser, PrimeStreamParser>();
        services.AddSingleton<IAgentStreamParser, AiderStreamParser>();
        services.AddSingleton<IAgentStreamParser, GooseStreamParser>();
        services.AddSingleton<IAgentStreamParser, VibeStreamParser>();
        services.AddSingleton<IAgentStreamParser, DotNetOpencodeStreamParser>();
        services.AddSingleton<IAgentStreamParser, CavemanCodeStreamParser>();
        services.AddSingleton<IAgentStreamParser, AutohandStreamParser>();
        services.AddSingleton<IAgentStreamParser, ClineStreamParser>();
        services.AddSingleton<IAgentStreamParser, KiloStreamParser>();
        services.AddSingleton<IAgentStreamParser, OmpStreamParser>();
        services.AddSingleton<IAgentStreamParser, ContinueStreamParser>();
        services.AddSingleton<IAgentStreamParser, QwenStreamParser>();
        services.AddSingleton<IAgentStreamParser, CmdStreamParser>();
        services.AddSingleton<IAgentStreamParser, CrushStreamParser>();
        services.AddSingleton<IAgentStreamParser, UnrealStreamParser>();
        services.AddSingleton<IAgentStreamParser, UnknownAgentStreamParser>();
        services.AddSingleton<IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>>(sp =>
            new Dictionary<AgentKind, IAgentToolCallCounter>
            {
                [AgentKind.Claude] = new ClaudeToolCallCounter(),
            });
    }
}
