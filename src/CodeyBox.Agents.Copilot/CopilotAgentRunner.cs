using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Drives the GitHub Copilot CLI.
///
/// <para><b>Two auth modes.</b> By default Copilot uses the GitHub OAuth token from
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> and GitHub's own model routing; the orchestrator must inject ONLY
/// a least-privilege token (or a Copilot-only token if the org allows it). Setting
/// <c>CodeyBox:Copilot:Provider:BaseUrl</c> switches it to BYOK, where inference goes to an
/// OpenAI-compatible endpoint of the operator's choosing and no GitHub account is involved for
/// inference at all. The BYOK credential itself arrives as an environment variable
/// (<see cref="ProviderApiKeyEnvironmentVariable"/>), never from config.</para>
///
/// <para><b>Prompt size.</b> Copilot takes its prompt as an argv element (<c>-p &lt;text&gt;</c>) and
/// offers no stdin prompt mode — verified against v1.0.82, where both <c>-p -</c> and a bare invocation
/// with the prompt on stdin ignore stdin. Unlike the agy/codex/gemini runners, this one therefore
/// cannot dodge Linux's 128 KiB MAX_ARG_STRLEN: a rework prompt carrying very many audit findings can
/// exceed it and surface as exit 126 from the sandbox wrapper's exec. There is no CLI affordance to work
/// around this today.</para>
/// </summary>
public sealed class CopilotAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public CopilotAgentRunner() : this(defaults: null) { }

    public CopilotAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Copilot;

    /// <summary>Default copilot binary name on the sandbox PATH. The in-VM smoke probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "copilot";

    /// <summary>Sandbox environment variable carrying the BYOK API key, read by Copilot itself.</summary>
    public const string ProviderApiKeyEnvironmentVariable = "COPILOT_PROVIDER_API_KEY";

    /// <summary>Sandbox environment variable carrying a BYOK bearer token. Copilot prefers this over
    /// <see cref="ProviderApiKeyEnvironmentVariable"/>, so supplying both is ambiguous — set one.</summary>
    public const string ProviderBearerTokenEnvironmentVariable = "COPILOT_PROVIDER_BEARER_TOKEN";

    /// <summary>Placeholder an operator may embed in a configured provider header value to request a
    /// fresh session identifier per agent invocation, e.g.
    /// <c>x-opencode-session: {{codeybox.session_id}}</c>. Matched with exact ordinal comparison;
    /// anything else passes through byte-identical, so purely static headers are unaffected. Keeping
    /// this a placeholder inside the existing string list (rather than a new schema member) means the
    /// static form keeps working unchanged and stays hot-reloadable with no config migration.</summary>
    public const string ProviderSessionIdPlaceholder = "{{codeybox.session_id}}";

    /// <summary>Non-secret sandbox environment variable carrying the session identifier generated for
    /// the current invocation, emitted only when <see cref="ProviderSessionIdPlaceholder"/> was
    /// present. It lets a failing run be correlated with the session id it used; the id is random, so
    /// unlike <c>COPILOT_PROVIDER_HEADERS</c> (which may carry secrets) this variable is safe to log.
    /// Never log <c>COPILOT_PROVIDER_HEADERS</c> itself.</summary>
    public const string ProviderSessionIdEnvironmentVariable = "CODEYBOX_COPILOT_SESSION_ID";

    /// <summary>Excluded tools applied when BYOK is on and the operator has expressed no preference.
    /// See <see cref="CopilotOptions.ExcludedTools"/> for why.</summary>
    public static readonly IReadOnlyList<string> DefaultByokExcludedTools = ["apply_patch"];

    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is
    /// provided. Sourced live from <see cref="AgentDefaultsSnapshot"/> so
    /// operator edits take effect on the next dispatched run without restart.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>Operator configuration. Defaults to subscription mode with no BYOK provider.</summary>
    public CopilotOptions Options { get; init; } = new();

    /// <summary>Generates the session identifier substituted for
    /// <see cref="ProviderSessionIdPlaceholder"/>. Defaults to a fresh random UUID per call and is an
    /// instance member so tests can inject a deterministic generator; the runner invokes it at most
    /// once per invocation, and only when a configured header actually contains the placeholder.
    /// Injected randomness, not ambient <c>Guid.NewGuid</c> reads inside the pure mapping core.</summary>
    public Func<string> SessionIdGenerator { get; init; } = NewProviderSessionId;

    /// <summary>Generates one fresh session identifier in the UUID form providers expect.</summary>
    public static string NewProviderSessionId() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Every environment variable the Copilot CLI reads a credential from. GH_TOKEN/GITHUB_TOKEN drive
    /// subscription mode; the provider key/bearer drive BYOK. All four must be declared or
    /// <c>SandboxEnvironmentVariablePolicy</c> rejects them as unclassified.
    /// </summary>
    public static readonly IReadOnlyList<string> CredentialEnvironmentVariables =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        ProviderApiKeyEnvironmentVariable,
        ProviderBearerTokenEnvironmentVariable,
    ];

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables
        => CredentialEnvironmentVariables;

    /// <summary>
    /// Renders BYOK settings to the environment variables Copilot reads. Empty when no base URL is
    /// configured: BYOK is inactive until <c>COPILOT_PROVIDER_BASE_URL</c> is set, and emitting the rest
    /// without it would be noise the CLI ignores. Pure, so the mapping is unit-testable without
    /// launching anything. Header values containing
    /// <see cref="ProviderSessionIdPlaceholder"/> are resolved with a freshly generated identifier;
    /// pass an explicit generator to the overload for deterministic tests.
    /// </summary>
    /// <remarks>
    /// The credential (API key / bearer token) is deliberately absent: it reaches the CLI through the
    /// credential provider's environment injection, so it is never assembled from config here and never
    /// passes through this method's output.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> BuildProviderEnvironment(CopilotOptions options)
        => BuildProviderEnvironment(options, NewProviderSessionId);

    /// <summary>
    /// Renders BYOK settings with per-invocation header resolution. Any configured header value
    /// containing <see cref="ProviderSessionIdPlaceholder"/> gets the placeholder replaced with a
    /// single freshly generated identifier (one generator call per invocation, shared by every
    /// occurrence so the whole invocation correlates to one provider session), and the identifier is
    /// additionally exported as <see cref="ProviderSessionIdEnvironmentVariable"/> for diagnosis.
    /// Headers without the placeholder are emitted byte-identical to the static form, and the
    /// generator is never invoked when no header needs it.
    /// </summary>
    /// <remarks>
    /// The credential (API key / bearer token) is deliberately absent: it reaches the CLI through the
    /// credential provider's environment injection, so it is never assembled from config here and never
    /// passes through this method's output. Likewise, never log the rendered
    /// <c>COPILOT_PROVIDER_HEADERS</c> value — it may carry secrets — while the exported session id
    /// is random and safe to log.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> BuildProviderEnvironment(
        CopilotOptions options,
        Func<string>? sessionIdGenerator)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var provider = options.Provider;
        if (!provider.IsConfigured || provider.BaseUrl is not { } baseUrl)
            return env;

        env["COPILOT_PROVIDER_BASE_URL"] = baseUrl;
        env["COPILOT_PROVIDER_TYPE"] = NormaliseChoice(provider.Type, "openai", "azure", "anthropic");
        env["COPILOT_PROVIDER_WIRE_API"] = NormaliseChoice(provider.WireApi, "completions", "responses");
        env["COPILOT_PROVIDER_TRANSPORT"] = NormaliseChoice(provider.Transport, "http", "websockets");

        Set("COPILOT_PROVIDER_AZURE_API_VERSION", provider.AzureApiVersion);
        Set("COPILOT_PROVIDER_MAX_PROMPT_TOKENS", Format(provider.MaxPromptTokens));
        Set("COPILOT_PROVIDER_MAX_OUTPUT_TOKENS", Format(provider.MaxOutputTokens));

        // Copilot parses these as newline-separated "Name: Value" pairs.
        var headers = provider.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToArray();
        if (headers.Length > 0)
        {
            string? sessionId = null;
            if (headers.Any(h => h.Contains(ProviderSessionIdPlaceholder, StringComparison.Ordinal)))
            {
                sessionId = (sessionIdGenerator ?? NewProviderSessionId)();
                ValidateSessionId(sessionId);
                var resolved = sessionId;
                headers = headers
                    .Select(h => h.Replace(ProviderSessionIdPlaceholder, resolved, StringComparison.Ordinal))
                    .ToArray();
            }
            env["COPILOT_PROVIDER_HEADERS"] = string.Join('\n', headers);
            if (sessionId is not null)
                env[ProviderSessionIdEnvironmentVariable] = sessionId;
        }

        // Copilot requires a provider for offline mode, so the flag is honoured only alongside one.
        if (options.Offline)
            env["COPILOT_OFFLINE"] = "true";

        return env;

        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                env[key] = value;
        }

        static string? Format(int? value) =>
            value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Guards the generated session id at the sink: it is spliced into newline-separated header
    /// lines, so a value carrying newlines, carriage returns or NUL would corrupt the framing
    /// Copilot parses (or smuggle an extra header). Random UUIDs always pass; a misbehaving
    /// injected generator fails loudly instead of emitting corrupt headers.
    /// </summary>
    private static void ValidateSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Any(ch => ch is '\n' or '\r' or '\0'))
            throw new ArgumentException(
                "Provider session id generator must return a non-blank value without newline or NUL characters.",
                nameof(sessionId));
    }

    /// <summary>
    /// Case-insensitively resolves an operator-supplied choice to one of the CLI's accepted literals,
    /// falling back to the first (the CLI's own default) rather than forwarding an unrecognised value
    /// that Copilot would reject at launch.
    /// </summary>
    private static string NormaliseChoice(string? value, params string[] accepted)
    {
        foreach (var candidate in accepted)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return accepted[0];
    }

    /// <summary>
    /// The tools to withhold: the operator's list when they set one (including an explicit empty list,
    /// which opts out), otherwise the BYOK default, otherwise nothing.
    /// </summary>
    internal static IReadOnlyList<string> ResolveExcludedTools(CopilotOptions options)
    {
        if (options.ExcludedTools is { } configured)
            return configured.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray();

        return options.Provider.IsConfigured ? DefaultByokExcludedTools : [];
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // --allow-all-tools is REQUIRED for non-interactive mode: without it Copilot waits on a
        // permission prompt that nothing will answer. --allow-all-paths is added because every
        // CodeyBox run is sandboxed, where Copilot's own path check guards nothing the VM does not
        // while still interrupting constantly (it keeps session state outside the working directory).
        // --allow-all-urls is deliberately NOT passed: network egress is a different boundary, and the
        // sandbox's network profile is what governs it.
        var argv = new List<string>
        {
            Binary,
            "-p",
            prompt,
            "--allow-all-tools",
            "--allow-all-paths",
        };

        // --model selects the model actually sent on the wire, including under BYOK, where it must name
        // a model the configured endpoint serves. Verified against v1.0.82: the COPILOT_MODEL /
        // COPILOT_PROVIDER_WIRE_MODEL environment variables do NOT change the wire model in -p mode,
        // so the id travels here and only here. Falls back to the config-sourced default
        // (CodeyBox:AgentDefaults:copilot) when the caller passes no explicit model, mirroring the
        // peer runners. Under BYOK there is no usable built-in default, so a missing id fails here
        // with a diagnosable message instead of invoking the CLI to surface its raw
        // "BYOK providers require an explicit model" exit.
        var effectiveModel = modelId ?? DefaultModelId;
        if (Options.Provider.IsConfigured && string.IsNullOrWhiteSpace(effectiveModel))
            throw new InvalidOperationException(
                "Copilot BYOK provider is configured (CodeyBox:Copilot:Provider:BaseUrl) but no model id is "
                + "available: no explicit modelId was supplied and CodeyBox:AgentDefaults:copilot has no default. "
                + "Set CodeyBox:AgentDefaults:copilot or supply an explicit modelId; BYOK providers require an explicit model.");
        if (!string.IsNullOrWhiteSpace(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Repeatable flag: Copilot takes one tool per occurrence rather than a list.
        foreach (var tool in ResolveExcludedTools(Options))
        {
            argv.Add("--excluded-tools");
            argv.Add(tool);
        }

        // Reasoning level is not a Copilot flag: it derives reasoning_effort from the model id, so the
        // choice is expressed by picking the model. Informational only on this runner.
        _ = reasoningMode;
        _ = captureStructuredStream;

        var env = BuildProviderEnvironment(Options, SessionIdGenerator);
        return new AgentInvocation(argv, ExtraEnvironment: env.Count == 0 ? null : env);
    }
}
