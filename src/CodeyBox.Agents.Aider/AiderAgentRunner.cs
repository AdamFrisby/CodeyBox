using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Aider;

/// <summary>
/// Drives the Aider CLI (binary <c>aider</c>, Python <c>aider-chat</c>, Apache-2.0)
/// in non-interactive one-shot mode: <c>--message-file /dev/stdin</c> reads the
/// prompt from piped stdin, applies the model's reply to the working tree, and
/// exits — the exact contract CodeyBox needs for a throwaway-VM dispatch.
///
/// <para><b>Transport decision (verified against aider 0.86.2).</b>
/// <c>--message</c> / <c>--msg</c> / <c>-m</c> ("send one message, process the
/// reply, then exit — disables chat mode") is the headless one-shot form. The
/// runner uses its file twin <c>--message-file /dev/stdin</c> with the prompt
/// piped on stdin instead of argv: Linux's <c>MAX_ARG_STRLEN</c> is 128 KiB per
/// single argv element and rework prompts can exceed it (verified live: a
/// piped-stdin message produced the reply and <c>Applied edit</c> normally).
/// Without either flag aider waits on interactive input, which never arrives
/// in the sandbox.</para>
///
/// <para><b>Exit-zero errors.</b> Aider exits 0 even when the run dies before
/// producing output (verified: a bad OpenRouter key exits 0 with only
/// <c>litellm.AuthenticationError … 401</c> on stdout). <see cref="RunAsync"/>
/// therefore lifts the terminal error region into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="AiderTerminalDiagnoser"/> so the pipeline's no-changes branch can
/// park quota/auth failures instead of dead-lettering them as "produced no
/// changes" — the same give-up shape <c>agy</c> and <c>pi</c> have (see
/// <c>AgentResult.TerminalDiagnostic</c>).</para>
///
/// <para><b>Auth.</b> Aider reads provider API keys from the environment through
/// its litellm layer (<c>OPENROUTER_API_KEY</c>, <c>OPENAI_API_KEY</c>,
/// <c>ANTHROPIC_API_KEY</c>, <c>GEMINI_API_KEY</c>, …; OpenRouter is supported
/// natively with <c>openrouter/&lt;model&gt;</c> ids). The shipped credential
/// mapping wires the host <c>CODEYBOX_AIDER_API_KEY</c> to sandbox-side
/// <c>OPENROUTER_API_KEY</c>; operators fronting other providers extend the
/// mapping with that provider's variable. <c>--env-file</c> / <c>.env</c> and
/// <c>-c</c> / <c>.aider.conf.yml</c> also exist for local use but the sandbox
/// path always uses the staged environment.</para>
///
/// <para><b>Repo hygiene.</b> The runner passes <c>--no-auto-commits</c> (CodeyBox
/// owns commits — aider must leave edits in the worktree for the pipeline to
/// collect), <c>--no-gitignore</c> (otherwise aider appends <c>.aider*</c> to
/// the repo's <c>.gitignore</c>, polluting the diff), and redirects aider's
/// default repo-dir history files to <c>/dev/null</c> via
/// <c>--chat-history-file</c> / <c>--input-history-file</c>. Telemetry and
/// self-update checks are disabled (<c>--analytics-disable</c>,
/// <c>--no-check-update</c>, <c>--no-show-release-notes</c>) so a run never
/// stalls on network outside the sandbox allow-list. <c>--no-pretty</c> keeps
/// captured logs free of ANSI escapes.</para>
/// </summary>
public sealed class AiderAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public AiderAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public AiderAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Aider;

    /// <summary>
    /// Default aider binary name inside the sandbox. Shared with
    /// <c>AiderInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "aider";

    /// <summary>Path to the aider binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[aider]</c>). Aider model ids are
    /// <c>provider/id</c>-qualified (<c>openrouter/…</c> for the shipped
    /// OpenRouter path) — always configure one explicitly, because aider's own
    /// startup default (<c>gpt-4o</c>) needs an OpenAI key the sandbox may not
    /// carry. When neither is set we omit <c>--model</c> and let aider pick its
    /// own startup default; we never inject a hardcoded id here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["OPENROUTER_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: --message-file /dev/stdin consumes the
        // piped-stdin prompt, applies the reply, and exits (chat mode
        // disabled). The /dev/stdin variant of --message-file is deliberate:
        // --message would put the whole rework prompt in one argv element
        // (128 KiB MAX_ARG_STRLEN ceiling), while a staged prompt file would
        // need an extra sandbox exec the ISandbox surface does not offer.
        var argv = new List<string> { Binary, "--message-file", "/dev/stdin" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Non-interactive hardening: never prompt, never commit, never mutate
        // repo metadata, never phone home. Each flag is verified in
        // `aider --help` (0.86.2); see the class doc for the rationale.
        argv.Add("--yes-always");
        argv.Add("--no-auto-commits");
        argv.Add("--no-gitignore");
        argv.Add("--analytics-disable");
        argv.Add("--no-check-update");
        argv.Add("--no-show-release-notes");
        argv.Add("--no-pretty");

        // Aider persists .aider.chat.history.md / .aider.input.history in the
        // repo dir by default; redirect both to /dev/null so one-shot runs
        // never pollute the worktree diff the pipeline collects (verified:
        // /dev/null targets run normally and create no files).
        argv.Add("--chat-history-file");
        argv.Add("/dev/null");
        argv.Add("--input-history-file");
        argv.Add("/dev/null");

        // Reasoning effort is deliberately NOT mapped: aider exposes both
        // --reasoning-effort (reasoning_effort API parameter) and
        // --thinking-tokens (thinking budget), and which knob — and which
        // value vocabulary — is valid depends on the backing provider behind
        // the configured --model. Emitting one unconditionally would fail
        // dispatches for the other provider family, so the value is ignored
        // rather than passed through to fail the CLI invocation.
        _ = reasoningMode;

        _ = captureStructuredStream;
        _ = credential;
        return new AgentInvocation(argv, Stdin: prompt);
    }

    public override async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        var result = await base.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            captureStructuredStream).ConfigureAwait(false);

        // Aider exits 0 on terminal run errors (verified: bad OpenRouter key).
        // Lift the terminal error region so the pipeline can classify it;
        // without this an exit-0 quota/auth give-up with no file changes
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && AiderTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
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
            "OPENROUTER_API_KEY");

    // The aider CLI runs inside the work-item sandbox; a host-side text-only
    // call with no sandbox returns failure (see RunTextOnlyRequiresSandboxAsync below).
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
