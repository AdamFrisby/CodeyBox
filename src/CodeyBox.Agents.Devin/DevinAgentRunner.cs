using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Drives the Devin CLI (binary <c>devin</c>, installed by
/// <c>https://cli.devin.ai/install.sh</c>) in non-interactive print mode:
/// <c>devin -p</c> with the prompt on stdin via
/// <c>--prompt-file /dev/stdin</c>.
///
/// <para><b>Transport decision (verified against devin 3000.11.1).</b>
/// <c>-p/--print [&lt;PROMPT&gt;]</c> runs a single non-interactive session and
/// exits. A bare <c>-p</c> does NOT read a piped prompt from stdin — verified
/// live that stdin is ignored unless <c>--prompt-file /dev/stdin</c> is passed,
/// which reads the pipe correctly. The prompt therefore travels on stdin via a
/// prompt file rather than positional <c>-- &lt;prompt&gt;</c> argv: Linux's
/// <c>MAX_ARG_STRLEN</c> is 128 KiB per argv element and rework prompts can
/// exceed it (same constraint as opencode/aider). Print mode writes the
/// assistant's answer as plain text on stdout — there is no stream-json flag —
/// so no structured-stream capture is offered.</para>
///
/// <para><b>Autonomy.</b> <c>--permission-mode</c> accepts
/// <c>auto|accept-edits|smart|dangerous</c> (default <c>auto</c>, read-only
/// tools only). A headless run has no human to approve anything, so the runner
/// passes <c>dangerous</c> (auto-approves all tools) — the VM boundary is the
/// security perimeter, matching <c>cursor --force</c> /
/// <c>claude --dangerously-skip-permissions</c>.
/// <c>--respect-workspace-trust false</c> is REQUIRED: print mode cannot show
/// the workspace-trust prompt and fails outright in an untrusted directory
/// (verified: exits nonzero with a trust error without it).</para>
///
/// <para><b>Model selection.</b> <c>--model &lt;id&gt;</c> (or the
/// <c>DEVIN_MODEL</c> env var) selects the model; when neither is configured
/// the CLI uses the account's server-side default. The runner passes the
/// member's <c>ModelId</c>, else the config-sourced
/// <see cref="DefaultModelId"/>, else omits the flag.</para>
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
/// <c>~/.local/share/devin/cli/sessions.db</c>; <c>-c</c>/<c>-r</c> resume them.
/// The scratchpad allowlist captures the database (and its WAL/SHM siblings)
/// so a preempted/turn-checkpointed sandbox keeps session state, but the
/// resume hook is not wired: <c>-c</c> combined with <c>-p</c> is unverified,
/// so a restored run re-dispatches fresh like the other file-state agents.
/// Terminal failures are surfaced on stderr as <c>Error: …</c> lines (verified:
/// "Error: Not logged in", auth/ACP failures); <see cref="RunAsync"/> lifts the
/// first such line into <see cref="AgentResult.TerminalDiagnostic"/> so the
/// pipeline parks quota/auth failures instead of dead-lettering them.</para>
/// </summary>
public sealed class DevinAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
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
    /// Default model passed to <c>--model</c> when no per-item override is
    /// provided. Sourced live from <see cref="AgentDefaultsSnapshot"/> (config
    /// key <c>CodeyBox:AgentDefaults[devin]</c>). When neither is set the flag
    /// is omitted and the account's server-side default applies.
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

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        var argv = new List<string>(FullAutonomyInvocationPrefix(Binary));

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // The Devin CLI has no reasoning-effort flag; ReasoningMode is accepted
        // so the agent-class config schema stays uniform but is not threaded
        // into argv.
        _ = reasoningMode;
        _ = credential;
        _ = captureStructuredStream;

        return PromptFileInvocation(argv, prompt);
    }

    /// <summary>
    /// Sandbox path template for the prompt file. A per-run <c>mktemp</c> name
    /// under <c>$TMPDIR</c>, so two phases in one sandbox never collide.
    /// </summary>
    private const string PromptFileTemplate = "${TMPDIR:-/tmp}/codeybox-devin-prompt.XXXXXX";

    /// <summary>
    /// Wraps a devin argv so the prompt reaches the CLI as a file it can open,
    /// and returns the invocation to execute.
    ///
    /// <para><b>Why not <c>--prompt-file /dev/stdin</c>.</b> That path must be
    /// RE-OPENED by the CLI, and the in-VM exec wrapper pipes stdin in before
    /// dropping to the sandbox user, so the open fails
    /// <c>Permission denied (os error 13)</c> and every dispatch dies in under
    /// a second. The pipe itself is delivered fine — only re-opening it is
    /// refused.</para>
    ///
    /// <para><b>Why the prompt still arrives on stdin.</b> <c>MAX_ARG_STRLEN</c>
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
    /// runs <paramref name="devinArgv"/> against it. Public so
    /// <c>DevinInVmSmokeProbe</c> exercises the exact prompt path a real
    /// dispatch uses — a probe that passed while dispatch failed is what let
    /// the <c>/dev/stdin</c> fault reach production.
    /// </summary>
    public static string BuildPromptFileScript(IReadOnlyList<string> devinArgv)
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

    /// <summary>
    /// The leading argv every real workspace invocation uses:
    /// <c>devin -p --permission-mode dangerous --respect-workspace-trust
    /// false</c>. Extracted as a single builder so <see cref="BuildInvocation"/>
    /// (real dispatch) and <c>DevinInVmSmokeProbe</c> (the in-VM turn check)
    /// construct the exact same prefix.
    /// </summary>
    public static IReadOnlyList<string> FullAutonomyInvocationPrefix(string binary) =>
        [binary, "-p", "--permission-mode", "dangerous", "--respect-workspace-trust", "false"];

    protected override AgentInvocation BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
    {
        _ = credential;
        // Text-only calls omit --permission-mode (the CLI default `auto`
        // auto-approves read-only tools only), so the run can answer about the
        // worktree but cannot write it or run arbitrary commands on untrusted
        // merge-conflict/resolver input — the same conservative shape cursor's
        // text-only path takes when it drops --force.
        var argv = new List<string> { Binary, "-p", "--respect-workspace-trust", "false" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
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

        // Devin reports run failures on stderr as `Error: …` lines (verified
        // against devin 3000.11.1: auth failures exit nonzero with
        // "Error: Not logged in…"). Lift the terminal error so the pipeline can
        // classify it; without this a quota/auth give-up with no file changes
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && DevinTerminalDiagnoser.TryExtractTerminalError(result.Stderr, result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }
}
