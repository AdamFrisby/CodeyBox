using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
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
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
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
        (string Name, string Email)? projectGitAuthor = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var queue = new InMemoryTaskQueue();
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent(mergeStrategy?.ToList() ?? [MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);
        var auditorList = (auditors ?? []).ToList();

        // Project repo: a single in-memory project pointing at the seed.
        // AuditTypes must include "scripted" so the ScriptedAuditorCatalog
        // gets a chance to return its auditors when there are any to run.
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            GitAuthorName = projectGitAuthor?.Name,
            GitAuthorEmail = projectGitAuthor?.Email,
            Audit = new ProjectAudit
            {
                MaxIterations = maxAuditIterations,
                AuditTypes = auditTypes,
            },
        });

        var presetCatalog = new ScriptedAuditorCatalog(auditorList);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var upstreamFactory = new TestUpstreamFactory();

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [], HostGitIdentity = hostGitIdentity },
            NullLogger<PipelineRunner>.Instance);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
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

    public TestPipeline(PipelineRunner pipeline, SqliteWorkItemStore store, ScriptedAgent agent, LocalGitHost gitHost, string gitRoot)
    {
        Pipeline = pipeline;
        Store = store;
        Agent = agent;
        GitHost = gitHost;
        GitRoot = gitRoot;
    }

    public void Dispose() => Store.Dispose();
}

internal sealed class StaticCredentialProvider : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(null);
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
internal sealed partial class ScriptedAgent : IAgentRunner
{
    private readonly Queue<MergeStrategy> _mergeStrategies;
    public Queue<FileWrite> WorkPlan { get; } = new();
    public AgentKind Kind => AgentKind.Claude;

    public ScriptedAgent(IEnumerable<MergeStrategy> mergeStrategies)
    {
        _mergeStrategies = new Queue<MergeStrategy>(mergeStrategies);
    }

    public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential, string? modelId = null, CancellationToken ct = default)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
        {
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);
        }
        return await HandleWorkAsync(sandbox, workingDirectory, ct);
    }

    private async Task<AgentResult> HandleWorkAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct)
    {
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
            ? new AgentResult(true, "ok", null, null)
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

internal sealed record FileWrite(string FileName, string Contents);

/// <summary>
/// Webhook dispatcher that captures all published events in memory.
/// Shared across stuck-probe test files.
/// </summary>
internal sealed class CapturingWebhookDispatcher : IWebhookDispatcher
{
    public List<WebhookEvent> Events { get; } = [];

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }
}
