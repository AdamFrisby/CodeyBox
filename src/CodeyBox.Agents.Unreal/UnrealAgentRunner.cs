using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// Drives the Unreal Labs unreal-agent CLI (binary <c>unreal-agent-runner</c>,
/// GitHub <c>github.com/unreallabsai/unreal-agent</c>, pinned release v0.1.1,
/// commit <c>b7c9bf1c5c</c>) in headless mode.
///
/// <para><b>Invocation Contract (Trap 1 avoidance):</b>
/// <c>unreal-agent-runner</c> accepts flags <c>-workspace</c>, <c>-log-directory</c>,
/// and <c>-session-directory</c>. It has NO <c>-p</c> or <c>--prompt</c> flag;
/// passing <c>/dev/stdin</c> or a prompt on argv fails. Prompt, model, and thinking level
/// must travel strictly as a JSON request envelope on standard input:
/// <c>{"prompt":..., "model":..., "thinking_level":...}</c>. Linux's <c>MAX_ARG_STRLEN</c>
/// is 128 KiB per argv element, and stdin delivery supports prompts of arbitrary size.</para>
///
/// <para><b>Log and Session Directories (Trap 2 avoidance):</b>
/// By default, <c>unreal-agent-runner</c> creates logs inside <c>&lt;workspace&gt;/logs</c>
/// and sessions inside <c>~/.unreal-agent/sessions</c>. If left inside the workspace,
/// logs pollute the repository under test and corrupt git diff calculations. The runner
/// explicitly directs logs to <c>/home/ubuntu/.unreal-agent/logs</c> and sessions to
/// <c>/home/ubuntu/.unreal-agent/sessions</c>, strictly outside the workspace.
/// <c>.unreal-agent/sessions</c> is declared in <see cref="ScratchpadHomeDirectories"/>,
/// while logs are kept out of the scratchpad allowlist because of unbounded growth.</para>
///
/// <para><b>Workspace .env Quarantine (Trap 3 avoidance):</b>
/// At startup, <c>unreal-agent-runner</c> loads <c>&lt;workspace&gt;/.env</c> via <c>loadDotEnv</c>.
/// If <c>SANDBOX_EGRESS_PROXY</c> is present in that <c>.env</c>, it overwrites <c>HTTPS_PROXY</c>
/// in the process environment, allowing an untrusted repository to hijack egress traffic.
/// Furthermore, <c>.env</c> can override provider, model, base URL, and credentials.
/// The runner therefore quarantines any workspace <c>.env</c> file to a unique backup before
/// dispatch, and restores it in a <c>finally</c> block when execution completes.</para>
///
/// <para><b>Authentication &amp; Account Safety:</b>
/// Supported pay-per-API providers include OpenRouter (<c>OPENROUTER_API_KEY</c>),
/// OpenAI (<c>OPENAI_API_KEY</c>), Fireworks (<c>FIREWORKS_API_KEY</c>), or
/// <c>UNREAL_HARNESS_LLM_API_KEY</c>. Host credential mapping routes
/// <c>CODEYBOX_UNREAL_API_KEY</c> to <c>OPENROUTER_API_KEY</c>. To protect against account
/// suspension or token theft, Codex subscription credentials (<c>OPENAI_CODEX_ACCESS_TOKEN</c>,
/// <c>OPENAI_CODEX_AUTH_FILE</c>, <c>auth.json</c>, or <c>UNREAL_HARNESS_LLM_PROVIDER=openai-codex</c>)
/// are strictly prohibited and fail fast.</para>
///
/// <para><b>Exit Codes:</b>
/// 0 indicates successful completion; 1 indicates error; 130 indicates external interruption
/// (SIGINT), which is classified as <see cref="AgentFailureKind.Infrastructure"/>.</para>
/// </summary>
public sealed class UnrealAgentRunner : CliAgentRunnerBase, IAgentDefaultModelProvider, IStructuredStreamAgentRunner
{
    private readonly AgentDefaultsSnapshot? _defaults;

    public UnrealAgentRunner() : this(defaults: null) { }

    public UnrealAgentRunner(AgentDefaultsSnapshot? defaults)
    {
        _defaults = defaults;
    }

    public override AgentKind Kind => AgentKind.Unreal;

    /// <summary>Default executable name inside the sandbox.</summary>
    public const string DefaultBinary = "unreal-agent-runner";

    /// <summary>Path to the binary inside the sandbox.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>Default log directory outside the workspace.</summary>
    public const string DefaultLogDirectory = "/home/ubuntu/.unreal-agent/logs";

    /// <summary>Log directory passed to <c>-log-directory</c>.</summary>
    public string LogDirectory { get; init; } = DefaultLogDirectory;

    /// <summary>Default session directory outside the workspace.</summary>
    public const string DefaultSessionDirectory = "/home/ubuntu/.unreal-agent/sessions";

    /// <summary>Session directory passed to <c>-session-directory</c>.</summary>
    public string SessionDirectory { get; init; } = DefaultSessionDirectory;

    public const string CredentialVariable = "OPENROUTER_API_KEY";
    public const string ProviderVariable = "UNREAL_HARNESS_LLM_PROVIDER";

    public const string MissingCredentialMarker =
        "no Unreal credential configured (set host CODEYBOX_UNREAL_API_KEY)";

    public const string ProhibitedCodexCredentialMarker =
        "Codex subscription credentials are prohibited for account-safety. Use pay-per-API credentials (e.g. OPENROUTER_API_KEY, OPENAI_API_KEY, or FIREWORKS_API_KEY).";

    public const int SigintExitCode = 130;

    protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".unreal-agent/sessions"];

    public string? DefaultModelId => _defaults?.GetDefault(Kind.Value);

    private static readonly HashSet<string> ValidThinkingLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "low", "medium", "high", "xhigh", "max"
    };

    public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        // unreal-agent-runner natively emits structured JSONL session events to stdout
        return Task.FromResult(true);
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
        if (IsCodexSubscriptionCredential(credential))
        {
            return new AgentResult(
                Success: false,
                Summary: ProhibitedCodexCredentialMarker,
                Stdout: null,
                Stderr: ProhibitedCodexCredentialMarker);
        }

        var quarantine = await QuarantineDotEnvAsync(sandbox, workingDirectory, ct).ConfigureAwait(false);
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
                modelId,
                reasoningMode,
                ct,
                stdoutChunkCallback,
                captureStructuredStream).ConfigureAwait(false);
        }
        finally
        {
            restoreNote = await RestoreDotEnvAsync(sandbox, quarantine.Quarantined, ct).ConfigureAwait(false);
        }

        return WithTerminalDiagnostic(WithRestoreNote(result, restoreNote));
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
        if (IsCodexSubscriptionCredential(credential))
        {
            return new AgentResult(
                Success: false,
                Summary: ProhibitedCodexCredentialMarker,
                Stdout: null,
                Stderr: ProhibitedCodexCredentialMarker);
        }

        var quarantine = await QuarantineDotEnvAsync(sandbox, workingDirectory, ct).ConfigureAwait(false);
        if (quarantine.Failure is { } quarantineFailure)
            return quarantineFailure;

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
                modelId,
                reasoningMode,
                ct,
                stdoutChunkCallback).ConfigureAwait(false);
        }
        finally
        {
            restoreNote = await RestoreDotEnvAsync(sandbox, quarantine.Quarantined, ct).ConfigureAwait(false);
        }

        return WithTerminalDiagnostic(WithRestoreNote(result, restoreNote));
    }

    public AgentFailureClassification ClassifyFailure(AgentResult result)
    {
        if (!result.Success && AgentSuspendResilience.ParseAgentExitCode(result.Summary) is { } exitCode)
        {
            if (exitCode == SigintExitCode)
            {
                return new AgentFailureClassification(
                    AgentFailureKind.Infrastructure,
                    Reason: "unreal-agent run interrupted by SIGINT (exit 130)");
            }
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

    protected override AgentInvocation BuildInvocation(
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        bool captureStructuredStream)
    {
        var effectiveModel = !string.IsNullOrEmpty(modelId) ? modelId : DefaultModelId;
        var effectiveWorkspace = !string.IsNullOrWhiteSpace(workingDirectory) ? workingDirectory : ".";

        var argv = new List<string>
        {
            Binary,
            "-workspace",
            effectiveWorkspace,
            "-log-directory",
            LogDirectory,
            "-session-directory",
            SessionDirectory
        };

        var extraEnv = BuildProviderEnvironment(credential);
        var stdin = FormatRequestJson(prompt, effectiveModel, reasoningMode);

        return new AgentInvocation(
            argv,
            ExtraEnvironment: extraEnv.Count == 0 ? null : extraEnv,
            Stdin: stdin);
    }

    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
        => BuildInvocation(
            workingDirectory: string.Empty,
            prompt,
            credential,
            modelId,
            reasoningMode,
            captureStructuredStream);

    protected override AgentInvocation BuildTextOnlyInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null)
        => BuildInvocation(
            workingDirectory: string.Empty,
            prompt,
            credential,
            modelId,
            reasoningMode,
            captureStructuredStream: false);

    internal static string FormatRequestJson(string prompt, string? model, string? reasoningMode)
    {
        var payload = new Dictionary<string, object>
        {
            ["prompt"] = prompt
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            payload["model"] = model;
        }

        if (!string.IsNullOrWhiteSpace(reasoningMode) && ValidThinkingLevels.Contains(reasoningMode.Trim()))
        {
            payload["thinking_level"] = reasoningMode.Trim().ToLowerInvariant();
        }

        return JsonSerializer.Serialize(payload) + "\n";
    }

    internal static Dictionary<string, string> BuildProviderEnvironment(AgentCredential? credential)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (credential is not null)
        {
            foreach (var (k, v) in credential.EnvironmentVariables)
            {
                env[k] = v;
            }
        }

        if (!env.ContainsKey(ProviderVariable))
        {
            if (env.ContainsKey("OPENROUTER_API_KEY"))
                env[ProviderVariable] = "openrouter";
            else if (env.ContainsKey("FIREWORKS_API_KEY"))
                env[ProviderVariable] = "fireworks";
            else if (env.ContainsKey("OPENAI_API_KEY"))
                env[ProviderVariable] = "openai";
        }

        return env;
    }

    internal static bool IsCodexSubscriptionCredential(AgentCredential? credential)
    {
        if (credential is null)
            return false;

        if (credential.EnvironmentVariables.ContainsKey("OPENAI_CODEX_ACCESS_TOKEN")
            || credential.EnvironmentVariables.ContainsKey("OPENAI_CODEX_AUTH_FILE"))
        {
            return true;
        }

        if (credential.EnvironmentVariables.TryGetValue(ProviderVariable, out var provider)
            && string.Equals(provider, "openai-codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var key in credential.Files.Keys)
        {
            if (key.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase)
                || key.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task<DotEnvQuarantineOutcome> QuarantineDotEnvAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new DotEnvQuarantineOutcome(null, null);

        var root = workingDirectory.TrimEnd('/');
        var envFile = $"{root}/.env";

        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", envFile],
        }, ct).ConfigureAwait(false);

        if (!probe.Success)
            return new DotEnvQuarantineOutcome(null, null);

        var backupSuffix = $".codeybox-quarantined-{Guid.NewGuid():N}"[..31];
        var backupFile = $"{envFile}{backupSuffix}";

        var move = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mv", "--", envFile, backupFile],
        }, ct).ConfigureAwait(false);

        if (!move.Success)
        {
            return new DotEnvQuarantineOutcome(
                null,
                new AgentResult(
                    Success: false,
                    Summary: $"refusing Unreal dispatch: workspace .env is present but could not be quarantined (exit {move.ExitCode})",
                    Stdout: move.Stdout,
                    Stderr: move.Stderr));
        }

        return new DotEnvQuarantineOutcome(new QuarantinedDotEnv(envFile, backupFile), null);
    }

    internal static async Task<string?> RestoreDotEnvAsync(
        ISandbox sandbox,
        QuarantinedDotEnv? quarantined,
        CancellationToken ct)
    {
        if (quarantined is null)
            return null;

        try
        {
            var reappeared = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["test", "-e", quarantined.Original],
            }, ct).ConfigureAwait(false);

            if (reappeared.Success)
            {
                return $"workspace {quarantined.Original} reappeared during run; backup left at {quarantined.Backup}";
            }

            var restore = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["mv", "--", quarantined.Backup, quarantined.Original],
            }, ct).ConfigureAwait(false);

            if (!restore.Success)
            {
                return $"failed to restore {quarantined.Original} from {quarantined.Backup} (exit {restore.ExitCode})";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"failed to restore {quarantined.Original}: {ex.GetType().Name}";
        }

        return null;
    }

    private static AgentResult WithTerminalDiagnostic(AgentResult result)
    {
        if (result.Success || result.TerminalDiagnostic is not null)
            return result;

        var diag = UnrealTerminalDiagnoser.TryExtractTerminalError(result.Stdout, result.Stderr);
        return diag is null ? result : result with { TerminalDiagnostic = diag };
    }

    private static AgentResult WithRestoreNote(AgentResult result, string? restoreNote)
    {
        if (string.IsNullOrEmpty(restoreNote))
            return result;

        var note = $".env restore incomplete: {restoreNote}";
        return result with
        {
            Stderr = string.IsNullOrEmpty(result.Stderr) ? note : $"{result.Stderr}\n{note}",
        };
    }
}

internal sealed record QuarantinedDotEnv(string Original, string Backup);

internal sealed record DotEnvQuarantineOutcome(
    QuarantinedDotEnv? Quarantined,
    AgentResult? Failure);
