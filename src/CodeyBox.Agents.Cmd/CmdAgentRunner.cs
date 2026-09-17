using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Drives the Command Code CLI (binary <c>cmd</c>, npm
/// <c>command-code</c>) in headless print mode:
/// <c>cmd --local-only -p --output-format json --no-session
/// --skip-onboarding --yolo</c> with the prompt on stdin and <c>-m</c> from
/// the agent-class member or the config-sourced default.
///
/// <para><b>Transport decision (verified against command-code 1.54.2).</b>
/// <c>-p/--print</c> is the documented headless contract ("outputs the
/// response to stdout, and exits"); a bare <c>cmd "prompt"</c> starts an
/// interactive session. Raw <c>-p</c> prints only the final response text,
/// so usage, the dispatch model id, and the terminal error shape would be
/// unrecoverable — <c>--output-format json</c> is the ONLY transport this
/// runner speaks, even when the caller did not ask for structured capture.
/// The stream is NDJSON frames
/// (<c>{"type":"event","event":{"type":"run_start"|"turn_start"|…}}</c> plus
/// a terminal <c>{"type":"result","subtype":"success"|"error"|"max_turns",…}</c>
/// line) carrying per-turn <c>usage {inputTokens, outputTokens,
/// cacheReadTokens}</c> and the full dispatch model id on
/// <c>model_request_start</c>. The prompt travels on stdin with no
/// positional query argument (verified live): Linux's
/// <c>MAX_ARG_STRLEN</c> is 128 KiB per argv element and rework prompts can
/// exceed it, and the multi-word query <em>must</em> be quoted on argv
/// anyway — stdin sidesteps both hazards.</para>
///
/// <para><b>Autonomy (verified live — the trap).</b> Headless mode blocks
/// file writes, file edits, and shell commands by default (reads/grep/glob
/// stay allowed). Without a permission flag the agent reads and talks but
/// changes nothing — exit 0, <c>subtype: "success"</c>, no diff — which the
/// pipeline would record as a blank pass, not a configuration error. The
/// runner therefore always passes <c>--yolo</c> (alias for
/// <c>--dangerously-skip-permissions</c>): the sandbox VM is disposable and
/// sits behind the host-enforced egress allowlist, which is exactly the
/// trusted environment the vendor docs require for that flag. The
/// fail-closed <c>dont-ask</c> allowlist mode was evaluated and rejected
/// for the coding path: it <em>denies</em> prompt-gated requests, which
/// would block the writes a work item exists to produce (it fits read-only
/// CI, not an editing agent). <c>--tools-all</c> is deliberately NOT
/// passed: the withheld headless tools are interactive-oriented
/// (approval-seeking), and the verified write path needs only
/// <c>--yolo</c>.</para>
///
/// <para><b>Plan-less BYOK (verified live — not vendor-locked).</b> The
/// runner always passes <c>--local-only</c> ("no Command Code traffic",
/// same as <c>CMD_LOCAL_ONLY=1</c>): billing reads never run, the Command
/// Code transport refuses, and telemetry is off, so a BYOK run touches only
/// the provider endpoint. Print mode still gates on the <em>presence</em>
/// of the Command Code account key, so
/// <see cref="PrepareAgentSandboxAsync"/> seeds <c>~/.commandcode/auth.json</c>
/// with the documented non-credential placeholder (see
/// <see cref="LocalOnlyAuthPlaceholder"/> and <see cref="CmdConfigBuilder"/>)
/// alongside the <c>providers.json</c> openrouter entry (key as a
/// <c>$OPENROUTER_API_KEY</c> environment reference — never a raw secret).
/// Verified: placeholder + <c>--local-only</c> + real OpenRouter key
/// completed real runs with no Command Code plan.</para>
///
/// <para><b>Exit codes (verified live: 0 ok, 1 model/key/config error, 3 no
/// auth, 4 spend-cap refusal, 8 max-turns).</b> <see cref="RunAsync"/> lifts
/// the terminal error region into <see cref="AgentResult.TerminalDiagnostic"/>
/// via <see cref="CmdTerminalDiagnoser"/> (which scans BOTH streams: the
/// config-shape failures land on stderr with empty stdout) <em>and</em> the
/// permission-gate signal: a <c>tool_hook_blocked</c> run exits 0 with
/// success subtype but no changes, so the blocked-write count is lifted too
/// — a run without the permission flag is never misreported as a successful
/// empty result. A missing binary surfaces as exit 127 +
/// command-not-found, which the base class classifies as infrastructure (see
/// <c>ClassifyFailure</c>) — never as "no changes".</para>
///
/// <para><b>Reasoning effort is deliberately NOT mapped.</b> <c>--effort</c>
/// exists, but the shipped free-tier model rejects it
/// (<c>… has no adjustable reasoning effort</c>, exit 1 — verified live),
/// so emitting it could fail dispatches the way kilo's <c>--variant</c>
/// would. Same rationale, same posture: ignore
/// <paramref name="reasoningMode"/> rather than pass it through.</para>
/// </summary>
public sealed class CmdAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public CmdAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>-m</c> value
    /// (and the seeded guest-config <c>models</c> entry) when a caller does
    /// not pass an explicit model, so the dispatch model is sourced from
    /// hot-reloadable config rather than a hardcoded literal.
    /// </param>
    public CmdAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Cmd;

    /// <summary>
    /// Default cmd binary name inside the sandbox. Shared with
    /// <c>CmdInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "cmd";

    /// <summary>Path to the cmd binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. It gates dispatch
    /// (the CLI resolves the seeded <c>$OPENROUTER_API_KEY</c> reference
    /// from the sandbox environment) and is read by the CLI only through
    /// that reference — the runner never emits the key on argv or writes it
    /// into the guest config files.
    /// </summary>
    public const string CredentialVariable = "OPENROUTER_API_KEY";

    /// <summary>
    /// Non-credential placeholder seeded as <c>~/.commandcode/auth.json
    /// apiKey</c>. Satisfies only the print-mode presence gate for plan-less
    /// <c>--local-only</c> BYOK runs (verified live); the runner always
    /// passes <c>--local-only</c>, under which the value is never
    /// transmitted anywhere. Must never be replaced with a real credential
    /// without also revisiting the local-only posture documented on
    /// <see cref="CmdConfigBuilder"/>.
    /// </summary>
    public const string LocalOnlyAuthPlaceholder = "codeybox-local-only";

    /// <summary>
    /// Default model passed to <c>-m</c> (and seeded into the guest config
    /// <c>models</c> map) when the agent-class member does not override it.
    /// Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[cmd]</c>). Stored in full
    /// <c>openrouter/…</c> form — BYOK models are provider-qualified and the
    /// stream echoes the full dispatch id, so the bare id would neither
    /// resolve the same route nor match the cost-attribution key. When
    /// neither is set the flag is omitted and the CLI fails closed on its
    /// own startup default — a model id is never invented here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => [CredentialVariable];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Seeds the guest <c>~/.commandcode/providers.json</c> (openrouter
    /// entry with the <c>$OPENROUTER_API_KEY</c> reference plus the
    /// config-sourced default and curated model metadata) and
    /// <c>~/.commandcode/auth.json</c> (presence-gate placeholder — see
    /// <see cref="LocalOnlyAuthPlaceholder"/>) before the CLI runs. The
    /// interactive <c>/connect</c> flow cannot run headless, so the runner
    /// writes both files during provisioning instead. Payloads travel
    /// through <see cref="SandboxCredentialFileWriter"/> (stdin transport,
    /// mode 0600) and carry no secret material: the provider key stays an
    /// environment reference and the auth value is a non-credential
    /// constant.
    /// </summary>
    protected override async Task<AgentResult?> PrepareAgentSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        _ = workingDirectory;
        _ = credential;
        _ = resume;

        // NOTE: the seeded models map carries the config-sourced default
        // plus the curated seed; undeclared dispatch ids are "sent anyway"
        // (verified), so a per-member -m outside this union still dispatches
        // (without declared metadata) rather than failing closed.
        var models = new List<string?>(CmdKnownModels.All.Count + 1);
        var defaultModel = DefaultModelId;
        if (!string.IsNullOrWhiteSpace(defaultModel))
            models.Add(defaultModel);
        models.AddRange(CmdKnownModels.All);

        // Neither builder takes fallible input (the endpoint is a fixed
        // vendor fact, the key stays an environment reference, the auth
        // value is a constant), so only the sandbox writes below can fail.
        var providersJson = CmdConfigBuilder.BuildProvidersJson(models);
        var authJson = CmdConfigBuilder.BuildAuthJson(LocalOnlyAuthPlaceholder);

        try
        {
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    CmdConfigBuilder.GuestProvidersRelativePath),
                providersJson,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    CmdConfigBuilder.GuestAuthRelativePath),
                authJson,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
        catch (SandboxCredentialFileWriteException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise cmd config: exit {ex.ExitCode}",
                Stdout: ex.Stdout,
                Stderr: ex.Stderr)
            {
                ExecutionUnavailable = ex.ExecutionUnavailable,
            };
        }

        return null;
    }

    /// <summary>
    /// Verifies <c>--output-format json</c> support with <c>cmd -p
    /// --help</c>. The runner's only transport is the JSON event stream, so
    /// a binary that no longer advertises the flag must fail closed here
    /// rather than dispatch into an unparseable plaintext run.
    /// </summary>
    public async Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        var help = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [Binary, "-p", "--help"],
        }, ct).ConfigureAwait(false);

        if (!help.Success)
            return false;

        var output = string.Concat(help.Stdout, "\n", help.Stderr);
        return output.Contains("--output-format", StringComparison.Ordinal)
            && output.Contains("json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: `-p` with no query argument reads the
        // piped-stdin prompt, acts on the repository, and exits.
        // --output-format json is the ONLY transport this runner speaks —
        // even when the caller did not ask for structured capture — so cost
        // attribution (usage on model_request_end/turn_end/result), failure
        // classification (run_error / subtype:error / stderr Error: lines),
        // and stream parsing never depend on which call path dispatched the
        // run. --local-only is MANDATORY (plan-less BYOK posture: no Command
        // Code traffic, so the auth placeholder is never transmitted — see
        // CmdConfigBuilder). --yolo is MANDATORY: without it headless mode
        // blocks writes/edits/shell and the run exits 0 with no changes
        // (verified — the blank-pass trap). --no-session keeps the ephemeral
        // VM from accumulating session files; --skip-onboarding keeps taste
        // onboarding out of automated runs.
        var argv = new List<string>
        {
            Binary,
            "--local-only",
            "-p",
            "--output-format", "json",
            "--no-session",
            "--skip-onboarding",
            "--yolo",
        };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort is deliberately NOT mapped: --effort exists but
        // the shipped free-tier model rejects it ("has no adjustable
        // reasoning effort", exit 1 — verified live), so emitting one could
        // fail dispatches. Same rationale as kilo's --variant.
        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
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

        // Lift the terminal error frame so the pipeline can classify it;
        // without this a quota/auth give-up with no file changes
        // terminal-fails as "produced no changes". The diagnoser scans BOTH
        // streams (config-shape failures land on stderr with empty stdout).
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && CmdTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        // Lift the headless permission-gate signal: a tool_hook_blocked run
        // exits 0 with subtype success but no changes, so without this lift
        // a run without the permission flag would read as a successful empty
        // result instead of a configuration failure. The runner always passes
        // --yolo, so a non-zero count means the flag was lost (stale image,
        // wrapper) — never a model outcome. A terminal error above already
        // explains the outcome and keeps priority.
        if (string.IsNullOrEmpty(result.TerminalDiagnostic))
        {
            var blocked = CmdTerminalDiagnoser.CountPermissionBlockedCalls(result.Stdout);
            if (blocked > 0)
            {
                return result with
                {
                    TerminalDiagnostic =
                        $"cmd blocked {blocked} write/shell tool call(s) behind the headless permission gate " +
                        "(tool_hook_blocked — headless mode without --yolo); no file writes or shell commands could run",
                };
            }
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
            CredentialVariable);

    // The cmd CLI runs inside the work-item sandbox; a host-side text-only
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
