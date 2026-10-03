using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineAgentExecutor — the data-plane side of the control/execution
// split defined by the ExecutorPhaseRequest/Result contract (see
// src/CodeyBox.Core/ExecutorPhase.cs).
//
// This type owns the mechanics of running agent work and nothing else:
// sandbox acquisition (placement-driven or direct), running commands inside
// a sandbox, materialising credential files, and projecting captured stdout
// to the agent-visible text. It never touches the work-item table, the
// queue, audit verdicts, or merge state — those are control-plane concerns
// on PipelineRunner / PipelineControlDecisions. In particular it must never
// gain a dependency on IWorkItemStore, IProjectRepository, or the pipeline
// state machine; PipelineControlExecutionSplitTests pins that.
//
// PipelineRunner keeps the facade (IPipelineRunner) and the orchestration
// spine; it calls into this type for every execution primitive below.
// Behaviour is unchanged: the bodies moved verbatim from
// PipelineRunner.SandboxExec.cs and PipelineRunner.CheckAndAct.cs.
internal sealed class PipelineAgentExecutor
{
    private readonly ISandboxProvider _sandboxes;
    private readonly SandboxPlacementAcquirer? _placer;

    public PipelineAgentExecutor(ISandboxProvider sandboxes, SandboxPlacementAcquirer? placer)
    {
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _placer = placer;
    }

    /// <summary>
    /// Acquires a work-phase sandbox. When placement is wired (production),
    /// builds the placement requirements from the work item's required
    /// capabilities plus the network profile and credential the phase's
    /// sandbox target already needs, places onto a member, and creates the
    /// sandbox on that member's registry provider.
    /// A permanent refusal (capability no member declares) throws
    /// <see cref="SandboxPlacementUnplaceableException"/> naming the
    /// capability so the item fails operator-visible; a transient refusal
    /// throws <see cref="SandboxProvisioningDeferredException"/> so the item
    /// requeues under the existing backoff. Null placer keeps the legacy
    /// direct-provider path.
    /// </summary>
    public Task<ISandbox> AcquireWorkPhaseSandboxAsync(
        WorkItemId workItemId,
        string phase,
        IReadOnlyList<string> requiredCapabilities,
        string? credentialName,
        string? networkProfile,
        SandboxSpec spec,
        CancellationToken ct)
    {
        if (_placer is null)
            return _sandboxes.CreateAsync(spec, ct);
        return _placer.AcquireAsync(
            new SandboxPlacementAcquisition(
                workItemId,
                phase,
                requiredCapabilities,
                credentialName,
                networkProfile,
                spec),
            ct);
    }

    public static async Task MaterialiseCredentialFilesAsync(ISandbox sandbox, AgentCredential credential, CancellationToken ct)
    {
        SandboxCredentialFileWriter.ValidateMaterializationPlan(credential, []);
        foreach (var (relativePath, contents) in credential.Files)
        {
            var safePath = SanitiseCredentialFileName(relativePath);
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(SandboxCredentialFileRoot.CredentialsDirectory, safePath),
                contents,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
        }
    }

    public static async Task Run(ISandbox sandbox, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv });
        if (r.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(r.ExitCode);
        if (!r.Success)
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}");
    }

    public static async Task RunWithCancellation(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (r.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(r.ExitCode);
        if (!r.Success)
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}");
    }

    // Best-effort recovery of a COW-inherited, root-owned per-user NuGet home run
    // once when preparing a tool-audit sandbox, before its `dotnet build`/`test`/
    // `format` gates. A broken home otherwise aborts restore with "Failed to read
    // NuGet.Config due to unauthorized access". The branch's MSBuild InitialTargets
    // hook (Directory.NuGetHomeHeal.targets) already heals every `dotnet build`/
    // `test` invocation on its own; this setup step is a complementary safety net
    // that also covers gate commands which do not evaluate those props (e.g. a bare
    // `dotnet restore` or `dotnet format`) and does the repair once so the shared
    // sandbox's later gates inherit a healthy home. It dot-sources the checked-out
    // branch's own repository-owned recovery (scripts/nuget-home-heal.sh), whose
    // on-disk repair persists for every gate sharing the sandbox; the trailing
    // `true` keeps the step best-effort so a missing script or unhealable home
    // never masks the real gate error. This adds no capability the audit sandbox
    // lacks — it already runs the branch's arbitrary build logic via `dotnet build`
    // in this same credential-free sandbox — and is a no-op when the home is
    // already usable.
    public static Task HealAuditNuGetHomeAsync(ISandbox sandbox, CancellationToken ct)
        => RunWithCancellation(
            sandbox,
            ct,
            "sh",
            "-c",
            "cd \"$1\" 2>/dev/null && [ -f scripts/nuget-home-heal.sh ] && "
                + ". ./scripts/nuget-home-heal.sh; true",
            "sh",
            SandboxConventions.WorkDir);

    public static void ThrowIfExecutionUnavailable(SandboxExecResult result)
    {
        if (result.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(result.ExitCode);
    }

    public static InvalidOperationException CommandFailed(SandboxExecResult result, IReadOnlyList<string> argv)
        => new($"command failed (exit {result.ExitCode}): {string.Join(' ', argv)}\n{result.Stderr}");

    // Runs a command but replaces the last argv element with "***" in any exception message,
    // used when the last element is a sensitive value (e.g. user.email) that must not reach
    // audit-tier logs.
    public static async Task RunMasked(ISandbox sandbox, params string[] argv)
    {
        await RunMasked(sandbox, CancellationToken.None, argv);
    }

    public static async Task RunMasked(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (!r.Success)
        {
            var masked = argv.Length > 0
                ? argv[..^1].Append("***").ToArray()
                : argv;
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', masked)}\n{r.Stderr}");
        }
    }

    internal static string SanitiseCredentialFileName(string path)
    {
        SandboxCredentialFileWriter.ValidateRelativePath(path, nameof(path));
        return path;
    }

    /// <summary>
    /// Projects a captured stdout blob to the agent-visible answer text.
    /// Envelope-framed runners (the devin.acp shim emits the agent's text
    /// JSON-escaped inside <c>finalText</c> envelopes) implement
    /// <see cref="IAgentVisibleTextExtractor"/>; every other runner's
    /// captured stdout is already the plain text. Callers that feed a
    /// plain-text parser (the check-and-act verdict sentinels, the
    /// <c>&lt;codeybox-question&gt;</c> block parser, the PR-description
    /// tail) MUST pass the capture through this — the sentinel JSON never
    /// parses out of raw NDJSON.
    /// </summary>
    public static string AgentVisibleStdout(IAgentRunner runner, string capturedStdout)
        => (runner as IAgentVisibleTextExtractor)?.ExtractAgentVisibleText(capturedStdout)
            ?? capturedStdout;
}
