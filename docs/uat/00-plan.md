# CodeyBox UAT Coverage Master Plan

This document is the Phase 1 inventory for the UAT coverage campaign. Phase 2 work items should choose one functional area or feature row, implement the automatable scenarios where possible, and convert the manual/spec-only scenarios into operator checklists or end-to-end harness notes. The inventory is source-driven: every feature cites concrete implementation files so Phase 2 can start writing tests without repeating the discovery pass.

## Table Of Contents

- [Pipeline And Worker Lifecycle](#pipeline-and-worker-lifecycle)
- [Sandbox Providers](#sandbox-providers)
- [Quota And Routing](#quota-and-routing)
- [Agent Runners And Credentials](#agent-runners-and-credentials)
- [Auditing And Reports](#auditing-and-reports)
- [Plugins](#plugins)
- [Work Item APIs And Queue Controls](#work-item-apis-and-queue-controls)
- [Projects And Configuration](#projects-and-configuration)
- [Persistence And Recovery](#persistence-and-recovery)
- [Upstream, Webhooks, And Releases](#upstream-webhooks-and-releases)
- [Cost, Telemetry, And Streams](#cost-telemetry-and-streams)
- [Operator Clients](#operator-clients)
- [Coverage Matrix](#coverage-matrix)

## Pipeline And Worker Lifecycle

### Work item pipeline state machine - Runs Work, Audit, Merge, and UpstreamPush phases in order

**Source**: `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Core/WorkItemState.cs`, `src/CodeyBox.Core/WorkItem.cs`, `src/CodeyBox.Orchestrator/IPipelineRunner.cs`
**Related PRs**: #63, #64, #65, #68, #107, #112

#### Primary user flows
1. Queued item starts on its project base branch - the runner transitions through `Working`, `WorkComplete`, `Auditing`, `AuditPassed`, `Merging`, `Merged`, `UpstreamPushing`, and `Done`.
2. Project with no auditors completes work and proceeds directly to merge - audit phase is skipped without losing merge or upstream behavior.
3. `PushUpstream=false` item completes local merge - item reaches `Done` without invoking the upstream remote.

#### Edge cases
- Entry state is `WorkComplete`, `AuditPassed`, or `Merged` after restart - the runner skips completed earlier phases and resumes at the correct phase.
- Base branch is omitted - project default branch or git default branch is resolved consistently.
- `MergeSha` is already present on resume - upstream completion receives the stored SHA rather than recomputing a merge.

#### Failure modes
- Agent returns non-zero in work or rework - item becomes `Failed` with classified failure details when available.
- Auditor returns blocking findings past `MaxIterations` - item becomes `AuditFailed`.
- Merge conflict resolver cannot produce a valid merge - item becomes `MergeConflictResolutionFailed`.
- Upstream push repeatedly fails - item remains recoverable according to upstream retry policy and surfaces `LastError`.

#### Test approach
- **Automatable**: state-transition integration tests with fake agents, fake auditors, fake upstream remotes, and fake git host branches; resume-from-mid-phase cases; upstream disabled path.
- **Manual / spec-only**: full sandboxed run against a real repository and real agent CLIs.

### Retry and rework entry points - Requeues failed work from selected pipeline phases

**Source**: `src/CodeyBox.Orchestrator/WorkItemRetrier.cs`, `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Core/IGitHost.cs`, `src/CodeyBox.Git/LocalGitHost.cs`
**Related PRs**: #68

#### Primary user flows
1. Operator retries `from: work` - failed item returns to `Queued`, retry counters reset where appropriate, and the work branch is reset to base.
2. Operator retries `from: audit` - item returns to `WorkComplete` and reruns audit without redoing work.
3. Operator retries `from: merge` or `from: upstream` - item resumes at `AuditPassed` or `Merged` when host repo state exists.

#### Edge cases
- Retry from a later phase with missing host repository - API rejects because branch state cannot be resumed.
- Retry after agent identity changes - branch reset and credential resolution use the new request state.
- Retry from cancelled or abandoned item - item can be requeued only through supported retry endpoint semantics.

#### Failure modes
- Base branch cannot be resolved during work-branch reset - retry fails with a surfaced error and does not silently reuse dirty commits.
- Concurrent retry races with worker transition - conditional store updates prevent double pickup.

#### Test approach
- **Automatable**: endpoint and store tests for all `from` values, missing repository validation, work-branch reset, and state reset.
- **Manual / spec-only**: operator UI retry from each terminal failure state while inspecting branch refs.

### Work branch lifecycle and merge safety - Manages branch creation, rebase-on-base, merge verification, and conflict scope

**Source**: `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Orchestrator/MergeScopeFence.cs`, `src/CodeyBox.Git/LocalGitHost.cs`, `src/CodeyBox.Core/IGitHost.cs`, `docs/git-workflow.md`
**Related PRs**: #68

#### Primary user flows
1. Work phase creates or updates a generated branch - the agent commits changes and pushes back to the host bare repo.
2. Pickup refreshes/rebases onto the latest base - work starts from current integration branch state.
3. Merge conflict resolution is delegated to an agent - the resolver may only modify conflict hunks plus configured buffer lines.

#### Edge cases
- Operator supplies a custom work branch - branch name is validated and must differ from base branch when both are set.
- No changes are produced by the agent - runner fails or skips according to pipeline semantics, not by accidental stale branch reuse.
- Merge tree verification sees conflicts not represented in the sandbox output - host verification blocks unsafe merge.

#### Failure modes
- Conflict resolver edits outside allowed hunks - scope fence rejects the merge.
- Non-fast-forward host push during work or merge - git host reconciles or reports a durable error.
- Invalid git identity - fallback author identity is used or a clear validation error is surfaced.

#### Test approach
- **Automatable**: local git repository tests for branch validation, rebase, conflict hunk extraction, scope fence violations, merge-tree verification, and no-change output.
- **Manual / spec-only**: adversarial merge-conflict session with a real CLI agent attempting out-of-scope edits.

### Shutdown cancellation and preemption - Preserves or recovers in-flight work during host shutdown

**Source**: `src/CodeyBox.Orchestrator/OrchestratorService.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Orchestrator/CancellationRegistry.cs`, `src/CodeyBox.Agents/CliAgentRunnerBase.cs`, `src/CodeyBox.Core/SandboxAbstractions.cs`
**Related PRs**: none known

#### Primary user flows
1. Host receives shutdown while work is running - cancellation is propagated, preempt-capable agents capture scratchpad state, and item is marked for recovery.
2. Host restarts - recoverable states are replayed and preempted work resumes where supported.
3. Operator cancels an in-flight item - cancellation token kills the phase and the item transitions to `Cancelled`.

#### Edge cases
- `StuckThresholdMinutes=0` or preemption unsupported by provider - shutdown falls back to state recovery without scratchpad resume.
- Cancellation happens during merge or upstream push - item maps back to the last durable phase.
- Preempt checkpoint path is missing or corrupt - resume falls back to a clean retry or failure with clear error.

#### Failure modes
- Agent ignores TERM during preempt - runner escalates via sandbox cancellation and preserves best-effort scratchpad.
- Host exits before DB transition completes - restart recovery maps stale state to a recoverable state or abandoned state after cap.

#### Test approach
- **Automatable**: fake `IPreemptibleAgentRunner` and sandbox tests for cancellation propagation, checkpoint metadata, and restart state mapping.
- **Manual / spec-only**: SIGTERM an orchestrator with real Multipass work in progress and verify post-restart resume.

### Stuck-agent detection - Detects idle agents and optionally retries the same phase

**Source**: `src/CodeyBox.Orchestrator/ProcFsAgentActivitySource.cs`, `src/CodeyBox.Orchestrator/StuckProbe.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Core/Project.cs`
**Related PRs**: #61

#### Primary user flows
1. Agent process has no CPU or TCP activity beyond threshold - probe classifies it as stuck and kills the phase.
2. Project enables `AutoRetryOnStuck` - item is requeued from the same phase until `MaxStuckRetries`.
3. Local development sets threshold to `0` - stuck detection is disabled.

#### Edge cases
- Procfs is unavailable or permission denied - activity source returns unknown and does not falsely kill.
- Ancestor process matches agent command - ancestor PID filter prevents counting the orchestrator or wrapper as agent activity.
- Agent keeps a TCP connection open but uses little CPU - activity counts as live.

#### Failure modes
- Stuck retry cap reached - item fails with a clear stuck-related error.
- Activity source throws mid-probe - runner treats it as inconclusive and logs instead of crashing.

#### Test approach
- **Automatable**: fake procfs snapshots, threshold boundaries, ancestor filtering, auto-retry caps, and disabled threshold.
- **Manual / spec-only**: real long-idle CLI process inside a sandbox.

### Worker pool dispatch and dead-worker recovery - Controls concurrency, pacing, registration, and orphaned workers

**Source**: `src/CodeyBox.Orchestrator/OrchestratorService.cs`, `src/CodeyBox.Orchestrator/WorkerPoolOptions.cs`, `src/CodeyBox.Orchestrator/SqliteWorkerRegistry.cs`, `src/CodeyBox.Orchestrator/DeadWorkerReaper.cs`, `src/CodeyBox.Orchestrator/InMemoryTaskQueue.cs`, `src/CodeyBox.Orchestrator/DeadWorkerOptions.cs`
**Related PRs**: none known

#### Primary user flows
1. Multiple queued items exist - worker pool runs at most `MaxConcurrentWorkers` and respects `MinSpawnInterval`.
2. Worker registers heartbeat - registry shows active worker and clears it on completion.
3. Dead-worker reaper finds expired heartbeat - in-flight item maps back to a recoverable state or fails/abandons after attempt cap.

#### Edge cases
- Legacy `Concurrency` config is used - options map it to worker-pool max concurrency with warning.
- Queue is paused after dequeue - item is re-enqueued and no worker starts.
- Dependency-gated queued item exists at startup - it is not enqueued until dependencies become terminal.

#### Failure modes
- In-memory queue loses items on process exit - startup replay reconstructs runnable queue state from SQLite.
- Dead worker owns an item already transitioned by another process - conditional updates avoid clobbering newer state.

#### Test approach
- **Automatable**: concurrency gate, spawn interval, startup replay, heartbeat expiry, recovery cap, and active-item de-duplication.
- **Manual / spec-only**: multi-process crash/kill drills against a shared state database.

## Sandbox Providers

### Multipass sandbox provider - Runs agents in isolated Ubuntu VMs with host-enforced network profiles

**Source**: `src/CodeyBox.Sandbox.Multipass/MultipassSandboxProvider.cs`, `src/CodeyBox.Core/SandboxAbstractions.cs`, `scripts/setup-host-networks.sh`, `docs/sandbox-providers.md`, `docs/host-firewall.md`, `docs/baseline-bake-examples.md`
**Related PRs**: none known

#### Primary user flows
1. Orchestrator requests a VM sandbox - provider launches or clones a Multipass VM, mounts workspace and credentials, transfers env, and returns an executable handle.
2. Network profile is configured - VM attaches to mapped bridge and host nftables enforce allowed egress.
3. Baseline images are enabled - provider bakes one stopped baseline per profile and clones it for later sandboxes.

#### Edge cases
- No profile is configured - provider falls back to default network and host firewall should block egress.
- Snap Multipass cannot read `/tmp` - staging root resolves under `~/snap/multipass/common`.
- Env file transfer hits transient SSH readiness - retry path eventually writes environment before exec.

#### Failure modes
- Launch, mount, or env transfer fails - provider deletes partially created VM and staging directory.
- VM name supplied for leak disposal contains invalid characters - disposal rejects unsafe names.
- Baseline bake races - per-profile semaphore serializes bake.

#### Test approach
- **Automatable**: unit tests for cloud-init generation, profile mapping, staging permissions, name validation, baseline locking, and retry helpers.
- **Manual / spec-only**: real Multipass VM launch, profile egress enforcement, and baseline clone timing.

### Bubblewrap sandbox provider - Runs transient Linux namespace sandboxes

**Source**: `src/CodeyBox.Sandbox.Bubblewrap/BubblewrapSandboxProvider.cs`, `src/CodeyBox.Core/SandboxAbstractions.cs`, `docs/sandbox-providers.md`
**Related PRs**: none known

#### Primary user flows
1. Provider creates bwrap sandbox - tmpfs and bind mounts are materialized under a private temp root.
2. Command executes - process runs with clean environment, mount/PID/user namespaces, and captured stdout/stderr.
3. No network requested - `--unshare-net` isolates network.

#### Edge cases
- Network is requested - provider shares host network and logs that hostname allowlists are not enforced.
- Read-only host bind path does not exist - provider skips missing default path safely.
- Command is cancelled - process tree is killed.

#### Failure modes
- Bubblewrap binary missing or exits non-zero - exec returns failure output to the runner.
- Temp root cleanup fails - disposal logs best-effort cleanup risk.

#### Test approach
- **Automatable**: argv construction, fd/stdout handling, cancellation kill, mount ordering, network/no-network switches.
- **Manual / spec-only**: actual namespace inspection on Linux hosts with bwrap installed.

### Process sandbox provider - Unsafe local-development runner

**Source**: `src/CodeyBox.Sandbox.Process/ProcessSandboxProvider.cs`, `src/CodeyBox.Api/Program.cs`, `docs/sandbox-providers.md`
**Related PRs**: none known

#### Primary user flows
1. Development environment uses process provider - commands run on host PATH with filesystem copies/symlinks under temp root.
2. Startup in non-Development without explicit unsafe opt-in - API refuses to load the process sandbox.

#### Edge cases
- Read-only mount is a directory - provider copies it and marks files read-only.
- Writable mount is required for git - provider symlinks to host path.

#### Failure modes
- Operator accidentally enables in production - startup guard or warning must make the risk explicit.
- Path translation misses a sandbox absolute path - command fails visibly rather than writing outside intended temp root unnoticed.

#### Test approach
- **Automatable**: path translation, mount materialization, startup guard, warning behavior.
- **Manual / spec-only**: none beyond operator environment validation.

### Sandbox leak detection and disposal - Finds stale managed sandboxes and exposes operator cleanup

**Source**: `src/CodeyBox.Orchestrator/SandboxLeakReaper.cs`, `src/CodeyBox.Api/SandboxEndpoints.cs`, `src/CodeyBox.Core/SandboxAbstractions.cs`, `src/CodeyBox.Sandbox.Multipass/MultipassSandboxProvider.cs`, `docs/sandbox-leaks.md`
**Related PRs**: none known

#### Primary user flows
1. Reaper scans provider-managed sandboxes - tracked active sandboxes are ignored and stale untracked VMs are reported.
2. Operator lists leaks via `/sandboxes/leaked` or `/admin/sandbox-leaks` - API returns unmanaged VM metadata.
3. Reaper or operator disposes a leaked sandbox - provider performs best-effort delete/purge.

#### Edge cases
- Preserved preempt marker exists - VM is not treated as a leak.
- Provider has no persistent lifecycle - leak list is empty.
- Auto-dispose is enabled (default) - reaper deletes stale entries after configured age.

#### Failure modes
- Provider list call fails - API/reaper logs and surfaces error without crashing the orchestrator.
- Disposal fails - endpoint returns a failure and audit log preserves the VM name.

#### Test approach
- **Automatable**: fake provider leak list, age thresholds, preempt marker handling, endpoint responses, disposal errors.
- **Manual / spec-only**: real stale Multipass VM cleanup.

## Quota And Routing

### Agent quota probes - Reads per-agent and per-model availability snapshots

**Source**: `src/CodeyBox.Core/IAgentQuotaProbe.cs`, `src/CodeyBox.Agents.Claude/ClaudeQuotaProbe.cs`, `src/CodeyBox.Agents.Codex/CodexQuotaProbe.cs`, `src/CodeyBox.Agents.Gemini/GeminiQuotaProbe.cs`, `src/CodeyBox.Orchestrator/QuotaRouter.cs`, `src/CodeyBox.Api/Program.cs`, `src/CodeyBox.Api/WorkItemEndpoints.cs`
**Related PRs**: #64

#### Primary user flows
1. Operator calls `/quota` - API returns cached availability for registered probes.
2. Claude OAuth probe reads usage shape - overall and model-specific availability are populated when present.
3. Codex WHAM and Gemini Cloud Code probes map usage JSON to `AgentQuotaSnapshot`.

#### Edge cases
- Probe credential is missing - snapshot is unknown with diagnostic notes.
- Endpoint shape changes - parser returns unknown instead of throwing.
- Model id is configured - router evaluates matching `PerModel` quota before overall quota.

#### Failure modes
- Probe HTTP request times out - router follows unknown policy.
- Probe reports negative or malformed availability - gate treats it as unknown and logs reason.

#### Test approach
- **Automatable**: fixture-driven parsers, missing credential cases, cache TTL, per-model lookup, `/quota` endpoint shape.
- **Manual / spec-only**: live probe against real vendor accounts.

### Agent class router - Chooses an agent/model using score, quota, and observed-failure gates

**Source**: `src/CodeyBox.Orchestrator/AgentClassRouter.cs`, `src/CodeyBox.Core/AgentClass.cs`, `src/CodeyBox.Api/Program.cs`, `docs/agent-classes.md`, `docs/quota-gate.md`
**Related PRs**: #64

#### Primary user flows
1. Work item has an `AgentClassId` - router filters members by `MinModelScore`, applies time-of-day modifiers, probes quota, and selects the highest viable member.
2. Project has `DefaultAgentClass` - items inherit routing without per-item override.
3. PayPerApi member is eligible - it is treated as 100 percent quota and can be selected when ranked high enough.

#### Edge cases
- Unknown class id - router falls through to direct agent pick with diagnostic reason.
- All members below `MinModelScore` - router returns no eligible members and item fails with `ROUTING_NO_ELIGIBLE`.
- Unknown quota with `UseObservedFailures` - recent quota-shaped failures block the same agent/model.

#### Failure modes
- All subscription members exhausted - item waits and is re-enqueued after `QuotaRecheckInterval`.
- Time window config is invalid - startup validation rejects configuration.
- Gemini high score lacks high reasoning mode - startup validation rejects unsafe config.

#### Test approach
- **Automatable**: routing matrix for score ordering, below-floor rejection, quota exhausted, observed failures, unknown policies, TOD modifiers, per-project failures.
- **Manual / spec-only**: operator-tuned class catalog with real quota pressure.

### Quota failure classification and auto-retry - Persists quota failures and retries after reset

**Source**: `src/CodeyBox.Orchestrator/QuotaFailureDetector.cs`, `src/CodeyBox.Core/QuotaFailureKind.cs`, `src/CodeyBox.Orchestrator/SqliteQuotaFailureStore.cs`, `src/CodeyBox.Orchestrator/QuotaRetryScheduler.cs`, `src/CodeyBox.Orchestrator/WorkItemRetrier.cs`, `src/CodeyBox.Core/WorkItem.cs`
**Related PRs**: #63, #64

#### Primary user flows
1. Agent stderr or stream-json contains quota text - detector classifies `LimitReached`, `RateLimitExceeded`, or `Unauthorized`.
2. Reset duration is parsed - `QuotaResetAt` and `NextQuotaRetryAt` are persisted on the failed work item.
3. Auto-retry is enabled - scheduler re-arms timers on startup and retries quota-failed items via the shared retrier.

#### Edge cases
- Structured stream wraps error under `msg`, `result`, or `error.message` - parser still extracts quota text.
- Clock drift safety margin is configured - targeted retry is scheduled after reset plus margin.
- Queue or project is paused - auto-retry skips without changing item state.

#### Failure modes
- Max auto retries reached - scheduler logs and leaves the item failed.
- Router still gates the item - retry is skipped until a later sweep/timer.
- Malformed stream-json - line is ignored and unstructured stderr/stdout are still scanned.

#### Test approach
- **Automatable**: detector phrase table, reset parsing, stream-json shapes, persistence columns, targeted timer rearm, max retry, paused queue behavior, webhook emission.
- **Manual / spec-only**: real quota exhaustion event from a vendor CLI.

### Transient transport auto-retry - Backs off and jitters retry after network/stream failures

**Source**: `src/CodeyBox.Core/AgentFailureClassifier.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Orchestrator/QuotaRetryScheduler.cs`, `src/CodeyBox.Orchestrator/WorkItemRetrier.cs`, `src/CodeyBox.Core/WorkItem.cs`
**Related PRs**: none known

#### Primary user flows
1. Agent stderr, stdout, or stream-json reports a conservative transport shape - classifier marks the work-item failure as `transient`.
2. Failure is persisted - `NextTransientRetryAt`, `TransientRetryAttempts`, and `TransientRetryFirstFailedAt` are stored on the failed item.
3. Auto-retry is due - scheduler requeues through `WorkItemRetrier` with auto-pick so an existing work branch resumes at audit when possible.

#### Edge cases
- Several items fail during one provider incident - jitter spreads retry timestamps instead of retrying all at once.
- Bare `timeout` appears in a build/test failure - classifier leaves it as normal, not transient.
- Queue or project is paused - auto-retry skips without consuming the retry budget.

#### Failure modes
- Attempt or elapsed cap is reached - item stays `Failed` with `FailureKind="transient-exhausted"`.
- Retry timestamp is missed during restart - startup re-arm and periodic sweep recover the due item.
- Auth or quota failure is observed - auth remains excluded and quota stays on the quota-reset path.

#### Test approach
- **Automatable**: classifier positive/negative patterns, JSON `turn.failed` parsing, backoff schedule, jitter spread, attempt cap, elapsed cap, only-transient gate, auto-pick resume.
- **Manual / spec-only**: provider-side incident or local egress fault with multiple parked items to inspect retry spread.

## Agent Runners And Credentials

### CLI runner base and preemption - Shared one-shot CLI execution, stream capture, and scratchpad resume

**Source**: `src/CodeyBox.Agents/CliAgentRunnerBase.cs`, `src/CodeyBox.Core/IAgentRunner.cs`, `src/CodeyBox.Core/SandboxAbstractions.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`
**Related PRs**: none known

#### Primary user flows
1. Runner builds an invocation - base class executes it inside the sandbox with credential env already mounted.
2. `CaptureStructuredStream` is requested - runner passes stdout chunks to persistence and live broadcasters.
3. Shutdown preempt is requested - runner TERM-kills matching agent process and archives allowlisted scratchpad paths.

#### Edge cases
- Prompt is passed by argv, stdin, or file depending on runner - base class does not assume transport.
- Multiple agents run in one sandbox process tree - `CODEYBOX_AGENT_RUN_ID` targets the active invocation.
- Scratchpad contains unsafe paths or huge files - archive validation skips invalid entries.

#### Failure modes
- Sandbox exec throws cancellation - caller maps to cancellation/preemption handling.
- Scratchpad restore fails - resumed run returns an agent failure.

#### Test approach
- **Automatable**: invocation wrapping, run-id matching, stdout callback propagation, scratchpad path validation, resume invocation.
- **Manual / spec-only**: CLI-specific scratchpad resume with real agent CLIs.

### Claude agent runner - Drives Claude Code CLI and Claude text-only calls

**Source**: `src/CodeyBox.Agents.Claude/ClaudeAgentRunner.cs`, `src/CodeyBox.Agents.Claude/ClaudeQuotaProbe.cs`, `src/CodeyBox.Agents.Claude/ClaudeSmokeProbe.cs`, `src/CodeyBox.Orchestrator/ClaudeOAuthFileCredentialProvider.cs`
**Related PRs**: #66

#### Primary user flows
1. Work item runs with Claude - invocation uses `claude --print --dangerously-skip-permissions`, default model, optional `--model`, and optional `--effort`.
2. OAuth credentials are available - runner materializes full `.claude/.credentials.json` in sandbox.
3. Structured stream is supported - runner adds `--output-format stream-json --verbose`.

#### Edge cases
- CLI lacks stream-json support - runner disables capture and appends warning.
- API key rather than OAuth is used - file materialization is skipped.
- Text-only generation is used for PR/changelog review - direct Anthropic HTTP path handles OAuth or API key.

#### Failure modes
- Credential file write fails - runner returns a failed `AgentResult`.
- Claude API rejects text-only call - error body is returned for diagnostics.

#### Test approach
- **Automatable**: argv building, effort flag, OAuth file materialization, stream support probe, text-only missing credential and HTTP failure.
- **Manual / spec-only**: real Claude Code run and OAuth refresh behavior.

### Codex agent runner - Drives Codex CLI and OpenAI text-only calls

**Source**: `src/CodeyBox.Agents.Codex/CodexAgentRunner.cs`, `src/CodeyBox.Agents.Codex/CodexQuotaProbe.cs`, `src/CodeyBox.Agents.Codex/CodexSmokeProbe.cs`, `src/CodeyBox.Orchestrator/CodexOAuthFileCredentialProvider.cs`
**Related PRs**: none known

#### Primary user flows
1. Work item runs with Codex - invocation uses `codex exec --dangerously-bypass-approvals-and-sandbox`, optional `--model`, and reasoning config.
2. Subscription auth is available - runner writes `~/.codex/auth.json` from `CODEX_AUTH_JSON`.
3. Structured stream is requested - runner probes for `--json-stream` or `--json` and uses the supported flag.

#### Edge cases
- CLI supports neither JSON flag - capture is disabled with warning.
- Text-only call has `CODEX_AUTH_JSON` containing `OPENAI_API_KEY` - runner extracts it.
- Reasoning mode is omitted - default model runs without config override.

#### Failure modes
- Auth materialization fails - runner returns failure before invoking Codex.
- Responses API returns error - text-only result includes status and body.

#### Test approach
- **Automatable**: auth file write, structured flag detection, reasoning config, text extraction, missing key, API failure.
- **Manual / spec-only**: real Codex CLI subscription run.

### Gemini agent runner - Drives Gemini CLI with model-encoded thinking and OAuth files

**Source**: `src/CodeyBox.Agents.Gemini/GeminiAgentRunner.cs`, `src/CodeyBox.Agents.Gemini/GeminiQuotaProbe.cs`, `src/CodeyBox.Agents.Gemini/GeminiSmokeProbe.cs`, `src/CodeyBox.Orchestrator/GeminiOAuthFileCredentialProvider.cs`
**Related PRs**: none known

#### Primary user flows
1. Work item runs with Gemini - invocation uses `gemini --yolo --skip-trust -p`, optional `--model`, and optional stream-json output.
2. OAuth credentials are available - runner writes `~/.gemini/oauth_creds.json` and `settings.json`.
3. High reasoning is requested - configured Gemini 3 model id carries thinking level because CLI has no reasoning flag.

#### Edge cases
- CLI emits ANSI to stderr/stdout - runner strips ANSI from returned output unless preserving structured stream.
- API key flow is used - OAuth file write is skipped.
- Structured stream unsupported - warning is appended.

#### Failure modes
- OAuth file materialization fails - runner returns failed result.
- Gemini API text-only call fails - error body is surfaced.

#### Test approach
- **Automatable**: argv construction, ANSI stripping, OAuth file materialization, structured support, text-only success/failure.
- **Manual / spec-only**: real Gemini CLI OAuth and model-thinking config validation.

### Copilot agent runner - Drives GitHub Copilot CLI as a direct agent option

**Source**: `src/CodeyBox.Agents.Copilot/CopilotAgentRunner.cs`, `src/CodeyBox.Agents/AgentRegistry.cs`, `src/CodeyBox.Core/AgentKind.cs`
**Related PRs**: none known

#### Primary user flows
1. Operator selects Copilot - runner invokes `copilot -p <prompt>` with injected GitHub token env.
2. Model or reasoning mode is supplied - runner ignores unsupported knobs without failing.

#### Edge cases
- Project skips credential smoke test - Copilot can be used where direct smoke probing is unavailable.
- Copilot CLI changes argument shape - centralized runner is the single update point.

#### Failure modes
- Missing or under-scoped token - CLI exits non-zero and work item fails with stderr.

#### Test approach
- **Automatable**: argv construction and ignored model/reasoning behavior.
- **Manual / spec-only**: real Copilot CLI token flow.

### Credential provider chain - Resolves built-in, plugin, and environment credentials per project

**Source**: `src/CodeyBox.Core/ICredentialProvider.cs`, `src/CodeyBox.Core/IProjectAwareCredentialProvider.cs`, `src/CodeyBox.Orchestrator/ChainedCredentialProvider.cs`, `src/CodeyBox.Orchestrator/EnvironmentCredentialProvider.cs`, `src/CodeyBox.Orchestrator/ClaudeOAuthFileCredentialProvider.cs`, `src/CodeyBox.Orchestrator/CodexOAuthFileCredentialProvider.cs`, `src/CodeyBox.Orchestrator/GeminiOAuthFileCredentialProvider.cs`, `src/CodeyBox.Core/Project.cs`, `docs/credential-plugins.md`
**Related PRs**: #66

#### Primary user flows
1. Agent starts - chain resolves the first provider that can supply credentials for that agent/project.
2. Project specifies `CredentialProviderPriority` - plugin providers are filtered and ordered per project.
3. Built-in OAuth file is present - full credential bundle is passed so runner can materialize CLI auth files.

#### Edge cases
- Provider returns expired or time-bound credential - chain respects provider metadata and smoke gate catches failure.
- Env-var fallback is available - missing OAuth files do not block API-key deployments.
- Plugin provider throws - chain falls through or reports clear error according to provider semantics.

#### Failure modes
- No provider supplies credentials - smoke gate or runner fails before expensive work.
- Credential leaks into logs - redactors must scrub raw output and Serilog enrichment.

#### Test approach
- **Automatable**: chain order, project priority filtering, env fallback, plugin fallthrough, OAuth JSON parsing, redaction.
- **Manual / spec-only**: real user home auth-file transfer into a VM.

### Credential smoke gate - Validates agent credentials before pickup

**Source**: `src/CodeyBox.Orchestrator/CredentialSmokeGate.cs`, `src/CodeyBox.Orchestrator/StartupSmokeProbeService.cs`, `src/CodeyBox.Orchestrator/AgentSmokeCache.cs`, `src/CodeyBox.Core/IAgentSmokeProbe.cs`, `src/CodeyBox.Core/Project.cs`
**Related PRs**: none known

#### Primary user flows
1. Startup smoke probes run for configured agents - failures are logged early.
2. Work item pickup checks smoke cache - known-bad credentials block work before sandbox creation.
3. Project opts out - smoke gate skips for projects with `SkipCredentialSmokeTest`.

#### Edge cases
- Cache TTL expires - probe is rerun.
- Probe times out - failure is cached with diagnostic.
- Agent has no smoke probe - gate degrades according to registry behavior.

#### Failure modes
- Smoke probe falsely fails during transient vendor outage - item may remain queued/failed until operator intervenes.

#### Test approach
- **Automatable**: cache TTL, startup timeout, project opt-out, missing probe, failure messages.
- **Manual / spec-only**: live credential smoke run per vendor.

## Auditing And Reports

### Audit presets and language discovery - Expands project language/audit-type config into concrete auditors

**Source**: `src/CodeyBox.Audit.Presets/PresetCatalog.cs`, `src/CodeyBox.Audit.Presets/PresetConfigLoader.cs`, `src/CodeyBox.Audit.Presets/PresetCatalogSelectionValidator.cs`, `src/CodeyBox.Audit.Presets/Defaults/languages/*.yaml`, `src/CodeyBox.Audit.Presets/Defaults/audit-types/*.yaml`, `src/CodeyBox.Core/LanguageProjectDiscovery.cs`, `src/CodeyBox.Projects/ProjectAuditorComposer.cs`, `docs/audit-types.md`, `docs/languages.md`
**Related PRs**: none known

#### Primary user flows
1. Project config declares languages and audit types - composer expands presets into deterministic and LLM auditors.
2. Languages are not explicitly configured - language discovery inspects repository files and selects matching presets.
3. Project override adjusts preset parameters - override is applied without modifying bundled defaults.

#### Edge cases
- Unknown language or audit type - validator suggests close matches where possible.
- Multi-language repo - composer includes auditors for every detected language.
- Custom prompt frame is set - LLM auditor prompt uses the project-specific frame.

#### Failure modes
- Malformed YAML or schema mismatch - startup/config load fails with actionable error.
- Duplicate auditor names from custom and preset sources - registry/composer must not silently mask one.

#### Test approach
- **Automatable**: preset loading, schema validation, fuzzy suggestions, language detection, multi-language composition, overrides.
- **Manual / spec-only**: operator config review for real project preset selection.

### Built-in deterministic auditors - Runs format, build, security, dependency, and diff-pattern checks

**Source**: `src/CodeyBox.Audit/DiffPatternAuditor.cs`, `src/CodeyBox.Audit.Shell/ShellCommandAuditor.cs`, `src/CodeyBox.Audit.Shell/DepsCveScanDeepAuditor.cs`, `src/CodeyBox.Audit.Presets/Presets/LanguagePresetAuditor.cs`, `src/CodeyBox.Core/IAuditor.cs`, `docs/audit.md`, `docs/security-audit.md`
**Related PRs**: none known

#### Primary user flows
1. C# format/build auditors run - shell auditor executes configured commands and converts failures to `AuditFinding`s.
2. Security auditors run `gitleaks` and `semgrep` - findings block according to `FailingSeverity`.
3. Diff-pattern auditor inspects changes - banned patterns or scope issues are reported.

#### Edge cases
- Auditor needs no agent credentials - it runs in tool sandbox without credential mounts.
- Auditor command times out - raw output and timeout finding are captured.
- No matching project files - language auditor should skip or pass as configured.

#### Failure modes
- Tool missing in sandbox image - auditor returns failure with install hint.
- Raw output is huge - report storage and endpoint truncation must remain bounded.

#### Test approach
- **Automatable**: fake shell exits, timeout behavior, finding conversion, capability grouping, gitleaks/semgrep command args.
- **Manual / spec-only**: real tool execution against representative repositories.

### LLM and deep auditors - Reviews work diffs and release branches with agent-backed auditors

**Source**: `src/CodeyBox.Audit.Llm/LlmReviewAuditor.cs`, `src/CodeyBox.Audit.Llm/OwaspAsvsDeepAuditor.cs`, `src/CodeyBox.Audit.Llm/ArchCoherenceDeepAuditor.cs`, `src/CodeyBox.Core/IDeepAuditor.cs`, `src/CodeyBox.Orchestrator/ReleaseService.cs`, `src/CodeyBox.Audit/ReworkPromptBuilder.cs`
**Related PRs**: #65

#### Primary user flows
1. LLM review auditor runs after work - selected audit runner reviews diff and emits structured findings.
2. Cross-review override is configured - auditor uses per-auditor or project audit agent instead of work agent.
3. Release enters `InReview` - deep auditors review the full release branch and generate remediation work items if needed.

#### Edge cases
- Audit agent credentials missing - pipeline falls back to work agent or surfaces startup validation.
- Model id belongs to work agent but audit agent differs - invalid model override is not passed.
- Deep audit languages are configured - context includes language list.

#### Failure modes
- LLM output cannot be parsed - auditor returns raw output and blocking finding.
- Deep audit remediation never converges - release becomes `Failed`.

#### Test approach
- **Automatable**: prompt construction, agent override selection, model/reasoning plumbing, parse failures, deep audit remediation loop with fake agents.
- **Manual / spec-only**: human review of LLM prompt quality and real release branch deep audit.

### Audit-agent startup validation - Warns when configured audit agents lack credentials

**Source**: `src/CodeyBox.Orchestrator/AuditAgentStartupValidationService.cs`, `src/CodeyBox.Api/Program.cs`, `src/CodeyBox.Core/Project.cs`, `src/CodeyBox.Core/ICredentialProvider.cs`
**Related PRs**: none known

#### Primary user flows
1. Host starts with projects configured - service enumerates projects asynchronously without blocking host startup.
2. Project has `AuditAgent` different from the work agent - credential provider is queried for that audit agent.
3. Project has per-auditor agent overrides - each distinct override is validated once and warnings name the project, audit agent, and fallback work agent.

#### Edge cases
- Audit agent equals project default work agent - validation skips it because normal work-agent credential checks cover it.
- Same audit agent appears globally and per-auditor - de-duplication prevents duplicate warnings.
- Project list cannot be loaded at startup - service logs debug and leaves runtime fallback behavior unchanged.

#### Failure modes
- Credential provider returns null - service logs a warning that runtime will fall through to the work agent.
- Credential provider throws - service logs a warning with the agent and project context.
- Startup validation task faults after `StartAsync` returns - host remains running and tests should observe `StartupTask`.

#### Test approach
- **Automatable**: project enumeration, global/per-auditor de-duplication, default-agent skip, missing credential warning, provider exception warning, non-blocking startup.
- **Manual / spec-only**: operator review of startup logs in a mixed-agent deployment.

### Audit iteration loop and parallelism - Reworks findings until pass or configured failure

**Source**: `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Core/IAuditor.cs`, `src/CodeyBox.Orchestrator/SqliteAuditReportStore.cs`, `src/CodeyBox.Api/AuditReportEndpoints.cs`, `docs/audit-reports.md`
**Related PRs**: #107

#### Primary user flows
1. Audit finds blocking issues - runner builds rework prompt, runs agent, and repeats audit up to `MaxIterations`.
2. Multiple auditors are configured - compatible auditors run in parallel and report individual raw output and findings.
3. Findings below `FailingSeverity` exist - audit passes while preserving non-blocking findings.

#### Edge cases
- `StopOnFirstFailure` is true - loop stops after the first blocking auditor.
- Auditor requires credentials and tool auditor does not - capability grouping keeps them in separate sandboxes.
- Rework produces no changes - next audit still determines pass/fail.

#### Failure modes
- Auditor throws - failure is captured as an audit finding/report instead of crashing the host.
- Parallel auditor cancellation - all running auditor tasks are cancelled on phase timeout.

#### Test approach
- **Automatable**: iteration counts, failing severity, stop-on-first-failure, parallel execution ordering, report persistence, raw output endpoint.
- **Manual / spec-only**: real mixed tool/LLM audit suite with multiple sandboxes.

### Audit finding schema and stable IDs - Represents and correlates findings across reports

**Source**: `src/CodeyBox.Core/IAuditor.cs`, `src/CodeyBox.Core/FindingIdComputer.cs`, `src/CodeyBox.Orchestrator/SqliteAuditReportStore.cs`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Components/Pages/AuditReports.razor`
**Related PRs**: none known

#### Primary user flows
1. Auditor emits `AuditFinding` - severity, title, description, location, and auditor name are persisted.
2. Admin report page displays findings across iterations - stable IDs allow matrix tracking.

#### Edge cases
- Location is null or points to deleted file - UI still renders finding.
- Same title appears from two auditors - ID includes enough context to avoid accidental merge.

#### Failure modes
- Severity string is unknown - parser defaults to `Error` and blocks.
- Finding has excessive text - persistence and UI must remain bounded.

#### Test approach
- **Automatable**: severity parsing, ID stability, report serialization, UI matrix grouping.
- **Manual / spec-only**: visual inspection of large finding sets.

### Audit report retention sweep - Deletes expired persisted audit report rows

**Source**: `src/CodeyBox.Orchestrator/AuditReportRetentionService.cs`, `src/CodeyBox.Orchestrator/SqliteAuditReportStore.cs`, `src/CodeyBox.Core/IAuditReportStore.cs`, `src/CodeyBox.Api/Program.cs`
**Related PRs**: none known

#### Primary user flows
1. Host starts - retention service immediately computes `UtcNow - CodeyBox:AuditLog:RetainedDays` and deletes older `audit_reports` rows.
2. Host keeps running - sweep repeats daily using `PeriodicTimer`.
3. Old rows are deleted - service logs the deleted count and cutoff only when rows were removed.

#### Edge cases
- `RetainedDays` is the minimum valid value - startup validation accepts values `>= 1` and the cutoff is still computed in UTC.
- Audit report table is empty - sweep completes with zero deleted rows and no noisy log.
- Shutdown occurs during timer wait or store delete - cancellation exits without warning noise.

#### Failure modes
- Store delete fails - service logs a warning and retries on the next daily sweep.
- Cutoff formatting or clock drift creates boundary ambiguity - tests should pin rows around the cutoff and assert only `started_at < cutoff` is deleted.

#### Test approach
- **Automatable**: immediate startup sweep, cutoff calculation, SQLite `DeleteOlderThanAsync`, zero-delete path, exception logging, cancellation handling.
- **Manual / spec-only**: long-running environment check that retained report rows match configured days after multiple daily sweeps.

## Plugins

### Plugin foundation - Discovers, loads, and initializes versioned plugins

**Source**: `src/CodeyBox.PluginSdk/CodeyBoxPluginAttribute.cs`, `src/CodeyBox.PluginSdk/IPluginInitializer.cs`, `src/CodeyBox.PluginSdk/IPluginHost.cs`, `src/CodeyBox.PluginSdk/PluginContext.cs`, `src/CodeyBox.Orchestrator/Plugins/PluginLoader.cs`, `src/CodeyBox.Orchestrator/Plugins/PluginInitializationService.cs`, `src/CodeyBox.Orchestrator/Plugins/PluginHost.cs`, `src/CodeyBox.Core/CodeyBoxApiVersion.cs`, `src/CodeyBox.Api/PluginEndpoints.cs`, `docs/plugins.md`
**Related PRs**: none known

#### Primary user flows
1. Plugin directory is configured - loader scans assemblies and creates isolated load contexts.
2. Plugin has supported API version - initializer runs and can register services.
3. Operator calls `/plugins` - loaded auditor plugin metadata is listed.

#### Edge cases
- Assembly has type load failures - loader logs and continues scanning other assemblies.
- Duplicate plugin IDs - startup rejects or records a deterministic error.
- Missing plugin ID in config - project setup surfaces a clear error.

#### Failure modes
- Plugin initializer throws - plugin fails to load without corrupting host DI.
- API version is unsupported - plugin is rejected with compatibility message.

#### Test approach
- **Automatable**: discovery, load context, attribute parsing, duplicate IDs, API version gate, initializer failure.
- **Manual / spec-only**: install external plugin DLL and inspect logs/UI.

### Auditor plugin SDK - Allows external auditors to join project audit composition

**Source**: `docs/auditor-plugins.md`, `src/CodeyBox.Core/IAuditor.cs`, `src/CodeyBox.Projects/ProjectAuditorComposer.cs`, `samples/CodeyBox.SampleAuditorPlugin/CodeyBox.SampleAuditorPlugin/NoTodoAuditor.cs`
**Related PRs**: none known

#### Primary user flows
1. Auditor plugin registers an `IAuditor` - composer includes it when project config selects it.
2. Plugin auditor reports findings - pipeline treats them like built-in auditors.

#### Edge cases
- Plugin auditor requires agent credentials - capability grouping respects `Required`.
- Plugin is referenced by project but not loaded - project audit setup reports missing plugin.

#### Failure modes
- Plugin auditor throws - audit report captures failure and does not crash host.

#### Test approach
- **Automatable**: sample plugin discovery, project selection, missing plugin, finding persistence.
- **Manual / spec-only**: third-party auditor plugin installation.

### Upstream remote plugin SDK - Allows external upstream providers

**Source**: `docs/upstream-plugins.md`, `src/CodeyBox.Core/IUpstreamRemote.cs`, `src/CodeyBox.Core/IUpstreamRemoteFactory.cs`, `src/CodeyBox.Core/IUpstreamPluginHost.cs`, `src/CodeyBox.Projects/UpstreamRemoteFactory.cs`, `samples/CodeyBox.SampleGiteaUpstreamPlugin/SampleGiteaUpstreamRemote.cs`
**Related PRs**: none known

#### Primary user flows
1. Project uses plugin upstream kind - factory resolves plugin remote and passes project config.
2. Plugin completes upstream push - pipeline records outcome and transitions to `Done`.

#### Edge cases
- Plugin-specific config is present - remote reads keys through plugin host.
- Plugin upstream kind is missing - work item fails before push with clear error.

#### Failure modes
- Plugin throws transient network error - upstream retry policy handles it.
- Plugin returns unsupported release/tag operation - release service logs and continues where optional.

#### Test approach
- **Automatable**: sample Gitea remote, config pass-through, missing kind, name collision.
- **Manual / spec-only**: real forge plugin flow.

### Credential provider plugin SDK - Allows external secret sources

**Source**: `docs/credential-plugins.md`, `src/CodeyBox.Core/ICredentialProvider.cs`, `src/CodeyBox.Core/IProjectAwareCredentialProvider.cs`, `src/CodeyBox.Orchestrator/ChainedCredentialProvider.cs`, `samples/CodeyBox.SampleVaultCredentialPlugin/SampleVaultCredentialProvider.cs`
**Related PRs**: none known

#### Primary user flows
1. Credential plugin is loaded - chain queries it between built-in file providers and env fallback.
2. Project priority lists plugin IDs - only allowed plugins are used and order is honored.

#### Edge cases
- Plugin returns no credential for an agent - chain falls through.
- Plugin returns time-bound credential - smoke/cache logic respects expiry where available.

#### Failure modes
- Secret backend unavailable - provider failure is surfaced and fallback behavior remains deterministic.

#### Test approach
- **Automatable**: sample Vault provider, priority order, fallthrough, time-bound credentials, project scoping.
- **Manual / spec-only**: real Vault/secret-manager integration.

## Work Item APIs And Queue Controls

### Work item CRUD and lifecycle endpoints - Creates, lists, patches, retries, cancels, and uncancels work items

**Source**: `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Core/Validation.cs`, `src/CodeyBox.Core/WorkItem.cs`, `docs/api.md`, `docs/work-items.md`
**Related PRs**: #63, #64

#### Primary user flows
1. Operator creates a work item - API validates project, title, prompt, branches, agent, timeouts, routing fields, and enqueues it.
2. Operator lists or gets work items - DTO includes state, dependencies, replay, agent class, failure kind, quota/transient retry, and release fields.
3. Operator patches a queued item - title, prompt, or agent can be changed before pickup.
4. Operator cancels or uncancels - cancellation cascades to queued dependents and uncancel resets to queued.

#### Edge cases
- ID segment uses `project:externalId` on supported endpoints - API resolves external ID within project.
- Title > 200 chars or prompt > 64 KB - create/promotion rejects.
- Patch non-queued item - endpoint rejects immutable runtime item.

#### Failure modes
- Unknown project/agent - endpoint returns `400` with available options where applicable.
- Concurrent cancel/retry - conditional updates prevent stale transition.

#### Test approach
- **Automatable**: request validation, DTO fields, patch restrictions, cancel cascade, uncancel, retry, external-id resolution.
- **Manual / spec-only**: API smoke via real HTTP client and admin UI.

### Work item diagnostics endpoints - Exposes diff, timeline, audit reports, stdout tail, timings, costs, and stream artifacts

**Source**: `src/CodeyBox.Api/WorkItemDiffEndpoints.cs`, `src/CodeyBox.Api/AuditLogTimelineReader.cs`, `src/CodeyBox.Api/AuditReportEndpoints.cs`, `src/CodeyBox.Api/WorkItemTimingsEndpoints.cs`, `src/CodeyBox.Api/WorkItemCostsEndpoints.cs`, `src/CodeyBox.Api/AgentStreamEndpoints.cs`, `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Core/AuditLog.cs`, `docs/audit-logging.md`, `docs/api.md`
**Related PRs**: none known

#### Primary user flows
1. Operator opens a work item detail - UI/API retrieve diff, timeline, stdout tail, audit reports, timings, costs, and agent streams.
2. Diff is within display limits - endpoint returns file changes and inline diff content.
3. Timeline is requested - audit log reader returns state transitions and related events in chronological order.
4. Raw audit output is requested - endpoint returns stored raw auditor output for a specific iteration/auditor.

#### Edge cases
- Diff is too large - endpoint returns a truncation hint rather than loading excessive content.
- Work item is referenced by external ID on supported endpoints - diagnostics resolve the same item as UUID lookup.
- Audit report or stream file is missing - endpoint returns `404` without affecting other diagnostics.

#### Failure modes
- Host bare repo is unavailable for diff - endpoint returns empty/error diagnostic instead of throwing.
- Audit log rolls across days - timeline reader merges relevant entries across configured files.
- Raw output contains secrets - redaction must be applied before storage or display according to redaction layer.

#### Test approach
- **Automatable**: diff truncation, timeline parsing/cross-day ordering, stdout-tail, audit report raw endpoint, costs/timings DTOs, missing artifact responses.
- **Manual / spec-only**: dashboard diagnostics drill-down during a real multi-iteration item.

### Dependencies, external IDs, ordering, and project gating - Coordinates queued work

**Source**: `src/CodeyBox.Orchestrator/WorkItemDependencies.cs`, `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemStore.cs`, `docs/external-ids.md`
**Related PRs**: none known

#### Primary user flows
1. Create item with `dependsOn` IDs or external IDs - API resolves dependencies and blocks pickup until they are terminal.
2. Dependency becomes terminal - orchestrator enqueues satisfied dependents.
3. Operator reorders queue - store updates `QueuePosition` and queued list reflects new order.

#### Edge cases
- More than 100 dependencies - create rejects to bound graph checks.
- Dependency cycle would be created - API rejects with cycle path.
- External ID duplicate in same project - unique index and precheck reject.

#### Failure modes
- Parent is cancelled - queued child is cancelled with parent-cascaded reason.
- Dependency references unknown external ID - create returns actionable bad request.

#### Test approach
- **Automatable**: ID/external-ID dependency resolution, cycle detection, dependent endpoint, reorder, cascade cancellation, uniqueness race.
- **Manual / spec-only**: batch queue workflow from external ticket IDs.

### Replay-on-different-agent - Creates linked comparison work items

**Source**: `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Core/WorkItem.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemStore.cs`, `docs/replay.md`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Components/Pages/WorkItemComparison.razor`
**Related PRs**: none known

#### Primary user flows
1. Operator posts `/workitems/{id}/replay` - API creates a linked item with same prompt/project and optional agent/model/class/work branch override.
2. Operator requests `/workitems/{id}/replays` - API returns source and linked replay items.
3. Admin comparison page displays original and replay state/results.

#### Edge cases
- Source has dependencies - replay inherits or preserves dependency behavior according to API rules.
- Source is deleted/cancelled - replay link is orphaned but replay continues.
- Override agent is unknown - API rejects.

#### Failure modes
- Work branch override conflicts with source/base branch - validation rejects.
- Replay creation races with source cancellation - store keeps replay consistent.

#### Test approach
- **Automatable**: replay creation, overrides, dependency inheritance, orphaning on delete/cancel, list endpoint.
- **Manual / spec-only**: qualitative cross-agent comparison in dashboard.

### Pause-and-ask operator input - Parks ambiguous work for human answers

**Source**: `src/CodeyBox.Orchestrator/QuestionParser.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemQuestionStore.cs`, `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Core/WorkItemQuestion.cs`, `src/CodeyBox.Core/Project.cs`, `docs/agent-questions.md`
**Related PRs**: none known

#### Primary user flows
1. Project allows agent questions - agent emits `<codeybox-question>` block and work item transitions to `NeedsOperatorInput`.
2. Operator answers - question is marked answered and item resumes at `WorkComplete` or appropriate phase.
3. Operator dismisses question - question is dismissed and item resumes.

#### Edge cases
- Project disallows questions - parser output is ignored or treated as normal agent output.
- Multiple questions are emitted - endpoint returns all persisted questions and answer targets one ID.
- Answer text is empty or oversized - API validation rejects.

#### Failure modes
- Answer arrives when item is no longer `NeedsOperatorInput` - endpoint returns conflict/bad request.
- Question XML is malformed - parser does not crash and work continues/fails normally.

#### Test approach
- **Automatable**: parser shapes, state transition, answer/dismiss endpoints, agent receives answer, webhook event.
- **Manual / spec-only**: dashboard answer flow with live agent output.

### Suggestions workflow - Captures adjacent issues and promotes or dismisses them

**Source**: `src/CodeyBox.Core/Suggestion.cs`, `src/CodeyBox.Orchestrator/SuggestionsFileParser.cs`, `src/CodeyBox.Orchestrator/SqliteSuggestionStore.cs`, `src/CodeyBox.Api/SuggestionEndpoints.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `docs/suggestions.md`
**Related PRs**: recent root-array hotfix

#### Primary user flows
1. Work or merge agent writes `.codeybox/suggestions.json` - parser validates schema and persists accepted suggestions on the work item.
2. Operator lists suggestions - API filters by project, category, and severity.
3. Operator promotes suggestion - API creates a new work item with XML-escaped advisory prompt and atomically marks suggestion accepted.
4. Operator dismisses suggestion - state changes to dismissed with optional reason.

#### Edge cases
- File uses root array or wrapped `suggestions` object - parser accepts supported shapes.
- Invalid category/severity/oversized file - entry or file is dropped without failing the work item.
- Promotion includes extra instructions - advisory content remains fenced and escaped.

#### Failure modes
- Work-item creation fails after suggestion claim - endpoint attempts to revert suggestion to open.
- Concurrent promotions - only one wins `TryAcceptAsync`.

#### Test approach
- **Automatable**: parser schema, size cap, root-array compatibility, persistence, promote escaping, concurrent promote, dismiss filters.
- **Manual / spec-only**: operator triage workflow in admin dashboard.

### Queue pause, resume, and status endpoints - Controls global and project pickup

**Source**: `src/CodeyBox.Api/WorkItemEndpoints.cs`, `src/CodeyBox.Api/ProjectBudgetEndpoints.cs`, `src/CodeyBox.Orchestrator/SqliteQueueController.cs`, `src/CodeyBox.Core/IQueueController.cs`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Components/Pages/Index.razor`
**Related PRs**: none known

#### Primary user flows
1. Operator pauses global queue - workers stop consuming new items while in-flight work continues.
2. Operator resumes global queue - pending items are consumed again.
3. Operator pauses/resumes a project - only that project's pickup is gated.
4. Operator checks `/queue/status` and `/workers/status` - response shows pause and worker state.

#### Edge cases
- Pause while worker is blocked in dequeue - item is re-enqueued after post-dequeue pause check.
- Pause reason is empty or long - stored reason remains bounded by endpoint validation where present.
- Resume already-running queue - no-op without extra audit noise.

#### Failure modes
- Queue state DB contains invalid enum - controller defaults safely to running and logs warning.
- Project pause state missing - project is treated as running.

#### Test approach
- **Automatable**: pause/resume endpoints, persisted state, project state, race after dequeue, status DTO.
- **Manual / spec-only**: dashboard controls during active workload.

## Projects And Configuration

### Project repository and defaults - Loads project repo, branch, agent, audit, upstream, network, budget, and release config

**Source**: `src/CodeyBox.Projects/ProjectRepository.cs`, `src/CodeyBox.Projects/ProjectsOptions.cs`, `src/CodeyBox.Core/Project.cs`, `src/CodeyBox.Api/Program.cs`, `docs/projects.md`, `docs/configuration.md`
**Related PRs**: none known

#### Primary user flows
1. API starts with configured projects - repository validates IDs, repository URLs, default branches, agent defaults, and upstream settings.
2. Work item pickup loads project - project settings override global defaults for audit, network, budget, credentials, changelog, and releases.
3. Project list/get endpoints expose operator-readable config.

#### Edge cases
- No projects are configured - API should still start but create requests fail with available project list.
- Project default branch is null - git host default branch is used.
- Per-project network profile overrides only some phases - unset phases remain denied/default according to provider.

#### Failure modes
- Invalid project ID or duplicate ID - startup/config load rejects.
- Upstream config references missing token env var - upstream flow fails with clear auth error.

#### Test approach
- **Automatable**: options binding, validation, default inheritance, endpoint DTO, network profiles, release-enabled gate.
- **Manual / spec-only**: real operator config file review and startup.

### API configuration and startup validation - Binds options and refuses unsafe or inconsistent settings

**Source**: `src/CodeyBox.Api/Program.cs`, `src/CodeyBox.Api/appsettings.json`, `src/CodeyBox.Api/ApiKeyAuth.cs`, `src/CodeyBox.Orchestrator/OrchestratorOptionsFactory.cs`, `src/CodeyBox.Orchestrator/WorkerPoolOptions.cs`, `src/CodeyBox.Orchestrator/AgentStreamsOptions.cs`
**Related PRs**: none known

#### Primary user flows
1. Host starts - options bind into `CodeyBoxOptions` and derived orchestrator options.
2. API key auth is configured - protected API requests require the expected header/token.
3. Sandbox provider is selected - development defaults are permissive while non-development refuses unsafe process sandbox.

#### Edge cases
- Legacy `Concurrency` and new worker pool options both appear - new worker pool semantics remain clear.
- OTel enabled with missing endpoint - startup validation rejects.
- Changelog webhook secret missing in non-development - startup refuses or endpoint rejects.

#### Failure modes
- Invalid agent class score or TOD modifier - startup throws before accepting work.
- Invalid `ExportProtocol` or URL - OTel validation fails fast.

#### Test approach
- **Automatable**: configuration binding, auth validator, unsafe sandbox guard, validation failures.
- **Manual / spec-only**: deployment smoke with production-like environment variables.

### API health check endpoint - Exposes an anonymous liveness probe for deployment monitors

**Source**: `src/CodeyBox.Api/Program.cs`, `src/CodeyBox.Api/ApiKeyAuth.cs`
**Related PRs**: none known

#### Primary user flows
1. Load balancer or operator calls `GET /healthz` - API returns `200 OK` with `{ "status": "ok" }`.
2. API key auth is enabled - `/healthz` remains reachable without an `Authorization` header because it is registered as an anonymous prefix.
3. Auth-disabled development host calls `/healthz` - response remains identical to the authenticated deployment behavior.

#### Edge cases
- Request includes an invalid bearer token - anonymous prefix bypasses token validation and still returns health.
- Request path starts with `/healthz` and has extra segments - middleware prefix behavior is explicit and should be covered according to `StartsWithSegments` semantics.
- Startup configuration is invalid - health endpoint is not served because host startup validation fails before requests are accepted.

#### Failure modes
- Middleware ordering changes - health endpoint incorrectly starts requiring auth or bypassing unrelated protected endpoints.
- Endpoint response shape changes - deployment probes and smoke checks lose a stable contract.

#### Test approach
- **Automatable**: in-memory API host tests for anonymous `GET /healthz`, response status/body, invalid-token request, and protected endpoint still requiring auth.
- **Manual / spec-only**: deployment/load-balancer liveness probe against a production-like host.

## Persistence And Recovery

### SQLite work-item and auxiliary stores - Persists durable pipeline state and related records

**Source**: `src/CodeyBox.Orchestrator/SqliteWorkItemStore.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemQuestionStore.cs`, `src/CodeyBox.Orchestrator/SqliteSuggestionStore.cs`, `src/CodeyBox.Orchestrator/SqliteAuditReportStore.cs`, `src/CodeyBox.Orchestrator/SqliteTimingStore.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemCostStore.cs`, `src/CodeyBox.Orchestrator/SqliteReleaseStore.cs`, `src/CodeyBox.Orchestrator/SqliteAgentStreamSummaryStore.cs`
**Related PRs**: #64

#### Primary user flows
1. First startup creates schema - tables and indexes are created in `StateDatabasePath`.
2. Later startup runs additive migrations - new columns such as `failure_kind`, `quota_reset_at`, `release_id`, and preempt fields are added idempotently.
3. Store writes use conditional updates - phase transitions and retries avoid lost updates.

#### Edge cases
- Existing DB lacks new column - migration catches duplicate-column exceptions and proceeds.
- External ID is null - partial unique index allows multiple nulls.
- WAL/busy timeout allows concurrent queue and work-item writes.

#### Failure modes
- Unique external ID race - store throws `WorkItemExternalIdConflictException` and API maps it.
- DB path directory is missing - store creates directory.

#### Test approach
- **Automatable**: schema creation, migration idempotence, unique index, conditional updates, serialization of dependencies/questions/suggestions.
- **Manual / spec-only**: upgrade an older real `state.db`.

### Restart resumption and recovery caps - Reconstructs runnable queue after process restart

**Source**: `src/CodeyBox.Orchestrator/OrchestratorService.cs`, `src/CodeyBox.Orchestrator/DeadWorkerReaper.cs`, `src/CodeyBox.Orchestrator/InMemoryTaskQueue.cs`, `src/CodeyBox.Core/WorkItem.cs`, `docs/recovery.md`
**Related PRs**: none known

#### Primary user flows
1. Orchestrator starts - dead-worker reaper runs, then pending items are replayed from SQLite into the in-memory queue.
2. Mid-flight states map to durable resume states - audit resumes from `WorkComplete`, merge from `AuditPassed`, upstream from `Merged`.
3. Repeated failed recovery hits cap - item becomes `AbandonedAfterRecoveryAttempts`.

#### Edge cases
- Queued item depends on non-terminal parent - startup does not enqueue it.
- Cancelled due to host shutdown - recovery may un-cancel according to cancellation reason.
- Terminal items are ignored.

#### Failure modes
- State mapping would lose work-phase changes - work crash maps to failed unless a preempt checkpoint exists.
- Recovery update race - conditional writes prevent overwriting terminal changes.

#### Test approach
- **Automatable**: state mapping table, dependency-aware replay, recovery attempts cap, host-shutdown cancellation recovery.
- **Manual / spec-only**: kill -9 orchestrator during each phase and inspect resumed state.

## Upstream, Webhooks, And Releases

### Upstream remotes and PR completion - Pushes merged work to noop, generic git, or GitHub remotes

**Source**: `src/CodeyBox.Core/IUpstreamRemote.cs`, `src/CodeyBox.Upstream/NoopUpstreamRemote.cs`, `src/CodeyBox.Upstream/GitGenericUpstreamRemote.cs`, `src/CodeyBox.Upstream.GitHub/GitHubUpstreamRemote.cs`, `src/CodeyBox.Projects/UpstreamRemoteFactory.cs`, `docs/upstream-plugins.md`, `docs/git-workflow.md`
**Related PRs**: #112

#### Primary user flows
1. Noop upstream is configured - upstream phase is skipped gracefully.
2. Generic git upstream is configured - host pushes merged branch to configured URL with credential helper env.
3. GitHub upstream is configured - branch is pushed, PR is opened, optionally auto-merged with configured strategy.

#### Edge cases
- Upstream non-fast-forward rejection - host fetches/rebases or merges according to reconcile strategy before retry.
- In-sandbox merge-phase push fallback is needed - pipeline can recover when host-side push path is unavailable.
- PR already exists - remote returns partial outcome with notes rather than duplicate failure.

#### Failure modes
- GitHub token missing or unauthorized - upstream phase fails with clear error and retry attempts remain bounded.
- Auto-merge conflict - item surfaces upstream completion failure without losing local merge.

#### Test approach
- **Automatable**: fake GitHub HTTP responses, generic git local remote, non-FF recovery, noop behavior, auto-merge request shape.
- **Manual / spec-only**: live GitHub PR open and auto-merge.

### Pull request descriptions and templates - Builds static or LLM-generated PR bodies

**Source**: `src/CodeyBox.Upstream.GitHub/LlmPullRequestDescriptionGenerator.cs`, `src/CodeyBox.Upstream.GitHub/PrDescriptionOptions.cs`, `src/CodeyBox.Core/IPullRequestDescriptionGenerator.cs`, `src/CodeyBox.Core/Project.cs`, `docs/git-workflow.md`
**Related PRs**: #112

#### Primary user flows
1. PR title template is configured - GitHub PR title uses work item placeholders.
2. PR description generation is enabled with sandbox image - LLM receives prompt, diff stat, full diff, addressed findings, and agent reasoning tail.
3. Generator disabled or times out - static fallback description is used.

#### Edge cases
- Diff exceeds `MaxDiffBytes` - generator truncates safely.
- Generator agent credential is missing - fallback description is used.
- Template omits placeholders - literal template is accepted.

#### Failure modes
- LLM call fails or times out - upstream still opens PR with fallback body.
- Description contains sensitive raw output - redaction and truncation should prevent leaks.

#### Test approach
- **Automatable**: prompt body, timeout, disabled config, truncation, fallback, title template.
- **Manual / spec-only**: qualitative review of generated PR body on GitHub.

### Outbound webhooks - Publishes pipeline, suggestion, budget, retry, and release events

**Source**: `src/CodeyBox.Core/IWebhookDispatcher.cs`, `src/CodeyBox.Core/WebhookEvent.cs`, `src/CodeyBox.Webhooks/HttpWebhookDispatcher.cs`, `src/CodeyBox.Webhooks/WebhookDispatcherOptions.cs`, `src/CodeyBox.Orchestrator/PipelineRunner.cs`, `src/CodeyBox.Orchestrator/BudgetAlertService.cs`, `docs/webhooks.md`
**Related PRs**: #63

#### Primary user flows
1. Work item transitions state - dispatcher posts configured event payload to matching endpoints.
2. Endpoint has secret env var - request includes HMAC/signature headers.
3. Event filter is configured - only selected events are sent.
4. Quota auto-retry fires - `work_item.auto_retry` event is published.

#### Edge cases
- Endpoint returns transient failure - dispatcher retries with backoff.
- Webhooks list is empty - null dispatcher no-ops.
- Payload includes external ID - consumers can correlate with external systems.

#### Failure modes
- Secret env var missing - dispatcher logs config issue and avoids sending unsigned payload where configured.
- Endpoint times out - retries are capped and failure is logged.

#### Test approach
- **Automatable**: fake HTTP handler, signature validation, event filters, retry/backoff, external ID payload, null dispatcher.
- **Manual / spec-only**: real webhook receiver integration.

### GitHub release webhook ingest and changelog generation - Handles release-published webhooks and manual changelog generation

**Source**: `src/CodeyBox.Api/ChangelogEndpoints.cs`, `src/CodeyBox.Api/ClaudeChangelogGenerator.cs`, `src/CodeyBox.Upstream.GitHub/GitHubPullRequestEnumerator.cs`, `src/CodeyBox.Core/IChangelogGenerator.cs`, `docs/changelog-automation.md`
**Related PRs**: #113

#### Primary user flows
1. Operator posts `/projects/{id}/release` - API enumerates merged PRs between tags and generates changelog markdown.
2. GitHub sends release webhook - endpoint validates signature, resolves project, enumerates PRs, and writes/publishes changelog output.
3. Project overrides changelog path/header - generator uses project settings over global settings.

#### Edge cases
- Previous tag cannot be resolved - webhook rejects with audit log entry.
- PR enumeration is capped - response notes cap.
- Changelog automation disabled - endpoint returns disabled result.

#### Failure modes
- HMAC signature missing or invalid - webhook returns 401.
- Generator LLM fails - endpoint surfaces failure and does not write misleading changelog.

#### Test approach
- **Automatable**: signature validation, event filtering, tag resolution, PR enumeration fixtures, disabled config, generator failure.
- **Manual / spec-only**: live GitHub release webhook.

### Release management workflow - Groups work items on release branches and performs deep audit before release

**Source**: `src/CodeyBox.Core/Release.cs`, `src/CodeyBox.Orchestrator/ReleaseService.cs`, `src/CodeyBox.Orchestrator/SqliteReleaseStore.cs`, `src/CodeyBox.Api/ReleaseEndpoints.cs`, `src/CodeyBox.Core/Project.cs`, `docs/releases.md`
**Related PRs**: #113

#### Primary user flows
1. Operator creates release - API validates project release config, name, target tag, and branch template.
2. First linked work item starts - release branch is created atomically from project base.
3. Operator closes release - once all linked items are terminal, service transitions to `InReview`.
4. Deep audit passes - service merges release branch into main, creates tag/release if supported, and marks `Released`.
5. Deep audit fails - release becomes `Failed`; operator reopens for remediation.

#### Edge cases
- Closed release has failed work items - webhook informs operator before review.
- Concurrent branch creation - `TrySetBranch` ensures one canonical branch record.
- Release management disabled for project - work item `releaseId` and release creation reject.

#### Failure modes
- Release branch merge to main conflicts - release sync conflict event fires and release remains unresolved.
- Remediation work item times out - deep audit fails.
- Unsupported upstream release creation - service logs optional release URL as null without failing.

#### Test approach
- **Automatable**: release API transitions, branch creation race, closed-to-review trigger, deep audit pass/fail, reopen/abandon/release endpoints.
- **Manual / spec-only**: full release to GitHub tag and release notes.

### Release main sync service - Periodically merges the project main branch into open release branches

**Source**: `src/CodeyBox.Orchestrator/ReleaseMainSyncService.cs`, `src/CodeyBox.Api/Program.cs`, `src/CodeyBox.Core/Release.cs`, `src/CodeyBox.Core/Project.cs`, `src/CodeyBox.Core/IUpstreamRemote.cs`, `src/CodeyBox.Core/IWebhookDispatcher.cs`
**Related PRs**: #113

#### Primary user flows
1. Background service wakes every five minutes - it lists open releases and skips anything without a release branch.
2. Project release config has `AutoSyncMainInterval` - due releases merge the project default base branch, or `main`, into the release branch via the configured upstream remote.
3. Merge succeeds - service records the in-memory last-sync timestamp and logs the source and target branches.
4. Merge reports conflict - service records the last-sync timestamp, publishes `release.sync_conflict`, and leaves conflict resolution to a human.

#### Edge cases
- Project cannot be found or release management is disabled - release is skipped.
- Auto-sync interval is null - release is skipped because periodic sync is disabled for that project.
- Host restarts - in-memory last-sync state resets, so open releases are eligible on the first sweep after startup.
- Custom project default branch is configured - sync uses that branch instead of hard-coded `main`.

#### Failure modes
- Listing open releases fails - service logs a warning and skips the sweep.
- Project load fails for one release - that release is skipped while other open releases continue.
- Upstream merge throws - service logs a warning and does not update last-sync time, allowing the next sweep to retry.
- Webhook dispatch fails on conflict - dispatcher failure is surfaced through webhook logging while release state remains unresolved.

#### Test approach
- **Automatable**: due/not-due interval logic, branchless skip, disabled project skip, successful merge, conflict webhook payload, retry-after-exception behavior, restart last-sync reset.
- **Manual / spec-only**: real release branch auto-sync against GitHub or another upstream provider.

## Cost, Telemetry, And Streams

### Cost capture and budget enforcement - Estimates per-item spend and enforces project caps

**Source**: `src/CodeyBox.Orchestrator/AgentCostExtractor.cs`, `src/CodeyBox.Orchestrator/AgentCostCalculator.cs`, `src/CodeyBox.Orchestrator/SqliteWorkItemCostStore.cs`, `src/CodeyBox.Orchestrator/BudgetAlertService.cs`, `src/CodeyBox.Api/WorkItemCostsEndpoints.cs`, `src/CodeyBox.Api/ProjectBudgetEndpoints.cs`, `src/CodeyBox.Core/Project.cs`, `docs/cost-reporting.md`, `docs/budget-alerts.md`
**Related PRs**: none known

#### Primary user flows
1. Agent output contains token usage - extractor records input/output/cache tokens by agent/model/phase.
2. Pricing config has matching rate - calculator estimates USD and persists per work item.
3. Project budget caps are configured - pickup enforces hourly/daily/concurrent/monthly caps.
4. Budget alert service crosses thresholds - webhook fires and hard cap can auto-pause project queue.

#### Edge cases
- Pricing missing for model - built-in fallback or zero/unknown estimate behavior is deterministic.
- Spend drops below threshold and auto-resume is enabled - project queue resumes.
- Cost rows span rolling 30-day window - older rows are excluded.

#### Failure modes
- Token parser sees malformed JSON/output - cost capture skips with diagnostic, pipeline still completes.
- Budget check race under concurrency - per-project lock and `StartedAt` write prevent cap overrun.

#### Test approach
- **Automatable**: extractor fixtures for Claude/Codex/Gemini, pricing lookup, cost endpoints, budget caps, alert state transitions, auto-pause/resume.
- **Manual / spec-only**: real invoice/usage reconciliation.

### Timing and OpenTelemetry export - Records phase timings and emits fleet observability signals

**Source**: `src/CodeyBox.Core/TimingScope.cs`, `src/CodeyBox.Core/TimingRecord.cs`, `src/CodeyBox.Orchestrator/SqliteTimingStore.cs`, `src/CodeyBox.Api/WorkItemTimingsEndpoints.cs`, `src/CodeyBox.Core/CodeyBoxActivities.cs`, `src/CodeyBox.Core/CodeyBoxMeters.cs`, `src/CodeyBox.Api/Program.cs`, `docs/timings.md`, `docs/observability.md`
**Related PRs**: none known

#### Primary user flows
1. Pipeline and sandbox phases run - timing rows capture phase/step durations.
2. Operator calls timing endpoints - API returns per-work-item and aggregate timing data.
3. OTel is enabled - traces and metrics export to configured OTLP collector with resource attributes.

#### Edge cases
- Timing scope fails to write - pipeline logs but continues.
- In-flight timing rows have no duration - aggregate endpoint excludes or marks inflight appropriately.
- OTel disabled - no-op registration avoids requiring collector config.

#### Failure modes
- Invalid OTel endpoint/protocol - startup validation rejects.
- Collector is unavailable - exporter failure does not stop pipeline.

#### Test approach
- **Automatable**: timing scope lifecycle, endpoint aggregation, OTel disabled no-op, OTel options validation, metrics emission.
- **Manual / spec-only**: inspect spans/metrics in real collector.

### Agent stream capture, analysis, and live stdout - Persists structured streams and broadcasts live output

**Source**: `src/CodeyBox.Orchestrator/AgentStreamStore.cs`, `src/CodeyBox.Orchestrator/AgentStreamJsonParser.cs`, `src/CodeyBox.Orchestrator/AgentStreamAnalysis.cs`, `src/CodeyBox.Orchestrator/StreamAnalysisService.cs`, `src/CodeyBox.Orchestrator/SqliteAgentStreamSummaryStore.cs`, `src/CodeyBox.Api/AgentStreamEndpoints.cs`, `src/CodeyBox.Api/AgentStdoutBroadcastService.cs`, `src/CodeyBox.Api/Hubs/AgentStdoutHub.cs`, `src/CodeyBox.Core/StdoutRingBuffer.cs`, `docs/agent-streams.md`, `docs/stream-analysis.md`
**Related PRs**: none known

#### Primary user flows
1. Agent emits structured stream - store persists NDJSON per work item, phase, and iteration.
2. Stream analysis runs - parsers summarize tokens, tool calls, stalls, and final message.
3. Operator calls stream endpoints - API lists files, downloads raw stream, runs on-demand analysis, and returns aggregate metrics.
4. Admin UI connects SignalR - live stdout is batched and displayed.

#### Edge cases
- Parser kind cannot be determined - unknown parser returns unsupported summary.
- On-demand analysis concurrency exceeds two - endpoint returns 429.
- Analysis exceeds 30 seconds - endpoint returns 504.
- Stdout tail requested after process end - ring buffer returns last chunks.

#### Failure modes
- Stream file missing or truncated - endpoint returns 404 or partial analysis without crashing.
- Sensitive output appears in raw stream - raw chunk/output redactors must remove configured secrets.

#### Test approach
- **Automatable**: parser selection, stream persistence, retention, endpoints, aggregate math, SignalR batching, stdout-tail ring buffer, redaction.
- **Manual / spec-only**: browser live stdout against a real long-running agent.

### Agent stream retention sweep - Deletes expired agent stream files and empty directories

**Source**: `src/CodeyBox.Orchestrator/AgentStreamRetentionService.cs`, `src/CodeyBox.Orchestrator/AgentStreamStore.cs`, `src/CodeyBox.Orchestrator/AgentStreamsOptions.cs`, `src/CodeyBox.Api/Program.cs`
**Related PRs**: none known

#### Primary user flows
1. Host starts with stream capture enabled - retention service runs one sweep immediately.
2. Host keeps running - service repeats sweeps daily by default.
3. Files are older than `CodeyBox:AgentStreams:RetainedDays` - store deletes expired `.jsonl` files and then removes empty stream directories.

#### Edge cases
- Stream capture is disabled - store sweep is a no-op.
- `RetainedDays` is `0` - streams are kept forever and sweep deletes nothing.
- Stream root directory does not exist - sweep returns zero deleted files.
- List and download endpoints run while deletion occurs - file operations tolerate missing files and return empty/404 diagnostics.

#### Failure modes
- Individual stream file or directory cannot be deleted - store logs warning and continues the sweep.
- Full sweep throws - retention service logs warning and retries on the next period.
- Shutdown cancels sweep - cancellation is propagated only when requested by the host.

#### Test approach
- **Automatable**: immediate sweep, disabled and keep-forever no-ops, expired file deletion, empty directory cleanup, per-file failure continuation, service exception logging.
- **Manual / spec-only**: verify disk usage reduction in a long-running deployment with real stream files.

### Fleet and worker observability endpoints - Summarizes fleet state for operators

**Source**: `src/CodeyBox.Api/FleetEndpoints.cs`, `src/CodeyBox.Api/WorkerRegistryEndpoints.cs`, `src/CodeyBox.Orchestrator/SqliteWorkerRegistry.cs`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Components/Pages/Fleet.razor`
**Related PRs**: none known

#### Primary user flows
1. Operator calls `/fleet/summary` - API summarizes queued, active, terminal, and error states across projects.
2. Operator calls `/workers` - active worker registrations and heartbeat ages are returned.
3. Dashboard fleet page renders summaries and degrades when some endpoints fail.

#### Edge cases
- Work item state enum has unknown persisted value - summary labels it safely.
- No workers registered - endpoint returns empty set.
- Large work item table - summary queries use indexes and limits.

#### Failure modes
- Worker registry DB query fails - fleet endpoint should degrade rather than blanking all data.

#### Test approach
- **Automatable**: summary aggregation, graceful degradation, worker registry list, empty states.
- **Manual / spec-only**: operator fleet dashboard during active workload.

## Operator Clients

### CLI client `codeybox` - Provides typed queue commands and configuration resolution

**Source**: `tools/CodeyBox.Cli/Program.cs`, `tools/CodeyBox.Cli/CliApp.cs`, `tools/CodeyBox.Cli/ConfigResolver.cs`, `tools/CodeyBox.Cli/Services/CodeyBoxClient.cs`, `tools/CodeyBox.Cli/Commands/*.cs`, `tools/CodeyBox.Cli/Models/*.cs`, `docs/cli.md`
**Related PRs**: none known

#### Primary user flows
1. Operator configures CLI - config command writes endpoint/auth defaults.
2. Operator adds, lists, shows, retries, cancels, or watches queue items - typed client sends correct API requests and renders output.
3. Operator selects output format - commands support human-readable and JSON where implemented.

#### Edge cases
- Auth is provided by CLI arg, env var, or config - resolver follows documented precedence.
- API returns non-success - CLI prints concise error output and exits non-zero.
- Watch polling sees transient failures - command retries or reports according to polling rules.

#### Failure modes
- Invalid config file - CLI reports parse/config error without stack trace.
- Network unavailable - command exits with actionable message.

#### Test approach
- **Automatable**: command handlers with fake HTTP, auth resolution order, output formats, error handling, watch polling.
- **Manual / spec-only**: installed CLI against running API.

### CLI version command - Prints the installed `codeybox` client version without contacting the API

**Source**: `tools/CodeyBox.Cli/CliApp.cs`, `tools/CodeyBox.Cli/Commands/VersionCommand.cs`, `tools/CodeyBox.Cli/Program.cs`
**Related PRs**: none known

#### Primary user flows
1. Operator runs `codeybox version` - CLI prints `CliApp.CliVersion` and exits successfully.
2. Operator has no API URL, API key, or config file - version still works because it does not resolve API configuration or create an HTTP client.
3. Operator invokes root help - version command appears alongside queue and configure commands.

#### Edge cases
- Global `--api-url` or `--api-key` options are supplied with `version` - command ignores them and still prints the local client version.
- Stdout is redirected - output remains a single line suitable for scripts.
- Version constant changes for a release - command output tracks the compiled `CliApp.CliVersion` value.

#### Failure modes
- Version command is not registered on the root command - installed CLI cannot report its version.
- Handler accidentally depends on config resolution - missing or malformed config breaks a local metadata command.

#### Test approach
- **Automatable**: command invocation for `version`, help registration, no client factory/config access, stdout single-line output.
- **Manual / spec-only**: installed CLI binary reports the expected packaged version.

### Project configuration wizard - Generates appsettings project entries from interactive prompts

**Source**: `src/CodeyBox.Cli/Program.cs`
**Related PRs**: none known

#### Primary user flows
1. Operator runs the wizard - Spectre.Console prompts for project ID, display name, repository URL, base branch, default agent, upstream kind, audit languages/types, and per-phase network profiles.
2. Operator selects GitHub upstream - wizard prompts owner, repository, and token environment variable and emits a GitHub upstream entry.
3. Operator selects generic git upstream - wizard prompts URL and optional token environment variable and emits a `git-generic` upstream entry.
4. Stdout is redirected - wizard writes plain indented JSON suitable for `appsettings.json` instead of rendering a panel.
5. Operator chooses to save - wizard resolves the requested path, asks before overwriting, writes JSON, or reports file-write errors.

#### Edge cases
- Project ID has invalid characters or length - prompt validation rejects anything outside 1-64 alphanumeric, dash, or underscore characters.
- Repository or generic upstream URL is empty, begins with `-`, contains control characters, or has an unsupported scheme - validation rejects it.
- Branch name contains `..`, ends with `.lock`, starts with a non-alphanumeric character, or exceeds 200 characters - validation rejects it.
- `CODEYBOX_NETWORK_PROFILES` is set - phase profile choices come from the environment instead of the built-in profile list.
- Audit language selection is empty - JSON still records an empty `Languages` list, while null optional fields such as omitted audit types are suppressed by serializer settings.

#### Failure modes
- Output file already exists and overwrite is declined - wizard reports write cancellation without changing the file.
- Output directory is unwritable or path is invalid - wizard catches the exception and prints a concise error.
- Terminal is non-interactive but stdin is not scripted - Spectre prompt execution blocks or fails; Phase 2 should document supported scripted invocation.

#### Test approach
- **Automatable**: validation regexes and helper builders, redirected-output JSON shape via scripted console input, upstream variants, network profile env handling, save/overwrite branches using a temp path.
- **Manual / spec-only**: terminal UX review for prompt ordering, highlighting, multi-select behavior, and generated snippet readability.

### Admin dashboard - Blazor Server operator UI for queue, work items, releases, audits, suggestions, costs, timings, fleet, and plugins

**Source**: `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Program.cs`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Services/CodeyBoxApiClient.cs`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/Components/Pages/*.razor`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/wwwroot/js/live-stdout.js`, `tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/wwwroot/css/admin.css`, `tools/CodeyBox.Admin/README.md`
**Related PRs**: none known

#### Primary user flows
1. Operator logs in - cookie auth protects dashboard pages when credentials are configured.
2. Queue page lists work items - supports new work item, edit queued item, cancel, retry, reorder, global/project pause, and budget badges.
3. Detail pages show diff, timeline, stdout, audits, costs, timings, replays, and questions.
4. Release pages manage release lifecycle and display audit iterations.
5. Suggestions pages filter, view, dismiss, bulk dismiss, and promote suggestions.
6. Fleet/plugins pages show system health and loaded extensions.

#### Edge cases
- API endpoint fails - page shows error banner while preserving navigation.
- Empty states - pages render clear empty content for no queue, no findings, no releases, no suggestions.
- Long titles/external IDs - table cells remain usable.

#### Failure modes
- Login credentials invalid - user returns to login with error.
- SignalR live stdout disconnects - component should reconnect or show stale state without breaking page.

#### Test approach
- **Automatable**: bUnit/component tests for page rendering and client calls, auth endpoints, forms, error banners.
- **Manual / spec-only**: browser UAT for real navigation, live SignalR stdout, and responsive layout.

### API authentication and redaction - Protects API access and suppresses secrets in logs/output

**Source**: `src/CodeyBox.Api/ApiKeyAuth.cs`, `src/CodeyBox.Core/SecretRedactor.cs`, `src/CodeyBox.Core/RawOutputRedactor.cs`, `src/CodeyBox.Core/RawChunkRedactor.cs`, `src/CodeyBox.Core/SensitiveDataRedactionEnricher.cs`, `src/CodeyBox.Git/GitCredentialHelper.cs`, `docs/security.md`
**Related PRs**: none known

#### Primary user flows
1. API key auth is enabled - requests without correct key are rejected.
2. Agent raw output contains configured secrets - logs, stream chunks, and persisted reports redact them.
3. Git credential helper supplies upstream credentials without placing tokens on argv.

#### Edge cases
- Auth disabled for development - API remains usable locally with explicit config.
- Secret appears split across chunks - raw chunk redactor handles streaming context as designed.
- Token-like string is benign - redaction may overmatch but should not corrupt control JSON.

#### Failure modes
- Secret redaction misses a credential - audit/security tests must catch representative patterns.
- Credential helper command fails - upstream push reports authentication failure.

#### Test approach
- **Automatable**: API auth middleware, redaction fixtures, chunk boundaries, credential helper env behavior.
- **Manual / spec-only**: review logs after a real failed credential flow.

## Coverage Matrix

| Feature | Automatable? | Spec-only? | Dependencies on other features |
|---|---:|---:|---|
| Work item pipeline state machine | Yes | Yes | Sandbox providers, agent runners, auditors, upstream remotes |
| Retry and rework entry points | Yes | Yes | Work branch lifecycle, SQLite stores, work item API |
| Work branch lifecycle and merge safety | Yes | Yes | Git host, sandbox providers, agent runners |
| Shutdown cancellation and preemption | Yes | Yes | CLI runner base, sandbox preemption, restart recovery |
| Stuck-agent detection | Yes | Yes | Procfs activity source, pipeline runner, retry |
| Worker pool dispatch and dead-worker recovery | Yes | Yes | Queue controller, SQLite stores, restart recovery |
| Multipass sandbox provider | Partial | Yes | Host firewall setup, Multipass daemon |
| Bubblewrap sandbox provider | Yes | Yes | Linux bwrap binary |
| Process sandbox provider | Yes | No | Startup configuration |
| Sandbox leak detection and disposal | Yes | Yes | Sandbox provider lifecycle, API endpoints |
| Agent quota probes | Yes | Yes | Vendor credentials, quota router |
| Agent class router | Yes | Yes | Quota probes, project config, quota failure store |
| Quota failure classification and auto-retry | Yes | Yes | Work item store, router, queue controller, webhooks |
| CLI runner base and preemption | Yes | Yes | Sandbox exec, agent-specific runners |
| Claude agent runner | Yes | Yes | Claude credentials, Claude CLI/API |
| Codex agent runner | Yes | Yes | Codex credentials, Codex CLI/OpenAI API |
| Gemini agent runner | Yes | Yes | Gemini credentials, Gemini CLI/API |
| Copilot agent runner | Yes | Yes | GitHub token, Copilot CLI |
| Credential provider chain | Yes | Yes | Built-in providers, plugin loader, project config |
| Credential smoke gate | Yes | Yes | Agent smoke probes, credential chain |
| Audit presets and language discovery | Yes | Yes | Project config, preset YAML |
| Built-in deterministic auditors | Yes | Yes | Sandbox tool availability |
| LLM and deep auditors | Yes | Yes | Agent runners, credentials, release service |
| Audit-agent startup validation | Yes | Yes | Project repository, credential provider chain |
| Audit iteration loop and parallelism | Yes | Yes | Auditors, pipeline runner, report store |
| Audit finding schema and stable IDs | Yes | Yes | Audit reports, admin UI |
| Audit report retention sweep | Yes | Yes | Audit report store, audit log retention config |
| Plugin foundation | Yes | Yes | Plugin SDK assemblies |
| Auditor plugin SDK | Yes | Yes | Plugin foundation, audit composer |
| Upstream remote plugin SDK | Yes | Yes | Plugin foundation, upstream factory |
| Credential provider plugin SDK | Yes | Yes | Plugin foundation, credential chain |
| Work item CRUD and lifecycle endpoints | Yes | Yes | Work item store, queue, project repository |
| Work item diagnostics endpoints | Yes | Yes | Git host, audit logs, report/cost/timing/stream stores |
| Dependencies, external IDs, ordering, and project gating | Yes | Yes | Work item API/store, orchestrator replay |
| Replay-on-different-agent | Yes | Yes | Work item API/store, admin comparison |
| Pause-and-ask operator input | Yes | Yes | Question parser/store, pipeline runner, webhooks |
| Suggestions workflow | Yes | Yes | Parser/store, work item API, admin UI |
| Queue pause, resume, and status endpoints | Yes | Yes | Queue controller, worker pool, admin UI |
| Project repository and defaults | Yes | Yes | Options binding, pipeline runner |
| API configuration and startup validation | Yes | Yes | Program startup, options classes |
| API health check endpoint | Yes | Yes | API host, API key auth middleware |
| SQLite work-item and auxiliary stores | Yes | Yes | State database path, all persistence-backed features |
| Restart resumption and recovery caps | Yes | Yes | Worker pool, SQLite stores, dead-worker reaper |
| Upstream remotes and PR completion | Yes | Yes | Git host, project upstream config, credentials |
| Pull request descriptions and templates | Yes | Yes | Agent text-only runner, GitHub upstream |
| Outbound webhooks | Yes | Yes | Pipeline events, HTTP dispatcher |
| GitHub release webhook ingest and changelog generation | Yes | Yes | GitHub upstream config, changelog generator |
| Release management workflow | Yes | Yes | Work item pipeline, deep auditors, upstream remotes |
| Release main sync service | Yes | Yes | Release store, project config, upstream remotes, webhooks |
| Cost capture and budget enforcement | Yes | Yes | Agent streams/output, pricing config, queue controller |
| Timing and OpenTelemetry export | Yes | Yes | Timing store, OTel configuration |
| Agent stream capture, analysis, and live stdout | Yes | Yes | Agent runners, stream store, SignalR, admin UI |
| Agent stream retention sweep | Yes | Yes | Agent stream store, stream retention config |
| Fleet and worker observability endpoints | Yes | Yes | Worker registry, work item store |
| CLI client `codeybox` | Yes | Yes | API endpoints, auth config |
| CLI version command | Yes | Yes | CLI root command registration |
| Project configuration wizard | Yes | Yes | Spectre.Console prompts, project config schema |
| Admin dashboard | Partial | Yes | API client, SignalR, all operator APIs |
| API authentication and redaction | Yes | Yes | API host, logging, agent streams, upstream credentials |
