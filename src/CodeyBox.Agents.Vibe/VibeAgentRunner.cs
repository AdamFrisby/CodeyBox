using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Drives the Mistral Vibe CLI (binary <c>vibe</c>, PyPI
/// <c>mistral-vibe</c>) in programmatic one-shot mode: <c>vibe -p</c> with
/// the prompt on stdin and <c>--output streaming</c>.
///
/// <para><b>Transport decision (verified against vibe 2.25.4).</b>
/// <c>-p/--prompt</c> <i>"does not start the chat interface and disables
/// interactive tools"</i>: with <c>const=""</c> argparse semantics a bare
/// <c>-p</c> plus a piped-stdin prompt enters programmatic mode (the prompt
/// is <c>args.prompt or stdin_prompt</c>), processes the prompt and exits —
/// exactly the non-interactive contract CodeyBox needs. The prompt travels
/// on stdin rather than <c>-p</c> argv or the positional <c>PROMPT</c>:
/// Linux's <c>MAX_ARG_STRLEN</c> is 128 KiB per argv element and rework
/// prompts can exceed it. <c>--output streaming</c> is the only transport
/// this runner speaks — even when the caller did not ask for structured
/// capture — so failure classification and stream parsing never depend on
/// which call path dispatched the run. Plain <c>text</c> output carries no
/// token totals either (and no framing); whole-doc <c>json</c> is one
/// pretty-printed blob, not a stream.</para>
///
/// <para><b>Autonomy.</b> Tool approval follows the selected
/// <c>--agent</c> (or the <c>default_agent</c> config, shipped default
/// <c>accept-edits</c>) — a headless run has no human to approve anything,
/// so this runner always passes <c>--auto-approve</c> (without
/// <c>--agent</c>, which selects the <c>auto-approve</c> agent outright).
/// <c>--trust</c> grants <i>"temporary trust for the current invocation"</i>
/// so the workspace trust prompt never stalls the run; nothing is persisted
/// to <c>trusted_folders.toml</c>.</para>
///
/// <para><b>Model selection.</b> Vibe has no <c>--model</c> flag: the dispatch
/// model is the guest config's <c>active_model</c>, overridable per-process
/// via <c>VIBE_ACTIVE_MODEL</c> — but only to a model <i>alias</i> defined in
/// the guest <c>~/.vibe/config.toml</c> <c>[[models]]</c> (a provider-native
/// id silently falls back to the mistral default and fails on its missing
/// key). This runner therefore sets <c>VIBE_ACTIVE_MODEL</c> from the
/// explicit member model, else the config-sourced default, and omits it when
/// neither is set so the guest's own <c>active_model</c> applies. The shipped
/// <c>appsettings.json</c> default (<c>nemotron-free</c>) must match an alias
/// in the baked guest config — see the Vibe quirks doc for the exact guest
/// <c>config.toml</c> snippet.</para>
///
/// <para><b>Terminal errors.</b> Vibe exits nonzero on provider failures
/// (verified: a missing key exits 1 with <c>Error: Missing … environment
/// variable …</c> on stderr; an OpenRouter 403 exits 1 with <c>Error: API
/// error from openrouter … Key limit exceeded …</c> on stderr).
/// <see cref="RunAsync"/> still lifts the terminal error into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="VibeTerminalDiagnoser"/> so the pipeline's no-changes branch
/// can park quota/auth failures instead of dead-lettering them as "produced
/// no changes" — the same give-up shape <c>agy</c> has (see
/// <c>AgentResult.TerminalDiagnostic</c>).</para>
///
/// <para><b>Auth.</b> The active provider's key arrives via the environment
/// variable named in the guest config's <c>[[providers]]</c>
/// (<c>api_key_env_var</c>); the shipped mapping wires the host
/// <c>CODEYBOX_VIBE_API_KEY</c> to sandbox-side <c>OPENROUTER_API_KEY</c>.
/// The setup wizard is skipped non-interactively: a missing key fails fast
/// with exit 1. Sessions persist under <c>$VIBE_HOME</c> inside the
/// throwaway VM and are discarded with it.</para>
/// </summary>
public sealed class VibeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly VibeOptionsAccessor? _options;

    public VibeAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the
    /// <c>VIBE_ACTIVE_MODEL</c> value when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal. Values are guest-config model
    /// aliases, not provider ids.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Vibe</c> knobs (turn bound). Read
    /// per-invocation so a hot reload applies to the next dispatch without a
    /// restart.
    /// </param>
    public VibeAgentRunner(AgentDefaultsSnapshot? defaults, VibeOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Vibe;

    /// <summary>
    /// Default vibe binary name inside the sandbox. Shared with
    /// <c>VibeInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "vibe";

    /// <summary>Path to the vibe binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Environment variable selecting the guest-config model alias for this
    /// invocation. The only per-dispatch model control vibe offers (there is
    /// no <c>--model</c> flag).
    /// </summary>
    public const string ActiveModelVariable = "VIBE_ACTIVE_MODEL";

    /// <summary>
    /// Default model alias passed via <c>VIBE_ACTIVE_MODEL</c> when the
    /// agent-class member does not override it. Sourced live from
    /// <see cref="AgentDefaultsSnapshot"/> (config key
    /// <c>CodeyBox:AgentDefaults[vibe]</c>). When neither is set the variable
    /// is omitted and vibe uses the guest config's own
    /// <c>active_model</c> — we never inject a hardcoded id here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    private int? ConfiguredMaxTurns
    {
        get
        {
            var value = _options?.Invoke().MaxTurns;
            return value is > 0 ? value : null;
        }
    }

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => ["OPENROUTER_API_KEY"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--output streaming</c> support with <c>vibe --help</c>.
    /// The runner's only transport is the NDJSON history-event stream, so a
    /// binary that no longer advertises the flag must fail closed here rather
    /// than dispatch into an unparseable plaintext run.
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
        return output.Contains("--output", StringComparison.Ordinal)
            && output.Contains("streaming", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `vibe -p` (bare flag, const="") plus a piped-stdin prompt enters
        // programmatic mode: no chat interface, no interactive tools, exit
        // after the run. The prompt travels on stdin so arbitrarily large
        // rework prompts never hit MAX_ARG_STRLEN. --output streaming is the
        // ONLY transport this runner speaks — even when the caller did not
        // ask for structured capture — so failure classification and stream
        // parsing never depend on which call path dispatched the run.
        var argv = new List<string>
        {
            Binary, "-p",
            "--trust",
            "--output", "streaming",
            "--auto-approve",
        };

        // Bound the autonomous turn loop: with --auto-approve there is no
        // user to stop a looping model, so an unbounded run would burn quota
        // until the pipeline timeout. Null/non-positive omits the flag.
        var maxTurns = ConfiguredMaxTurns;
        if (maxTurns.HasValue)
        {
            argv.Add("--max-turns");
            argv.Add(maxTurns.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Model control travels as a process env var: vibe has no --model
        // flag. The value must be a guest-config model alias; anything else
        // (including a provider-native id) falls back to the guest default
        // and fails loudly on its missing key rather than dispatching
        // against the wrong backend.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        Dictionary<string, string>? extraEnvironment = null;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            extraEnvironment = new Dictionary<string, string>
            {
                [ActiveModelVariable] = effectiveModel,
            };
        }

        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation(argv, ExtraEnvironment: extraEnvironment, Stdin: prompt);
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

        // Vibe reports provider failures on stderr (verified: missing key
        // and OpenRouter 403 both exit 1 with `Error: …`). Lift the terminal
        // error so the pipeline can classify it; without this a quota/auth
        // give-up with no file changes terminal-fails as "produced no
        // changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && VibeTerminalDiagnoser.TryExtractTerminalError(result.Stderr, result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }
}
