using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Drives the Continue CLI (binary <c>cn</c>, npm
/// <c>@continuedev/cli</c>, Apache-2.0) in one-shot headless mode:
/// <c>cn --print --auto</c> with the prompt on stdin.
///
/// <para>Continue needs no vendor account: auth is a provider API key the
/// runner seeds into the guest <c>~/.continue/config.yaml</c> (first-class
/// <c>openrouter</c> provider block with <c>apiBase</c> + <c>apiKey</c>) via
/// the pure <see cref="ContinueConfigBuilder"/> before every dispatch —
/// verified live with no Continue login. The config carries exactly ONE
/// model entry (the explicit member model, else the config-sourced default)
/// because model selection is first-entry-wins (verified live: a two-entry
/// file served the first entry, and <c>--model &lt;name&gt;</c> — a hub-slug
/// adder — did not switch entries).</para>
///
/// <para><b>Transport decision (verified against @continuedev/cli
/// 1.5.47).</b> <c>--print</c> is the one-shot contract (prompt on stdin —
/// verified — dodging the 128 KiB MAX_ARG_STRLEN ceiling; no positional
/// prompt argument). <c>--format json</c> is deliberately NOT passed: it is
/// not a machine envelope but a prompt-level coercion forcing the MODEL's
/// final response to be JSON (<c>{"message": "…"}</c> on a plain reply,
/// <c>{"status": "…"}</c> when the model answers in kind), which would
/// corrupt work output — success output is plain text, and provider failures
/// surface as a <c>{"status":"error","message":"…"}</c> envelope on stdout
/// with exit 0 either way. <c>--auto</c> is passed explicitly (all tools
/// allowed — the sandbox VM is throwaway with no human to approve
/// anything); the CLI's own permission table confirms headless runs in auto
/// mode regardless, so the flag pins the behaviour rather than enabling it.
/// Reasoning effort is deliberately NOT mapped: <c>cn</c> exposes no
/// reasoning flag on the headless path.</para>
///
/// <para><b>Exit codes (verified 0 live; 127 per shell contract).</b>
/// Success exits 0 with the final text on stdout (which may legitimately be
/// empty when the work landed in files). Provider failures ALSO exit 0 with
/// the <c>{"status":"error",…}</c> envelope on stdout (observed: the
/// $0-spend-limit paid-model refusal; same envelope as the onboarding-gate
/// interceptor failure). <see cref="RunAsync"/> lifts that envelope into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="ContinueTerminalDiagnoser"/> so the pipeline's no-changes
/// branch can park quota/auth failures instead of dead-lettering them as
/// "produced no changes". A missing binary surfaces as exit 127 +
/// command-not-found, which the base class classifies as infrastructure (see
/// <c>ClassifyFailure</c>) — never as "no changes".</para>
/// </summary>
public sealed class ContinueAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly ContinueOptionsAccessor? _options;

    public ContinueAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the seeded
    /// guest-config model entry when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Continue</c> knobs (inference base
    /// URL). Read per-invocation so a hot reload applies to the next
    /// dispatch without a restart.
    /// </param>
    public ContinueAgentRunner(AgentDefaultsSnapshot? defaults, ContinueOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Continue;

    /// <summary>
    /// Default Continue binary name inside the sandbox. Shared with
    /// <c>ContinueInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "cn";

    /// <summary>Path to the Continue binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. It gates dispatch
    /// (the CLI cannot authenticate without a seeded config) and its value
    /// is written into the guest <c>config.yaml</c> model entry by the
    /// per-dispatch seeding step — the CLI reads the key only from that
    /// file on the config path, never from this variable directly.
    /// </summary>
    public const string CredentialVariable = "OPENROUTER_API_KEY";

    /// <summary>
    /// Marker surfaced when the credential bundle carries no usable API key.
    /// Failing here keeps the run out of the CLI's provider call, which
    /// could only fail at request time with a relayed provider error.
    /// </summary>
    public const string MissingCredentialMarker =
        "no Continue credential configured (set host CODEYBOX_CONTINUE_API_KEY)";

    /// <summary>
    /// Marker surfaced when neither the member nor the config-sourced
    /// default supplies a model. The seeded guest config carries exactly the
    /// dispatch model (first-entry-wins), so dispatching without one would
    /// write a modelless file the CLI rejects — fail here with the named
    /// cause instead. A model id is never invented here.
    /// </summary>
    public const string MissingModelMarker =
        "no Continue model configured (set the member ModelId or CodeyBox:AgentDefaults[continue])";

    /// <summary>
    /// Default model written to the seeded guest-config model entry when
    /// the agent-class member does not override it. Sourced live from
    /// <see cref="AgentDefaultsSnapshot"/> (config key
    /// <c>CodeyBox:AgentDefaults[continue]</c>) in raw provider-catalog form
    /// (no qualifier — Continue resolves the dispatch model from the first
    /// <c>models[]</c> entry, not a <c>provider/model</c> flag).
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    internal string ConfiguredBaseUrl => string.IsNullOrWhiteSpace(_options?.Invoke().BaseUrl)
        ? ContinueOptions.DefaultBaseUrl
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
    /// Validates the credential bundle before the CLI runs. The guest config
    /// itself is seeded by <see cref="RunAsync"/> and
    /// <see cref="RunTextOnlyAsync"/> (the only paths that know the dispatch
    /// model — selection is first-entry-wins, so the file must carry exactly
    /// it); this hook only fails fast so no path dispatches without a key.
    /// The resume path reuses the config seeded by the initial dispatch in
    /// the same sandbox.
    /// </summary>
    protected override Task<AgentResult?> PrepareAgentSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = resume;
        _ = ct;

        if (!TryGetApiKey(credential, out _))
        {
            return Task.FromResult<AgentResult?>(new AgentResult(
                Success: false,
                Summary: MissingCredentialMarker,
                Stdout: null,
                Stderr: MissingCredentialMarker));
        }

        return Task.FromResult<AgentResult?>(null);
    }

    /// <summary>
    /// Seeds the single-model guest config for <paramref name="modelId"/>.
    /// Returns null on success, else the failure to short-circuit with.
    /// </summary>
    internal async Task<AgentResult?> SeedGuestConfigAsync(
        ISandbox sandbox,
        string apiKey,
        string modelId,
        CancellationToken ct)
    {
        string configYaml;
        try
        {
            configYaml = ContinueConfigBuilder.BuildConfigYaml(ConfiguredBaseUrl, apiKey, modelId);
        }
        catch (ArgumentException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise continue config: {ex.Message}",
                Stdout: null,
                Stderr: ex.Message);
        }

        try
        {
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ContinueConfigBuilder.GuestConfigRelativePath),
                configYaml,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
        catch (SandboxCredentialFileWriteException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise continue config: exit {ex.ExitCode}",
                Stdout: ex.Stdout,
                Stderr: ex.Stderr)
            {
                ExecutionUnavailable = ex.ExecutionUnavailable,
            };
        }

        return null;
    }

    /// <summary>
    /// Seeds the single-model guest config for the effective dispatch model
    /// and runs the one-shot headless invocation. Seeding lives here (not in
    /// <see cref="PrepareAgentSandboxAsync"/>) because only this method sees
    /// the per-run <paramref name="modelId"/> — and first-entry-wins
    /// selection means the file must carry exactly the dispatch model.
    /// </summary>
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
        var effectiveModel = ResolveEffectiveModel(modelId);
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return new AgentResult(
                Success: false,
                Summary: MissingModelMarker,
                Stdout: null,
                Stderr: MissingModelMarker);
        }

        if (!TryGetApiKey(credential, out var apiKey))
        {
            return new AgentResult(
                Success: false,
                Summary: MissingCredentialMarker,
                Stdout: null,
                Stderr: MissingCredentialMarker);
        }

        if (await SeedGuestConfigAsync(sandbox, apiKey, effectiveModel, ct).ConfigureAwait(false) is { } seedFailure)
            return seedFailure;

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

        // Lift the exit-0 error envelope so the pipeline can classify it;
        // without this a quota/auth give-up with no file changes succeeds
        // vacuously as "produced no changes" with the envelope as summary.
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && ContinueTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // One-shot headless form: `--print` processes the piped-stdin prompt,
        // acts on the repository, and exits. No positional prompt argument
        // (verified: a stdin-only prompt produced the reply normally):
        // Linux's MAX_ARG_STRLEN is 128 KiB per argv element and rework
        // prompts can exceed it. `--auto` allows every tool (no human in the
        // sandbox); `--format json` is deliberately absent — it coerces the
        // model's answer into JSON rather than framing the transport, which
        // would corrupt work output. The dispatch model rides the seeded
        // guest config (first entry), so no model flag is emitted.
        // Reasoning effort is deliberately NOT mapped: cn exposes no
        // reasoning flag on the headless path.
        _ = modelId;
        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation([Binary, "--print", "--auto"], Stdin: prompt);
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

    // The cn CLI runs inside the work-item sandbox; a host-side text-only
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

        // The text-only path bypasses RunAsync, so seed the guest config
        // here: without it the CLI has no provider key and cannot dispatch.
        // An explicit model wins, else the config-sourced default (callers
        // pass null today — see the merge-review call site).
        var effectiveModel = ResolveEffectiveModel(modelId);
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return new TextOnlyAgentResult(false, MissingModelMarker, null, MissingModelMarker);
        }

        if (!TryGetApiKey(credential, out var apiKey))
        {
            return new TextOnlyAgentResult(false, MissingCredentialMarker, null, MissingCredentialMarker);
        }

        if (await SeedGuestConfigAsync(sandbox, apiKey, effectiveModel, ct).ConfigureAwait(false) is { } seedFailure)
        {
            return new TextOnlyAgentResult(
                false,
                seedFailure.Summary,
                seedFailure.Stdout,
                seedFailure.Stderr);
        }

        return await ExecuteTextOnlyInSandboxAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct).ConfigureAwait(false);
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
}
