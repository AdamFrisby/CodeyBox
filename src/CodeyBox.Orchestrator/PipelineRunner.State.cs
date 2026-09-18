using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.State.cs — Constructor, configuration fields, and shared pipeline state. The orchestration spine (RunAsync) lives in PipelineRunner.cs; phase logic lives in sibling partials.
public sealed partial class PipelineRunner
{
    private const int AuditEscalationHistoryLimit = 25;
    private const int AuditEscalationFindingsPerIterationLimit = 20;
    private const int AuditEscalationFindingDescriptionLimit = 2000;
    // Synthetic quota probes only ask provider availability; router score is
    // irrelevant, but AgentMembership requires a valid score.
    private const int SyntheticQuotaProbeQualityScore = 100;
    private const int CompletionReviewContextMaxChars = 64 * 1024;
    private const int CompletionReviewFileMaxChars = 8 * 1024;
    private const int CompletionReviewMaxFiles = 80;
    private const int PlanArtifactMaxChars = 64 * 1024;
    private static readonly AgentKind AuditToolAgentKind = new("audit-tool");

    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly IPullRequestService _prs;
    private readonly IProjectRepository _projects;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly ProjectAuditorComposer _auditorComposer;
    private readonly ProjectMechanicalFixerComposer _mechanicalFixerComposer;
    private readonly IReadOnlyList<IMechanicalFixerInputProvider> _mechanicalFixerInputProviders;
    private readonly IWorkItemStore _store;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IWorkItemTerminalTransition _terminalTransitions;
    private readonly IWorkItemTerminalRevisionBuilder _terminalRevisionBuilder;
    private readonly PipelineOptions _opts;
    private readonly ILogger<PipelineRunner> _log;
    private readonly AuditorTelemetryEmitter _auditorTelemetry;
    private readonly CredentialSmokeGate? _smokeGate;
    private readonly ISuggestionStore? _suggestions;
    private readonly IAuditReportStore? _auditReports;
    private readonly IAuditProgressStore? _auditProgress;
    private readonly ITimingStore? _timings;
    private readonly IWorkItemCostStore? _costStore;
    private readonly IAgentUsageStore? _usageStore;
    // Local operator-budget provider. The audit-phase quota gate
    // (EvaluateAuditCandidateQuotaAsync) consults it and takes MIN with the real
    // probe, mirroring AgentClassRouter.ApplyBudgetAsync so the work and audit
    // phases gate on the same synthetic budget quota. Optional: when unwired the
    // audit gate falls back to probe-only behaviour.
    private readonly IAgentBudgetProvider? _budgetProvider;
    private readonly IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? _costExtractors;
    private readonly AgentCostCalculator? _costCalculator;
    private readonly IStdoutBroadcaster? _stdoutBroadcaster;
    private readonly IAgentStreamStore? _agentStreams;
    private readonly IWorkItemAutoRetryScheduler? _retryScheduler;
    private readonly AgentClassRouter? _classRouter;
    private readonly IAgentQuotaAvailabilityPublisher? _quotaAvailabilityPublisher;
    private readonly IAgentFallbackHistoryStore? _fallbackHistory;
    private readonly IAgentInvolvementStore? _involvement;
    private readonly InvolvementTracker _involvementTracker;
    private readonly IAgentAvailabilityRegistry? _availability;
    private readonly IAgentAuthAvailabilityRegistry _authAvailability;
    private readonly IAgentDispatchAvailability? _dispatchAvailability;
    private readonly IAgentPauseController? _agentPauses;
    private readonly IAgentSupervisionService? _agentSupervision;
    private readonly IPreMergeVerifier? _preMergeVerifier;
    private readonly IRequiredBuildVerifier _requiredBuildVerifier;
    private readonly ICheckAndActCompletionRunner? _checkCompletionRunner;
    // Bounded post-agent transition cap. Wraps Transition/TransitionFailed so a
    // hang in store.UpdateAsync (sqlite write contention) or
    // webhooks.PublishAsync (slow remote sink) fails the item within bounded
    // time instead of holding the pool slot indefinitely. Resolved on every
    // call so hot-reload edits to PostAgentTransitionTimeout take effect on
    // the next transition without restarting the pipeline. Null when DI does
    // not wire the watchdog (legacy / minimal test fixtures) — in that case
    // transitions run unbounded as before.
    private readonly Func<WorkerProgressWatchdogOptions>? _watchdogOptionsAccessor;
    // Hot-reloadable feature flag for the between-iteration incremental
    // rebase. Optional: when null the feature is disabled regardless of
    // config — tests and embeddings that don't wire the snapshot keep the
    // pre-feature behaviour.
    private readonly IncrementalRebaseSnapshot? _incrementalRebase;
    // Hot-reloadable quota-fallback and merge-staging retry knobs. Defaulted to
    // a private snapshot (unchanging defaults) when DI does not supply one.
    private readonly PipelineTuningSnapshot _pipelineTuning;
    // Optional stale-base remediation router. When wired AND enabled, a
    // stale-base conflict discovered at upstream-push time re-dispatches the
    // item into conflict-rework instead of parking. Null preserves the
    // historical park behaviour (unchanged for tests / legacy embedders).
    private readonly StaleBaseConflictReworkRouter? _staleBaseReworkRouter;
    // Optional NotDiffAttributable flake-escalation path. When wired AND
    // enabled, an audit iteration whose only actionable failures reproduce on
    // the base branch spawns an isolated fix item and parks the parent on a
    // dependsOn gate instead of burning rework iterations. Null preserves the
    // historical rework behaviour (unchanged for tests / legacy embedders).
    private readonly NonDeterministicTestEscalationService? _flakeEscalation;
    private readonly NonDeterministicTestEscalationSnapshot? _flakeEscalationOptions;
    // Per-agent concurrency view used by BuildAgenticConflictCandidatesAsync to
    // deprioritize agents whose operator-configured cap is at ceiling. The cap
    // is shorthand for "this agent's API account budget is currently
    // saturated"; a second concurrent call from the resolver against the same
    // account is what produces the HTTP 429 reported in c9fd5b75. Both are
    // optional so tests/embeddings that don't wire concurrency can keep their
    // previous "always-route-to-
    // primary" semantics.
    private readonly IAgentRunningCounters? _agentRunningCounters;
    // Shared swappable holder for per-agent caps. Same instance is held by
    // OrchestratorService, so the hot-reload coordinator's call to
    // OrchestratorService.ApplyAgentConcurrencyReload (which writes through
    // the shared snapshot) is observable here on the next GetCapSafe read.
    private readonly AgentConcurrencySnapshot? _concurrencySnapshot;
    // Per-agent concurrency-cap gate (extracted). Owns the IsAtAgentCap /
    // GetCapSafe / GetRunningSafe cluster; PipelineRunner delegates to it.
    private readonly AgentConcurrencyGate _concurrencyGate;
    // Intermediate webhook/event publishing (extracted). Owns the
    // TryPublishEventAsync / Publish*Iteration/Audit/Merge cluster;
    // PipelineRunner delegates to it.
    private readonly PipelineWebhookPublisher _webhookPublisher;
    // Questions parking + suggestions pickup (extracted). Owns the
    // TryParkForQuestionsAsync / TryReadSuggestionsFileAsync /
    // PickUpSuggestionsAsync cluster; PipelineRunner delegates to it.
    private readonly QuestionsSuggestionsParker _questionsSuggestions;
    // In-VM agentic conflict resolver. Mid-rebase / mid-merge conflicts are
    // resolved by invoking the configured agent's normal CLI inside the same
    // sandbox via IAgentRunner.RunAsync — supersedes the old text-only LLM
    // call that used to POST raw /v1/messages with subscription OAuth tokens
    // (ToS-unsafe) and was limited to a 128 KiB per-file payload (couldn't
    // resolve large conflict files). Hot-reloadable through the options
    // snapshot the resolver holds; the same instance is reused across phases.
    private readonly AgenticConflictResolver _agenticConflictResolver;
    // Pure prompt builders (extracted cold-tier cluster). Owns the Build*Prompt /
    // Build*EscalationMessage cluster; PipelineRunner delegates to it.
    private readonly PromptComposer _promptComposer;
    // Cost and token-usage recording (extracted cold-tier cluster). Owns the
    // TryRecordCostAsync / TryRecordCompletionCostAsync / TryGetUsageSummaryAsync /
    // BuildUsageEvent cluster; PipelineRunner delegates to it.
    private readonly CostUsageRecorder _costUsageRecorder;
    // Convergence-brief composer for the delegation phase. Null in minimal
    // compositions / tests that don't exercise delegation; a Delegating entry
    // with no composer parks to NeedsOperatorInput instead of running blind.
    private readonly ConvergenceBriefComposer? _briefComposer;
    // Append-only delegation event log (brief + agent/model + resulting diff).
    // Null disables durable recording; the phase still runs but the operator
    // loses the post-hoc "what was the delegate told / what did it do" trail.
    private readonly IDelegationEventStore? _delegationEvents;
    // Hot-reloadable delegation knobs (result-diff bounds). Defaults to
    // built-in values when no accessor is wired.
    private readonly Func<DelegationOptions> _delegationOptionsAccessor;
    private readonly DelegationEscalationService? _delegationEscalation;
    // Upper bound for parsed reset-window hints extracted from an agent's stdout/stderr.
    // Without a cap, a maliciously-crafted Retry-After header (or prompt-injected output)
    // could park an item arbitrarily far in the future. 24h is the longest legitimate
    // subscription reset cadence we know about (Gemini daily); anything beyond is treated
    // as suspect and clamped.
    // <para><b>Legacy security fallback:</b> used only by <see cref="ClampQuotaReset"/>
    // when the caller omits <c>maxWindow</c>. Production consumers pass
    // <c>_pipelineTuning.Current.MaxParsedQuotaResetWindow</c>; this static remains so
    // that defensive-callers and tests that don't wire the snapshot still get the 24h cap.</para>
    internal static readonly TimeSpan MaxParsedQuotaResetWindow = TimeSpan.FromHours(24);
    // Subscription-billed quota probes, keyed by AgentKind. PayPerApi / Null probes are
    // routing utilities (not real quota sources) and intentionally excluded.
    // Used by both ResolveAuditAgentRunnerAsync (audit-agent quota gate) and
    // InvokeAgentWithQuotaFallbackAsync (work-agent mid-iteration probe write-back) —
    // a single probe set serves both. Probes are resolved by member key
    // (see AgentQuotaProbeCatalog): one probe per agent kind is the common case,
    // but several probes may share a kind when each narrows Handles to the
    // members it meters.
    private readonly IReadOnlyList<IAgentQuotaProbe>? _quotaProbes;
    private readonly QuotaRouterOptions _auditQuotaOptions;
    private readonly QuotaGatePolicy _auditQuotaGatePolicy;
    private readonly IWorkItemQuestionStore? _questionStore;
    private readonly IQuotaFailureStore? _quotaFailures;
    private readonly IQuotaFailureClassifier _quotaClassifier;
    private readonly IQuotaFailureAuditEmitter _quotaAuditEmitter;
    private readonly IAgentAuthFailureClassifier _authFailureClassifier;
    private readonly IAgentAuthRequiredHandler _authRequiredHandler;
    // Structured port replacing the freeform AgentAvailability.Reason
    // substring sniff in IsAuthCorroboratingSmokeFailure. Wired through DI in
    // production (same singleton as the registry); legacy embedders that pass
    // the registry positionally fall through to the registry-as-reader cast.
    private readonly IAgentAuthRequiredAvailabilityReader? _authRequiredReader;
    private readonly IInVmSmokeGate? _inVmSmokeGate;
    private readonly ITaskQueue? _taskQueue;
    private readonly OrchestratorOptions _orchestratorOptions;
    private readonly CancellationRegistry? _cancellations;
    private readonly AgentPromptPreprocessorChain _promptPreprocessors;
    private readonly IKnobRegistry? _knobRegistry;
    // Optional store for plan-derived test cases. Null in minimal compositions /
    // tests that don't exercise the emit path; when null, plan approval simply
    // skips test-case emission (the plan itself is still approved).
    private readonly ITestCaseStore? _testCaseStore;
    // Optional post-implementation e2e-replay authoring/verification gate. Null
    // in compositions/tests that don't exercise it; when null the gate is a
    // no-op. It is also internally disabled unless its own knob is on.
    private readonly WorkItemE2eReplayGate? _e2eReplayGate;
    // Optional best-effort JobTrack test-case exporter. Null in
    // compositions/tests that don't wire it; when null, no propagation runs.
    // Gated per-project (Project.JobTrackExport.Enabled) even when wired.
    private readonly IJobTrackTestCaseExporter? _jobTrackExporter;
    // Optional verification-deployment provisioning for the deployment stage
    // of the audit ladder. Null in compositions/tests that don't exercise
    // deployment-stage auditing; when null, an iteration that would otherwise
    // provision a deployment instead records a configuration-shaped
    // AuditUnavailableException (never a fake pass) — but only when the
    // project actually enables the phase with a recipe and auditors.
    private readonly IDeploymentManager? _deploymentManager;
    private readonly IDeploymentSubstrateProvider? _deploymentSubstrates;
    // Optional durable store for parked human deployment reviews. Null in
    // compositions/tests that don't exercise human review; when null, a
    // deployment stage containing a human-kind auditor records a
    // configuration-shaped AuditUnavailableException (never a fake pass).
    private readonly IHumanDeploymentReviewStore? _humanReviews;
    private readonly IMergeScopeResolver _mergeScopeResolver;
    private readonly Func<Guid> _dispatchClaimIdFactory;
    // Shared host-hooks suppression directory. It holds no per-instance
    // state — it is only handed to git as core.hooksPath so host-side
    // commands never execute repo hooks — so all instances reuse one
    // directory created once per process. A per-instance GUID-suffixed
    // directory here leaked one temp entry per construction (nothing ever
    // deleted it); on a tmpfs /tmp that growth sits in RAM.
    //
    // The directory lives under the per-user application-data root, never
    // under a well-known name in the world-writable shared temp path: any
    // other local user could pre-create such a path as a plain file (every
    // later construction would then fail — a persistent local DoS), as a
    // symlink to an attacker directory, or as a directory containing
    // hostile hook scripts, all of which git would then use as the
    // orchestrator user via core.hooksPath. A directory only this user can
    // write to cannot be pre-created by another user, so no ownership
    // handshake is needed — the leaf checks below only fail closed on
    // unexpected local state.
    internal static string SharedDisabledHostHooksPath => SharedDisabledHostHooks.Value;

    private static readonly Lazy<string> SharedDisabledHostHooks = new(
        ResolveSharedDisabledHostHooksPath,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string ResolveSharedDisabledHostHooksPath()
    {
        // This factory must never throw: Lazy would cache the exception and
        // every later PipelineRunner construction would fail with it. Every
        // creation step below fails closed to the next fallback instead.
        var preferred = PerUserDisabledHostHooksPath();
        if (preferred is not null && TryEnsureRealDirectory(preferred))
            return preferred;

        // Fallback for accounts without a resolvable per-user location: one
        // unpredictable per-process directory in the shared temp path. The
        // GUID suffix makes the name unguessable so it cannot be
        // pre-created, and the codeybox- prefix keeps it visible to the
        // startup sweep, which reaps it once it ages out (it counts as fresh
        // while this process lives, so the sweep leaves it alone).
        var fallback = Path.Combine(
            Path.GetTempPath(), "codeybox-disabled-host-hooks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fallback);
            TryRestrictToOwnerOnly(fallback);
        }
        catch (Exception ex) when (IsBenignHooksDirFailure(ex))
        {
            // Best effort only: git surfaces an unusable hooks path loudly at
            // the call site. Returning the path (rather than throwing and
            // poisoning the Lazy) keeps the failure local and diagnosable.
        }

        return fallback;
    }

    private static string? PerUserDisabledHostHooksPath()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(profile))
                    return null;
                baseDir = Path.Combine(profile, ".cache");
            }

            return Path.GetFullPath(Path.Combine(baseDir, "CodeyBox", "disabled-host-hooks"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryEnsureRealDirectory(string path)
    {
        try
        {
            // Refuse links without following them: the target may live
            // outside this user's directories.
            if (new DirectoryInfo(path).LinkTarget is not null)
                return false;

            // A plain file at the path is unexpected state — refuse it rather
            // than throwing (which the Lazy would cache) or deleting data.
            if (File.Exists(path) && !Directory.Exists(path))
                return false;

            Directory.CreateDirectory(path);

            // Re-check after creation so a link swapped in underneath is not
            // used. The per-user parent is writable only by this user, so a
            // cross-user swap here is impossible; this only fails closed on
            // same-user weirdness.
            var created = new DirectoryInfo(path);
            if (!created.Exists || created.LinkTarget is not null)
                return false;

            TryRestrictToOwnerOnly(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryRestrictToOwnerOnly(string path)
    {
        // Tighten an inherited umask so group/other cannot write. Best
        // effort: the per-user parent directory is the real guard, so a
        // failure here must not fail the whole resolution.
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (IsBenignHooksDirFailure(ex) || ex is PlatformNotSupportedException)
        {
            // Best effort only: the per-user parent directory is the real
            // guard, so a failure here must not fail the whole resolution.
        }
    }

    // Best-effort hooks-directory creation only ever swallows filesystem
    // access failures; anything else still throws.
    private static bool IsBenignHooksDirFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private readonly string _disabledHostHooksPath;
    // Resumable Claude session worker. Null when not registered in DI (the
    // default for tests / minimal compositions). Composed with the global
    // CodeyBox:ClaudeSession:Enabled flag and per-project opt-in
    // (Project.ClaudeSession.Enabled) by ShouldEnterClaudeSessionMode — items
    // that opt out of all three keep the legacy independent-phase pipeline.
    //
    // Stored as ISessionAgentRunner (not the concrete worker) so tests can
    // inject a fake without spinning up the real Claude CLI machinery. The
    // production DI path supplies a provider session runner whose snapshot
    // delegate flows through _claudeHandleSnapshot for restart recovery.
    private readonly ISessionAgentRunner? _claudeSessionWorker;
    private readonly Func<AgentSessionHandle, AgentSessionHandle>? _claudeHandleSnapshot;
    private readonly AgentSessionDispatchOptions _claudeSessionOptions;
    // AsyncLocal flows through the deep work/audit/rework call chain without
    // having to thread an explicit parameter through every helper. Scoped at
    // the top of RunAsync (set when session-mode applies) and read by
    // RunAgentPhaseAsync to swap in the persistent worker VM + worker turn.
    // Per-pipeline-execution by construction, so two concurrent work items
    // never see each other's lifecycle.
    private readonly AsyncLocal<ClaudeSessionLifecycle?> _ambientSessionLifecycle = new();
    private readonly PickupRebaseLockRegistry _rebaseLocks;
    // CancellationTokenSource timers use a uint millisecond due-time internally;
    // keep computed phase caps inside that runtime ceiling.
    private static readonly TimeSpan MaxCancellationTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private static readonly TimeSpan AuditorTimeoutTeardownGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Overridable in tests to inject a programmable activity source without
    /// modifying the production constructor. Defaults to the OS-appropriate
    /// implementation (<see cref="ProcFsAgentActivitySource"/> on Linux,
    /// <see cref="NullAgentActivitySource"/> elsewhere).
    /// </summary>
    internal Func<IAgentActivitySource> ActivitySourceFactory { get; set; }
        = () => OperatingSystem.IsLinux()
            ? new ProcFsAgentActivitySource()
            : NullAgentActivitySource.Instance;

    /// <summary>
    /// Overridable poll interval for the stuck probe. Default is
    /// <see cref="StuckProbe.DefaultPollInterval"/> (30 s). Set to a short
    /// duration in tests to avoid real wall-clock waits.
    /// </summary>
    internal TimeSpan StuckProbePollInterval { get; set; } = StuckProbe.DefaultPollInterval;

    public PipelineRunner(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        IPullRequestService prs,
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        ProjectAuditorComposer auditorComposer,
        IWorkItemStore store,
        IWebhookDispatcher webhooks,
        PipelineOptions opts,
        ILogger<PipelineRunner> log,
        CredentialSmokeGate? smokeGate = null,
        ISuggestionStore? suggestions = null,
        IEnumerable<IAgentQuotaProbe>? auditQuotaProbes = null,
        QuotaRouterOptions? auditQuotaOptions = null,
        IAuditReportStore? auditReports = null,
        ITimingStore? timingStore = null,
        IWorkItemCostStore? costStore = null,
        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? costExtractors = null,
        AgentCostCalculator? costCalculator = null,
        IWorkItemQuestionStore? questionStore = null,
        IStdoutBroadcaster? stdoutBroadcaster = null,
        IAgentStreamStore? agentStreams = null,
        IQuotaFailureStore? quotaFailures = null,
        IWorkItemAutoRetryScheduler? retryScheduler = null,
        AgentClassRouter? classRouter = null,
        IAgentFallbackHistoryStore? fallbackHistory = null,
        IQuotaFailureClassifier? quotaClassifier = null,
        IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>? toolCallCounters = null,
        ITaskQueue? taskQueue = null,
        OrchestratorOptions? orchestratorOptions = null,
        IAgentAvailabilityRegistry? availability = null,
        IAgentRunningCounters? agentRunningCounters = null,
        AgentConcurrencyOptions? agentConcurrency = null,
        IPreMergeVerifier? preMergeVerifier = null,
        AgentConcurrencySnapshot? agentConcurrencySnapshot = null,
        IAgentUsageStore? usageStore = null,
        IAgentBudgetProvider? budgetProvider = null,
        IncrementalRebaseSnapshot? incrementalRebase = null,
        PipelineTuningSnapshot? pipelineTuning = null,
        AgenticConflictResolver? agenticConflictResolver = null,
        IAgentInvolvementStore? involvement = null,
        Func<WorkerProgressWatchdogOptions>? watchdogOptionsAccessor = null,
        IRequiredBuildVerifier? requiredBuildVerifier = null,
        IAgentDispatchAvailability? dispatchAvailability = null,
        IAuditProgressStore? auditProgress = null,
        IAgentPauseController? agentPauseController = null,
        AgentPromptPreprocessorChain? promptPreprocessors = null,
        ICheckAndActCompletionRunner? checkCompletionRunner = null,
        IAgentSupervisionService? agentSupervision = null,
        // Resumable session runner — accepted as an abstraction so the
        // orchestration boundary doesn't take a hard dependency on any
        // provider-specific concrete type. The composition root (Program.cs)
        // hands in the per-provider concrete session runner
        // implementing ISessionAgentRunner; tests substitute fakes through
        // the same parameter. Null disables session-mode dispatch entirely
        // (every item takes the legacy independent-phase path).
        ISessionAgentRunner? sessionAgentRunner = null,
        // Orchestrator-owned dispatch gate. Carries only the master switch
        // PipelineRunner needs to decide whether to consider session mode
        // for a given item; per-provider knobs (transport, metrics,
        // overrides) live on the provider's own options shape and stay
        // confined to the composition root.
        AgentSessionDispatchOptions? sessionDispatchOptions = null,
        // Optional persistence snapshot hook the session runner provides
        // when its handle metadata evolves over time (e.g. captured CLI
        // session ids stamped after the first turn). Production composition
        // root wires this to the concrete runner's snapshot method; null
        // when no snapshotting is needed (or in tests that don't assert on
        // the persisted shape).
        Func<AgentSessionHandle, AgentSessionHandle>? sessionHandleSnapshot = null,
        CancellationRegistry? cancellationRegistry = null,
        IWorkItemTerminalTransition? terminalTransitions = null,
        IWorkItemTerminalRevisionBuilder? terminalRevisionBuilder = null,
        ProjectMechanicalFixerComposer? mechanicalFixerComposer = null,
        IEnumerable<IMechanicalFixerInputProvider>? mechanicalFixerInputProviders = null,
        IKnobRegistry? knobRegistry = null,
        IAgentAuthFailureClassifier? authFailureClassifier = null,
        IAgentAuthAvailabilityRegistry? authAvailability = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        // Composition-root auth-required path. When supplied, the availability
        // plumbing is owned by the host's DI graph and not rebuilt here.
        // Legacy embedders / tests that don't wire this still get the
        // handler-built path below.
        IAgentAuthRequiredHandler? authRequiredHandler = null,
        IAgentAuthRequiredAvailabilityReader? authRequiredReader = null,
        // Optional store for plan-derived test cases. Null disables emission
        // entirely (plans still approve). Only planned items reach the emit path,
        // so unplanned items are never touched regardless of wiring.
        ITestCaseStore? testCaseStore = null,
        IMergeScopeResolver? mergeScopeResolver = null,
        IAgentQuotaAvailabilityPublisher? quotaAvailabilityPublisher = null,
        // Optional post-implementation gate ensuring every declared e2e-replay
        // test case ends up with a committed replay that re-runs green. Null
        // disables it; when wired it self-gates on its own knob.
        WorkItemE2eReplayGate? e2eReplayGate = null,
        Func<Guid>? dispatchClaimIdFactory = null,
        // Optional best-effort exporter that propagates a completed item's test
        // cases to JobTrack. Null disables propagation; when wired it self-gates
        // on each project's JobTrackExport.Enabled opt-in.
        IJobTrackTestCaseExporter? jobTrackExporter = null,
        // Toolchain-fault classification for gate subprocess results. Null
        // falls back to the built-in platform-agnostic signatures; the record
        // store defaults to an in-memory bounded store. The composition root
        // wires the hot-reloadable snapshot-backed classifier and the shared
        // store so a config-only signature takes effect without restart.
        IToolchainFaultClassifier? toolchainFaultClassifier = null,
        IToolchainFaultRecordStore? toolchainFaultRecords = null,
        // Verification-deployment provisioning for the deployment stage of
        // the audit ladder. Null disables the physical provisioning path;
        // composition roots that enable deployment-stage auditing wire both.
        IDeploymentManager? deploymentManager = null,
        IDeploymentSubstrateProvider? deploymentSubstrates = null,
        // Durable store for parked human deployment reviews. Null disables
        // the human park/resume path; a human-kind auditor without a store
        // fails loudly instead of parking into nowhere.
        IHumanDeploymentReviewStore? humanReviews = null,
        StaleBaseConflictReworkRouter? staleBaseReworkRouter = null,
        NonDeterministicTestEscalationService? flakeEscalation = null,
        NonDeterministicTestEscalationSnapshot? flakeEscalationOptions = null,
        // Delegation phase (operator-triggered escape hatch). All three are
        // optional: a Delegating entry with no composer parks to
        // NeedsOperatorInput instead of running; a null event store only
        // drops the durable brief/diff trail.
        ConvergenceBriefComposer? briefComposer = null,
        IDelegationEventStore? delegationEvents = null,
        Func<DelegationOptions>? delegationOptionsAccessor = null,
        // Delegation triggers (operator + automatic escalation). Optional:
        // without it the audit-max path keeps parking for the operator.
        DelegationEscalationService? delegationEscalation = null,
        // Per-branch pickup-rebase lock table. Optional: production DI shares
        // one instance process-wide (matching the pre-extraction static
        // behavior); tests inject a fresh instance per fixture for isolation.
        PickupRebaseLockRegistry? rebaseLockRegistry = null)
    {
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _agents = agents;
        _credentials = credentials;
        _prs = prs;
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _auditorComposer = auditorComposer;
        _mechanicalFixerComposer = mechanicalFixerComposer ?? ProjectMechanicalFixerComposer.FromFixers([]);
        _mechanicalFixerInputProviders = mechanicalFixerInputProviders?.ToList() ?? [];
        _store = store;
        _webhooks = webhooks;
        _opts = opts;
        _timings = timingStore;
        _costStore = costStore;
        _usageStore = usageStore;
        _budgetProvider = budgetProvider;
        _costExtractors = costExtractors;
        _costCalculator = costCalculator;
        _stdoutBroadcaster = stdoutBroadcaster;
        _agentStreams = agentStreams;
        _quotaFailures = quotaFailures;
        if (quotaClassifier is null)
        {
            // No classifier wired — fall back to an empty composite so the pipeline
            // still runs (some test bootstraps don't care about quota detection),
            // but log a warning so a misconfigured production DI graph is visible
            // instead of silently losing every quota-failure observation.
            log.LogWarning(
                "PipelineRunner constructed without an IQuotaFailureClassifier; " +
                "quota-failure detection is disabled. Wire CompositeQuotaFailureClassifier in DI.");
            var fallback = new CompositeQuotaFailureClassifier(Array.Empty<IAgentQuotaFailureDetector>());
            _quotaClassifier = fallback;
            _quotaAuditEmitter = fallback;
        }
        else
        {
            _quotaClassifier = quotaClassifier;
            // The orchestrator's composite implements both contracts; fall back
            // to a no-op emitter when an alternative classifier was wired that
            // only handles classification (test/fake setups).
            _quotaAuditEmitter = quotaClassifier as IQuotaFailureAuditEmitter
                ?? NullQuotaFailureAuditEmitter.Instance;
        }
        _authFailureClassifier = authFailureClassifier ?? new AgentAuthFailureClassifier();
        _inVmSmokeGate = inVmSmokeGate;
        _retryScheduler = retryScheduler;
        _classRouter = classRouter;
        _quotaAvailabilityPublisher = quotaAvailabilityPublisher;
        _dispatchClaimIdFactory = dispatchClaimIdFactory ?? Guid.NewGuid;
        _fallbackHistory = fallbackHistory;
        _involvement = involvement;
        _log = log;
        _involvementTracker = new InvolvementTracker(_involvement, _log);
        _auditorTelemetry = new AuditorTelemetryEmitter(_timings, toolCallCounters, _log);
        _smokeGate = smokeGate;
        _suggestions = suggestions;
        _auditReports = auditReports;
        // Null intentionally disables durable audit-progress history for narrow
        // test fixtures; production DI wires this dependency explicitly.
        _auditProgress = auditProgress;
        _quotaProbes = auditQuotaProbes is null ? null
            : AgentQuotaProbeCatalog.BuildSubscriptionProbes(auditQuotaProbes);
        _auditQuotaOptions = auditQuotaOptions ?? new QuotaRouterOptions();
        _auditQuotaGatePolicy = new QuotaGatePolicy(_auditQuotaOptions);
        _questionStore = questionStore;
        _taskQueue = taskQueue;
        _orchestratorOptions = orchestratorOptions ?? new OrchestratorOptions();
        _cancellations = cancellationRegistry;
        _promptPreprocessors = promptPreprocessors ?? AgentPromptPreprocessorChain.Empty;
        _knobRegistry = knobRegistry;
        _testCaseStore = testCaseStore;
        _e2eReplayGate = e2eReplayGate;
        _jobTrackExporter = jobTrackExporter;
        _deploymentManager = deploymentManager;
        _deploymentSubstrates = deploymentSubstrates;
        _humanReviews = humanReviews;
        _mergeScopeResolver = mergeScopeResolver ?? NullMergeScopeResolver.Instance;
        _availability = availability;
        // Prefer the DI-injected handler when supplied: keeps the registry
        // plumbing in one place (the composition root) rather than duplicated
        // here and in ReleaseService. When neither the handler nor the
        // registry is wired, fall back to a fail-loud placeholder so a
        // legacy embedder that never trips an auth-required side effect keeps
        // working while a regression that does silently rely on it surfaces
        // an InvalidOperationException at the first publish.
        _authAvailability = authAvailability ?? MissingAgentAuthAvailabilityRegistry.Instance;
        _authRequiredReader = authRequiredReader
            ?? (authAvailability as IAgentAuthRequiredAvailabilityReader);
        _authRequiredHandler = authRequiredHandler
            ?? new AgentAuthRequiredHandler(_authAvailability, _webhooks, _log);
        _dispatchAvailability = dispatchAvailability;
        _agentPauses = agentPauseController;
        _agentSupervision = agentSupervision;
        _agentRunningCounters = agentRunningCounters;
        // Prefer the shared snapshot when DI supplies it (production path —
        // OrchestratorService holds the same instance, so hot-reload swaps
        // are observed here). Test fixtures that only pass the legacy
        // options-shaped parameter get a private snapshot. Null means
        // "no per-agent cap state wired" — GetCapSafe returns 0 (= unlimited).
        _concurrencySnapshot = agentConcurrencySnapshot
            ?? (agentConcurrency is null ? null : new AgentConcurrencySnapshot(agentConcurrency));
        _concurrencyGate = new AgentConcurrencyGate(_agentRunningCounters, _concurrencySnapshot);
        _webhookPublisher = new PipelineWebhookPublisher(_store, _webhooks, _gitHost, _log);
        _preMergeVerifier = preMergeVerifier;
        _requiredBuildVerifier = requiredBuildVerifier
            ?? throw new ArgumentNullException(
                nameof(requiredBuildVerifier),
                "PipelineRunner requires an IRequiredBuildVerifier supplied by the composition root.");
        _terminalTransitions = terminalTransitions
            ?? throw new ArgumentNullException(
                nameof(terminalTransitions),
                "PipelineRunner requires an IWorkItemTerminalTransition supplied by the composition root.");
        _terminalRevisionBuilder = terminalRevisionBuilder
            ?? throw new ArgumentNullException(
                nameof(terminalRevisionBuilder),
                "PipelineRunner requires an IWorkItemTerminalRevisionBuilder supplied by the composition root.");
        _checkCompletionRunner = checkCompletionRunner;
        _incrementalRebase = incrementalRebase;
        _pipelineTuning = pipelineTuning ?? new PipelineTuningSnapshot(new PipelineTuningOptions());
        _staleBaseReworkRouter = staleBaseReworkRouter;
        _flakeEscalation = flakeEscalation;
        _flakeEscalationOptions = flakeEscalationOptions;
        _questionsSuggestions = new QuestionsSuggestionsParker(
            _questionStore, _suggestions, _store, _webhooks, _pipelineTuning, _log,
            (item, state, ct, project) => Transition(item, state, ct, project));
        // Wire the credential-file materialiser into the default resolver so
        // a cross-kind fallback candidate (whose file-based creds aren't yet on
        // disk in the sandbox the primary provisioned) can authenticate before
        // its CLI runs. Custom-injected resolvers are passed through as-is for
        // tests and for callers that wire their own hook.
        _agenticConflictResolver = agenticConflictResolver
            ?? new AgenticConflictResolver(
                credentialFileMaterialiser: MaterialiseCredentialFilesAsync,
                agentSupervision: _agentSupervision,
                authFailureClassifier: _authFailureClassifier);
        _promptComposer = new PromptComposer();
        _costUsageRecorder = new CostUsageRecorder(_costStore, _usageStore, _costCalculator, _costExtractors, _log);
        _disabledHostHooksPath = SharedDisabledHostHooksPath;
        _watchdogOptionsAccessor = watchdogOptionsAccessor;
        // The session-runner abstraction is the single seam: production
        // hands in the per-provider concrete session runner
        // implementing ISessionAgentRunner; tests substitute a fake
        // through the same parameter. The snapshot hook is forwarded
        // verbatim — the composition root chooses whether to wire it.
        _claudeSessionWorker = sessionAgentRunner;
        _claudeHandleSnapshot = sessionHandleSnapshot;
        _claudeSessionOptions = sessionDispatchOptions ?? new AgentSessionDispatchOptions();
        _briefComposer = briefComposer;
        _delegationEvents = delegationEvents;
        _delegationOptionsAccessor = delegationOptionsAccessor ?? (() => new DelegationOptions());
        _delegationEscalation = delegationEscalation;
        _rebaseLocks = rebaseLockRegistry ?? PickupRebaseLockRegistry.Shared;
        _requiredBuildGate = new RequiredBuildGate(
            _requiredBuildVerifier,
            _auditReports is null ? null : PersistAuditReportAsync,
            toolchainFaultClassifier ?? new ToolchainFaultClassifier(snapshot: null),
            toolchainFaultRecords ?? new InMemoryToolchainFaultRecordStore());
    }

    private readonly RequiredBuildGate _requiredBuildGate;

}
