using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Drives the <c>caveman-code</c> CLI
/// (<c>github.com/JuliusBrussee/caveman-code</c>, npm
/// <c>@juliusbrussee/caveman-code</c>, MIT) in non-interactive print mode.
///
/// <para>Invocation is <c>caveman-code -p</c> with the prompt on stdin —
/// verified against 0.65.2: a piped prompt with no positional message
/// reaches agent init (it fails at auth, not arg parsing), which dodges the
/// 128 KiB MAX_ARG_STRLEN ceiling rework prompts can blow through, mirroring
/// the Gemini runner. <c>--mode json</c> is deliberately NOT used: print-mode
/// JSON emits the CLI's internal (unfrozen) session events, while the frozen
/// <c>exec --json</c> stream cannot take the prompt on stdin (the exec
/// subcommand is dispatched before stdin is read, so large prompts would have
/// to ride argv). Stdout stays the final assistant text, exactly like the
/// opencode runner.</para>
///
/// <para>Auth is BYOK API keys only: the CLI reads provider keys from the
/// environment (<c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>,
/// <c>GEMINI_API_KEY</c>, … — full list in
/// <see cref="CredentialEnvironmentVariables"/>). OAuth (<c>/login</c>)
/// stores tokens in the OS keychain, which has no headless path, so it is
/// not supported here. Keys arrive via the sandbox environment (see
/// <see cref="DirectCredentialEnvironmentVariables"/>) — the runner never
/// passes <c>--api-key</c> because secrets must not ride argv.</para>
///
/// <para>Status caveat: upstream froze this repo in August 2026 (active work
/// moved to the <c>caveman wrap</c> successor, whose <c>caveman</c> binary
/// shadows this package's primary alias). The runner invokes the unambiguous
/// <c>caveman-code</c> alias so both packages can coexist on a host. Default
/// model <c>openai/gpt-5.5</c> is the configuration the upstream 25-task
/// MicroBench measured (1.93x fewer tokens than Codex CLI, 14/25 vs 15/25
/// passes, gpt-5.5 xhigh).</para>
/// </summary>
public sealed class CavemanCodeAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider
{
    /// <summary>
    /// Default CLI binary inside the sandbox. The npm package installs two
    /// aliases (<c>caveman</c> primary, <c>caveman-code</c>); this runner uses
    /// the long alias because the successor <c>caveman wrap</c> package
    /// installs a colliding <c>caveman</c> binary (upstream README warning).
    /// Shared with <see cref="CavemanCodeInVmSmokeProbe"/> so the smoke check
    /// and the real runner always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "caveman-code";

    /// <summary>
    /// Provider API-key variables the CLI reads from its environment,
    /// verified against <c>caveman-code --help</c> (0.65.2). Endpoint-style
    /// providers (Azure OpenAI, AWS Bedrock) need companion config beyond a
    /// bare key and are intentionally not covered — see agent-quirks.
    /// </summary>
    public static readonly IReadOnlyList<string> CredentialEnvironmentVariables =
    [
        "ANTHROPIC_API_KEY",
        "OPENAI_API_KEY",
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GROQ_API_KEY",
        "CEREBRAS_API_KEY",
        "XAI_API_KEY",
        "OPENROUTER_API_KEY",
        "MISTRAL_API_KEY",
        "MINIMAX_API_KEY",
        "KIMI_API_KEY",
        "ZAI_API_KEY",
        "DEEPSEEK_API_KEY",
        "OPENCODE_API_KEY",
        "AI_GATEWAY_API_KEY",
    ];

    /// <summary>
    /// Thinking levels accepted by <c>--thinking</c>, verified against
    /// <c>caveman-code --help</c> (0.65.2). A <c>ReasoningMode</c> outside
    /// this set is dropped rather than forwarded: the CLI degrades an
    /// unknown level to a startup warning, and a misspelled routing knob
    /// must not change dispatch behaviour.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidThinkingLevels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "off", "minimal", "low", "medium", "high", "xhigh",
        };

    private readonly AgentDefaultsSnapshot? _defaults;

    public CavemanCodeAgentRunner() : this(defaults: null) { }

    public CavemanCodeAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.CavemanCode;

    /// <summary>Path to the CLI binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".cave"];

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables =>
        CredentialEnvironmentVariables.ToArray();

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Builds the <c>caveman-code -p</c> argv. The
    /// <paramref name="captureStructuredStream"/> parameter is currently
    /// discarded — print-mode <c>--mode json</c> emits the CLI's internal
    /// unfrozen session events, and the frozen <c>exec --json</c> stream
    /// cannot take the prompt on stdin. The runner does not implement
    /// <see cref="IStructuredStreamAgentRunner"/>; callers requesting
    /// structured capture get plain stdout/stderr back.
    /// </summary>
    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `caveman-code -p` reads the prompt appended to stdin (verified:
        // `echo ... | caveman-code -p` with no positional reaches auth init).
        // Stdin keeps large rework prompts under MAX_ARG_STRLEN.
        var argv = new List<string> { Binary, "-p" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort maps 1:1 onto --thinking; the valid set is
        // verified against --help so only known levels are forwarded.
        if (!string.IsNullOrEmpty(reasoningMode)
            && ValidThinkingLevels.Contains(reasoningMode))
        {
            argv.Add("--thinking");
            argv.Add(reasoningMode.ToLowerInvariant());
        }

        _ = captureStructuredStream;
        return new AgentInvocation(argv, Stdin: prompt);
    }
}
