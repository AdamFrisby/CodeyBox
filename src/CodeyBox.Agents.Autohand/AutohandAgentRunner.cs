using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Drives the Autohand CLI (binary <c>autohand</c>, npm
/// <c>autohand-cli</c>, Apache-2.0) in one-shot headless mode:
/// <c>autohand -p</c> with the prompt on stdin and
/// <c>--output-format stream-json</c>.
///
/// <para><b>Transport decision (verified against autohand-cli 0.9.7).</b>
/// <c>-p/--prompt</c> is documented as <i>Run a single instruction in command
/// mode</i> on the dedicated Headless Mode page: the agent processes the
/// instruction, acts on the repository, and exits — exactly the
/// non-interactive contract CodeyBox needs. The prompt travels on stdin with
/// a bare <c>-p</c> (verified: a piped-stdin prompt produced the reply and a
/// file edit normally) rather than <c>-p &lt;text&gt;</c> argv: Linux's
/// <c>MAX_ARG_STRLEN</c> is 128 KiB per argv element and rework prompts can
/// exceed it. <c>--output-format stream-json</c> is the only transport this
/// runner speaks — even when the caller did not ask for structured capture —
/// so failure classification and stream parsing never depend on which call
/// path dispatched the run. Whole-doc <c>json</c> is rejected by this CLI
/// version (<c>Invalid --output-format value "json"</c>).</para>
///
/// <para><b>Authentication (verified live — differs from the docs).</b>
/// Without <c>--bare</c> the CLI forces an interactive Autohand-account
/// device login (<c>Initiating authentication… visit autohand.ai/signin</c>)
/// even with a valid provider key, which blocks forever in the sandbox; with
/// <c>--bare</c> it runs headless with no vendor account. Bare mode instead
/// requires <c>AUTOHAND_API_KEY</c> in the environment (the shipped credential
/// mapping wires host <c>CODEYBOX_AUTOHAND_API_KEY</c> to it), but that
/// variable is only a gate — the provider credential is read exclusively
/// from the guest <c>~/.autohand/config.json</c> provider block, which
/// environment variables never backfill (verified: empty or dummy file keys
/// fail even with both env vars set). The runner therefore seeds the guest
/// config via <see cref="PrepareAgentSandboxAsync"/> using
/// <see cref="SandboxCredentialFileWriter"/> (stdin transport, mode 0600),
/// built by the pure <see cref="AutohandConfigBuilder"/> from the same bundle
/// value. A missing key fails fast there instead of dispatching into a
/// wizard that blocks.</para>
///
/// <para><b>Autonomy.</b> The sandbox VM is throwaway with no human to approve
/// anything, so the runner passes <c>--yes</c> (auto-confirm risky actions)
/// and <c>--unrestricted</c> (no approval prompts at all — the
/// <c>GOOSE_MODE=auto</c> equivalent). The CLI's immutable security checks
/// still run before configurable policy, so unrestricted cannot override the
/// built-in blacklist. <c>--offline</c> disables startup network operations
/// (model-catalog refreshes, update checks) that would stall against the
/// sandbox egress allow-list; provider inference itself is unaffected.
/// <c>--plan</c> is never passed (read-only planning would produce no
/// changes by design). The dispatch model comes from <c>--model</c>
/// (explicit member model, else the config-sourced default) and the provider
/// id from <c>CodeyBox:Autohand</c> (hot-reloadable).</para>
///
/// <para><b>Exit-zero errors.</b> Autohand exits 0 on some terminal failures
/// (verified: <c>{"type":"error","message":"Command did not complete
/// successfully."}</c> exits 0). <see cref="RunAsync"/> therefore lifts the
/// terminal error into <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="AutohandTerminalDiagnoser"/> so the pipeline's no-changes
/// branch can park quota/auth failures instead of dead-lettering them as
/// "produced no changes".</para>
///
/// <para><b>Cost.</b> The bare stream carries no machine-readable usage frame
/// (verified: tool/file/result events only; the human footer prints a rounded
/// <c>12.5k tokens used</c> total with no input/output split), so
/// <see cref="AutohandCostExtractor"/> reports unknown (null) rather than a
/// fabricated zero.</para>
/// </summary>
public sealed class AutohandAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider
{
    private readonly AgentDefaultsSnapshot? _defaults;
    private readonly AutohandOptionsAccessor? _options;

    public AutohandAgentRunner() : this(defaults: null, options: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies the <c>--model</c>
    /// value (and the seeded guest-config model) when a caller does not pass
    /// an explicit model, so the dispatch model is sourced from
    /// hot-reloadable config rather than a hardcoded literal.
    /// </param>
    /// <param name="options">
    /// Live accessor for the <c>CodeyBox:Autohand</c> knobs (provider id).
    /// Read per-invocation so a hot reload applies to the next dispatch
    /// without a restart.
    /// </param>
    public AutohandAgentRunner(AgentDefaultsSnapshot? defaults, AutohandOptionsAccessor? options = null)
    {
        _defaults = defaults;
        _options = options;
    }

    public override AgentKind Kind => AgentKind.Autohand;

    /// <summary>
    /// Default autohand binary name inside the sandbox. Shared with
    /// <c>AutohandInVmSmokeProbe</c> so the smoke check and the real runner
    /// always invoke the same binary.
    /// </summary>
    public const string DefaultBinary = "autohand";

    /// <summary>Path to the autohand binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Credential variable carrying the provider API key. It gates bare mode
    /// (the CLI refuses to start without it) and its value is written into
    /// the guest config's provider block by
    /// <see cref="PrepareAgentSandboxAsync"/> — the CLI reads the key only
    /// from that file, never from this variable directly.
    /// </summary>
    public const string CredentialVariable = "AUTOHAND_API_KEY";

    /// <summary>
    /// Marker surfaced when the credential bundle carries no usable API key.
    /// Failing here keeps the run out of the CLI's first-run wizard, which
    /// would block on interactive setup instead of failing fast.
    /// </summary>
    public const string MissingCredentialMarker =
        "no Autohand credential configured (set host CODEYBOX_AUTOHAND_API_KEY)";

    /// <summary>
    /// Default model passed to <c>--model</c> (and seeded into the guest
    /// config) when the agent-class member does not override it. Sourced live
    /// from <see cref="AgentDefaultsSnapshot"/> (config key
    /// <c>CodeyBox:AgentDefaults[autohand]</c>). When neither is set both the
    /// flag and the file field are omitted and the CLI uses its own startup
    /// default — a bare <c>--model</c> without a configured provider default
    /// would resolve against the wrong catalog, so it is never invented here.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    internal string ConfiguredProvider => string.IsNullOrWhiteSpace(_options?.Invoke().Provider)
        ? AutohandOptions.DefaultProvider
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
    /// Seeds the guest <c>~/.autohand/config.json</c> before the CLI runs.
    /// The CLI reads its provider credential exclusively from that file
    /// (environment variables never backfill it — verified live), so without
    /// this step every dispatch would land in the blocking first-run wizard
    /// or fail with <c>Setup cancelled</c>. The payload travels through
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

        string configJson;
        try
        {
            configJson = AutohandConfigBuilder.BuildConfigJson(
                ConfiguredProvider,
                ResolveEffectiveModel(modelId: null),
                apiKey);
        }
        catch (ArgumentException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise autohand config: {ex.Message}",
                Stdout: null,
                Stderr: ex.Message);
        }

        // NOTE: the seeded file model is the config-sourced default, not the
        // per-item override: PrepareAgentSandboxAsync does not receive the
        // dispatch modelId, and the explicit --model flag (emitted by
        // BuildInvocation) wins over the file default at runtime anyway.
        try
        {
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    AutohandConfigBuilder.GuestConfigRelativePath),
                configJson,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
        catch (SandboxCredentialFileWriteException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise autohand config: exit {ex.ExitCode}",
                Stdout: ex.Stdout,
                Stderr: ex.Stderr)
            {
                ExecutionUnavailable = ex.ExecutionUnavailable,
            };
        }

        return null;
    }

    /// <summary>
    /// Verifies <c>--output-format stream-json</c> support with
    /// <c>autohand --help</c>. The runner's only transport is the JSONL event
    /// stream, so a binary that no longer advertises the flag must fail
    /// closed here rather than dispatch into an unparseable run.
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
        // One-shot headless form: bare `-p` consumes the piped-stdin prompt,
        // acts on the repository, and exits. The stdin variant is deliberate:
        // `-p <text>` would put the whole rework prompt in one argv element
        // (128 KiB MAX_ARG_STRLEN ceiling). --output-format stream-json is
        // the ONLY transport this runner speaks — even when the caller did
        // not ask for structured capture — so failure classification and
        // stream parsing never depend on which call path dispatched the run.
        var argv = new List<string>
        {
            Binary, "-p",
            "--output-format", "stream-json",
            // No vendor account in the sandbox: bare mode skips the device
            // login, hooks, LSP, attribution, and AGENTS.md discovery. The
            // bare gate requires AUTOHAND_API_KEY in the exec environment
            // (see DirectCredentialEnvironmentVariables).
            "--bare",
            // Throwaway VM with no human: auto-confirm risky actions and take
            // no approval prompts at all (the GOOSE_MODE=auto equivalent).
            // Immutable security checks still run first, so this cannot
            // override the built-in blacklist. Never --plan: read-only
            // planning produces no changes by design.
            "--yes",
            "--unrestricted",
            // No startup network beyond inference: catalog refreshes and
            // update checks would stall against the egress allow-list.
            "--offline",
        };

        var effectiveModel = ResolveEffectiveModel(modelId);
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort is deliberately NOT mapped: autohand exposes
        // --thinking/--temperature with provider-dependent vocabularies, and
        // emitting one unconditionally would fail dispatches for other
        // provider families — same rationale as the aider runner.
        _ = captureStructuredStream;
        _ = credential;
        _ = reasoningMode;
        return new AgentInvocation(
            argv,
            ExtraEnvironment: null,
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

        // Autohand exits 0 on some terminal failures (verified: the generic
        // error frame exits 0 with the cause only in the event stream). Lift
        // the terminal error so the pipeline can classify it; without this an
        // exit-0 quota/auth give-up with no file changes terminal-fails as
        // "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && AutohandTerminalDiagnoser.TryExtractTerminalError(result.Stdout) is { } terminalError)
        {
            return result with { TerminalDiagnostic = terminalError };
        }

        return result;
    }
}
