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
public sealed class CopilotAgentRunner : CliAgentRunnerBase
{
    public override AgentKind Kind => AgentKind.Copilot;

    /// <summary>Default copilot binary name on the sandbox PATH. The in-VM smoke probe pins to this so the probe and runner can never drift.</summary>
    public const string DefaultBinary = "copilot";

    /// <summary>Sandbox environment variable carrying the BYOK API key, read by Copilot itself.</summary>
    public const string ProviderApiKeyEnvironmentVariable = "COPILOT_PROVIDER_API_KEY";

    /// <summary>Sandbox environment variable carrying a BYOK bearer token. Copilot prefers this over
    /// <see cref="ProviderApiKeyEnvironmentVariable"/>, so supplying both is ambiguous — set one.</summary>
    public const string ProviderBearerTokenEnvironmentVariable = "COPILOT_PROVIDER_BEARER_TOKEN";

    /// <summary>Excluded tools applied when BYOK is on and the operator has expressed no preference.
    /// See <see cref="CopilotOptions.ExcludedTools"/> for why.</summary>
    public static readonly IReadOnlyList<string> DefaultByokExcludedTools = ["apply_patch"];

    public string Binary { get; init; } = DefaultBinary;

    /// <summary>Operator configuration. Defaults to subscription mode with no BYOK provider.</summary>
    public CopilotOptions Options { get; init; } = new();

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
    /// launching anything.
    /// </summary>
    /// <remarks>
    /// The credential (API key / bearer token) is deliberately absent: it reaches the CLI through the
    /// credential provider's environment injection, so it is never assembled from config here and never
    /// passes through this method's output.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> BuildProviderEnvironment(CopilotOptions options)
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
            env["COPILOT_PROVIDER_HEADERS"] = string.Join('\n', headers);

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
        // so the id travels here and only here.
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
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

        var env = BuildProviderEnvironment(Options);
        return new AgentInvocation(argv, ExtraEnvironment: env.Count == 0 ? null : env);
    }
}
