using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Drives the Cline CLI (binary <c>cline</c>, npm <c>cline</c>, Apache-2.0)
/// in one-shot headless mode: <c>cline --json "prompt"</c>.
///
/// <para><b>Transport decision (verified against cline 3.0.62).</b> A
/// positional prompt runs a single turn and exits — exactly the
/// non-interactive contract CodeyBox needs — and <c>--json</c> switches the
/// run to NDJSON (<c>hook_event</c> / <c>agent_event</c> / terminal
/// <c>run_result</c> frames). The prompt travels as an argv positional: the
/// only alternative the CLI hints at (piped stdin) was probed and rejected
/// by this version — with no positional prompt the CLI exits 1 with
/// <c>JSON output mode requires a prompt argument or piped stdin</c> even
/// when stdin is piped. Whole-run framing stays parseable because the runner
/// always passes <c>--json</c>, even when the caller did not ask for
/// structured capture, so failure classification and stream parsing never
/// depend on which call path dispatched the run.</para>
///
/// <para><b>Authentication (verified live in clean isolated state).</b> The
/// CLI reads the provider key from <c>OPENROUTER_API_KEY</c> in the
/// environment (the shipped credential mapping wires host
/// <c>CODEYBOX_CLINE_API_KEY</c> to it) — a fresh state dir with a bad key
/// fails with <c>User not found.</c>, and with the real key the run
/// succeeds, so no config file is required. The default provider is
/// <c>cline</c> (a vendor account), so the runner always passes
/// <c>-P</c> from <c>CodeyBox:Cline</c> (shipped as <c>openrouter</c>):
/// without it the run bills the vendor account and fails fast with
/// <c>Unauthorized: … re-authenticate your Cline account</c> even when the
/// provider key is valid. The <c>-k</c> key-override flag is deliberately
/// never emitted — it would place the secret in argv (visible via
/// <c>ps</c>); the environment is the only key channel. <c>cline auth</c>
/// and <c>cline mcp install</c> require a TTY, so the runner performs
/// neither; operators may additionally pre-seed
/// <c>~/.cline/data/settings/providers.json</c> during provisioning, but the
/// env key alone is sufficient for dispatch. The CLI fails fast rather than
/// opening a browser, which is the behaviour the sandbox needs.</para>
///
/// <para><b>Autonomy.</b> The sandbox VM is throwaway with no human to
/// approve anything, so the runner passes <c>--auto-approve true</c> (also
/// the CLI default — passed explicitly to pin it against a future default
/// flip). <c>--plan</c> is never passed (read-only planning would produce
/// no changes by design).</para>
///
/// <para><b>Exit codes.</b> Cline exits non-zero on terminal failures
/// (verified: bad-key auth and $0-spend-limit quota refusals both exit 1
/// with a <c>run_result finishReason: error</c> frame and a
/// <c>{"type":"error","message":"…"}</c> line on stderr). <see cref="RunAsync"/>
/// additionally lifts the terminal error into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="ClineTerminalDiagnoser"/> so the pipeline's no-changes branch
/// can park quota/auth failures.</para>
///
/// <para><b>Cost.</b> The terminal <c>run_result</c> frame carries
/// <c>usage</c> / <c>aggregateUsage</c> with <c>inputTokens</c> /
/// <c>outputTokens</c> / <c>cacheReadTokens</c> and the dispatch model id,
/// which <see cref="ClineCostExtractor"/> reads. Output with no
/// machine-readable usage extracts to unknown (null), never a fabricated
/// zero.</para>
/// </summary>
public sealed class ClineAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly ClineOptionsAccessor? _options;

    public ClineAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>-m/--model</c>
    /// value when a caller does not pass an explicit model, so the dispatch
    /// model is sourced from hot-reloadable config rather than a hardcoded
    /// literal. A <c>$0</c>-spend-limit OpenRouter key only serves ids ending
    /// <c>:free</c>; the shipped default is such an id.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Cline</c> knobs (provider id).
    /// Read per-invocation so a hot reload applies to the next dispatch
    /// without a restart.
    /// </param>
    public ClineAgentRunner(AgentDefaultsSnapshot? defaults, ClineOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Cline;

    /// <summary>
    /// Default cline binary name inside the sandbox. Shared with
    /// <c>ClineInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "cline";

    /// <summary>Path to the cline binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. The CLI reads the
    /// OpenRouter key from this variable directly (verified: no config file
    /// required); it is never passed via <c>-k</c> so the secret stays out
    /// of argv.
    /// </summary>
    public const string CredentialVariable = "OPENROUTER_API_KEY";

    /// <summary>
    /// Default model passed to <c>-m/--model</c> when the agent-class member
    /// does not override it. Sourced live from
    /// <see cref="AgentDefaultsSnapshot"/> (config key
    /// <c>CodeyBox:AgentDefaults[cline]</c>). When neither is set the flag
    /// is omitted and the CLI uses its provider default — a bare
    /// <c>-m</c> without a configured model would resolve against the wrong
    /// catalog, so it is never invented here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    internal string ConfiguredProvider => string.IsNullOrWhiteSpace(_options?.Invoke().Provider)
        ? ClineOptions.DefaultProvider
        : _options!.Invoke().Provider!.Trim();

    /// <summary>
    /// Effective dispatch model: explicit member model wins, else the
    /// config-sourced <see cref="DefaultModelId"/>. Null when neither is set.
    /// </summary>
    internal string? ResolveEffectiveModel(string? modelId) =>
        !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => [CredentialVariable];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--json</c> support with <c>cline --help</c>. The runner's
    /// only transport is the NDJSON event stream, so a binary that no longer
    /// advertises the flag must fail closed here rather than dispatch into
    /// an unparseable run.
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
        return output.Contains("--json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: the positional prompt runs a single turn
        // and exits (verified against 3.0.62). Piped-stdin prompts were
        // probed and rejected by this version, so the prompt travels on
        // argv — the documented twin of the -p <text> form other runners
        // avoid for MAX_ARG_STRLEN reasons, but the only transport this CLI
        // accepts. --json is the ONLY transport this runner speaks — even
        // when the caller did not ask for structured capture — so failure
        // classification and stream parsing never depend on which call path
        // dispatched the run.
        var argv = new List<string>
        {
            Binary,
            "--json",
            // The default provider is the cline vendor account: without -P
            // the run ignores OPENROUTER_API_KEY and fails fast on vendor
            // auth (verified). The provider id comes from the
            // hot-reloadable CodeyBox:Cline knob, never a literal.
            "-P", ConfiguredProvider,
        };

        var effectiveModel = ResolveEffectiveModel(modelId);
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("-m");
            argv.Add(effectiveModel);
        }

        // Throwaway VM with no human: pin tool auto-approval on (also the
        // CLI default — passed explicitly so a future default flip cannot
        // silently park headless runs on approval prompts). Never --plan:
        // read-only planning produces no changes by design.
        argv.Add("--auto-approve");
        argv.Add("true");

        // The prompt is positional and required: without it the CLI exits 1
        // (it does not fall back to stdin in this version). `--` ends option
        // parsing so a prompt beginning with `-` can never be reinterpreted
        // as CLI flags (verified against cline 3.0.62, commander-based: `--`
        // stops flag parsing; a dash-prefixed prompt then fails closed with
        // exit 1 `Unknown command or unquoted prompt` instead of dispatching
        // as flags, while normal prompts pass through unchanged).
        argv.Add("--");
        argv.Add(prompt);

        // Reasoning effort is deliberately NOT mapped: cline's --thinking
        // vocabulary (none|low|medium|high|xhigh) is provider-dependent and
        // emitting one unconditionally would fail dispatches for provider
        // families without reasoning support — same rationale as the aider
        // runner.
        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation(argv);
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

        // Lift the terminal NDJSON error so the pipeline can classify it;
        // without this a quota/auth give-up with no file changes
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && ClineTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }
}
