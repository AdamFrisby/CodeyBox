using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Hot-reloadable accessor for <see cref="PrimeSectionOptions"/> (mirrors
/// <c>CrockSandboxOptionsAccessor</c>): production wiring supplies an
/// accessor backed by <c>IOptionsMonitor</c>; tests construct the runner
/// with no DI graph and get the type defaults.
/// </summary>
public delegate PrimeSectionOptions PrimeSectionOptionsAccessor();

/// <summary>
/// Drives the Prime Agent CLI (binary <c>prime-agent</c>, installed via
/// <c>https://app.primeintellect.ai/prime-agent/install.sh</c>) in
/// non-interactive mode via <c>-p/--print</c> with <c>--mode json</c>: the
/// run prints its response and exits while emitting one JSON event per
/// stdout line, with cumulative provider-reported <c>usage</c> on assistant
/// message frames and a terminal <c>agent_end</c> frame.
///
/// <para><b>Transport decision (verified against prime-agent 0.9.5).</b>
/// <c>-p --mode json</c> is the primary transport. Bare <c>-p</c> prints only
/// the final response text, so token usage, the dispatch model id, and the
/// terminal error shape would all be unrecoverable. <c>--mode json</c> exits
/// after the run like <c>-p</c> but keeps every event needed:
/// <c>message_end</c> / <c>turn_end</c> / <c>agent_end</c> carry
/// <c>message.usage {input, output, cacheRead, cacheWrite, totalTokens}</c>,
/// <c>message.model</c>, and — on failure — <c>stopReason: "error"</c> with
/// <c>errorMessage</c>. <c>--mode rpc</c> is a bidirectional prompt/response
/// protocol needing a driver loop for no additional signal on a one-shot
/// run. <c>--autonomous</c> is deliberately NOT passed: budget exhaustion
/// exits non-zero ("Autonomous run stopped before terminal evidence"),
/// which the pipeline treats as a reported failure before staging diffs —
/// losing real work. A plain <c>-p</c> run already works multi-turn until
/// the model stops (verified live: a file edit completed across 3 turns,
/// exit 0), so the exit-0 contract is preserved and diffs are always
/// collected.</para>
///
/// <para><b>Exit-zero errors.</b> Prime exits 0 even when the run dies before
/// producing output (verified: a bad key exits 0 with
/// <c>stopReason:"error"</c> + <c>errorMessage:"401 User not found…"</c> in
/// the event stream; a missing key exits 0 with
/// <c>No API key found for the selected model.</c> on stderr).
/// <see cref="RunAsync"/> therefore lifts the terminal error region into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="PrimeTerminalDiagnoser"/> (which scans BOTH streams) so the
/// pipeline's no-changes branch can park quota/auth failures instead of
/// dead-lettering them as "produced no changes".</para>
///
/// <para><b>Auth.</b> Prime reads provider API keys from the environment
/// (<c>OPENROUTER_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>,
/// … — 26 provider variables recognised; full table in prime's
/// <c>providers.md</c>). The shipped credential mapping wires the host
/// <c>CODEYBOX_PRIME_API_KEY</c> to sandbox-side <c>OPENROUTER_API_KEY</c>;
/// operators fronting other providers extend the mapping with that
/// provider's variable and set <c>CodeyBox:Prime:Provider</c> to match.
/// <c>PRIME_API_KEY</c> is only the Prime Inference provider entry, not a
/// vendor account for the CLI itself. Interactive <c>/login</c> state is
/// in-session only and is not shipped into sandboxes.</para>
///
/// <para><b>Project trust.</b> Unlike pi's ask-default posture, prime loads
/// <c>AGENTS.md</c> context non-interactively with no approval prompt
/// (verified in a fresh directory: a marker rule fired with exit 0 and empty
/// stderr). The CLI exposes no approve/trust override flags, so there is
/// nothing to pass — and nothing that could gate an unattended run.</para>
/// </summary>
public sealed class PrimeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public PrimeAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public PrimeAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Prime;

    /// <summary>
    /// Default prime-agent binary name inside the sandbox. Shared with
    /// <c>PrimeInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "prime-agent";

    /// <summary>Path to the prime-agent binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Hot-reloadable accessor for the prime section options (notably the
    /// <c>--provider</c> default). Defaults to the type's defaults
    /// (<c>openrouter</c>) so tests that construct the runner with no DI
    /// graph continue to compile; production wiring supplies an accessor
    /// backed by <c>IOptionsMonitor&lt;CodeyBoxOptions&gt;</c>.
    /// </summary>
    public PrimeSectionOptionsAccessor PrimeOptions { get; init; } =
        static () => new PrimeSectionOptions();

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[prime]</c>). The id is the
    /// provider-catalog id (OpenRouter-style when the provider is
    /// <c>openrouter</c>, e.g. <c>anthropic/claude-haiku-4.5</c>) — passed
    /// verbatim, never rewritten.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// The thinking levels <c>prime-agent --help</c> accepts for
    /// <c>--thinking</c> (verified against prime-agent 0.9.5; identical
    /// vocabulary to pi). <see cref="BuildInvocation"/> only emits the flag
    /// for an exact (case-insensitive) member of this set; anything else is
    /// ignored so a typo cannot fail a dispatch at the CLI layer.
    /// </summary>
    internal static readonly IReadOnlySet<string> ThinkingLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max",
    };

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["OPENROUTER_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--mode json</c> support with <c>prime-agent --help</c>.
    /// The runner's only transport is the JSON event stream, so a binary
    /// that no longer advertises the flag must fail closed here rather than
    /// dispatch into an unparseable plaintext run.
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
        // `prime-agent -p --mode json` prints the response and exits (the
        // -p one-shot contract) while emitting the full session event
        // stream on stdout. It is the ONLY transport this runner speaks —
        // even when the caller did not ask for structured capture — so cost
        // attribution (usage on message events), failure classification
        // (stopReason/errorMessage), and stream parsing never depend on
        // which call path dispatched the run.
        var argv = new List<string> { Binary, "-p", "--mode", "json" };

        // Ephemeral session: the sandbox VM is discarded after the run, so
        // persisting ~/.prime/agent/sessions buys nothing and leaves
        // unbounded session files behind on long-lived images.
        argv.Add("--no-session");

        // Disable startup network operations (version checks, telemetry).
        // The sandbox network allow-list does not include the vendor
        // endpoints, and stalling the run on them serves no dispatch
        // purpose. The flag keeps the posture visible in argv rather than
        // hidden in the environment (equivalent to PI_OFFLINE=1).
        argv.Add("--offline");

        // Provider routing. The shipped default (openrouter) matches the
        // credential mapping; operators fronting another provider change
        // CodeyBox:Prime:Provider instead of forking this runner. A blank
        // value omits the flag and lets the CLI resolve its own default.
        var provider = PrimeOptions().Provider;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            argv.Add("--provider");
            argv.Add(provider.Trim());
        }

        // Fall back to the config-sourced default when the caller passes no
        // explicit model, mirroring GeminiAgentRunner. When neither is set we
        // omit --model and let prime pick its own startup default — we never
        // inject a hardcoded id here.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort maps 1:1 onto prime's --thinking flag. Only exact
        // allowlist members are emitted; anything else is ignored rather
        // than passed through to fail the CLI invocation.
        if (!string.IsNullOrEmpty(reasoningMode) && ThinkingLevels.Contains(reasoningMode))
        {
            argv.Add("--thinking");
            argv.Add(reasoningMode);
        }

        // Pass the prompt via stdin rather than as a positional argv.
        // Linux's MAX_ARG_STRLEN is 128 KiB per single argv element; rework
        // prompts can exceed that. Verified against prime-agent 0.9.5: `-p
        // --mode json` with a piped-stdin prompt and no positional prompt
        // arg answered on stdin alone ("STDIN-HELLO"), so stdin is a
        // complete prompt channel.
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

        // Prime exits 0 on terminal run errors (verified: bad-key 401 in the
        // event stream, missing key on stderr). Lift the terminal error
        // region so the pipeline can classify it; without this an exit-0
        // quota/auth give-up with no file changes terminal-fails as
        // "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && PrimeTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr) is { } terminalError)
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

    // The prime-agent CLI runs inside the work-item sandbox; a host-side
    // text-only call with no sandbox returns failure (see RunTextOnlyRequiresSandboxAsync below).
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
