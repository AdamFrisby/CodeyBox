using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Drives the Crush CLI (binary <c>crush</c>, npm
/// <c>@charmland/crush</c>, Charm) in headless one-shot mode:
/// <c>crush run -q -m &lt;model&gt;</c> with the prompt on stdin.
///
/// <para><b>Transport decision (verified against @charmland/crush
/// 0.95.0).</b> <c>run</c> is the documented non-interactive contract
/// ("Run a single non-interactive prompt and exit"); a bare <c>crush</c>
/// starts an interactive TUI. The prompt travels on stdin with no
/// positional prompt argument (verified live — a stdin-only prompt
/// produced the reply normally): Linux's <c>MAX_ARG_STRLEN</c> is 128 KiB
/// per argv element and rework prompts can exceed it. <c>-q/--quiet</c>
/// hides the spinner so stdout carries only the reply. <c>-m/--model</c>
/// takes <c>model</c> or <c>provider/model</c>; the shipped member uses the
/// qualified OpenRouter form (verified: <c>crush models</c> lists it
/// natively). Output is plain text only — there is no structured-output
/// flag on <c>run</c> — so failure detection comes from the exit code plus
/// text via <see cref="CrushTerminalDiagnoser"/>, and cost/quota reads
/// come from nowhere (see <see cref="CrushCostExtractor"/>).</para>
///
/// <para><b>Auth is a bare environment variable — no config is seeded.</b>
/// The CLI reads <c>OPENROUTER_API_KEY</c> directly from the process
/// environment (verified live on a bare machine with no crush config: the
/// run dispatched and completed). The runner therefore writes no guest
/// config files; the key travels only through the sandbox environment via
/// the shipped mapping (host <c>CODEYBOX_CRUSH_API_KEY</c> →
/// <c>OPENROUTER_API_KEY</c>) and is never emitted on argv. Telemetry is on
/// by default, so every dispatch carries
/// <c>CRUSH_DISABLE_METRICS=1</c>.</para>
///
/// <para><b>Autonomy needs no flag.</b> Permissions are already
/// auto-approved inside <c>run</c> (verified live: a seeded-bug repo-edit
/// run fixed the file with no approval flag). <c>--yolo</c> is root-only —
/// <c>crush run --yolo</c> hard-fails with <c>Unknown flag: --yolo</c>
/// (verified live) — so the runner never emits it.</para>
///
/// <para><b>Model is mandatory.</b> The CLI picks its own paid default when
/// <c>-m</c> is omitted, which fails as quota confusion on a
/// <c>:free</c>-only key (<c>forbidden: Key limit exceeded</c> — verified
/// live), so dispatch without an explicit member model or the
/// config-sourced default fails fast naming the missing model instead of
/// burning a dispatch on the wrong tier. A model id is never invented
/// here.</para>
///
/// <para><b>crushrc is quarantined, then restored.</b> Crush executes
/// <c>./.crushrc</c> and <c>./crushrc</c> as Bash at startup (verified live
/// for <c>.crushrc</c>; both are documented, upstream's own security note
/// says never to launch in a directory whose config is unreviewed), so a
/// repository under work could ship shell that runs when the agent starts.
/// The runner moves those files aside before dispatch and restores them
/// afterwards on every CLI-touching path (<see cref="RunAsync"/>,
/// <see cref="RunResumedAsync"/>, and the sandbox text-only path) — the
/// run executes with no repo-local shell, and the working tree is left as
/// found. A quarantine that cannot complete fails closed with the named
/// cause instead of dispatching with live repo shell. See
/// <see cref="QuarantineCrushrcAsync"/> and the Crush quirks doc.</para>
///
/// <para><b>Exit codes (verified live: 0 ok, 1 terminal failure, 127 per
/// shell contract).</b> Success exits 0 with the reply on stdout (which
/// may legitimately be empty when the work landed in files). Terminal
/// failures exit 1 with a styled <c>ERROR</c> block on stderr and empty
/// stdout — <see cref="RunAsync"/> lifts the marked line into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="CrushTerminalDiagnoser"/> so the pipeline can classify it. A
/// missing binary surfaces as exit 127 + command-not-found, which the base
/// class classifies as infrastructure (see <c>ClassifyFailure</c>) — never
/// as "no changes".</para>
///
/// <para><b>Reasoning effort is deliberately NOT mapped.</b> <c>run</c>
/// exposes <c>--reasoning-effort</c>, but accepted levels depend on the
/// model and unsupported values are rejected at dispatch — emitting one
/// could fail dispatches the way kilo's <c>--variant</c> would. Same
/// rationale, same posture: ignore <paramref name="reasoningMode"/> rather
/// than pass it through.</para>
/// </summary>
public sealed class CrushAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public CrushAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>-m</c> value
    /// when a caller does not pass an explicit model, so the dispatch model
    /// is sourced from hot-reloadable config rather than a hardcoded
    /// literal.
    /// </param>
    public CrushAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Crush;

    /// <summary>
    /// Default Crush binary name inside the sandbox. Shared with
    /// <c>CrushInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "crush";

    /// <summary>Path to the Crush binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. It gates dispatch
    /// (the CLI reads it directly from the process environment — no config
    /// file involved) and the runner never emits the key on argv.
    /// </summary>
    public const string CredentialVariable = "OPENROUTER_API_KEY";

    /// <summary>
    /// Telemetry opt-out carried on every dispatch. Crush enables metrics by
    /// default; automated sandbox runs must not phone home.
    /// </summary>
    public const string MetricsOptOutVariable = "CRUSH_DISABLE_METRICS";

    /// <summary>
    /// Marker surfaced when the credential bundle carries no usable API key.
    /// Failing here keeps the run out of the CLI's provider call, which
    /// could only fail at request time with the relayed provider error.
    /// </summary>
    public const string MissingCredentialMarker =
        "no Crush credential configured (set host CODEYBOX_CRUSH_API_KEY)";

    /// <summary>
    /// Marker surfaced when neither the member nor the config-sourced
    /// default supplies a model. The CLI falls back to its own paid default
    /// when <c>-m</c> is omitted, which fails as quota confusion on a
    /// <c>:free</c>-only key — fail here with the named cause instead. A
    /// model id is never invented here.
    /// </summary>
    public const string MissingModelMarker =
        "no Crush model configured (set the member ModelId or CodeyBox:AgentDefaults[crush])";

    /// <summary>
    /// Repo-local files Crush executes as Bash at startup, in resolution
    /// order. Both names execute (verified live for <c>.crushrc</c>; both
    /// are documented upstream) — quarantine must cover both, not just the
    /// dotted form.
    /// </summary>
    internal static readonly IReadOnlyList<string> CrushrcFileNames = [".crushrc", "crushrc"];

    /// <summary>
    /// Default model passed to <c>-m</c> when the agent-class member does
    /// not override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[crush]</c>) in the qualified
    /// <c>provider/model</c> form the runner passes. When neither is set the
    /// run fails fast (see <see cref="MissingModelMarker"/>) — the CLI's
    /// own paid default is never dispatched implicitly.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    /// <summary>
    /// Effective dispatch model: explicit member model wins, else the
    /// config-sourced <see cref="DefaultModelId"/>. Null when neither is set.
    /// </summary>
    internal string? ResolveEffectiveModel(string? modelId) =>
        !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => [CredentialVariable];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Moves the repo-local crushrc files (see <see cref="CrushrcFileNames"/>)
    /// aside before dispatch so no repository-shipped shell executes when
    /// Crush starts. Each present file moves to a unique
    /// <c>&lt;name&gt;.codeybox-quarantined-&lt;run&gt;</c> sibling (unique
    /// per call so concurrent runs in one sandbox never share a backup
    /// path), probed with <c>test -f</c> first so an absent file costs one
    /// cheap exec and no failure.
    /// </summary>
    /// <returns>
    /// The files moved, for <see cref="RestoreCrushrcAsync"/>. When the
    /// working directory is not a rooted guest path, or a present file
    /// cannot be moved, returns a failure describing the named cause —
    /// dispatching with live repo shell is worse than refusing the run.
    /// </returns>
    internal async Task<CrushrcQuarantineOutcome> QuarantineCrushrcAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !workingDirectory.StartsWith('/'))
        {
            return new CrushrcQuarantineOutcome(
                null,
                new AgentResult(
                    Success: false,
                    Summary: $"refusing Crush dispatch: working directory '{workingDirectory}' is not a rooted guest path, so repo-local crushrc files cannot be quarantined",
                    Stdout: null,
                    Stderr: "crush executes repo-local .crushrc/crushrc as shell at startup; without a rooted working directory the quarantine cannot run"));
        }

        var backupSuffix = $".codeybox-quarantined-{Guid.NewGuid():N}"[..31];
        var moved = new List<QuarantinedCrushrc>(CrushrcFileNames.Count);
        var root = workingDirectory.TrimEnd('/');
        foreach (var name in CrushrcFileNames)
        {
            var original = $"{root}/{name}";
            var probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["test", "-f", original],
            }, ct).ConfigureAwait(false);
            if (!probe.Success)
                continue;

            var backup = $"{original}{backupSuffix}";
            var move = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["mv", "--", original, backup],
            }, ct).ConfigureAwait(false);
            if (!move.Success)
            {
                await RestoreCrushrcAsync(sandbox, moved, ct).ConfigureAwait(false);
                return new CrushrcQuarantineOutcome(
                    null,
                    new AgentResult(
                        Success: false,
                        Summary: $"refusing Crush dispatch: repo-local {name} is present but could not be quarantined (exit {move.ExitCode})",
                        Stdout: move.Stdout,
                        Stderr: move.Stderr));
            }

            moved.Add(new QuarantinedCrushrc(original, backup));
        }

        return new CrushrcQuarantineOutcome(moved, Failure: null);
    }

    /// <summary>
    /// Restores files moved by <see cref="QuarantineCrushrcAsync"/>. A file
    /// whose original path reappeared during the run (created by the agent
    /// or the CLI) is left in place and named in the returned note — the
    /// run's output takes precedence over silently overwriting it. Never
    /// throws: per-file failures accumulate into the returned note so one
    /// bad restore cannot mask the run's own outcome.
    /// </summary>
    /// <returns>Null when every file restored cleanly, else a human-readable
    /// note naming what was left unrestored and why.</returns>
    internal static async Task<string?> RestoreCrushrcAsync(
        ISandbox sandbox,
        IReadOnlyList<QuarantinedCrushrc> moved,
        CancellationToken ct)
    {
        if (moved.Count == 0)
            return null;

        List<string>? problems = null;
        foreach (var file in moved)
        {
            try
            {
                var reappeared = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["test", "-e", file.Original],
                }, ct).ConfigureAwait(false);
                if (reappeared.Success)
                {
                    problems ??= [];
                    problems.Add(
                        $"repo-local {file.Original} reappeared during the run; quarantined backup left at {file.Backup}");
                    continue;
                }

                var restore = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["mv", "--", file.Backup, file.Original],
                }, ct).ConfigureAwait(false);
                if (!restore.Success)
                {
                    problems ??= [];
                    problems.Add(
                        $"failed to restore {file.Original} from {file.Backup} (exit {restore.ExitCode})");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems ??= [];
                problems.Add($"failed to restore {file.Original}: {ex.GetType().Name}");
            }
        }

        return problems is null ? null : string.Join("; ", problems);
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
        if (ResolveEffectiveModel(modelId) is not { } effectiveModel)
        {
            return new AgentResult(
                Success: false,
                Summary: MissingModelMarker,
                Stdout: null,
                Stderr: MissingModelMarker);
        }

        if (!TryGetApiKey(credential, out _))
        {
            return new AgentResult(
                Success: false,
                Summary: MissingCredentialMarker,
                Stdout: null,
                Stderr: MissingCredentialMarker);
        }

        var quarantine = await QuarantineCrushrcAsync(sandbox, workingDirectory, ct).ConfigureAwait(false);
        if (quarantine.Failure is { } quarantineFailure)
            return quarantineFailure;

        AgentResult result;
        string? restoreNote;
        try
        {
            result = await base.RunAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                effectiveModel,
                reasoningMode,
                ct,
                stdoutChunkCallback,
                captureStructuredStream).ConfigureAwait(false);
        }
        finally
        {
            restoreNote = await RestoreCrushrcAsync(sandbox, quarantine.Moved!, ct).ConfigureAwait(false);
        }

        return WithTerminalDiagnostic(WithRestoreNote(result, restoreNote), nativeSessionId: null);
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
        // The resume path re-dispatches the CLI in the same working
        // directory, so it carries the same fail-fast and quarantine
        // obligations as the initial dispatch.
        if (ResolveEffectiveModel(modelId) is not { } effectiveModel)
        {
            return new AgentResult(
                Success: false,
                Summary: MissingModelMarker,
                Stdout: null,
                Stderr: MissingModelMarker)
            {
                NativeSessionId = resume.NativeSessionId,
            };
        }

        if (!TryGetApiKey(credential, out _))
        {
            return new AgentResult(
                Success: false,
                Summary: MissingCredentialMarker,
                Stdout: null,
                Stderr: MissingCredentialMarker)
            {
                NativeSessionId = resume.NativeSessionId,
            };
        }

        var quarantine = await QuarantineCrushrcAsync(sandbox, workingDirectory, ct).ConfigureAwait(false);
        if (quarantine.Failure is { } quarantineFailure)
            return quarantineFailure with { NativeSessionId = resume.NativeSessionId };

        AgentResult result;
        string? restoreNote;
        try
        {
            result = await base.RunResumedAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                resume,
                effectiveModel,
                reasoningMode,
                ct,
                stdoutChunkCallback).ConfigureAwait(false);
        }
        finally
        {
            restoreNote = await RestoreCrushrcAsync(sandbox, quarantine.Moved!, ct).ConfigureAwait(false);
        }

        result = WithRestoreNote(result, restoreNote);
        return WithTerminalDiagnostic(result, resume.NativeSessionId);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: `run` acts on the repository and exits.
        // -q hides the spinner so stdout carries only the reply; -m pins the
        // dispatch model (an explicit member model wins, else the
        // config-sourced default — the CLI's own paid default is never
        // dispatched implicitly, see MissingModelMarker). The prompt travels
        // on stdin with no positional prompt argument: Linux's
        // MAX_ARG_STRLEN is 128 KiB per argv element and rework prompts can
        // exceed it. CRUSH_DISABLE_METRICS=1 opts out of the
        // on-by-default telemetry for automated runs. Reasoning effort is
        // deliberately NOT mapped: --reasoning-effort levels are
        // model-dependent and unsupported values are rejected at dispatch.
        // --yolo is deliberately absent: it is a root-only flag that `run`
        // rejects, and run auto-approves permissions without it.
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        var argv = new List<string> { Binary, "run", "-q" };
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("-m");
            argv.Add(effectiveModel);
        }

        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation(
            argv,
            ExtraEnvironment: new Dictionary<string, string>
            {
                [MetricsOptOutVariable] = "1",
            },
            Stdin: prompt);
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

    // The crush CLI runs inside the work-item sandbox; a host-side text-only
    // call with no sandbox returns failure (see RunTextOnlyAsync below).
    public bool TextOnlyRequiresSandbox => true;

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        if (sandbox is null || workingDirectory is null)
            return await RunTextOnlyRequiresSandboxAsync(ct).ConfigureAwait(false);

        // The text-only path bypasses RunAsync, so it repeats the fail-fast
        // and quarantine obligations: without the key the CLI hard-fails on
        // "No providers configured", without a model it burns a paid
        // default, and without the quarantine repo shell would execute.
        if (ResolveEffectiveModel(modelId) is not { } effectiveModel)
        {
            return new TextOnlyAgentResult(false, MissingModelMarker, null, MissingModelMarker);
        }

        if (!TryGetApiKey(credential, out _))
        {
            return new TextOnlyAgentResult(false, MissingCredentialMarker, null, MissingCredentialMarker);
        }

        var quarantine = await QuarantineCrushrcAsync(sandbox, workingDirectory, ct).ConfigureAwait(false);
        if (quarantine.Failure is { } quarantineFailure)
        {
            return new TextOnlyAgentResult(
                false,
                quarantineFailure.Summary,
                quarantineFailure.Stdout,
                quarantineFailure.Stderr);
        }

        TextOnlyAgentResult result;
        try
        {
            result = await ExecuteTextOnlyInSandboxAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                effectiveModel,
                reasoningMode,
                ct).ConfigureAwait(false);
        }
        finally
        {
            await RestoreCrushrcAsync(sandbox, quarantine.Moved!, ct).ConfigureAwait(false);
        }

        if (CrushTerminalDiagnoser.TryExtractTerminalError(result.Output, result.Error) is { } terminalError
            && string.IsNullOrEmpty(result.Error))
        {
            return result with { Error = terminalError };
        }

        return result;
    }

    internal static bool TryGetApiKey(AgentCredential? credential, out string apiKey)
    {
        apiKey = string.Empty;
        if (credential is null)
            return false;
        if (!credential.EnvironmentVariables.TryGetValue(CredentialVariable, out var key))
            return false;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        apiKey = key;
        return true;
    }

    /// <summary>
    /// Lifts the styled-stderr failure block into
    /// <see cref="AgentResult.TerminalDiagnostic"/> so the pipeline can
    /// classify it; without this a quota/auth give-up with no file changes
    /// terminal-fails as "produced no changes". Scans BOTH streams (the
    /// markers are human rendering, not a stream-guaranteed envelope).
    /// </summary>
    private static AgentResult WithTerminalDiagnostic(AgentResult result, AgentNativeSessionId? nativeSessionId)
    {
        var withSession = nativeSessionId is null || result.Success || result.NativeSessionId is not null
            ? result
            : result with { NativeSessionId = nativeSessionId };
        if (!string.IsNullOrEmpty(withSession.TerminalDiagnostic))
            return withSession;
        if (CrushTerminalDiagnoser.TryExtractTerminalError(withSession.Stdout, withSession.Stderr) is { } terminalError)
            return withSession with { TerminalDiagnostic = terminalError };
        return withSession;
    }

    private static AgentResult WithRestoreNote(AgentResult result, string? restoreNote)
    {
        if (string.IsNullOrEmpty(restoreNote))
            return result;
        var note = $"crushrc restore incomplete: {restoreNote}";
        return result with
        {
            Stderr = string.IsNullOrEmpty(result.Stderr) ? note : $"{result.Stderr}\n{note}",
        };
    }
}

/// <summary>
/// A repo-local crushrc file moved aside before dispatch.
/// </summary>
internal sealed record QuarantinedCrushrc(string Original, string Backup);

/// <summary>
/// Outcome of <see cref="CrushAgentRunner.QuarantineCrushrcAsync"/>: either
/// the moved files (possibly empty — no crushrc present costs only the
/// probes) or a failure that must short-circuit the run.
/// </summary>
internal sealed record CrushrcQuarantineOutcome(
    IReadOnlyList<QuarantinedCrushrc>? Moved,
    AgentResult? Failure);
