namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Operator configuration for the GitHub Copilot CLI runner. Bind from <c>CodeyBox:Copilot</c>.
///
/// <para>Everything here is non-secret. The BYOK credential itself (API key or bearer token) is
/// deliberately NOT a member: it arrives through the credential provider as an environment variable
/// like every other agent secret, so it never sits in a config file. See
/// <see cref="CopilotAgentRunner.ProviderApiKeyEnvironmentVariable"/>.</para>
/// </summary>
public sealed class CopilotOptions
{
    /// <summary>Bring-your-own-key provider. Inert until <see cref="CopilotProviderOptions.BaseUrl"/>
    /// is set, in which case Copilot infers against that endpoint instead of GitHub's model routing.</summary>
    public CopilotProviderOptions Provider { get; set; } = new();

    /// <summary>
    /// Runs Copilot with no GitHub access beyond the model provider (<c>COPILOT_OFFLINE</c>): no GitHub
    /// authentication, telemetry, web tools, GitHub MCP server or auto-update.
    ///
    /// <para>Copilot rejects offline mode without a provider — it could then neither authenticate nor
    /// infer — so the flag is emitted only when <see cref="Provider"/> is configured, rather than being
    /// passed through to fail at launch.</para>
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Tools withheld from the model (<c>--excluded-tools</c>, one flag per entry). Null means "use the
    /// default for the current mode"; an explicit empty list opts out of that default entirely.
    ///
    /// <para>The default under BYOK is <c>["apply_patch"]</c>. Copilot offers <c>apply_patch</c> as an
    /// OpenAI <b>custom tool with a Lark grammar</b> (<c>"type":"custom"</c> rather than
    /// <c>"type":"function"</c>), and a server implementing only function tools rejects the WHOLE tools
    /// array — verified against the operator's llama.cpp-backed endpoint, which answers
    /// <c>Failed to parse tools: Unsupported tool type</c> with HTTP 500, so no turn can start.
    /// Excluding it costs one editing path and is the difference between a local model working and not
    /// starting at all.</para>
    ///
    /// <para>Whether Copilot sends the custom tool at all depends on the <c>--model</c> id: an id it does
    /// not recognise (a local model's own name) suppresses both the custom tool and
    /// <c>reasoning_effort</c>, while a well-known id (<c>gpt-5.6-*</c>) sends both. The exclusion is
    /// therefore harmless in the first case and necessary in the second, which is why it defaults on for
    /// BYOK rather than being conditioned on the model id.</para>
    /// </summary>
    public IList<string>? ExcludedTools { get; set; }
}

/// <summary>
/// Copilot's "bring your own key" endpoint configuration. Copilot exposes this axis ONLY through the
/// environment — there are no argv flags for it — so it is modelled here and rendered to variables by
/// <see cref="CopilotAgentRunner.BuildProviderEnvironment"/>.
/// </summary>
public sealed class CopilotProviderOptions
{
    /// <summary>API endpoint URL, e.g. <c>http://host:13305/v1</c>. Required: BYOK is inactive until this
    /// is set and every other member here is ignored without it. Copilot appends
    /// <c>/chat/completions</c> to this value, so include the API version segment the server expects.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Provider dialect: <c>openai</c> (default; covers any OpenAI-compatible server such as
    /// Ollama, vLLM or llama.cpp), <c>azure</c>, or <c>anthropic</c>.</summary>
    public string Type { get; set; } = "openai";

    /// <summary>Which OpenAI API surface to call: <c>completions</c> (default) or <c>responses</c>.</summary>
    public string WireApi { get; set; } = "completions";

    /// <summary>Transport: <c>http</c> (default) or <c>websockets</c>, the latter only meaningful with
    /// <see cref="WireApi"/> = <c>responses</c>.</summary>
    public string Transport { get; set; } = "http";

    /// <summary>Azure API version. Null uses the GA versionless v1 route.</summary>
    public string? AzureApiVersion { get; set; }

    /// <summary>Extra HTTP headers sent only to the provider endpoint, as <c>Name: Value</c> entries.
    /// Joined with newlines, which is the separator Copilot parses.</summary>
    public IList<string> Headers { get; set; } = [];

    /// <summary>Prompt-token ceiling advertised to Copilot. Null leaves Copilot's own default.</summary>
    public int? MaxPromptTokens { get; set; }

    /// <summary>Output-token ceiling advertised to Copilot. Null leaves Copilot's own default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Whether BYOK is actually configured. Everything else is inert without a base URL.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
