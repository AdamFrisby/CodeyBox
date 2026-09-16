using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Drives the Goose CLI (binary <c>goose</c>, Block/aaif-goose, Apache-2.0)
/// in one-shot headless mode: <c>goose run</c> with the prompt on stdin and
/// <c>--output-format stream-json</c>.
///
/// <para><b>Transport decision (verified against goose 1.50.1).</b>
/// <c>goose run -t &lt;text&gt;</c> without <c>-s/--interactive</c> processes
/// the prompt and exits — exactly the non-interactive contract CodeyBox
/// needs (the agent-orchestrator project deliberately forces
/// <c>-t "" --interactive</c> because it wants a human-steered session; one
/// turn is what CodeyBox wants). The prompt travels via <c>-i -</c> (stdin)
/// rather than <c>-t</c> argv: Linux's <c>MAX_ARG_STRLEN</c> is 128 KiB per
/// argv element and rework prompts can exceed it. <c>--output-format
/// stream-json</c> is the only transport this runner speaks — even when the
/// caller did not ask for structured capture — so cost attribution (the
/// terminal <c>type: "complete"</c> totals frame), failure classification
/// (content <c>type: "error"</c> blocks), and stream parsing never depend on
/// which call path dispatched the run. Plain <c>text</c> output carries no
/// token totals; whole-doc <c>json</c> is one pretty-printed blob, not a
/// stream.</para>
///
/// <para><b>Autonomy.</b> Goose reads its approval mode from the
/// <c>GOOSE_MODE</c> environment variable, not a CLI flag
/// (<c>auto / approve / chat / smart_approve</c>). This runner always sets
/// <c>GOOSE_MODE=auto</c>: the sandbox VM is throwaway with no human to
/// approve anything, so any approval prompt would hang or fail the run.
/// Provider/model selection comes from <c>--provider/--model</c> (explicit
/// member model, else the config-sourced default) and otherwise defers to
/// the guest's own default provider.</para>
///
/// <para><b>Exit-zero errors.</b> Goose exits 0 even when the provider call
/// fails (verified: an OpenRouter 401 exits 0 with the cause only in a
/// content <c>type: "error"</c> block). <see cref="RunAsync"/> therefore
/// lifts the terminal error into <see cref="AgentResult.TerminalDiagnostic"/>
/// via <see cref="GooseTerminalDiagnoser"/> so the pipeline's no-changes
/// branch can park quota/auth failures instead of dead-lettering them as
/// "produced no changes".</para>
///
/// <para><b>Auth.</b> Goose reads provider API keys from the environment
/// (the thirteen variables in <see cref="GooseSmokeProbe"/> — e.g.
/// <c>OPENROUTER_API_KEY</c>, <c>OPENAI_API_KEY</c>,
/// <c>ANTHROPIC_API_KEY</c>) or from <c>~/.config/goose/config.yaml</c> /
/// <c>secrets.yaml</c>. The shipped credential mapping wires the host
/// <c>CODEYBOX_GOOSE_API_KEY</c> to sandbox-side <c>OPENROUTER_API_KEY</c>;
/// operators fronting other providers extend the mapping with that
/// provider's variable. A missing key fails fast with exit 1
/// (<c>Error Configuration value not found: …</c>).</para>
///
/// <para><b>Sessions.</b> <c>--no-session</c> keeps automated runs out of the
/// session store: the sandbox VM is discarded after the run, so persisting a
/// session buys nothing.</para>
/// </summary>
public sealed class GooseAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly GooseOptionsAccessor? _options;

    public GooseAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>--model</c>
    /// value when a caller does not pass an explicit model, so the dispatch
    /// model is sourced from hot-reloadable config rather than a hardcoded
    /// literal.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Goose</c> knobs (provider id,
    /// turn bound). Read per-invocation so a hot reload applies to the next
    /// dispatch without a restart.
    /// </param>
    public GooseAgentRunner(AgentDefaultsSnapshot? defaults, GooseOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Goose;

    /// <summary>
    /// Default goose binary name inside the sandbox. Shared with
    /// <c>GooseInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "goose";

    /// <summary>Path to the goose binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Approval mode forced into the sandbox environment. <c>auto</c> is the
    /// only mode in which a non-interactive run can act: any approval prompt
    /// would wait on a human that does not exist in the sandbox.
    /// </summary>
    public const string ForcedApprovalMode = "auto";

    /// <summary>
    /// Default model passed to <c>--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[goose]</c>). When neither is set
    /// the flag is omitted and goose uses its own configured default model.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    private string? ConfiguredProvider => string.IsNullOrWhiteSpace(_options?.Invoke().Provider)
        ? null
        : _options!.Invoke().Provider!.Trim();

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
    /// Verifies <c>--output-format stream-json</c> support with
    /// <c>goose run --help</c>. The runner's only transport is the JSONL
    /// event stream, so a binary that no longer advertises the flag must
    /// fail closed here rather than dispatch into an unparseable run.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "run", "--help"],
        }, ct).ConfigureAwait(false);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--output-format", StringComparison.Ordinal)
            && output.Contains("stream-json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `goose run` without -s/--interactive processes the prompt and
        // exits: the one-shot contract CodeyBox needs. The prompt travels on
        // stdin via `-i -` (a file path of `-` means stdin) so arbitrarily
        // large rework prompts never hit MAX_ARG_STRLEN. --output-format
        // stream-json is the ONLY transport this runner speaks — even when
        // the caller did not ask for structured capture — so cost
        // attribution (terminal complete frame), failure classification
        // (content error blocks), and stream parsing never depend on which
        // call path dispatched the run.
        var argv = new List<string>
        {
            Binary, "run",
            "-i", "-",
            "--output-format", "stream-json",
            "--no-session",
        };

        // Fall back to the config-sourced default when the caller passes no
        // explicit model, mirroring PiAgentRunner. When neither is set both
        // flags are omitted and goose uses its own configured default
        // provider/model — a bare --model without --provider would resolve
        // against the wrong provider catalog, so --provider is emitted only
        // from the CodeyBox:Goose knob, never inferred.
        var provider = ConfiguredProvider;
        if (!string.IsNullOrEmpty(provider))
        {
            argv.Add("--provider");
            argv.Add(provider);
        }

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Bound the autonomous turn loop: with GOOSE_MODE=auto there is no
        // user to stop a looping model, so an unbounded run would burn quota
        // until the pipeline timeout. Null/non-positive omits the flag.
        var maxTurns = ConfiguredMaxTurns;
        if (maxTurns.HasValue)
        {
            argv.Add("--max-turns");
            argv.Add(maxTurns.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation(
            argv,
            ExtraEnvironment: new Dictionary<string, string>
            {
                // Goose honors no CLI flag for approvals; the mode travels as
                // a process env var. Forced (not defaulted): a headless run
                // with approve/chat/smart_approve would stall on a human that
                // does not exist.
                ["GOOSE_MODE"] = ForcedApprovalMode,
            },
            Stdin: prompt);
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

        // Goose exits 0 on provider errors (verified: OpenRouter 401 exits 0
        // with the cause only in a content error block). Lift the terminal
        // error so the pipeline can classify it; without this an exit-0
        // quota/auth give-up with no file changes terminal-fails as
        // "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && GooseTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }
}
