using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Drives the Devin CLI (binary <c>devin</c>, installed by
/// <c>https://cli.devin.ai/install.sh</c>) over Agent Client Protocol:
/// <c>devin acp</c> is a stdio JSON-RPC server (verified against devin
/// 3000.11.1), so the dispatch exec runs an embedded Python 3 client shim
/// (<see cref="DevinAcpShim"/>, <c>Resources/devin-acp-client.py</c>) that
/// performs the <c>initialize</c> → <c>session/new</c> →
/// <c>session/set_mode</c> → <c>session/prompt</c> handshake and folds every
/// inbound ACP frame into a single-line <c>devin.acp</c> NDJSON envelope on
/// stdout.
///
/// <para><b>Why ACP (verified against devin 3000.11.1).</b>
/// <c>-p/--print</c> writes the assistant's answer as plain text only when
/// the run ends — there is no incremental stream — so a long turn (e.g. a
/// full test suite) never touched the agent-stream file and the
/// worker-progress watchdog recycled live runs as stuck. ACP emits a
/// <c>session/update</c> notification per tool call, message chunk, and
/// usage tick; the shim flushes each as an envelope, so stream mtime
/// advances for the life of the turn and a genuinely wedged run still
/// stalls (and is recycled) exactly as before.</para>
///
/// <para><b>Reuse note.</b> The repo's other ACP transport
/// (<c>AcpClaudeTransport</c> + the NativeAOT <c>claude-acp-bridge</c>)
/// impersonates an IDE WebSocket for <c>claude --ide</c>; it cannot speak
/// to a stdio ACP server, so devin gets its own thin client — the two share
/// the framed-stdin payload delivery contract
/// (<c>CodeyBox.Agents.FramedStdin</c>).</para>
///
/// <para><b>Autonomy.</b> ACP sessions expose modes
/// (<c>accept-edits|smart|ask|plan|bypass</c>); the global
/// <c>--permission-mode</c> flag does NOT apply to <c>devin acp</c>
/// sessions (verified: the session still opens in <c>accept-edits</c>), so
/// the shim issues <c>session/set_mode bypass</c> before the prompt — the
/// ACP equivalent of print mode's <c>--permission-mode dangerous</c>,
/// matching <c>cursor --force</c> /
/// <c>claude --dangerously-skip-permissions</c>: a headless run has no human
/// to approve tools and the VM boundary is the security perimeter.
/// <c>session/request_permission</c> requests that still arrive are
/// answered with the most durable allow option as a defence in depth.</para>
///
/// <para><b>Model selection.</b> <c>devin acp --model &lt;id&gt;</c> pins
/// the session model, exactly like print mode's <c>--model</c>. The runner
/// passes the member's <c>ModelId</c>, else the config-sourced
/// <see cref="DefaultModelId"/>, else omits the flag (account server-side
/// default). The shim scrubs <c>DEVIN_REFUSAL_FALLBACK</c> and
/// <c>DEVIN_MODEL</c> from the child environment: the first switches
/// refused requests to OTHER Devin models (paid), the second silently
/// picks the session model when no flag is passed — a dispatch must never
/// drift off the configured model.</para>
///
/// <para><b>Prompt delivery.</b> The prompt travels on stdin after the
/// base64 shim block and is piped into a <c>mktemp</c> file the shim reads;
/// it never enters argv or the environment (<c>MAX_ARG_STRLEN</c> is 128 KiB
/// per element and rework prompts exceed it — same constraint as
/// opencode/aider, and the reason positional <c>-- &lt;prompt&gt;</c> argv
/// is not used).</para>
///
/// <para><b>Auth.</b> The CLI reads <c>~/.local/share/devin/credentials.toml</c>
/// (XDG data dir) written by <c>devin auth login</c>/<c>auth import</c>; there
/// is NO env-var alternative (<c>DEVIN_API_KEY</c> does not exist in the
/// binary). The orchestrator ships the file's contents to the sandbox via
/// <c>CODEYBOX_DEVIN_AUTH_TOML</c> and the runner materialises it inside the
/// VM at the matching XDG path before invoking the binary. When the env var is
/// absent this is a no-op and image-provisioned auth applies.</para>
///
/// <para><b>Sessions.</b> The CLI persists sessions in a sqlite database at
/// <c>~/.local/share/devin/cli/sessions.db</c>; ACP advertises
/// <c>loadSession</c> but the resume hook is not wired — a restored run
/// re-dispatches fresh like the other file-state agents. Terminal failures
/// surface either as shim <c>turn_error</c>/<c>fatal</c> envelopes
/// (<see cref="DevinAcpOutcome"/>) or as <c>Error: …</c> lines lifted by
/// <see cref="DevinTerminalDiagnoser"/>; <see cref="RunAsync"/> lifts the
/// first into <see cref="AgentResult.TerminalDiagnostic"/> so the pipeline
/// parks quota/auth failures instead of dead-lettering them.</para>
///
/// <para><b>Envelope provenance.</b> <c>devin.acp</c> lines are claimed by
/// type tag, so nothing agent-controlled may ever land as a bare line on the
/// claimable stdout stream. Two mechanisms enforce that: the shim retargets
/// its whole stderr surface (inherited by the CLI and every tool subprocess)
/// through a relay that re-emits each line as a <c>codeybox.stderr</c>
/// envelope — provenance at the emission point, safe even where the exec
/// wrapper's log-file tee merges streams — and
/// <c>AgentInvocation.StdoutIsEnvelopeFramed</c> pins the dispatch to the
/// attached exec-pipe transport so no bearer credential exists in-VM that
/// could POST forged bytes into the stream. See
/// <see cref="DevinAcpEnvelope.IsEnvelope"/> for the residual trust boundary.
/// </para>
/// </summary>
public sealed class DevinAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner, IStructuredStreamAgentRunner
{
    /// <summary>
    /// Sandbox environment variable carrying the Devin credentials.toml
    /// contents from the orchestrator's credential bundle.
    /// </summary>
    public const string AuthTomlEnvironmentVariable = "CODEYBOX_DEVIN_AUTH_TOML";

    private static readonly EnvBackedCredentialFile AuthCredentialFile = new(
        AuthTomlEnvironmentVariable,
        ".local/share/devin/credentials.toml",
        "devin credentials",
        MaterialiseFromSandboxEnvironmentWhenCredentialMissing: true);
    private readonly AgentDefaultsSnapshot? _defaults;

    public DevinAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>--model</c> value
    /// when a caller does not pass an explicit model, so the dispatch model is
    /// sourced from hot-reloadable config rather than a hardcoded literal.
    /// </param>
    public DevinAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Devin;

    /// <summary>
    /// Default Devin CLI binary name inside the sandbox. Shared with
    /// <c>DevinInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "devin";

    /// <summary>Path to the devin binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// ACP session mode applied via <c>session/set_mode</c> for full-autonomy
    /// workspace dispatches — the ACP equivalent of print mode's
    /// <c>--permission-mode dangerous</c> (verified against devin 3000.11.1:
    /// <c>bypass</c> is the CLI's "Bypass Permissions" mode and auto-approves
    /// all tools).
    /// </summary>
    public const string FullAutonomyAcpMode = "bypass";

    /// <summary>
    /// Default model passed to <c>devin acp --model</c> when no per-item
    /// override is provided. Sourced live from
    /// <see cref="AgentDefaultsSnapshot"/> (config key
    /// <c>CodeyBox:AgentDefaults[devin]</c>). When neither is set the flag is
    /// omitted and the account's server-side default applies.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// Bash/Python 3 materialiser for Devin's credentials file in the sandbox
    /// at <c>~/.local/share/devin/credentials.toml</c> from
    /// <c>CODEYBOX_DEVIN_AUTH_TOML</c>. Shared verbatim with
    /// <c>DevinInVmSmokeProbe</c> so the smoke probe exercises the exact same
    /// destination path as a real dispatch.
    /// </summary>
    public static readonly string AuthMaterialiseScript = BuildEnvBackedCredentialScript(AuthCredentialFile);

    // The sqlite session database (and WAL/SHM sidecars) is the CLI's only
    // per-directory state worth preserving across a preempt/checkpoint. The
    // sibling `cli/_versions` tree holds the toolchain itself and is
    // deliberately excluded — archiving installed binaries would balloon the
    // scratchpad.
    protected override IReadOnlyList<string> ScratchpadHomeDirectories =>
    [
        ".local/share/devin/cli/sessions.db",
        ".local/share/devin/cli/sessions.db-wal",
        ".local/share/devin/cli/sessions.db-shm",
    ];

    protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles => [AuthCredentialFile];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Structured-stream capability probe: the runner's only dispatch
    /// transport is <c>devin acp</c>, whose session-update envelopes ARE the
    /// structured stream. <c>devin acp --help</c> exits 0 on builds that ship
    /// the subcommand (3000.11.1 onward); older CLIs fail the probe and the
    /// pipeline falls back to plaintext capture — while the dispatch itself
    /// fails fast in the shim spawn stage rather than silently re-running
    /// the stream-less print mode that tripped the watchdog.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "acp", "--help"],
        }, ct).ConfigureAwait(false);

        return help.Success;
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // The Devin CLI has no reasoning-effort flag; ReasoningMode is accepted
        // so the agent-class config schema stays uniform but is not threaded
        // into argv. ACP envelopes are the only output mode this runner
        // speaks, so captureStructuredStream needs no argv switch — but the
        // envelope framing is still declared so the exec layer keeps stderr
        // off the claimable channel end to end (host-side wrapping, the
        // wrapper's no-merge tee, and the attached exec-pipe transport) — a
        // model-controlled stderr line shaped like devin.acp output would
        // otherwise forge stream events and falsify cost records.
        _ = reasoningMode;
        _ = credential;
        _ = captureStructuredStream;

        return new AgentInvocation(
            ["bash", "-c", BuildAcpDispatchScript(AcpShimArgs(Binary, EffectiveModelId(modelId), FullAutonomyAcpMode))],
            Stdin: DevinAcpShim.BuildDispatchStdin(prompt),
            StdoutIsEnvelopeFramed: true);
    }

    private string? EffectiveModelId(string? modelId)
        => !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;

    /// <summary>
    /// Sandbox path template for the per-run ACP working directory. A per-run
    /// <c>mktemp</c> dir under <c>$TMPDIR</c>, so two phases in one sandbox
    /// never collide.
    /// </summary>
    private const string AcpDirTemplate = "${TMPDIR:-/tmp}/codeybox-devin-acp.XXXXXX";

    /// <summary>
    /// The shim argv (after the script path) for one ACP turn.
    /// <paramref name="modelId"/> is passed verbatim to
    /// <c>devin acp --model</c> — the configured swe-2 id, never a fallback;
    /// omit when null so the account server-side default applies.
    /// <paramref name="mode"/> is applied via <c>session/set_mode</c> before
    /// the prompt (<see cref="FullAutonomyAcpMode"/> for dispatches).
    /// </summary>
    internal static IReadOnlyList<string> AcpShimArgs(string binary, string? modelId, string? mode)
    {
        var args = new List<string> { "--binary", binary };
        if (!string.IsNullOrEmpty(modelId))
        {
            args.Add("--model");
            args.Add(modelId);
        }
        if (!string.IsNullOrEmpty(mode))
        {
            args.Add("--mode");
            args.Add(mode);
        }
        return args;
    }

    /// <summary>
    /// The <c>bash -c</c> script that materialises the embedded shim and the
    /// piped prompt into a private <c>mktemp</c> dir, then runs the shim.
    /// Internal so <c>DevinInVmSmokeProbe</c> (same assembly) exercises the
    /// exact dispatch path — a probe that passed while dispatch failed is
    /// what let the <c>/dev/stdin</c> fault reach production.
    ///
    /// <para>Stdin is framed as
    /// <see cref="DevinAcpShim.BuildDispatchStdin"/> produces: base64 shim
    /// lines, the end-marker line, then the verbatim prompt which
    /// <c>cat</c> pipes to the prompt file. <c>umask 077</c> + the EXIT trap
    /// keep the materialised files 0600 and remove them however the turn
    /// ends, so a later phase sharing the sandbox cannot read a previous
    /// prompt. Python is NOT <c>exec</c>'d, so the trap still runs; bash then
    /// exits with the shim's own status, and
    /// <see cref="PreemptProcessPattern"/> still matches the
    /// <c>devin acp</c> child process.</para>
    /// </summary>
    internal static string BuildAcpDispatchScript(IReadOnlyList<string> shimArgs)
    {
        ArgumentNullException.ThrowIfNull(shimArgs);
        if (shimArgs.Count == 0)
            throw new ArgumentException("Devin ACP shim args must be non-empty.", nameof(shimArgs));

        return string.Join('\n',
            "set -eu",
            "umask 077",
            $"cb_dir=$(mktemp -d \"{AcpDirTemplate}\")",
            "trap 'rm -rf \"$cb_dir\"' EXIT INT TERM",
            "cb_shim=\"$cb_dir/acp_client.py\"",
            "cb_shim_b64=\"$cb_dir/acp_client.b64\"",
            "cb_prompt=\"$cb_dir/prompt.txt\"",
            FramedStdin.BashReaderBlock(DevinAcpShim.StdinEndMarker, "cb_shim_b64", "cb_found"),
            "[ \"$cb_found\" = 1 ] || { echo 'missing devin acp shim terminator' >&2; exit 1; }",
            "base64 -d \"$cb_shim_b64\" > \"$cb_shim\"",
            "cat > \"$cb_prompt\"",
            "python3 \"$cb_shim\" --prompt-file \"$cb_prompt\" "
                + string.Join(' ', shimArgs.Select(ShellQuote)));
    }

    /// <summary>
    /// Sandbox path template for the print-mode prompt file used by the
    /// text-only path. A per-run <c>mktemp</c> name under <c>$TMPDIR</c>, so
    /// two phases in one sandbox never collide.
    /// </summary>
    private const string PromptFileTemplate = "${TMPDIR:-/tmp}/codeybox-devin-prompt.XXXXXX";

    /// <summary>
    /// Wraps a devin argv so the prompt reaches the CLI as a file it can open,
    /// and returns the invocation to execute. Used by the text-only path,
    /// which stays on print mode: <c>devin -p</c> answers about the worktree
    /// without exposing a tool runtime to untrusted resolver input.
    ///
    /// <para><b>Why the prompt arrives on stdin.</b> <c>MAX_ARG_STRLEN</c>
    /// (128 KiB per element) caps argv <i>and</i> the environment alike, and
    /// rework prompts exceed it, so neither can carry the prompt. <c>cat</c>
    /// reads the already-open descriptor 0 rather than re-opening it, which is
    /// the operation the sandbox user is allowed to perform, and writes the
    /// bytes to a file that the CLI can then open normally. The prompt never
    /// enters argv, the environment, or <c>/proc/&lt;pid&gt;/environ</c>.</para>
    ///
    /// <para><c>umask 077</c> and <c>mktemp</c> keep the file 0600, and the
    /// trap removes it however the turn ends, so a later phase sharing the
    /// sandbox cannot read a previous prompt. The CLI is NOT <c>exec</c>'d, so
    /// that trap still runs; bash then exits with devin's own status, and
    /// <see cref="PreemptProcessPattern"/> still matches the child process.</para>
    /// </summary>
    private static AgentInvocation PromptFileInvocation(IReadOnlyList<string> devinArgv, string prompt)
        => new(["bash", "-c", BuildPromptFileScript(devinArgv)], Stdin: prompt);

    /// <summary>
    /// The <c>bash -c</c> script body that materialises the piped prompt and
    /// runs <paramref name="devinArgv"/> against it (print mode, text-only
    /// calls only — workspace dispatches go through
    /// <see cref="BuildAcpDispatchScript"/>).
    /// </summary>
    private static string BuildPromptFileScript(IReadOnlyList<string> devinArgv)
    {
        ArgumentNullException.ThrowIfNull(devinArgv);
        if (devinArgv.Count == 0)
            throw new ArgumentException("Devin argv must be non-empty.", nameof(devinArgv));

        var command = string.Join(' ', devinArgv.Select(ShellQuote));
        return string.Join('\n',
            "set -eu",
            "umask 077",
            $"cb_prompt=$(mktemp \"{PromptFileTemplate}\")",
            "trap 'rm -f \"$cb_prompt\"' EXIT INT TERM",
            "cat > \"$cb_prompt\"",
            command + " --prompt-file \"$cb_prompt\"");
    }

    protected override AgentInvocation BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
    {
        _ = credential;
        // Text-only calls stay on print mode and omit --permission-mode (the
        // CLI default `auto` auto-approves read-only tools only), so the run
        // can answer about the worktree but cannot write it or run arbitrary
        // commands on untrusted merge-conflict/resolver input — the same
        // conservative shape cursor's text-only path takes when it drops
        // --force.
        var argv = new List<string> { Binary, "-p", "--respect-workspace-trust", "false" };

        var effectiveModel = EffectiveModelId(modelId);
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        _ = reasoningMode;
        return PromptFileInvocation(argv, prompt);
    }

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
        => GetSandboxSubscriptionTextOnlyUnavailabilityReason(
            credential,
            AuthTomlEnvironmentVariable);

    // The Devin CLI runs inside the work-item sandbox; a host-side text-only
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

        return PostProcessAcpResult(result);
    }

    public override async Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null)
    {
        // Checkpoint-resumed dispatches drive the same ACP shim, so they get
        // the same outcome verification and typed-failure lifting.
        var result = await base.RunResumedAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            resume,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback).ConfigureAwait(false);

        return PostProcessAcpResult(result);
    }

    /// <summary>
    /// Maps the exec result through the shim's terminal outcome. Two failure
    /// surfaces exist: the shim reports protocol/turn failures as typed
    /// terminal envelopes (<c>turn_error</c> / <c>fatal</c>), and the CLI
    /// itself reports auth/startup failures on stderr as <c>Error: …</c>
    /// lines (verified against devin 3000.11.1: "Error: Not logged in…"
    /// exits nonzero before ACP starts). Whichever fired is lifted into
    /// <see cref="AgentResult.TerminalDiagnostic"/> so the pipeline can
    /// classify it; without this a quota/auth give-up with no file changes
    /// terminal-fails as "produced no changes".
    /// </summary>
    private static AgentResult PostProcessAcpResult(AgentResult result)
    {
        if (!string.IsNullOrEmpty(result.TerminalDiagnostic))
            return result;

        var outcome = DevinAcpOutcome.Extract(result.Stdout);

        // A zero exit without the shim's terminal envelope means the run was
        // cut short before the turn outcome was written (killed shim, capped
        // stdout) — honest failure, not a silent success. The flip runs
        // before the diagnostic lifts so a turn_error/fatal envelope can
        // never keep Success=true even if the exec layer reported 0.
        if (result.Success && outcome.Event != DevinAcpOutcome.TerminalEvent.TurnComplete)
        {
            result = result with
            {
                Success = false,
                Summary = "devin acp exited without reporting a turn outcome",
            };
        }

        if (outcome.Diagnostic is { } acpFailure)
            return result with { TerminalDiagnostic = acpFailure };

        if (DevinTerminalDiagnoser.TryExtractTerminalError(result.Stderr, result.Stdout) is { } terminalError)
            return result with { TerminalDiagnostic = terminalError };

        return result;
    }
}
