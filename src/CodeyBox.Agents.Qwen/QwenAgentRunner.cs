using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Drives the Qwen Code CLI (binary <c>qwen</c>, npm
/// <c>@qwen-code/qwen-code</c>, Apache-2.0) in non-interactive mode.
/// A fork of gemini-cli, so its surface feels familiar next to the Gemini
/// adapter — but the wire vocabulary here is Claude-shaped
/// (<c>system</c>/<c>assistant</c>/<c>result</c> with <c>session_id</c>,
/// <c>duration_ms</c>, <c>num_turns</c>), not Gemini's.
///
/// <para><b>Transport decision (verified against qwen 0.24.0).</b> The runner
/// emits <c>qwen --approval-mode yolo --auth-type openai --output-format
/// stream-json [-m &lt;model&gt;]</c> with the prompt on stdin and NO
/// positional prompt argument. <c>--output-format stream-json</c> is the
/// primary transport — not text (usage, dispatch model, and terminal error
/// shapes would be unrecoverable) and not buffered <c>json</c> (same events,
/// but nothing streams until exit, so a killed run leaves no partial
/// signal). The positional prompt form is current; <c>-p/--prompt</c> is
/// marked deprecated in <c>qwen --help</c> and is never emitted. Stdin alone
/// is a complete prompt channel (verified live: a piped-stdin prompt with no
/// positional argument answered and exited 0). Prompt-via-stdin also dodges
/// the 128 KiB MAX_ARG_STRLEN ceiling rework prompts can blow
/// through.</para>
///
/// <para><b>Exit codes (verified live: success 0, provider 401 1, paid model
/// on a $0-spend-limit key 1; documented: 53 session-turn cap, 55
/// wall-time/tool-call budget, 130 SIGINT).</b> A provider error exits 1
/// with a <c>result/subtype:"error_during_execution"</c> frame on stdout
/// plus an <c>AlreadyReportedError</c> object on stderr — no exit-zero
/// masquerade — but with no file changes the run would still terminal-fail
/// as "produced no changes" without a lifted cause. <see cref="RunAsync"/>
/// therefore lifts the terminal error region into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="QwenTerminalDiagnoser"/> (which scans BOTH streams). The
/// documented non-zero codes are mapped rather than treated as generic
/// failure: 53/55 annotate <c>TerminalDiagnostic</c> and classify as
/// <see cref="AgentFailureKind.Normal"/> with a budget/turn-cap reason, and
/// 130 classifies as <see cref="AgentFailureKind.Infrastructure"/> (external
/// interruption, not a work failure) — see
/// <see cref="ClassifyFailure"/>. A missing binary surfaces as exit 127 +
/// command-not-found, which the base class classifies as infrastructure —
/// never as "no changes".</para>
///
/// <para><b>Auth.</b> The runner pins <c>--auth-type openai</c> so dispatch
/// routes deterministically to env-key auth (the Qwen OAuth path is
/// discontinued upstream; the unset default would not resolve headless).
/// Credentials travel as direct process env: <c>OPENAI_API_KEY</c> (the
/// shipped mapping wires host <c>CODEYBOX_QWEN_API_KEY</c> to it),
/// <c>OPENAI_BASE_URL</c> (host <c>CODEYBOX_QWEN_BASE_URL</c> — the
/// OpenRouter endpoint for the shipped member), and <c>OPENAI_MODEL</c>
/// (host <c>CODEYBOX_QWEN_MODEL</c> — fallback when neither the member nor
/// the default names a model). Operators fronting a non-OpenAI-compatible
/// provider extend the mapping with that provider's variable; switching the
/// pinned <c>--auth-type</c> itself needs a code change. Trusted Folders
/// are disabled by default upstream, so there is no trust dialog to
/// defeat — and no trust override is passed (the sandbox tree is untrusted
/// repo content).</para>
///
/// <para><b>Autonomy.</b> <c>--approval-mode yolo</c> auto-approves every
/// tool call; the sandbox VM boundary is the real permission boundary.
/// <c>QWEN_CODE_UNATTENDED_RETRY=1</c> keeps the run alive past transient
/// 429/529 responses (the documented unattended combination with a
/// wall-clock budget), and <c>QWEN_CODE_SUPPRESS_YOLO_WARNING=1</c> silences
/// the yolo-no-sandbox stderr notice that would otherwise pollute every
/// capture. <c>ReasoningMode</c> is informational only: qwen has no CLI
/// effort flag (reasoning tiers live in per-model settings), so it is
/// accepted and ignored like the Gemini runner.</para>
/// </summary>
public sealed class QwenAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public QwenAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public QwenAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Qwen;

    /// <summary>
    /// Default qwen binary name inside the sandbox. Shared with
    /// <c>QwenInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "qwen";

    /// <summary>Path to the qwen binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>-m/--model</c> when the agent-class member
    /// does not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[qwen]</c>). When neither is set
    /// we omit <c>-m</c> and let qwen pick its own startup default — we never
    /// inject a hardcoded id here. A $0-spend-limit OpenRouter key only
    /// serves ids ending <c>:free</c>; a paid id fails with
    /// <c>Key limit exceeded</c>, which the detector parks as quota
    /// exhaustion rather than misrouting to a paid default.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// Unattended-retry posture: keep the run alive past transient 429/529
    /// provider responses. The documented combination pairs this with a
    /// wall-clock budget so a persistently-failing provider cannot extend
    /// the job indefinitely; the budget abort surfaces as exit 55, mapped
    /// in <see cref="ClassifyFailure"/>.
    /// </summary>
    internal const string UnattendedRetryEnvironmentVariable = "QWEN_CODE_UNATTENDED_RETRY";

    /// <summary>
    /// Silences qwen's yolo-no-sandbox startup notice on stderr. The sandbox
    /// VM boundary is the reviewed permission posture (see the class doc),
    /// so the warning would only pollute every capture.
    /// </summary>
    internal const string SuppressYoloWarningEnvironmentVariable = "QWEN_CODE_SUPPRESS_YOLO_WARNING";

    /// <summary>
    /// Distinct qwen exit codes that carry a meaning beyond generic failure
    /// (documented upstream; 53/55 verified by contract in
    /// <see cref="ClassifyFailure"/> and the terminal diagnoser).
    /// </summary>
    internal const int SessionTurnsExitCode = 53;
    internal const int BudgetExceededExitCode = 55;
    internal const int SigintExitCode = 130;

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".qwen"];

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables =>
        ["OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--output-format stream-json</c> support with
    /// <c>qwen --help</c>. The runner's only transport is the structured
    /// event stream, so a binary that no longer advertises the flag must
    /// fail closed here rather than dispatch into an unparseable plaintext
    /// run.
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
        // One-shot headless dispatch: --approval-mode yolo auto-approves
        // every tool call (the sandbox VM is the permission boundary) and
        // --auth-type openai pins env-key routing (Qwen OAuth is
        // discontinued; the unset default would not resolve headless).
        // --output-format stream-json is the ONLY transport this runner
        // speaks — even when the caller did not ask for structured capture
        // — so cost attribution (usage on message/result frames), failure
        // classification (error_during_execution), and stream parsing never
        // depend on which call path dispatched the run. The deprecated -p
        // flag is never emitted: the prompt travels on stdin (verified: a
        // stdin-only prompt answered and exited 0), which also dodges the
        // 128 KiB MAX_ARG_STRLEN ceiling.
        var argv = new List<string>
        {
            Binary,
            "--approval-mode", "yolo",
            "--auth-type", "openai",
            "--output-format", "stream-json",
        };

        // Fall back to the config-sourced default when the caller passes no
        // explicit model, mirroring GeminiAgentRunner. When neither is set
        // we omit -m and let qwen pick its own startup default — we never
        // inject a hardcoded id here.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("-m");
            argv.Add(effectiveModel);
        }

        // Qwen has no CLI reasoning-effort flag (tiers live in per-model
        // settings, not argv), so ReasoningMode is informational only —
        // accepted and ignored like the Gemini runner.
        _ = reasoningMode;
        _ = captureStructuredStream;
        _ = credential;
        return new AgentInvocation(
            argv,
            ExtraEnvironment: new Dictionary<string, string>
            {
                [UnattendedRetryEnvironmentVariable] = "1",
                [SuppressYoloWarningEnvironmentVariable] = "1",
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

        // Qwen exits non-zero on terminal run errors (verified: provider
        // 401 and paid-model-on-$0-key both exit 1), but with no file
        // changes the run would still terminal-fail as "produced no
        // changes" without a lifted cause. The provider error lands in the
        // stdout JSON frame AND as an AlreadyReportedError object on
        // stderr, so both streams are scanned. Budget/turn-cap aborts (exit
        // 55/53) carry no provider error frame; name the cause explicitly
        // so the no-changes branch parks on a legible signal instead of a
        // generic non-zero.
        if (string.IsNullOrEmpty(result.TerminalDiagnostic))
        {
            var terminalError = QwenTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr)
                ?? TryExtractExitCodeDiagnostic(result.Summary);
            if (terminalError is not null)
                return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }

    /// <summary>
    /// Maps qwen's documented non-zero codes before the shared heuristics.
    /// Implicit <see cref="IAgentRunner"/> implementation shadowing the
    /// interface default (the base class defines no override point): a
    /// turn-cap or budget abort is a bounded give-up (Normal with a legible
    /// reason), and SIGINT is an external interruption (Infrastructure), not
    /// a work failure. Anything else follows the interface-default order
    /// (execution-unavailable, auth-required, success, shared heuristics)
    /// inline so this method can never steal the exit-127 / quota / auth /
    /// network signals.
    /// </summary>
    public AgentFailureClassification ClassifyFailure(AgentResult result)
    {
        // Map qwen's documented non-zero codes before the shared heuristics:
        // a turn-cap or budget abort is a bounded give-up (Normal with a
        // legible reason), and SIGINT is an external interruption
        // (Infrastructure), not a work failure. Anything else defers to the
        // base classifier (exit-127 binary-not-found, quota/auth/network
        // shapes) so this override can never steal those signals.
        if (!result.Success && TryParseExitedCode(result.Summary) is { } exitCode)
        {
            if (exitCode == BudgetExceededExitCode)
                return new AgentFailureClassification(
                    AgentFailureKind.Normal,
                    Reason: "qwen run aborted on wall-time/tool-call budget (exit 55)");
            if (exitCode == SessionTurnsExitCode)
                return new AgentFailureClassification(
                    AgentFailureKind.Normal,
                    Reason: "qwen run exceeded the session-turn cap (exit 53)");
            if (exitCode == SigintExitCode)
                return new AgentFailureClassification(
                    AgentFailureKind.Infrastructure,
                    Reason: "qwen run interrupted by SIGINT (exit 130)");
        }

        if (result.ExecutionUnavailable)
        {
            return new AgentFailureClassification(
                AgentFailureKind.Infrastructure,
                Reason: "sandbox execution was unavailable");
        }
        if (AgentFailureClassifier.DetectAuthRequired(Kind, result.Stderr, result.Stdout) is { } authRequired)
            return authRequired.Classification;
        if (result.Success)
            return new AgentFailureClassification(AgentFailureKind.Normal);
        return AgentFailureClassifier.Classify(Kind, result.Stderr, result.Stdout, result.Summary);
    }

    private static string? TryExtractExitCodeDiagnostic(string? summary)
    {
        if (TryParseExitedCode(summary) is not { } exitCode)
            return null;
        return exitCode switch
        {
            BudgetExceededExitCode => "qwen run aborted on wall-time/tool-call budget (exit 55)",
            SessionTurnsExitCode => "qwen run exceeded the session-turn cap (exit 53)",
            SigintExitCode => "qwen run interrupted by SIGINT (exit 130)",
            _ => null,
        };
    }

    private static int? TryParseExitedCode(string? summary)
    {
        // CliAgentRunnerBase summarises failures as "agent exited {code}".
        if (string.IsNullOrEmpty(summary))
            return null;
        const string prefix = "agent exited ";
        var index = summary.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var rest = summary[(index + prefix.Length)..].TrimStart();
        var digits = new string(rest.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var code) ? code : null;
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
            "OPENAI_API_KEY");

    // The qwen CLI runs inside the work-item sandbox; a host-side text-only
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
