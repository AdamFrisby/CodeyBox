using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Drives the Cursor CLI ("agent", from cursor.sh) in non-interactive mode.
/// The binary is named <c>agent</c> in the sandbox image (NOT <c>cursor-agent</c>).
///
/// <para><b>HARD CONSTRAINT — never fast mode.</b> Cursor's fast mode burns
/// ~6x more credits for the same output with no parallelism-relevant speed
/// benefit; this pipeline cares about throughput, not per-iteration latency.
/// <see cref="BuildInvocation"/> never emits <c>--fast</c> or any equivalent
/// flag, and a regression test pins this. If Cursor ever flips the default to
/// fast-by-default, the runner must explicitly opt out (no toggle is exposed
/// for operators here; that would be a separate proposal, evaluated against
/// the 6x cost penalty).</para>
///
/// <para><b>Auth model.</b> Cursor's CLI uses subscription auth written by
/// <c>agent login</c> to a credentials file on the host. The path is operator-
/// configurable via <c>CODEYBOX_CURSOR_AUTH_FILE</c> with a sensible default
/// (<c>~/.cursor/credentials.json</c>). The file's contents are shipped to the
/// sandbox via <c>CODEYBOX_CURSOR_AUTH_JSON</c> and materialised at sandbox-
/// prepare time (same pattern as Codex's <c>~/.codex/auth.json</c> flow); the
/// host's credential directory is never bind-mounted into untrusted agent
/// sandboxes. When the env var is absent, this is a no-op and the in-sandbox
/// CLI is expected to use whatever auth path the operator provisioned in the
/// image.</para>
/// </summary>
public sealed class CursorAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public CursorAgentRunner() : this(defaults: null) { }

    public CursorAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Cursor;

    /// <summary>
    /// Default Cursor CLI binary name inside the sandbox. The binary is
    /// <c>agent</c>, NOT <c>cursor-agent</c>. Shared with
    /// <c>CursorInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "agent";

    /// <summary>
    /// The flag that auto-accepts Cursor's workspace-trust prompt — stage 3 of
    /// the 2026-05-28 cascade ("Workspace Trust Required", exit 1). The runner
    /// must ALWAYS pass it (the multipass VM boundary is the security perimeter,
    /// so per-workspace consent inside the sandbox is noise). Exposed as a single
    /// source of truth so <c>BuildInvocation</c> (real dispatch),
    /// <c>CursorInVmSmokeProbe</c>'s stage-3 trust step (via
    /// <see cref="WorkspaceTrustInvocationPrefix"/>), and
    /// <c>CursorAgentRunnerTrustRegressionTests</c> (the argv-level pin) all
    /// reference the same literal and cannot drift apart. Stage 3 is therefore
    /// caught at smoke time too, not only by the argv regression test.
    /// </summary>
    public const string WorkspaceTrustFlag = "--trust";

    /// <summary>
    /// The leading argv every real workspace invocation uses:
    /// <c>agent --print --trust --force</c>. Extracted as a single builder so
    /// <see cref="BuildInvocation"/> (real dispatch) and
    /// <c>CursorInVmSmokeProbe</c> (stage-3 in-VM trust check) construct the
    /// exact same trust-bearing prefix — if <see cref="WorkspaceTrustFlag"/> is
    /// ever dropped here, both the dispatch path and the smoke step lose it, so
    /// the smoke step surfaces "Workspace Trust Required" at smoke time instead
    /// of letting it cascade on first dispatch.
    /// </summary>
    public static IReadOnlyList<string> WorkspaceTrustInvocationPrefix(string binary) =>
        [binary, "--print", WorkspaceTrustFlag, "--force"];

    /// <summary>
    /// Bash that materialises Cursor's subscription credentials into the
    /// sandbox at <c>~/.config/cursor/auth.json</c> from
    /// <c>CODEYBOX_CURSOR_AUTH_JSON</c>. Shared verbatim with
    /// <c>CursorInVmSmokeProbe</c> so the smoke probe exercises the exact same
    /// destination path as a real dispatch — path drift here is the PR #138
    /// failure this probe is meant to catch.
    /// </summary>
    public const string AuthMaterialiseScript =
        "set -eu; if [ -s \"$HOME/.config/cursor/auth.json\" ]; then exit 0; fi; if [ -n \"${CODEYBOX_CURSOR_AUTH_JSON:-}\" ]; then mkdir -p \"$HOME/.config/cursor\"; umask 077; printf '%s' \"$CODEYBOX_CURSOR_AUTH_JSON\" > \"$HOME/.config/cursor/auth.json\"; fi";

    /// <summary>
    /// Path to the Cursor CLI inside the sandbox. The binary is <c>agent</c>,
    /// not <c>cursor-agent</c>. Override only if the sandbox image installs
    /// it elsewhere.
    /// </summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> when no per-item override is
    /// provided. Sourced live from <see cref="AgentDefaultsSnapshot"/>.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".cursor/sessions", ".cursor/history"];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Materialises Cursor's subscription credentials into the sandbox at
    /// <c>~/.config/cursor/auth.json</c> when <c>CODEYBOX_CURSOR_AUTH_JSON</c>
    /// is present in the credential bundle. Mirrors
    /// <c>CodexAgentRunner.PrepareSandboxAsync</c>: preserves any pre-existing
    /// non-empty file (e.g. restored from a checkpoint scratchpad), short-
    /// circuits when the env var is absent (no-op), and writes the file
    /// 0600 via <c>umask 077</c>.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", AuthMaterialiseScript],
        }, ct);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise cursor auth: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // HARD CONSTRAINT: never include --fast or any fast-mode equivalent.
        // Cursor's fast mode burns ~6x more credits for no benefit in this
        // pipeline (we optimise for parallelism, not per-iteration latency).
        // The CursorAgentRunner_FastModeRegressionTests fixture pins this; if
        // a future Cursor release changes its default to fast-by-default the
        // invocation must explicitly opt out, NOT be made operator-toggleable.
        //
        // --trust auto-accepts the workspace-trust prompt that Cursor would
        // otherwise raise on /work; the multipass VM boundary is our
        // security perimeter so per-workspace consent inside the sandbox is
        // noise. --force is the same idea for per-command tool prompts (see
        // Claude's --dangerously-skip-permissions for the equivalent
        // rationale on that runner).
        var argv = new List<string>(WorkspaceTrustInvocationPrefix(Binary));

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // The Cursor CLI does not currently expose a reasoning-effort flag
        // analogous to Claude's --effort. ReasoningMode is informational only
        // for this runner — the parameter is accepted (so the agent-class
        // config schema stays uniform) but not threaded into argv. If a future
        // Cursor release adds one, wire it here.
        _ = reasoningMode;

        // Cursor CLI (verified against 2026.05.27-fe9a6e2) emits NDJSON in
        // the same system/user/assistant/result shape as Claude when
        // --output-format stream-json --stream-partial-output are set.
        // Enabling these makes the orchestrator's AgentStreams capture the
        // structured stream live to disk, giving the same audit / cost-
        // extraction visibility we get for Claude. Earlier releases of this
        // CLI did not expose the flags; if a baseline image installs an
        // older Cursor the dispatch will fail with "unknown option" and the
        // operator can either pin captureStructuredStream off for cursor or
        // rebake with a newer Cursor in MultipassExtraRuncmd.
        if (captureStructuredStream)
        {
            argv.Add("--output-format");
            argv.Add("stream-json");
            argv.Add("--stream-partial-output");
        }

        // Pass the prompt via stdin rather than positional argv. Linux's
        // MAX_ARG_STRLEN is 128 KiB per single argv element; rework prompts
        // including audit findings can exceed that and surface as exit 126
        // from the sandbox wrapper's `exec "$@"`. The sandbox wrapper forwards
        // stdin automatically when SandboxExec.Stdin is non-null.
        return new AgentInvocation(argv, Stdin: prompt);
    }

    protected override AgentInvocation BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
    {
        _ = credential;
        // Text-only calls use --print without --trust/--force so the CLI cannot
        // auto-approve tool prompts on untrusted merge-conflict/resolver input.
        var argv = new List<string> { Binary, "--print" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        _ = reasoningMode;
        return new AgentInvocation(argv, Stdin: prompt);
    }

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
        => GetSandboxSubscriptionTextOnlyUnavailabilityReason(
            credential,
            "CODEYBOX_CURSOR_AUTH_JSON");

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
