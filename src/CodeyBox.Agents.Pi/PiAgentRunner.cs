using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Drives the Pi coding-agent CLI (binary <c>pi</c>, npm
/// <c>@earendil-works/pi-coding-agent</c>, MIT-licensed) in non-interactive
/// mode via its <c>--mode json</c> transport: one JSON event per stdout line,
/// with cumulative provider-reported <c>usage</c> on message events and a
/// terminal <c>agent_end</c> frame.
///
/// <para><b>Transport decision (verified against pi 0.85.1).</b>
/// <c>--mode json</c> is the primary transport — not raw <c>-p</c> and not
/// <c>--mode rpc</c>. Raw <c>-p</c> prints only the final response text, so
/// token usage, the dispatch model id, and the terminal error shape would all
/// be unrecoverable. <c>--mode rpc</c> is a bidirectional prompt/response
/// protocol (prompt command in, events out) that needs a driver loop for no
/// additional signal on a one-shot run. <c>--mode json</c> exits after the run
/// like <c>-p</c> but keeps every event we need: <c>message_end</c> /
/// <c>turn_end</c> / <c>agent_end</c> carry <c>message.usage {input, output,
/// cacheRead, cacheWrite, totalTokens}</c>, <c>message.model</c>, and — on
/// failure — <c>stopReason: "error"</c> with <c>errorMessage</c>.</para>
///
/// <para><b>Exit-zero errors.</b> Pi exits 0 even when the run dies before
/// producing output (verified: missing API key and a 401 both exit 0 with the
/// cause only in the event stream). <see cref="RunAsync"/> therefore lifts the
/// terminal error region into <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="PiTerminalDiagnoser"/> so the pipeline's no-changes branch can
/// park quota/auth failures instead of dead-lettering them as "produced no
/// changes" — the same give-up shape <c>agy</c> has (see
/// <c>AgentResult.TerminalDiagnostic</c>).</para>
///
/// <para><b>Auth.</b> Pi reads provider API keys from the environment
/// (<c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>, <c>GEMINI_API_KEY</c>, … —
/// full table in pi's providers.md). The shipped credential mapping wires the
/// host <c>CODEYBOX_PI_API_KEY</c> to sandbox-side <c>ANTHROPIC_API_KEY</c>;
/// operators fronting other providers extend the mapping with that provider's
/// variable from the same table. Subscription (<c>/login</c>) state is
/// interactive-only and is not shipped into sandboxes.</para>
///
/// <para><b>Project trust.</b> Non-interactive pi loads <c>AGENTS.md</c> context
/// files but ignores project-local settings/extensions/skills unless the
/// project is trusted. This runner deliberately passes NEITHER
/// <c>--approve</c> NOR <c>--no-approve</c>: the sandbox working tree is
/// untrusted repo content, so the <c>ask</c> default (ignore project
/// resources) is the safe posture. An operator that vets their tree can set a
/// saved trust decision on the image; the runner must not override that.</para>
/// </summary>
public sealed class PiAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public PiAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public PiAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Pi;

    /// <summary>
    /// Default pi binary name inside the sandbox. Shared with
    /// <c>PiInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "pi";

    /// <summary>Path to the pi binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[pi]</c>). Pi accepts
    /// <c>provider/id</c>-qualified ids, fuzzy patterns, and an optional
    /// <c>:thinking</c> suffix — prefer a qualified id so the run does not
    /// depend on pi's own startup default.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// The thinking levels <c>pi --help</c> accepts for <c>--thinking</c>
    /// (verified against pi 0.85.1). <see cref="BuildInvocation"/> only emits
    /// the flag for an exact (case-insensitive) member of this set; anything
    /// else is ignored so a typo cannot fail a dispatch at the CLI layer.
    /// </summary>
    internal static readonly IReadOnlySet<string> ThinkingLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max",
    };

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["ANTHROPIC_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--mode json</c> support with <c>pi --help</c>. The runner's
    /// only transport is the JSON event stream, so a binary that no longer
    /// advertises the flag must fail closed here rather than dispatch into an
    /// unparseable plaintext run.
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
        // `pi --mode json` processes the prompt and exits (like -p) while
        // emitting the full session event stream on stdout. It is the ONLY
        // transport this runner speaks — even when the caller did not ask for
        // structured capture — so cost attribution (usage on message events),
        // failure classification (stopReason/errorMessage), and stream parsing
        // never depend on which call path dispatched the run. --mode rpc is
        // rejected deliberately: it needs a bidirectional driver loop for no
        // extra signal on a one-shot run.
        var argv = new List<string> { Binary, "--mode", "json" };

        // Ephemeral session: the sandbox VM is discarded after the run, so
        // persisting ~/.pi/agent/sessions buys nothing and leaves unbounded
        // session files behind on long-lived images.
        argv.Add("--no-session");

        // Disable pi.dev startup network (version check, package update
        // checks, install telemetry). The sandbox network allow-list does not
        // include pi.dev, and stalling the run on it serves no dispatch
        // purpose. Equivalent to PI_OFFLINE=1; the flag keeps the posture
        // visible in argv rather than hidden in the environment.
        argv.Add("--offline");

        // Fall back to the config-sourced default when the caller passes no
        // explicit model, mirroring GeminiAgentRunner. When neither is set we
        // omit --model and let pi pick its own startup default — we never
        // inject a hardcoded id here.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort maps 1:1 onto pi's --thinking flag. Only exact
        // allowlist members are emitted; anything else (including pi's own
        // future levels this list has not learned) is ignored rather than
        // passed through to fail the CLI invocation.
        if (!string.IsNullOrEmpty(reasoningMode) && ThinkingLevels.Contains(reasoningMode))
        {
            argv.Add("--thinking");
            argv.Add(reasoningMode);
        }

        // Pass the prompt via stdin rather than as a positional argv.
        // Linux's MAX_ARG_STRLEN is 128 KiB per single argv element; rework
        // prompts can exceed that. Verified against pi 0.85.1: `pi --mode
        // json` with a piped-stdin prompt and no positional prompt arg emits
        // the session header and proceeds to model resolution (it failed on
        // auth, not on "no prompt"), so stdin alone is a complete prompt.
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

        // Pi exits 0 on terminal run errors (verified: missing key, 401).
        // Lift the terminal error region so the pipeline can classify it;
        // without this an exit-0 quota/auth give-up with no file changes
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && PiTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
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
            "ANTHROPIC_API_KEY");

    // The pi CLI runs inside the work-item sandbox; a host-side text-only
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
