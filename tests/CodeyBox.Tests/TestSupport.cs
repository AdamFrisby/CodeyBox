using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Shared helpers for the pipeline integration tests. Wires a fully working
/// orchestrator using the in-process Process sandbox + a scripted agent +
/// scripted auditors.
/// </summary>
internal static class TestSupport
{
    public static async Task<string> CreateSeedRepoAsync(string root, string name = "seed")
    {
        var seed = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init", "-b", "main");
        await RunGit(seed, "config", "user.email", "t@l");
        await RunGit(seed, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await RunGit(seed, "add", "README.md");
        await RunGit(seed, "commit", "-m", "initial");
        return seed;
    }

    public static async Task<(int code, string stdout, string stderr)> RunGit(string cwd, params string[] args)
    {
        var rc = await RunGitNoThrow(cwd, args);
        if (rc.code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {rc.stderr}");
        return rc;
    }

    public static async Task<(int code, string stdout, string stderr)> RunGitNoThrow(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Builds a complete working pipeline using the Process sandbox. Returns
    /// the disposable resources (caller wraps in using/await using) plus the
    /// configured PipelineRunner.
    /// </summary>
    public static TestPipeline BuildPipeline(
        string workspace,
        string seedRepoUrl,
        IEnumerable<IAuditor>? auditors = null,
        int maxAuditIterations = 3,
        IEnumerable<MergeStrategy>? mergeStrategy = null,
        HostGitIdentity? hostGitIdentity = null,
        (string Name, string Email)? projectGitAuthor = null,
        IAuditReportStore? auditReportStore = null,
        int maxLlmAuditorParallelism = 3,
        ProjectUpstream? upstream = null,
        IUpstreamRemoteFactory? upstreamFactory = null,
        PipelineOptions? pipelineOptions = null,
        IAgentStreamStore? agentStreams = null,
        ITimingStore? timingStore = null,
        string? defaultBaseBranch = "main",
        ProjectAudit? projectAudit = null,
        IPresetCatalog? presetCatalogOverride = null,
        ISandboxProvider? sandboxProvider = null,
        bool graphicalSandbox = false,
        ProjectNetworkProfiles? networkProfiles = null,
        ICredentialProvider? credentials = null,
        IProjectRepository? projectRepository = null,
        AgentClassRouter? classRouter = null,
        QuotaRouterOptions? auditQuotaOptions = null,
        Func<IGitHost, IGitHost>? gitHostDecorator = null,
        IWebhookDispatcher? webhookDispatcher = null,
        IWorkItemCostStore? costStore = null,
        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? costExtractors = null,
        AgentCostCalculator? costCalculator = null,
        string? stateDbPathOverride = null,
        IPreMergeVerifier? preMergeVerifier = null,
        IRequiredBuildVerifier? requiredBuildVerifier = null,
        IncrementalRebaseSnapshot? incrementalRebase = null,
        PipelineTuningSnapshot? pipelineTuning = null,
        ITaskQueue? taskQueue = null,
        Func<SqliteWorkItemStore, IWorkItemStore>? workItemStoreDecorator = null,
        IAgentInvolvementStore? involvement = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        IAuditProgressStore? auditProgressOverride = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = stateDbPathOverride ?? Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var pipelineStore = workItemStoreDecorator?.Invoke(store) ?? store;
        var queue = taskQueue ?? new InMemoryTaskQueue();
        var realGitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        // Tests that need to inject failures (e.g. SetBranchToCommitAsync throw)
        // wrap the real host. PipelineRunner sees the decorator; tests still
        // hold a handle to the underlying LocalGitHost via TestPipeline.GitHost.
        IGitHost gitHost = gitHostDecorator?.Invoke(realGitHost) ?? realGitHost;
        var sandboxes = sandboxProvider ?? new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent(mergeStrategy?.ToList() ?? [MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);
        var auditorList = (auditors ?? []).ToList();

        // Project repo: a single in-memory project pointing at the seed.
        // AuditTypes must include "scripted" so the ScriptedAuditorCatalog
        // gets a chance to return its auditors when there are any to run.
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var audit = projectAudit ?? new ProjectAudit
        {
            MaxIterations = maxAuditIterations,
            AuditTypes = auditTypes,
            MaxLlmAuditorParallelism = maxLlmAuditorParallelism,
        };
        var defaultProject = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = defaultBaseBranch,
            DefaultAgent = AgentKind.Claude,
            GitAuthorName = projectGitAuthor?.Name,
            GitAuthorEmail = projectGitAuthor?.Email,
            GraphicalSandbox = graphicalSandbox,
            NetworkProfiles = networkProfiles ?? new ProjectNetworkProfiles(),
            Upstream = upstream ?? ProjectUpstream.Noop,
            Audit = audit,
        };
        var projects = projectRepository ?? new InMemoryProjectRepository(defaultProject);

        var presetCatalog = presetCatalogOverride ?? new ScriptedAuditorCatalog(auditorList);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var resolvedUpstreamFactory = upstreamFactory ?? new TestUpstreamFactory();
        var resolvedOptions = pipelineOptions ?? new PipelineOptions
        {
            SandboxImageReference = "ignored",
            AgentAllowedHosts = [],
            HostGitIdentity = hostGitIdentity,
        };

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials ?? new StaticCredentialProvider(), prs,
            projects, resolvedUpstreamFactory, composer,
            pipelineStore,
            webhookDispatcher ?? new NullWebhookDispatcher(),
            resolvedOptions,
            NullLogger<PipelineRunner>.Instance,
            timingStore: timingStore,
            auditReports: auditReportStore,
            agentStreams: agentStreams,
            auditQuotaOptions: auditQuotaOptions,
            classRouter: classRouter,
            costStore: costStore,
            costExtractors: costExtractors,
            costCalculator: costCalculator,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            }),
            preMergeVerifier: preMergeVerifier,
            incrementalRebase: incrementalRebase,
            pipelineTuning: pipelineTuning,
            taskQueue: queue,
            involvement: involvement,
            requiredBuildVerifier: requiredBuildVerifier ?? new SandboxRequiredBuildVerifier(
                sandboxes,
                gitHost,
                resolvedOptions),
            dispatchAvailability: inVmSmokeGate is null
                ? null
                : new AgentDispatchAvailability(inVmSmokeGate: inVmSmokeGate),
            auditProgress: auditProgressOverride ?? store);

        return new TestPipeline(pipeline, store, agent, realGitHost, gitRoot, queue, involvement);
    }
}

/// <summary>Bundle of resources returned by <see cref="TestSupport.BuildPipeline"/>.</summary>
internal sealed class TestPipeline : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public ScriptedAgent Agent { get; }
    public LocalGitHost GitHost { get; }
    public string GitRoot { get; }
    public ITaskQueue Queue { get; }
    public IAgentInvolvementStore? Involvement { get; }

    public TestPipeline(PipelineRunner pipeline, SqliteWorkItemStore store, ScriptedAgent agent, LocalGitHost gitHost, string gitRoot,
        ITaskQueue? queue = null,
        IAgentInvolvementStore? involvement = null)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        GitHost = gitHost;
        GitRoot = gitRoot;
        Queue = queue ?? new InMemoryTaskQueue();
        Involvement = involvement;
    }

    public void Dispose() => Store.Dispose();
}

internal sealed class StaticCredentialProvider : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(null);
}

internal sealed class TestRequiredBuildVerifier : IRequiredBuildVerifier
{
    public static TestRequiredBuildVerifier NotApplicable => new(
        RequiredBuildProbeResult.NotApplicable,
        RequiredBuildVerificationResult.Skipped);

    private readonly RequiredBuildProbeResult _probeResult;
    private readonly RequiredBuildVerificationResult _verificationResult;

    public TestRequiredBuildVerifier(
        RequiredBuildProbeResult probeResult,
        RequiredBuildVerificationResult verificationResult)
    {
        _probeResult = probeResult;
        _verificationResult = verificationResult;
    }

    public int ProbeCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    public Task<RequiredBuildProbeResult> ProbeAsync(
        RequiredBuildProbeRequest request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        ProbeCalls++;
        return Task.FromResult(_probeResult);
    }

    public Task<RequiredBuildVerificationResult> VerifyAsync(
        RequiredBuildVerificationRequest request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        VerifyCalls++;
        return Task.FromResult(_verificationResult);
    }
}

internal sealed class TestUpstreamFactory : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project) => new NoopUpstreamRemote();
}

internal sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _byId;
    public InMemoryProjectRepository(params Project[] projects)
        => _byId = projects.ToDictionary(p => p.Id.Value);
    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id.Value, out var p) ? p : null);
    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>([.. _byId.Values]);
}

/// <summary>
/// Test preset catalog: returns a fixed list of auditors as the only
/// "audit type" preset. The composer concatenates these into the project's
/// effective auditor list.
/// </summary>
internal sealed class ScriptedAuditorCatalog : IPresetCatalog
{
    private readonly IReadOnlyList<IAuditor> _auditors;
    public ScriptedAuditorCatalog(IReadOnlyList<IAuditor> auditors) { _auditors = auditors; }

    public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx) => [];
    public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx) => _auditors;
    public IReadOnlyList<string> KnownLanguages => [];
    public IReadOnlyList<string> KnownAuditTypes => _auditors.Count == 0 ? [] : ["scripted"];
    public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}";
}

internal enum MergeStrategy
{
    /// <summary>Run the actual git merge command — used for the merge phase.</summary>
    RealMerge,
    /// <summary>Misbehave: agent does nothing during merge (orchestrator should fail verification).</summary>
    NoOp,
}

/// <summary>
/// Scripted agent with two modes:
///   - On work prompts: writes a configured filename with configured contents.
///   - On merge prompts (detected by "# Merge task" header): performs the
///     real git merge (or skips, per <see cref="MergeStrategy"/>).
///
/// File-write contents are consumed in order; provide one entry per
/// expected work-phase (or rework-phase) invocation.
/// </summary>
internal partial class ScriptedAgent : IAgentRunner, IStructuredStreamAgentRunner, ITextOnlyAgentRunner
{
    private readonly Queue<MergeStrategy> _mergeStrategies;
    public Queue<FileWrite> WorkPlan { get; } = new();
    public Queue<Func<IReadOnlyList<ConflictResolverFile>, IReadOnlyDictionary<string, string>>> ConflictResolutionPlan { get; } = new();
    /// <summary>
    /// Handlers invoked when the scripted agent recognises the conflict-rework
    /// prompt (third-line merge fallback). The handler receives the sandbox +
    /// working directory and returns the <see cref="AgentResult"/> the
    /// orchestrator will see. Use this to script destructive actions
    /// (git reset --hard), semantic-incompatible declarations, or
    /// fully-resolved rebases for the conflict-rework tests.
    /// </summary>
    public Queue<Func<ISandbox, string, CancellationToken, Task<AgentResult>>> ConflictReworkPlan { get; } = new();
    /// <summary>
    /// Captured prompts the conflict-rework path sent to the agent. One entry
    /// per <see cref="ConflictReworkPlan"/> invocation. Tests use this to
    /// assert the prompt scaffolding (forbidden actions, file list, etc.).
    /// </summary>
    public List<string> ConflictReworkPrompts { get; } = new();
    public Queue<string> StdoutChunks { get; } = new();
    public Queue<IReadOnlyList<string>> StdoutChunkBatches { get; } = new();
    /// <summary>
    /// Captured prompts the check-and-act path sent to this agent. One entry
    /// per <see cref="CheckPlan"/> invocation. Tests use this to assert the
    /// prompt scaffolding (sentinels, question, read-only rules) without
    /// re-implementing the prompt builder.
    /// </summary>
    public List<string> CheckInvocations { get; } = new();
    /// <summary>
    /// Scripted check-and-act verdicts. Each entry is the raw stdout the
    /// agent emits when the orchestrator dispatches a check prompt — should
    /// contain the verdict sentinels + JSON between them. Dequeued in order;
    /// running out throws.
    /// </summary>
    public Queue<string> CheckPlan { get; } = new();
    public List<bool> CaptureStructuredStreamCalls { get; } = new();
    public Func<ISandbox, string, CancellationToken, Task>? BeforeWorkAsync { get; set; }
    public int StructuredStreamSupportProbeCount { get; private set; }
    public string? ResultStdout { get; set; }
    public AgentKind Kind { get; init; } = AgentKind.Claude;
    /// <summary>
    /// When non-null, <see cref="GetTextOnlyUnavailabilityReason"/> returns this
    /// value (simulating a missing text-only credential), and
    /// <see cref="RunTextOnlyAsync"/> records its invocation in
    /// <see cref="TextOnlyInvocations"/> only when called despite the gate —
    /// which is the bug under test.
    /// </summary>
    public string? TextOnlyUnavailabilityReason { get; set; }
    public List<string> TextOnlyInvocations { get; } = new();
    /// <summary>
    /// When non-empty, each <see cref="RunTextOnlyAsync"/> call dequeues a
    /// scripted result before the default conflict-resolution handler runs.
    /// Used to simulate transient text-only failures during resolver cascade.
    /// </summary>
    public Queue<TextOnlyAgentResult> TextOnlyResults { get; } = new();

    /// <summary>
    /// Captured prompts the agentic (in-sandbox) conflict resolver sent to this
    /// agent via <see cref="RunAsync"/>. One entry per resolver attempt. The
    /// prompt starts with "# Conflict-resolution mode (in-sandbox agentic resolver)".
    /// </summary>
    public List<string> AgenticConflictInvocations { get; } = new();

    /// <summary>
    /// When non-empty, each agentic conflict-resolution invocation dequeues a
    /// scripted <see cref="AgentResult"/> instead of running the default
    /// <see cref="ConflictResolutionPlan"/> handler. Used to simulate
    /// transient agentic-resolver failures so the resolver falls through to
    /// the next candidate.
    /// </summary>
    public Queue<AgentResult> AgenticConflictResults { get; } = new();

    /// <summary>
    /// Hunk-scoped resolution handler queue. Each handler receives the
    /// parsed hunk slices the resolver sent and returns a per-hunk replacement
    /// (index → resolved-region content). Used when the conflict resolver
    /// switches to hunk-scoped mode (any file exceeds the configured payload
    /// cap). For whole-file mode tests, keep using
    /// <see cref="ConflictResolutionPlan"/>.
    /// </summary>
    public Queue<Func<IReadOnlyList<ConflictResolverHunkInput>, IReadOnlyDictionary<int, string>>> ConflictResolutionHunkPlan { get; } = new();

    private sealed record ConflictResolverInputJson(List<ConflictResolverInputFileJson>? Files);
    private sealed record ConflictResolverInputFileJson(string? Path, string? Content);
    private sealed record ConflictResolverHunkInputJson(List<ConflictResolverHunkInputItemJson>? Hunks);
    private sealed record ConflictResolverHunkInputItemJson(
        int? Index,
        string? Path,
        int? ConflictStartLine,
        int? ConflictEndLine,
        int? ContextStartLine,
        int? ContextEndLine,
        string? Content);

    public ScriptedAgent(IEnumerable<MergeStrategy> mergeStrategies)
    {
        _mergeStrategies = new Queue<MergeStrategy>(mergeStrategies);
    }

    public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        StructuredStreamSupportProbeCount++;
        return Task.FromResult(true);
    }

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
    {
        _ = credential;
        return TextOnlyUnavailabilityReason;
    }

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = ct;
        OnTextOnlyInvoked(sandbox, workingDirectory, prompt);
        TextOnlyInvocations.Add(prompt);
        if (TextOnlyResults.Count > 0)
            return Task.FromResult(TextOnlyResults.Dequeue());
        if (prompt.StartsWith("# Merge conflict resolver", StringComparison.Ordinal))
        {
            // The hunk-scoped prompt format includes the "Hunk inputs are
            // provided as JSON:" marker; whole-file uses "Conflicted file
            // inputs are provided as JSON:". Route by marker so existing
            // tests keep working unchanged and new hunk-mode tests can opt
            // into ConflictResolutionHunkPlan.
            if (prompt.Contains("Hunk inputs are provided as JSON:", StringComparison.Ordinal))
            {
                if (ConflictResolutionHunkPlan.Count == 0)
                    return Task.FromResult(new TextOnlyAgentResult(false, "ScriptedAgent: ran out of hunk-scoped conflict-resolution plan entries", null, null));

                var hunks = ParseConflictResolverHunks(prompt);
                var resolved = ConflictResolutionHunkPlan.Dequeue()(hunks);
                var output = JsonSerializer.Serialize(new
                {
                    hunks = resolved.Select(kvp => new
                    {
                        index = kvp.Key,
                        path = hunks.First(h => h.Index == kvp.Key).Path,
                        content = kvp.Value,
                    }),
                });
                return Task.FromResult(new TextOnlyAgentResult(true, "resolved", output, null));
            }

            if (ConflictResolutionPlan.Count == 0)
                return Task.FromResult(new TextOnlyAgentResult(false, "ScriptedAgent: ran out of conflict-resolution plan entries", null, null));

            var files = ParseConflictResolverFiles(prompt);
            var resolvedFiles = ConflictResolutionPlan.Dequeue()(files);
            var fileOutput = JsonSerializer.Serialize(new
            {
                files = resolvedFiles.Select(static f => new { path = f.Key, content = f.Value }),
            });
            return Task.FromResult(new TextOnlyAgentResult(true, "resolved", fileOutput, null));
        }

        if (prompt.StartsWith("# Advisory merge security review", StringComparison.Ordinal))
        {
            return Task.FromResult(new TextOnlyAgentResult(
                true,
                "reviewed",
                ResultStdout ?? """
                    {"findings":[{"title":"scripted advisory finding","description":"Advisory-only scripted merge security review finding.","location":"file.txt:1"}]}
                    """,
                null));
        }

        return Task.FromResult(new TextOnlyAgentResult(false, "unsupported text-only prompt", null, null));
    }

    protected virtual void OnTextOnlyInvoked(ISandbox? sandbox, string? workingDirectory, string prompt)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = prompt;
    }

    public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
    {
        CaptureStructuredStreamCalls.Add(captureStructuredStream);

        if (StdoutChunkBatches.Count > 0)
        {
            foreach (var chunk in StdoutChunkBatches.Dequeue())
                stdoutChunkCallback?.Invoke(chunk);
        }
        else
        {
            while (StdoutChunks.Count > 0)
                stdoutChunkCallback?.Invoke(StdoutChunks.Dequeue());
        }

        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
        {
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);
        }
        if (prompt.Contains("# Conflict-resolution mode (third-line fallback)", StringComparison.Ordinal))
        {
            ConflictReworkPrompts.Add(prompt);
            if (ConflictReworkPlan.Count == 0)
                return new AgentResult(false, "ScriptedAgent: ran out of conflict-rework plan entries", null, null);
            var handler = ConflictReworkPlan.Dequeue();
            return await handler(sandbox, workingDirectory, ct);
        }
        if (prompt.StartsWith("# Conflict-resolution mode (in-sandbox agentic resolver)", StringComparison.Ordinal))
        {
            return await HandleAgenticConflictAsync(sandbox, workingDirectory, prompt, ct);
        }
        if (prompt.StartsWith("# Check-and-Act task", StringComparison.Ordinal))
        {
            return await HandleCheckAsync(prompt, stdoutChunkCallback, ct);
        }
        return await HandleWorkAsync(sandbox, workingDirectory, ct);
    }

    private Task<AgentResult> HandleCheckAsync(
        string prompt, Action<string>? stdoutChunkCallback, CancellationToken ct)
    {
        _ = ct;
        CheckInvocations.Add(prompt);
        if (CheckPlan.Count == 0)
            return Task.FromResult(new AgentResult(false, "ScriptedAgent: ran out of check-plan entries", null, null));
        var verdictStdout = CheckPlan.Dequeue();
        stdoutChunkCallback?.Invoke(verdictStdout);
        return Task.FromResult(new AgentResult(true, "ok", verdictStdout, null));
    }

    private async Task<AgentResult> HandleAgenticConflictAsync(
        ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        AgenticConflictInvocations.Add(prompt);
        if (AgenticConflictResults.Count > 0)
            return AgenticConflictResults.Dequeue();

        // Parse the bulleted file list from the prompt — mirrors the shape
        // emitted by AgenticConflictResolver.BuildAgenticConflictResolverPrompt.
        var files = ParseAgenticConflictFiles(prompt);
        if (files.Count == 0)
            return new AgentResult(false, "ScriptedAgent: agentic conflict prompt listed no files", null, null);
        if (ConflictResolutionPlan.Count == 0)
            return new AgentResult(false, "ScriptedAgent: ran out of conflict-resolution plan entries", null, null);

        // Read each conflicted file from the sandbox so the existing plan
        // handler (which takes ConflictResolverFile inputs) keeps working.
        var resolverInputs = new List<ConflictResolverFile>(files.Count);
        foreach (var file in files)
        {
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{workingDirectory}/{file}"],
            }, ct);
            if (!read.Success)
                return new AgentResult(false, $"ScriptedAgent: failed to read '{file}': {read.Stderr}", null, null);
            resolverInputs.Add(new ConflictResolverFile(file, read.Stdout));
        }

        var resolvedFiles = ConflictResolutionPlan.Dequeue()(resolverInputs);
        foreach (var (path, content) in resolvedFiles)
        {
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{path}"],
                Stdin = content,
            }, ct);
            if (!write.Success)
                return new AgentResult(false, $"ScriptedAgent: failed to write '{path}': {write.Stderr}", null, null);
            var add = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", path],
            }, ct);
            if (!add.Success)
                return new AgentResult(false, $"ScriptedAgent: failed to git add '{path}': {add.Stderr}", null, null);
        }

        return new AgentResult(true, "agentic resolved", null, null);
    }

    private static IReadOnlyList<string> ParseAgenticConflictFiles(string prompt)
    {
        // The prompt lists conflicted files as bullet entries of the form
        // "  - `path/to/file`" between the "Conflicted files" header and the
        // next blank-line-terminated section. Match those exactly.
        var marker = "Conflicted files (relative to the working tree):\n";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return [];
        start += marker.Length;
        var end = prompt.IndexOf("\n\n", start, StringComparison.Ordinal);
        if (end < 0) end = prompt.Length;
        var block = prompt[start..end];
        var files = new List<string>();
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- `", StringComparison.Ordinal)) continue;
            var open = line.IndexOf('`');
            var close = line.LastIndexOf('`');
            if (close > open) files.Add(line[(open + 1)..close]);
        }
        return files;
    }

    private static IReadOnlyList<ConflictResolverFile> ParseConflictResolverFiles(string prompt)
    {
        const string marker = "Conflicted file inputs are provided as JSON:";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return [];
        start += marker.Length;
        var end = prompt.IndexOf("\n\nReturn a single JSON object", start, StringComparison.Ordinal);
        if (end < 0)
            return [];
        var json = prompt[start..end].Trim();
        var parsed = JsonSerializer.Deserialize<ConflictResolverInputJson>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return parsed?.Files?
            .Where(static f => !string.IsNullOrWhiteSpace(f.Path))
            .Select(static f => new ConflictResolverFile(f.Path!, f.Content ?? string.Empty))
            .ToList()
            ?? [];
    }

    private static IReadOnlyList<ConflictResolverHunkInput> ParseConflictResolverHunks(string prompt)
    {
        const string marker = "Hunk inputs are provided as JSON:";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return [];
        start += marker.Length;
        var end = prompt.IndexOf("\n\nFor each input hunk", start, StringComparison.Ordinal);
        if (end < 0)
            return [];
        var json = prompt[start..end].Trim();
        var parsed = JsonSerializer.Deserialize<ConflictResolverHunkInputJson>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return parsed?.Hunks?
            .Where(static h => h.Index is not null && !string.IsNullOrWhiteSpace(h.Path))
            .Select(static h => new ConflictResolverHunkInput(
                h.Index!.Value,
                h.Path!,
                h.ConflictStartLine ?? 0,
                h.ConflictEndLine ?? 0,
                h.ContextStartLine ?? 0,
                h.ContextEndLine ?? 0,
                h.Content ?? string.Empty))
            .ToList()
            ?? [];
    }

    private async Task<AgentResult> HandleWorkAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct)
    {
        if (BeforeWorkAsync is not null)
            await BeforeWorkAsync(sandbox, workingDirectory, ct);

        if (WorkPlan.Count == 0)
            throw new InvalidOperationException("ScriptedAgent: ran out of work-phase plan entries");
        var fw = WorkPlan.Dequeue();
        var path = $"{workingDirectory}/{fw.FileName}";
        var r = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", path],
            Stdin = fw.Contents,
        }, ct);
        return r.Success
            ? new AgentResult(true, "ok", ResultStdout, null)
            : new AgentResult(false, "fail", r.Stdout, r.Stderr);
    }

    private async Task<AgentResult> HandleMergeAsync(ISandbox sandbox, string workingDirectory, string prompt, CancellationToken ct)
    {
        var strategy = _mergeStrategies.Count > 0 ? _mergeStrategies.Dequeue() : MergeStrategy.RealMerge;
        if (strategy == MergeStrategy.NoOp)
            return new AgentResult(true, "no-op", null, null);

        // Parse "merge branch `<work>` into branch `<base>`" from the prompt.
        var m = MergePromptShape().Match(prompt);
        if (!m.Success)
            return new AgentResult(false, "could not parse merge prompt", null, null);
        var workBranch = m.Groups[1].Value;
        var baseBranch = m.Groups[2].Value;

        // Run the actual merge inside the sandbox.
        string[] mergeArgv = ["git", "-C", workingDirectory, "merge", "--no-ff",
            "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}"];
        var rc = await sandbox.ExecAsync(new SandboxExec { Argv = mergeArgv }, ct);
        if (!rc.Success)
            return new AgentResult(false, $"merge step failed: {string.Join(' ', mergeArgv)}", rc.Stdout, rc.Stderr);
        _ = baseBranch;
        return new AgentResult(true, "merged", null, null);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}

/// <summary>
/// Scripted agent whose text-only path records sandbox-dispatched invocations
/// separately for orchestrator routing tests.
/// </summary>
internal sealed class SandboxTextOnlyScriptedAgent : ScriptedAgent
{
    public SandboxTextOnlyScriptedAgent(IEnumerable<MergeStrategy> mergeStrategies)
        : base(mergeStrategies)
    {
    }

    public List<string> SandboxTextOnlyInvocations { get; } = new();

    protected override void OnTextOnlyInvoked(ISandbox? sandbox, string? workingDirectory, string prompt)
    {
        if (sandbox is not null && workingDirectory is not null)
            SandboxTextOnlyInvocations.Add(prompt);
    }
}

internal sealed record FileWrite(string FileName, string Contents);

/// <summary>
/// One per-hunk payload as the conflict resolver sees it in hunk-scoped mode:
/// the file path, the conflict region's 1-indexed line range, the slice's
/// surrounding context range, and the file slice itself (with conflict markers
/// still present). The resolver's job is to produce replacement text for ONLY
/// the conflict region — the orchestrator splices it back at the conflict
/// coordinates.
/// </summary>
internal sealed record ConflictResolverHunkInput(
    int Index,
    string Path,
    int ConflictStartLine,
    int ConflictEndLine,
    int ContextStartLine,
    int ContextEndLine,
    string Content);

/// <summary>
/// Webhook dispatcher that captures all published events in memory.
/// Shared across stuck-probe test files.
/// </summary>
internal sealed class CapturingWebhookDispatcher : IWebhookDispatcher
{
    private readonly object _gate = new();
    private readonly List<WebhookEvent> _events = [];

    public IReadOnlyList<WebhookEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToList();
        }
    }

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        lock (_gate)
            _events.Add(evt);
        return Task.CompletedTask;
    }
}
