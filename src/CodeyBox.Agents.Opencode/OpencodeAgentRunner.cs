using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Drives the sst/opencode CLI (<c>opencode run</c>) in non-interactive mode.
///
/// <para>opencode bundles access to multiple model providers (DeepSeek,
/// Anthropic, OpenAI, …) under a single subscription credential — the
/// "opencode Go" tier. The default model picked here is intentionally a
/// DeepSeek variant because that is the differentiating capability opencode
/// adds versus the other registered agents; operators override <c>ModelId</c>
/// per agent-class member to route through any other provider the
/// subscription supports.</para>
///
/// <para>Auth: opencode hard-reads a credentials file written by
/// <c>opencode auth login</c>. Path is set per-deployment via
/// <c>CODEYBOX_OPENCODE_AUTH_FILE</c>; <see cref="OpencodeAgentRunner"/>
/// materialises the file from <c>OPENCODE_AUTH_JSON</c> in the credential
/// bundle before invoking the CLI, mirroring the Codex pattern.</para>
/// </summary>
public sealed class OpencodeAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private const string AuthJsonEnvironmentVariable = "OPENCODE_AUTH_JSON";
    private const string AuthDestinationEnvironmentVariable = "OPENCODE_AUTH_DEST_PATH";
    private static readonly EnvBackedCredentialFile AuthCredentialFile = new(
        AuthJsonEnvironmentVariable,
        ".local/share/opencode/auth.json",
        "opencode auth",
        DestinationEnvironmentVariable: AuthDestinationEnvironmentVariable);
    private readonly AgentDefaultsSnapshot? _defaults;

    public OpencodeAgentRunner() : this(defaults: null) { }

    public OpencodeAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Opencode;

    /// <summary>
    /// Default opencode CLI binary name inside the sandbox. Shared with
    /// <c>OpencodeInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "opencode";

    /// <summary>
    /// Bash that materialises opencode's credentials file from
    /// <c>OPENCODE_AUTH_JSON</c> into the XDG-default location (overridable via
    /// <c>OPENCODE_AUTH_DEST_PATH</c>). Shared verbatim with
    /// <c>OpencodeInVmSmokeProbe</c> so the env-reading smoke/create-time path
    /// writes to the same destination as a real dispatch's credential-stdin
    /// materialisation path.
    /// </summary>
    public static readonly string AuthMaterialiseScript = BuildEnvBackedCredentialScript(AuthCredentialFile);

    /// <summary>Path to the opencode binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".local/share/opencode", ".config/opencode"];

    protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles => [AuthCredentialFile];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Builds the <c>opencode run</c> argv. The <paramref name="captureStructuredStream"/>
    /// parameter is currently discarded — opencode's structured stream
    /// format has not been verified against a live invocation in this
    /// environment, so the runner does not implement
    /// <see cref="IStructuredStreamAgentRunner"/>. If you flip a caller to
    /// request structured stream capture, expect plain stdout/stderr back
    /// rather than parsed events.
    /// </summary>
    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `opencode run <prompt>` is the documented non-interactive entry
        // point. Pass the prompt via stdin (matches the Codex / Gemini
        // pattern) to dodge the 128 KiB MAX_ARG_STRLEN ceiling that rework
        // prompts can blow through.
        var argv = new List<string> { Binary, "run" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort: DeepSeek-R1 / Anthropic via opencode both have a
        // reasoning knob, but the exact CLI flag has not been verified
        // against `opencode run --help` in this environment. Operators that
        // need it can set the OPENCODE_REASONING_FLAG env var on the host
        // to the correct flag name (e.g. "--reasoning-effort"); when set we
        // append it followed by the requested mode. Without verification we
        // do NOT speculate on the flag name. See docs/agents.md.
        if (!string.IsNullOrEmpty(reasoningMode))
        {
            var flag = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
            if (!string.IsNullOrEmpty(flag))
            {
                argv.Add(flag);
                argv.Add(reasoningMode);
            }
        }

        _ = captureStructuredStream;
        return new AgentInvocation(argv, Stdin: prompt);
    }

    protected override AgentInvocation BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildInvocation(prompt, credential, modelId, reasoningMode, captureStructuredStream: false);

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
        => GetSandboxSubscriptionTextOnlyUnavailabilityReason(
            credential,
            "OPENCODE_AUTH_JSON");

    // The opencode CLI runs inside the work-item sandbox; a host-side text-only
    // call with no sandbox returns failure (see RunTextOnlyAsync below).
    public bool TextOnlyRequiresSandbox => true;

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        if (sandbox is null || workingDirectory is null)
            return RunTextOnlyRequiresSandboxAsync(ct);

        return ExecuteTextOnlyInSandboxAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct);
    }
}
