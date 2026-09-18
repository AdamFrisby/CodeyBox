using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Cursor;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the mid-iteration agent-switch credential behaviour shared by the
/// quota-fallback path and the conflict-resolver path (work item
/// <c>a09d2275</c>): a phase entered under agent A and switched to agent B
/// mid-iteration must execute B with B's own credentials; a swap to an agent
/// whose credential cannot be materialised is refused with the original
/// failure kept and no dispatch attempted; and a provider 401 on the first
/// attempt after a swap is infrastructure, never "agent requires
/// re-authentication".
/// </summary>
public sealed class AgentRunnerSwitchTests : IDisposable
{
    private const string DirectTokenVar = "TEST_DIRECT_TOKEN";
    private const string CursorQuotaStderr =
        "You're out of usage. Switch to Auto, or ask your admin to increase your limit to continue.";

    private readonly string _workspace;

    public AgentRunnerSwitchTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-switch-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public void AssessSwitch_MismatchedCredentialAgent_IsRefused()
    {
        var runner = new CredentialCapturingRunner(AgentKind.Claude, new ScriptableAgent(AgentKind.Claude));
        var otherAgentCredential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { [DirectTokenVar] = "cursor-key" },
            new Dictionary<string, string>());

        var assessment = AgentRunnerSwitchGate.AssessSwitch(runner, otherAgentCredential);

        Assert.False(assessment.Allowed);
        Assert.Contains("cursor", assessment.RefusalReason, StringComparison.Ordinal);
        Assert.Contains("claude", assessment.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AssessSwitch_UnclassifiableCredentialVariable_IsRefused()
    {
        var runner = new CredentialCapturingRunner(AgentKind.Claude, new ScriptableAgent(AgentKind.Claude));
        var unclassified = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["UNCLASSIFIED_SWITCH_TOKEN"] = "secret" },
            new Dictionary<string, string>());

        var assessment = AgentRunnerSwitchGate.AssessSwitch(runner, unclassified);

        Assert.False(assessment.Allowed);
        Assert.NotNull(assessment.RefusalReason);
        Assert.DoesNotContain("secret", assessment.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AssessSwitch_MatchingClassifiedCredential_IsAllowed()
    {
        var runner = new CredentialCapturingRunner(AgentKind.Claude, new ScriptableAgent(AgentKind.Claude));
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { [DirectTokenVar] = "claude-key" },
            new Dictionary<string, string>());

        var assessment = AgentRunnerSwitchGate.AssessSwitch(runner, credential);

        Assert.True(assessment.Allowed);
        Assert.Same(credential, assessment.Credential);
    }

    [Fact]
    public void AssessSwitch_NullCredential_IsAllowed()
    {
        // A missing credential is never a refusal: the candidate may still
        // authenticate from ambient sandbox state, and a 401 that follows a
        // swap is reclassified as infrastructure rather than refused here.
        var runner = new CredentialCapturingRunner(AgentKind.Claude, new ScriptableAgent(AgentKind.Claude));

        var assessment = AgentRunnerSwitchGate.AssessSwitch(runner, incomingCredential: null);

        Assert.True(assessment.Allowed);
        Assert.Null(assessment.Credential);
    }

    [Fact]
    public void ToPostSwapInfrastructureFailure_PreservesAgentPhaseAndEvidence()
    {
        var auth = new AgentAuthRequiredException(
            AgentKind.Copilot,
            "rework",
            "auth required from agent output: Authentication failed with provider (HTTP 401).");

        var infra = AgentRunnerSwitchGate.ToPostSwapInfrastructureFailure(auth, "rework");

        Assert.Equal(AgentKind.Copilot, infra.Agent);
        Assert.Equal("rework", infra.Phase);
        Assert.Contains("runner swap", infra.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", infra.Message, StringComparison.Ordinal);
        Assert.Same(auth, infra.InnerException);
    }

    [Fact]
    public async Task MidIterationSwap_ExecutesIncomingAgentWithItsOwnCredentials()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            cursorCredential: CredentialFor(AgentKind.Cursor, "cursor-key"),
            claudeCredential: CredentialFor(AgentKind.Claude, "claude-key"));

        fix.Cursor.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: CursorQuotaStderr));
        fix.ClaudeAgent.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));

        var item = NewItem(AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The fallback ran and the incoming agent observed its OWN credential.
        Assert.Equal(1, fix.Claude.CallCount);
        var seen = Assert.Single(fix.Claude.SeenCredentials);
        Assert.NotNull(seen);
        Assert.Equal(AgentKind.Claude, seen!.Agent);
        Assert.Equal("claude-key", seen.EnvironmentVariables[DirectTokenVar]);

        // End to end: each attempt's sandbox spec carried THAT attempt's
        // credential — the exhausted agent's key on the first spec, the
        // incoming agent's key on the second. Before the swap released the
        // warm sandbox, the second spec never existed and the incoming agent
        // inherited the first sandbox's environment.
        Assert.Equal(2, fix.Sandboxes.Specs.Count);
        Assert.Single(
            fix.Sandboxes.Specs,
            s => s.Environment.TryGetValue(DirectTokenVar, out var v) && v == "cursor-key");
        Assert.Single(
            fix.Sandboxes.Specs,
            s => s.Environment.TryGetValue(DirectTokenVar, out var v) && v == "claude-key");
    }

    [Fact]
    public async Task MidIterationSwap_UnmaterialisableCredential_RefusedWithOriginalFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            cursorCredential: CredentialFor(AgentKind.Cursor, "cursor-key"),
            // Belongs to cursor, not claude: the switch gate must refuse it.
            claudeCredential: new AgentCredential(
                AgentKind.Cursor,
                new Dictionary<string, string> { [DirectTokenVar] = "cursor-key" },
                new Dictionary<string, string>()));
        fix.ClaudeAgent.WorkPlan.Enqueue(new FileWrite("must-not-run.txt", "v1"));

        fix.Cursor.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: CursorQuotaStderr));

        var item = NewItem(AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        // The original quota failure is preserved — the item parks on the
        // cursor quota failure (quota park, or the pre-existing healthy-probe
        // redirect to transient retry that also applies to the established
        // "no registered runner" skip path), never on an auth failure from a
        // doomed dispatch of the refused agent.
        Assert.True(
            final!.State == WorkItemState.WaitingForQuotaReset
                || final.State == WorkItemState.WaitingForTransientRetry,
            $"unexpected state {final.State}");
        Assert.Contains("exhausted mid-work", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("quota failure", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(WorkItemFailureKinds.AuthRequired, final.FailureKind);

        // No dispatch of the refused agent was attempted.
        Assert.Equal(0, fix.Claude.CallCount);
        Assert.Empty(fix.Claude.SeenCredentials);
        Assert.DoesNotContain(
            fix.Webhooks.Events,
            e => e.Event == "agent.fallback"
                && e.Details is AgentFallbackDetails d
                && d.ToAgent == AgentKind.Claude.Value);
    }

    [Fact]
    public async Task PostSwapAuthFailure_IsInfrastructure_NotReauthentication()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(
            seed,
            cursorCredential: CredentialFor(AgentKind.Cursor, "cursor-key"),
            claudeCredential: CredentialFor(AgentKind.Claude, "claude-key"));

        fix.Cursor.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: null,
            Stderr: CursorQuotaStderr));
        // The swapped-in agent 401s on its very first attempt — the shape the
        // detector used to report as "agent requires re-authentication".
        fix.ClaudeAgent.ScriptedExceptions.Enqueue(new AgentAuthRequiredException(
            AgentKind.Claude,
            "work",
            "auth required from agent output: Authentication failed with provider at https://openrouter.ai/api/v1 (HTTP 401)."));

        var item = NewItem(AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final!.FailureKind);
        Assert.NotEqual(WorkItemFailureKinds.AuthRequired, final.FailureKind);
        Assert.Contains("runner swap", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", final.LastError ?? string.Empty, StringComparison.Ordinal);

        // The swap itself happened exactly once — the failure is the
        // classification of the post-swap attempt, not a missing dispatch.
        Assert.Equal(1, fix.Claude.CallCount);
    }

    [Fact]
    public async Task AuthFailureWithoutSwap_RemainsReauthenticationRequirement()
    {
        // A genuinely expired credential with no preceding swap must still
        // fail the item as auth-required: the post-swap guard must not weaken
        // the auth-failure detector.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildSoloPipeline(
            seed,
            cursorCredential: CredentialFor(AgentKind.Cursor, "cursor-key"));

        fix.Cursor.ScriptedExceptions.Enqueue(new AgentAuthRequiredException(
            AgentKind.Cursor,
            "work",
            "auth required from agent output: login prompt matched."));

        var item = NewItem(AgentKind.Cursor);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, final!.FailureKind);
    }

    [Fact]
    public async Task ConflictResolver_RefusedCandidateNeverDispatches_AndWinnerScopedToOwnCredential()
    {
        var sandbox = new SwitchConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", "<<<<<<<\nbase\n=======\nwork\n>>>>>>>\n");

        var refused = new EnvRecordingRunner(new AgentKind("refused-agent"));
        var refusedCredential = new AgentCredential(
            new AgentKind("other-agent"),
            new Dictionary<string, string> { ["REFUSED_TOKEN"] = "refused-secret" },
            new Dictionary<string, string>());

        var winner = new EnvRecordingRunner(new AgentKind("winner-agent"));
        var winnerCredential = new AgentCredential(
            winner.Kind,
            new Dictionary<string, string> { ["WINNER_TOKEN"] = "winner-secret" },
            new Dictionary<string, string>());

        var observing = new EnvObservingSandbox(sandbox);
        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions
            {
                MaxIterations = 2,
                MaxAttemptsPerAgent = 1,
            }));

        var result = await resolver.ResolveAsync(
            observing,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [
                new AgenticConflictResolverCandidate(refused, refusedCredential),
                new AgenticConflictResolverCandidate(winner, winnerCredential),
            ],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Same(winner, result.ChosenRunner);
        Assert.Equal(0, refused.InvocationCount);
        Assert.Equal(1, winner.InvocationCount);

        // The refused candidate never dispatched, and its secret never
        // reached any exec environment; the winner ran scoped to its own
        // credential. (Refusals are recorded on the failure trail; success
        // summaries only name the winning attempt.)
        var seenWinner = Assert.Single(winner.SeenCredentials);
        Assert.Same(winnerCredential, seenWinner);
        Assert.DoesNotContain(
            observing.SeenEnvironments.SelectMany(env => env.Keys),
            name => name == "REFUSED_TOKEN");
        Assert.Contains(
            observing.SeenEnvironments,
            env => env.TryGetValue("WINNER_TOKEN", out var v) && v == "winner-secret");
    }

    [Fact]
    public async Task ConflictResolver_AllCandidatesRefused_FailsWithRefusalTrail()
    {
        var sandbox = new SwitchConflictSandbox();
        sandbox.AddConflictedFile("src/a.txt", "<<<<<<<\nbase\n=======\nwork\n>>>>>>>\n");

        var refused = new EnvRecordingRunner(new AgentKind("refused-agent"));
        var refusedCredential = new AgentCredential(
            new AgentKind("other-agent"),
            new Dictionary<string, string> { ["REFUSED_TOKEN"] = "refused-secret" },
            new Dictionary<string, string>());

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions
            {
                MaxIterations = 2,
                MaxAttemptsPerAgent = 1,
            }));

        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(refused, refusedCredential)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, refused.InvocationCount);
        Assert.Null(result.ChosenRunner);
        // The refusal is visible on the failure trail instead of dispatching
        // a candidate whose credential cannot be materialised.
        Assert.Contains("credential refused", result.Summary, StringComparison.Ordinal);
        Assert.Contains("refused-agent", result.Summary, StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static AgentCredential CredentialFor(AgentKind kind, string token) =>
        new(kind,
            new Dictionary<string, string> { [DirectTokenVar] = token },
            new Dictionary<string, string>());

    private SwitchFixture BuildPipeline(
        string seedRepoUrl,
        AgentCredential? cursorCredential,
        AgentCredential? claudeCredential)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new SwitchRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var cursorInner = new ScriptableAgent(AgentKind.Cursor);
        var claudeInner = new ScriptableAgent(AgentKind.Claude);
        var cursor = new CredentialCapturingRunner(AgentKind.Cursor, cursorInner);
        var claude = new CredentialCapturingRunner(AgentKind.Claude, claudeInner);
        var registry = new AgentRegistry([cursor, claude]);

        var frontier = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Cursor, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            DefaultAgentClass = "frontier",
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };

        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var cursorProbe = new RecordingProbe(AgentKind.Cursor);
        var claudeProbe = new RecordingProbe(AgentKind.Claude);

        var quotaOptions = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var router = new AgentClassRouter(
            [frontier],
            [cursorProbe, claudeProbe],
            quotaOptions,
            NullLogger<AgentClassRouter>.Instance);

        var fallbackHistory = new InMemoryAgentFallbackHistoryStore();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry,
            new PerAgentCredentialProvider(new Dictionary<AgentKind, AgentCredential?>
            {
                [AgentKind.Cursor] = cursorCredential,
                [AgentKind.Claude] = claudeCredential,
            }),
            prs, projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            auditQuotaProbes: [cursorProbe, claudeProbe],
            auditQuotaOptions: quotaOptions,
            classRouter: router,
            fallbackHistory: fallbackHistory,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new CursorQuotaFailureDetector(),
                new ClaudeQuotaFailureDetector(),
            }),
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new SwitchFixture(pipeline, store, cursorInner, cursor, claudeInner, claude, webhooks, sandboxes);
    }

    private SwitchFixture BuildSoloPipeline(string seedRepoUrl, AgentCredential? cursorCredential)
    {
        var fixture = BuildPipeline(seedRepoUrl, cursorCredential, claudeCredential: null);
        return fixture;
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "agent switch test",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        AgentClassId = null,
        PushUpstream = false,
    };

    private sealed class SwitchFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Cursor { get; }
        public CredentialCapturingRunner CursorRunner { get; }
        public ScriptableAgent ClaudeAgent { get; }
        public CredentialCapturingRunner Claude { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public SwitchRecordingSandboxProvider Sandboxes { get; }

        public SwitchFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent cursor,
            CredentialCapturingRunner cursorRunner,
            ScriptableAgent claudeAgent,
            CredentialCapturingRunner claude,
            CapturingWebhookDispatcher webhooks,
            SwitchRecordingSandboxProvider sandboxes)
        {
            Pipeline = pipeline;
            Store = store;
            Cursor = cursor;
            CursorRunner = cursorRunner;
            ClaudeAgent = claudeAgent;
            Claude = claude;
            Webhooks = webhooks;
            Sandboxes = sandboxes;
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed class PerAgentCredentialProvider(
        IReadOnlyDictionary<AgentKind, AgentCredential?> byAgent) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default) =>
            Task.FromResult(byAgent.TryGetValue(agent, out var credential) ? credential : null);
    }

    private sealed class SwitchRecordingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        private readonly List<SandboxSpec> _specs = new();

        public string Name => inner.Name;

        public IReadOnlyList<SandboxSpec> Specs
        {
            get { lock (_specs) return _specs.ToList(); }
        }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            lock (_specs) _specs.Add(spec);
            return inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// Policy-declaring test runner that captures the credential it was
    /// invoked with and delegates execution to an inner
    /// <see cref="ScriptableAgent"/> so quota-fallback scenarios script
    /// failures and file writes exactly like the existing fallback tests.
    /// </summary>
    private sealed class CredentialCapturingRunner(AgentKind kind, ScriptableAgent inner)
        : IAgentRunner, IAgentCredentialEnvironmentPolicy
    {
        public AgentKind Kind { get; } = kind;
        public ScriptableAgent Inner { get; } = inner;
        public List<AgentCredential?> SeenCredentials { get; } = [];
        public int CallCount => Inner.CallCount;

        public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal) { DirectTokenVar };

        public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations { get; } = [];

        public Task<AgentResult> RunAsync(
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
            SeenCredentials.Add(credential);
            return Inner.RunAsync(
                sandbox, workingDirectory, prompt, credential,
                modelId, reasoningMode, ct, stdoutChunkCallback, captureStructuredStream);
        }
    }

    /// <summary>
    /// Minimal in-memory sandbox for the resolver-path test: serves unmerged
    /// paths, marker grep, and git add through <see cref="ISandbox"/> only so
    /// the candidate's scoped environment is observable.
    /// </summary>
    private sealed class SwitchConflictSandbox : ISandbox
    {
        private readonly HashSet<string> _unmerged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string Id => "switch-conflict-fake";
        public List<string> AddedFiles { get; } = new();

        public void AddConflictedFile(string relativePath, string content)
        {
            _files[relativePath] = content;
            _unmerged.Add(relativePath);
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var argv = exec.Argv;
            if (argv.Count >= 4 && argv[0] == "sh" && argv[1] == "-c"
                && argv[2].StartsWith("cat > ", StringComparison.Ordinal))
            {
                // Scripted file write: ["sh", "-c", "cat > \"$0\"", "<workdir>/<rel>"] with Stdin payload.
                var target = argv.Count > 3 ? argv[3] : string.Empty;
                const string workPrefix = "/work/";
                if (!target.StartsWith(workPrefix, StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(1, "", $"unsupported write target '{target}'"));
                _files[target[workPrefix.Length..]] = exec.Stdin ?? string.Empty;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            if (argv.Count >= 4 && argv[0] == "git" && argv[1] == "-C" && argv[3] == "ls-files" && argv.Contains("-u"))
            {
                var sb = new System.Text.StringBuilder();
                var oid = new string('a', 40);
                foreach (var path in _unmerged.Order(StringComparer.Ordinal))
                    sb.Append("100644 ").Append(oid).Append(" 2\t").Append(path).Append('\0');
                return Task.FromResult(new SandboxExecResult(0, sb.ToString(), ""));
            }

            if (argv.Count >= 4 && argv[0] == "git" && argv[1] == "-C" && argv[3] == "diff"
                && argv.Contains("--diff-filter=U"))
            {
                return Task.FromResult(new SandboxExecResult(0, string.Join('\n', _unmerged.Order(StringComparer.Ordinal)), ""));
            }

            if (argv.Count >= 4 && argv[0] == "git" && argv[1] == "-C" && argv[3] == "grep")
            {
                var sepIdx = -1;
                for (var i = 4; i < argv.Count; i++)
                {
                    if (argv[i] == "--") { sepIdx = i; break; }
                }
                var matched = new List<string>();
                if (sepIdx >= 0)
                {
                    for (var i = sepIdx + 1; i < argv.Count; i++)
                    {
                        var path = argv[i];
                        if (_files.TryGetValue(path, out var content) && HasMarkers(content))
                            matched.Add(path);
                    }
                }
                return matched.Count == 0
                    ? Task.FromResult(new SandboxExecResult(1, "", ""))
                    : Task.FromResult(new SandboxExecResult(0, string.Join('\n', matched), ""));
            }

            if (argv.Count >= 5 && argv[0] == "git" && argv[1] == "-C" && argv[3] == "add" && argv[4] == "--")
            {
                for (var i = 5; i < argv.Count; i++)
                {
                    if (!_files.ContainsKey(argv[i]))
                        return Task.FromResult(new SandboxExecResult(1, "", $"pathspec '{argv[i]}' did not match"));
                    AddedFiles.Add(argv[i]);
                    _unmerged.Remove(argv[i]);
                }
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public Task KillActiveExecsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static bool HasMarkers(string content)
        {
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                    || line.StartsWith("=======", StringComparison.Ordinal)
                    || line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private sealed class EnvObservingSandbox(ISandbox inner) : ISandboxDecorator
    {
        private readonly List<IReadOnlyDictionary<string, string>> _seen = new();

        public ISandbox InnerSandbox => inner;
        public string Id => inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => inner.BatchLaunchMode;
        public SandboxResourceMetrics? ResourceMetrics => inner.ResourceMetrics;

        public IReadOnlyList<IReadOnlyDictionary<string, string>> SeenEnvironments
        {
            get { lock (_seen) return _seen.ToList(); }
        }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            lock (_seen)
                _seen.Add(exec.ExtraEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal));
            return inner.ExecAsync(exec, ct);
        }

        public Task SyncStateToHostAsync(CancellationToken ct = default) => inner.SyncStateToHostAsync(ct);
        public Task KillActiveExecsAsync(CancellationToken ct = default) => inner.KillActiveExecsAsync(ct);
        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) => inner.GetScreenshotAsync(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
            inner.SynthesizeInputAsync(events, ct);

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default) =>
            inner.GetAccessibilityAtPointAsync(x, y, ct);

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
            inner.GetAccessibilityTreeJsonAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Policy-declaring resolver-path runner that resolves the conflict
    /// through <see cref="ISandbox"/> execs (so the scoped environment
    /// applies) and records the credential it ran with.
    /// </summary>
    private sealed class EnvRecordingRunner(AgentKind kind) : IAgentRunner, IAgentCredentialEnvironmentPolicy
    {
        public AgentKind Kind { get; } = kind;
        public int InvocationCount { get; private set; }
        public List<AgentCredential?> SeenCredentials { get; } = [];

        public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "WINNER_TOKEN", "REFUSED_TOKEN" };

        public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations { get; } = [];

        public async Task<AgentResult> RunAsync(
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
            InvocationCount++;
            SeenCredentials.Add(credential);
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/src/a.txt"],
                Stdin = "base + work\n",
            }, ct);
            if (!write.Success)
                return new AgentResult(false, "write failed", write.Stdout, write.Stderr);
            var add = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", "src/a.txt"],
            }, ct);
            return add.Success
                ? new AgentResult(true, "resolved", null, null)
                : new AgentResult(false, "git add failed", add.Stdout, add.Stderr);
        }
    }
}
