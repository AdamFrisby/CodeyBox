using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Drives the Kilo Code CLI (binary <c>kilo</c>, npm
/// <c>@kilocode/cli</c>, MIT-licensed) in one-shot headless mode:
/// <c>kilo run --auto --format json</c> with the prompt on stdin.
///
/// <para>Kilo's CLI is an OpenCode fork sharing its CLI surface and plugin
/// runtime, but it is NOT driven through the existing
/// <c>OpencodeAgentRunner</c>: the binary name differs, <c>--auto</c> is
/// mandatory (without it a non-interactive run auto-rejects every permission
/// request and exits 1, which reads as a refusal rather than a configuration
/// error), the structured transport is an explicit <c>--format json</c> flag
/// (opencode's runner speaks no structured stream at all), and auth is a
/// seeded <c>kilo.jsonc</c> rather than opencode's <c>auth.json</c>. A shared
/// adapter would couple two CLIs whose flags, config schemas, and event
/// vocabularies can drift independently — the fork boundary is the honest
/// seam.</para>
///
/// <para><b>Transport decision (verified against @kilocode/cli 7.7.2).</b>
/// <c>run --auto --format json</c> is the only transport this runner speaks —
/// even when the caller did not ask for structured capture — so cost
/// attribution (token usage on <c>step-finish</c>), failure classification
/// (the <c>type: "error"</c> frame), and stream parsing never depend on which
/// call path dispatched the run. <c>--format default</c> prints only the
/// final response text (usage and the terminal error shape would be
/// unrecoverable). The prompt travels on stdin with no positional
/// <c>[message..]</c> argument (verified: a piped-stdin prompt produced the
/// reply normally): Linux's <c>MAX_ARG_STRLEN</c> is 128 KiB per argv element
/// and rework prompts can exceed it.</para>
///
/// <para><b>Authentication (verified live).</b> Kilo publishes no first-class
/// <c>openrouter</c> provider id, so the OpenRouter path goes through the
/// generic <c>openai-compatible</c> provider with <c>options.baseURL</c> +
/// <c>apiKey</c> in <c>~/.config/kilo/kilo.jsonc</c>. The interactive
/// first-run <c>/connect</c> cannot run headless, so the runner seeds the
/// global file (where <c>{env:VAR}</c> interpolation does resolve —
/// repo-local config does not) via
/// <see cref="PrepareAgentSandboxAsync"/> using
/// <see cref="SandboxCredentialFileWriter"/> (stdin transport, mode 0600),
/// built by the pure <see cref="KiloConfigBuilder"/> from the same bundle
/// value. A missing key fails fast there instead of dispatching into a run
/// that can only fail at model-resolution time.</para>
///
/// <para><b>Autonomy.</b> The sandbox VM is throwaway with no human to approve
/// anything, so the runner passes <c>--auto</c> (auto-approve permissions
/// that are not explicitly denied). <c>--auto</c> is mandatory, not best
/// effort: omitting it makes the CLI reject its own tool calls and exit 1.
/// The dispatch model comes from <c>-m/--model</c> (explicit member model,
/// else the config-sourced default) in <c>provider/model</c> form.
/// Reasoning effort is deliberately NOT mapped: kilo's <c>--variant</c>
/// vocabulary is provider-specific and unverified on the openai-compatible
/// path, and emitting one unconditionally could fail dispatches — same
/// rationale as the autohand runner.</para>
///
/// <para><b>Exit codes (verified 0/1 live; 124 per CLI contract).</b> Success
/// exits 0; model/auth failures exit 1 with a <c>type: "error"</c> event on
/// stdout plus an <c>Error: …</c> line on stderr; 124 signals a CLI-side
/// timeout. <see cref="RunAsync"/> lifts the terminal error frame into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="KiloTerminalDiagnoser"/> so the pipeline's no-changes branch
/// can park quota/auth failures instead of dead-lettering them as "produced
/// no changes". A missing binary surfaces as exit 127 + command-not-found,
/// which the base class classifies as infrastructure (see
/// <c>ClassifyFailure</c>) — never as "no changes".</para>
/// </summary>
public sealed class KiloAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly KiloOptionsAccessor? _options;

    public KiloAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>-m</c> value
    /// (and the seeded guest-config <c>models</c> entry) when a caller does
    /// not pass an explicit model, so the dispatch model is sourced from
    /// hot-reloadable config rather than a hardcoded literal.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Kilo</c> knobs (inference base URL).
    /// Read per-invocation so a hot reload applies to the next dispatch
    /// without a restart.
    /// </param>
    public KiloAgentRunner(AgentDefaultsSnapshot? defaults, KiloOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Kilo;

    /// <summary>
    /// Default kilo binary name inside the sandbox. Shared with
    /// <c>KiloInVmSmokeProbe</c> so the smoke check and the real runner always
    /// invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "kilo";

    /// <summary>Path to the kilo binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. It gates dispatch
    /// (the CLI cannot authenticate without a seeded config) and its value
    /// is written into the guest <c>kilo.jsonc</c> provider block by
    /// <see cref="PrepareAgentSandboxAsync"/> — the CLI reads the key only
    /// from that file on the openai-compatible path, never from this
    /// variable directly.
    /// </summary>
    public const string CredentialVariable = "KILO_API_KEY";

    /// <summary>
    /// Marker surfaced when the credential bundle carries no usable API key.
    /// Failing here keeps the run out of the CLI's first-run connect flow,
    /// which would block on interactive setup instead of failing fast.
    /// </summary>
    public const string MissingCredentialMarker =
        "no Kilo credential configured (set host CODEYBOX_KILO_API_KEY)";

    /// <summary>
    /// Default model passed to <c>-m/--model</c> (and seeded into the guest
    /// config <c>models</c> map) when the agent-class member does not
    /// override it. Sourced live from <see cref="AgentDefaultsSnapshot"/>
    /// (config key <c>CodeyBox:AgentDefaults[kilo]</c>). Stored in full
    /// <c>provider/model</c> form — kilo resolves <c>-m</c> against the
    /// seeded map, so a bare id would fail with <c>Model not found</c>.
    /// When neither is set both the flag and the map entry are omitted and
    /// the CLI fails closed with <c>Model not found</c>, naming the cause —
    /// a model id is never invented here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    internal string ConfiguredBaseUrl => string.IsNullOrWhiteSpace(_options?.Invoke().BaseUrl)
        ? KiloOptions.DefaultBaseUrl
        : _options!.Invoke().BaseUrl!.Trim();

    /// <summary>
    /// Effective dispatch model: explicit member model wins, else the
    /// config-sourced <see cref="DefaultModelId"/>. Null when neither is set.
    /// </summary>
    internal string? ResolveEffectiveModel(string? modelId) =>
        !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;

    protected override IReadOnlyList<string> DirectCredentialEnvironmentVariables => [CredentialVariable];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Seeds the guest <c>~/.config/kilo/kilo.jsonc</c> before the CLI runs.
    /// The CLI resolves the openai-compatible provider credential exclusively
    /// from that file (the interactive <c>/connect</c> flow cannot run
    /// headless), so without this step every dispatch would fail at
    /// model-resolution time. The payload travels through
    /// <see cref="SandboxCredentialFileWriter"/> (stdin transport, mode
    /// 0600), never argv. A missing/blank key short-circuits with
    /// <see cref="MissingCredentialMarker"/> instead of dispatching.
    /// </summary>
    protected override async Task<AgentResult?> PrepareAgentSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        _ = workingDirectory;
        _ = resume;

        if (credential is null
            || !credential.EnvironmentVariables.TryGetValue(CredentialVariable, out var apiKey)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            return new AgentResult(
                Success: false,
                Summary: MissingCredentialMarker,
                Stdout: null,
                Stderr: MissingCredentialMarker);
        }

        // NOTE: the seeded models map carries the config-sourced default plus
        // the curated seed: PrepareAgentSandboxAsync does not receive the
        // dispatch modelId, and a per-member -m outside this union fails
        // closed with "Model not found" (see KiloConfigBuilder). The
        // explicit -m flag (emitted by BuildInvocation) selects among the
        // seeded entries at runtime.
        var models = new List<string?>(KiloKnownModels.All.Count + 1);
        var defaultModel = DefaultModelId;
        if (!string.IsNullOrWhiteSpace(defaultModel))
            models.Add(defaultModel);
        models.AddRange(KiloKnownModels.All);

        string configJson;
        try
        {
            configJson = KiloConfigBuilder.BuildConfigJson(ConfiguredBaseUrl, apiKey, models);
        }
        catch (ArgumentException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise kilo config: {ex.Message}",
                Stdout: null,
                Stderr: ex.Message);
        }

        try
        {
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    KiloConfigBuilder.GuestConfigRelativePath),
                configJson,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
        catch (SandboxCredentialFileWriteException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise kilo config: exit {ex.ExitCode}",
                Stdout: ex.Stdout,
                Stderr: ex.Stderr)
            {
                ExecutionUnavailable = ex.ExecutionUnavailable,
            };
        }

        return null;
    }

    /// <summary>
    /// Verifies <c>--format json</c> support with <c>kilo run --help</c>.
    /// The runner's only transport is the JSON event stream, so a binary that
    /// no longer advertises the flag must fail closed here rather than
    /// dispatch into an unparseable plaintext run.
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
        return output.Contains("--format", StringComparison.Ordinal)
            && output.Contains("json", StringComparison.Ordinal);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: `run --auto` processes the piped-stdin
        // prompt, acts on the repository, and exits. --auto is MANDATORY:
        // without it the CLI auto-rejects every permission request and exits
        // 1, which reads as a refusal rather than a configuration error.
        // --format json is the ONLY transport this runner speaks — even when
        // the caller did not ask for structured capture — so cost
        // attribution, failure classification, and stream parsing never
        // depend on which call path dispatched the run.
        var argv = new List<string>
        {
            Binary, "run",
            "--auto",
            "--format", "json",
        };

        var effectiveModel = ResolveEffectiveModel(modelId);
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("-m");
            argv.Add(effectiveModel);
        }

        // Reasoning effort is deliberately NOT mapped: kilo's --variant
        // vocabulary is provider-specific and unverified on the
        // openai-compatible path — same rationale as the autohand runner.
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
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && KiloTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
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
            CredentialVariable);

    // The kilo CLI runs inside the work-item sandbox; a host-side text-only
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
