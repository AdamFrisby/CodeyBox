using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Drives the OMP coding-agent CLI (binary <c>omp</c>, npm
/// <c>@oh-my-pi/pi-coding-agent</c>, MIT-licensed) in non-interactive mode
/// via <c>-p/--print</c> with <c>--mode json</c>: the run processes the
/// prompt, acts on the repository, streams one JSON event per stdout line,
/// and exits without entering the TUI.
///
/// <para><b>Transport decision (verified against omp 18.2.2).</b> A bare
/// <c>omp "prompt"</c> is interactive; <c>-p/--print</c> is what makes the
/// run one-shot. <c>-p --mode json</c> is the primary transport — not raw
/// <c>-p</c> and not <c>--mode rpc</c>. Raw <c>-p</c> prints only the final
/// response text, so token usage, the dispatch model id, and the terminal
/// error shape would all be unrecoverable. <c>--mode rpc</c> is a
/// bidirectional prompt/response protocol needing a driver loop for no
/// additional signal on a one-shot run. <c>--mode json</c> exits after the
/// run like <c>-p</c> but keeps every event needed: <c>message_end</c> /
/// <c>turn_end</c> carry <c>message.usage {input, output, cacheRead,
/// cacheWrite, totalTokens}</c> plus <c>message.model</c>, and — on failure
/// — <c>stopReason: "error"</c> with <c>errorMessage</c>. The terminal
/// <c>agent_end</c> frame carries the same assistant message inside a
/// <c>messages</c> array (with <c>isTerminal: true</c>) rather than pi's
/// <c>message</c> envelope — see <see cref="OmpTerminalDiagnoser"/>.</para>
///
/// <para><b>Exit codes (verified live: success 0, missing key 1, paid model
/// on a $0-spend-limit key 1).</b> Unlike pi (which exits 0 on terminal run
/// errors), omp exits non-zero, so a quota/auth give-up never masquerades
/// as success — but with no file changes it would still terminal-fail as
/// "produced no changes" without a lifted cause. <see cref="RunAsync"/>
/// therefore lifts the terminal error region into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="OmpTerminalDiagnoser"/> (which scans BOTH streams: the
/// missing-key crash lands on stderr with only the session header on
/// stdout). A missing binary surfaces as exit 127 + command-not-found,
/// which the base class classifies as infrastructure (see
/// <c>ClassifyFailure</c>) — never as "no changes".</para>
///
/// <para><b>Auth.</b> OMP reads provider API keys from the environment
/// (<c>OPENROUTER_API_KEY</c> documented directly, <c>OPENAI_BASE_URL</c> as
/// a fallback; custom providers live in <c>~/.omp/agent/models.yml</c>).
/// The shipped credential mapping wires the host
/// <c>CODEYBOX_OMP_API_KEY</c> to sandbox-side <c>OPENROUTER_API_KEY</c>;
/// operators fronting other providers extend the mapping with that
/// provider's variable. Subscription (<c>/login</c>) state is
/// interactive-only and is not shipped into sandboxes (<c>omp usage</c>
/// only sees login accounts, so there is no quota-meter probe — members
/// fall through to the <c>NullQuotaProbe</c> unknown path).</para>
///
/// <para><b>Autonomy.</b> <c>tools.approvalMode</c> already defaults to
/// <c>yolo</c> (verified: <c>omp config get tools.approvalMode</c> prints
/// <c>yolo</c>), so the runner passes no approval flag — there is nothing
/// to defeat for unattended use. <c>--approval-mode</c> and
/// <c>--max-time</c> exist for operator budgets but are not emitted by
/// default. Deliberately NOT passed: <c>--offline</c> (absent from
/// <c>omp --help</c> — pi's flag did not survive the fork, so emitting it
/// would fail the dispatch), <c>--provider</c> (legacy; the model id plus
/// the provider env key resolves the route — verified for both the
/// <c>openrouter/…</c>-qualified and bare id forms), and any trust override
/// (the sandbox working tree is untrusted repo content).</para>
/// </summary>
public sealed class OmpAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public OmpAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public OmpAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Omp;

    /// <summary>
    /// Default omp binary name inside the sandbox. Shared with
    /// <c>OmpInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary. This is the upstream <c>oh-my-pi</c> binary —
    /// not the <c>oh-omp</c> fork, which is a different project.
    /// </summary>
    public const string DefaultBinary = "omp";

    /// <summary>Path to the omp binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[omp]</c>). OMP accepts
    /// <c>provider/id</c>-qualified ids, fuzzy patterns, and bare ids (the
    /// provider env key disambiguates — verified live for both
    /// <c>openrouter/nvidia/…</c> and bare <c>nvidia/…</c>); the id is
    /// passed verbatim, never rewritten. The stream reports the bare
    /// provider-catalog id in <c>message.model</c> either way.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// The thinking levels <c>omp --help</c> accepts for <c>--thinking</c>
    /// (verified against omp 18.2.2; pi's vocabulary plus <c>auto</c>).
    /// <see cref="BuildInvocation"/> only emits the flag for an exact
    /// (case-insensitive) member of this set; anything else is ignored so a
    /// typo cannot fail a dispatch at the CLI layer.
    /// </summary>
    internal static readonly IReadOnlySet<string> ThinkingLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max", "auto",
    };

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["OPENROUTER_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--mode json</c> support with <c>omp --help</c>. The
    /// runner's only transport is the JSON event stream, so a binary that no
    /// longer advertises the flag must fail closed here rather than dispatch
    /// into an unparseable plaintext run.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "--help"],
        }, ct).ConfigureAwait(false);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--mode", StringComparison.Ordinal)
            && output.Contains("json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `omp -p --mode json` processes the prompt, acts on the repository,
        // and exits without entering the TUI. It is the ONLY transport this
        // runner speaks — even when the caller did not ask for structured
        // capture — so cost attribution (usage on message events), failure
        // classification (stopReason/errorMessage), and stream parsing never
        // depend on which call path dispatched the run. A bare `omp
        // "prompt"` is interactive and must never be emitted here.
        var argv = new List<string> { Binary, "-p", "--mode", "json" };

        // Ephemeral session: the sandbox VM is discarded after the run, so
        // persisting ~/.omp/agent/sessions buys nothing and leaves unbounded
        // session files behind on long-lived images.
        argv.Add("--no-session");

        // Fall back to the config-sourced default when the caller passes no
        // explicit model, mirroring GeminiAgentRunner. When neither is set we
        // omit --model and let omp pick its own startup default — we never
        // inject a hardcoded id here. A $0-spend-limit OpenRouter key only
        // serves ids ending `:free`; a paid id fails with `Key limit
        // exceeded`, which the detector parks as quota exhaustion.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort maps 1:1 onto omp's --thinking flag (pi's levels
        // plus auto). Only exact allowlist members are emitted; anything
        // else is ignored rather than passed through to fail the CLI
        // invocation.
        if (!string.IsNullOrEmpty(reasoningMode) && ThinkingLevels.Contains(reasoningMode))
        {
            argv.Add("--thinking");
            argv.Add(reasoningMode);
        }

        // Pass the prompt via stdin rather than as a positional argv.
        // Linux's MAX_ARG_STRLEN is 128 KiB per single argv element; rework
        // prompts can exceed that. Verified against omp 18.2.2: `-p --mode
        // json` with a piped-stdin prompt and no positional MESSAGES arg
        // answered on stdin alone, so stdin is a complete prompt channel.
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

        // Omp exits non-zero on terminal run errors (verified: missing key
        // and paid-model-on-$0-key both exit 1), so unlike pi there is no
        // exit-zero masquerade — but with no file changes the run would
        // still terminal-fail as "produced no changes" without a lifted
        // cause. The missing-key crash lands on stderr (stdout carries only
        // the session header), so both streams are scanned.
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && OmpTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr) is { } terminalError)
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

    // The omp CLI runs inside the work-item sandbox; a host-side text-only
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
