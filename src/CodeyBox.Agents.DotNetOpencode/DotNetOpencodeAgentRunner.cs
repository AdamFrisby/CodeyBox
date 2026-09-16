using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Drives the <c>dotnet-opencode</c> CLI (Hona/dotnet-opencode, a .NET port of
/// OpenCode V2) in headless mode via <c>run --format json --standalone</c>,
/// with the prompt on stdin.
///
/// <para><b>Why a new adapter, not the existing opencode one</b> (verified
/// 2026-09-16 against
/// <c>0.1.0-ci.20260905083303.33955573552.1</c>; see
/// <c>docs/reference/agent-quirks.md#dotnet-opencode-honadotnet-opencode</c>).
/// The CLIs share command names but differ in every integration dimension:
/// binary (<c>dotnet-opencode</c> shim, needs the .NET 11 preview runtime) vs
/// <c>opencode</c>; transport (<c>--format json</c> event envelope
/// <c>{type, timestamp, sessionID, part|error}</c> vs unverified plaintext);
/// credentials (global <c>~/.config/opencode/opencode.json</c> with
/// <c>{env:VAR}</c> indirection — bare <c>ANTHROPIC_API_KEY</c> is ignored,
/// shared auth files/databases are not consulted — vs the Go-subscription
/// <c>auth.json</c> file); quota (provider HTTP shapes only, no Go
/// subscription windows); baseline (.NET 11 preview SDK + ripgrep vs the
/// install script). Folding both behind one <see cref="AgentKind"/> would
/// couple the existing opencode path to flags it never verified.</para>
///
/// <para><b>Transport.</b> <c>--format json</c> is the ONLY transport this
/// runner speaks — even when the caller did not ask for structured capture —
/// so cost attribution (<c>step_finish</c> <c>part.tokens</c>), failure
/// classification (<c>type:error</c> frames), and stream parsing never depend
/// on which call path dispatched the run. <c>--standalone</c> selects a
/// private scoped server: the default managed service binds a port and
/// outlives the run, so a second dispatch in the same VM fails with
/// "listener address is already in use"; the sandbox VM is discarded after
/// the run, making daemon reuse pure overhead. <c>--auto</c> approves asked
/// permissions once — without it headless runs cancel forms and reject
/// permission-gated tool calls, which would turn every dispatch into an
/// empty run. Explicit denials are still honoured.</para>
///
/// <para><b>Exit-zero answers.</b> Upstream documents that permission/form
/// rejection paths can return normally (exit 0) without a model answer.
/// <see cref="RunAsync"/> therefore lifts terminal error frames into
/// <see cref="AgentResult.TerminalDiagnostic"/> via
/// <see cref="DotNetOpencodeTerminalDiagnoser"/> so the pipeline's no-changes
/// branch can park auth/config failures instead of dead-lettering them as
/// "produced no changes" — the same give-up shape <c>pi</c> has.</para>
///
/// <para><b>Auth.</b> The CLI accepts no provider key from the bare
/// environment and performs no shared-auth import; headless auth is the
/// global config file's <c>provider.&lt;id&gt;.options.apiKey</c>, which
/// supports <c>{env:VAR}</c> indirection. The runner materialises the whole
/// file from <c>DOTNETOPENCODE_CONFIG_JSON</c> in the credential bundle
/// (verbatim host mapping <c>CODEYBOX_DOTNETOPENCODE_CONFIG_JSON</c>,
/// cursor-style). Operators keeping keys out of the bundle put
/// <c>{env:ANTHROPIC_API_KEY}</c> in the config JSON and add that provider's
/// variable as a second mapping.</para>
/// </summary>
public sealed class DotNetOpencodeAgentRunner : CliAgentRunnerBase, IStructuredStreamAgentRunner, IAgentDefaultModelProvider, ITextOnlyAgentRunner
{
    internal const string ConfigJsonEnvironmentVariable = "DOTNETOPENCODE_CONFIG_JSON";
    private static readonly EnvBackedCredentialFile ConfigCredentialFile = new(
        ConfigJsonEnvironmentVariable,
        ".config/opencode/opencode.json",
        "dotnet-opencode provider config");
    private readonly AgentDefaultsSnapshot? _defaults;

    public DotNetOpencodeAgentRunner() : this(defaults: null) { }

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). Supplies
    /// <see cref="DefaultModelId"/> when a caller does not pass an explicit
    /// model, so the dispatch model is sourced from hot-reloadable config
    /// rather than a hardcoded literal.
    /// </param>
    public DotNetOpencodeAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.DotNetOpencode;

    /// <summary>
    /// Default dotnet-opencode binary name inside the sandbox: the
    /// <c>dotnet tool install</c> shim on PATH. Shared with
    /// <c>DotNetOpencodeInVmSmokeProbe</c> so the smoke check and the real
    /// runner always invoke the same binary. (<c>dotnet opencode</c> is the
    /// same entry point via the .NET CLI; the shim avoids depending on a
    /// <c>dotnet</c> host wrapper on PATH.)
    /// </summary>
    public const string DefaultBinary = "dotnet-opencode";

    /// <summary>Path to the dotnet-opencode binary inside the sandbox. Defaults to <see cref="DefaultBinary"/>.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Default model passed to <c>--model</c> (<c>provider/model</c> form)
    /// when the agent-class member does not override it. Sourced live from
    /// <see cref="AgentDefaultsSnapshot"/>.
    /// </summary>
    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles => [ConfigCredentialFile];

    protected override string PreemptProcessPattern => Binary;

    /// <summary>
    /// Verifies <c>--format json</c> support with <c>run --help</c>. The
    /// runner's only transport is the JSON event stream, so a binary that no
    /// longer advertises the flag must fail closed here rather than dispatch
    /// into an unparseable plaintext run.
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
        // `dotnet-opencode run` is the documented headless entry point.
        // --format json is unconditional (the runner's only transport);
        // --standalone avoids the managed-service port/election overhead in
        // a discardable VM; --auto lets permission-gated tool calls proceed
        // headless (forms are otherwise cancelled and the run comes back
        // empty). Prompt via stdin dodges the 128 KiB MAX_ARG_STRLEN ceiling
        // rework prompts can blow through — redirected stdin is appended to
        // the message by the CLI.
        var argv = new List<string> { Binary, "run", "--format", "json", "--standalone", "--auto" };

        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        if (!string.IsNullOrEmpty(effectiveModel))
        {
            argv.Add("--model");
            argv.Add(effectiveModel);
        }

        // Reasoning effort: the CLI documents --thinking as a show-reasoning
        // boolean, not an effort level, so there is no flag to map
        // reasoningMode onto. Operators needing reasoning blocks set it via
        // their own wrapper, not here.
        _ = reasoningMode;
        _ = captureStructuredStream;
        _ = credential;
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

        // Upstream permission/form rejections can exit 0 with no model
        // answer. Lift the terminal error region so the pipeline can classify
        // it; without this an exit-0 auth/config give-up with no file changes
        // terminal-fails as "produced no changes".
        if (string.IsNullOrEmpty(result.TerminalDiagnostic)
            && DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr) is { } terminalError)
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
            ConfigJsonEnvironmentVariable);

    // The dotnet-opencode CLI runs inside the work-item sandbox; a host-side
    // text-only call with no sandbox returns failure (see
    // RunTextOnlyRequiresSandboxAsync below).
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
