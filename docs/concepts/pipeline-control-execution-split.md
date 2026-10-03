# Pipeline control/execution split

How `PipelineRunner` is divided along the control/execution boundary
defined by the `ExecutorPhaseRequest`/`ExecutorPhaseResult` contract
(`src/CodeyBox.Core/ExecutorPhase.cs`), and what resisted assignment.

## The seam

- **Control plane** — `src/CodeyBox.Orchestrator/PipelineControlDecisions.cs`:
  pure, dependency-free decisions. Work-item verdict evaluation, audit
  orchestration (which auditors run, in what order, how many iterations,
  convergence), rework/merge verdict content, escalation message content,
  and work-item admission validation. It takes and returns
  pipeline-domain types only (`AuditResult`, `IAuditor` metadata,
  `Project`, `WorkItem`, progress snapshots). It must never reference
  `CodeyBox.Sandbox`, `ISandbox`, `ISandboxProvider`, `IAgentRunner`, or
  `IAgentRegistry` — `PipelineControlExecutionSplitTests` pins this with
  reflection.
- **Data plane** — `src/CodeyBox.Orchestrator/PipelineAgentExecutor.cs`:
  sandbox acquisition (placement-driven or direct), in-sandbox commands,
  credential-file materialisation, and agent-visible-text projection. It
  never touches the work-item table, the queue, audit verdicts, or merge
  state, and must never reference `IWorkItemStore`, `IProjectRepository`,
  `WorkItem`, `Project`, or the pipeline itself — pinned by the same
  test.
- **Facade** — `PipelineRunner` keeps implementing `IPipelineRunner`
  unchanged. Callers do not change. `RunAsync` orchestrates: decisions via
  `PipelineControlDecisions`, execution via its composed
  `PipelineAgentExecutor`. Construction is unchanged (the executor is
  composed internally from the same provider/placer arguments).

No state transition, retry rule, audit gate, or failure classification
changed: every moved body is verbatim, and the full suite passes
unmodified (six one-line `PipelineRunner.X` forwarders remain only where
existing tests pin that path; new code must call the seam types
directly).

## What moved where

Control (`PipelineControlDecisions`): auditor ordering and verdict
evaluation (`OrderAuditorsForShortCircuit`, `HasAuditBlockingFinding`,
`IsDeclaredShortCircuitBlockingResult`, `RequiresPassedBuildTestGate`,
`BuildTestGateOrderingTier`, `BatchOrderingTier`,
`MissingBuildTestGateFinding`, `SkippedGateConsumerResult`,
`NormalizePlanReviewRunResult`); iteration budgets and history
(`MissingCompletedAuditors`, `ResolveAuditMaxIterations` and its
budget helpers, `FingerprintFindings`, finding mapping, escalation
truncation/details/signals, convergence); admission validation
(`ValidateAgentControlSpec`).

Execution (`PipelineAgentExecutor`): `AcquireWorkPhaseSandboxAsync`
(decomposed to take the work-item id plus capabilities instead of the
whole item), `Run`, `RunWithCancellation`, `RunMasked`,
`HealAuditNuGetHomeAsync`, `ThrowIfExecutionUnavailable`,
`CommandFailed`, `MaterialiseCredentialFilesAsync`,
`SanitiseCredentialFileName`, `AgentVisibleStdout`.

## What resisted assignment, and why

1. **Build/test-gate run normalisation** (`NormalizeBuildTestGateRun`,
   `BuildTestGatePassEvidence`, `IsOptionalSkippedBuildTestGate`,
   `HasPassedBuildAndTestGateEvidence`) stays on `PipelineRunner`. These
   are control decisions, but they operate on the private
   `AuditorRunRecord`/`AuditorBatchResult` batch types, which bundle each
   auditor with its bound `IAgentRunner`. Moving the functions would drag
   an agent-runner dependency into the control plane. The fix is to split
   the binding (bind auditor→runner at execution time, evaluate the
   verdict record at control time) — until then the four functions stay
   put and call into `PipelineControlDecisions` for the severity check.
   This is the clearest contract gap found: the batch record couples a
   control verdict with an execution binding.
2. **Full agent-turn invocation** (`RunAgentPhaseAsync` and the
   resume/preempt/checkpoint machinery around it) stays on the runner.
   Every dozen lines it interleaves execution detail (sandbox handles,
   stream chunks, checkpoint refs) with control state (quota routing,
   iteration bookkeeping, recovery leases, store writes). Moving it needs
   contract fields for checkpoint/lease/stream attribution first; adding
   those silently through `PayloadJson` would defeat the separation, so
   they are deliberately not added here.
3. **Agent-exit → `ExecutorPhaseResult` translation** is not yet a single
   choke point. `AgentResult` carries no token usage, while
   `ExecutorPhaseResult` requires `Usage`; usage attribution today lives
   in control-side cost extractors keyed by `AgentKind`. Until the
   contract exposes usage attribution, translation stays split between
   failure classification (control) and stream capture (execution).
4. **Stream capture fan-out** (aggregator + stdout broadcast + stream
   store keyed by phase/iteration) stays inline: the phase/iteration
   labels and store writes are control state, the chunk flow is
   execution. Same contract gap as (2).
5. **Sandbox spec building** (`BuildSandboxSpec`) stays on the runner:
   it fuses control inputs (options, project secret policy, timing
   labels) with execution inputs (mounts, credential files). A clean move
   needs the spec factory to become an explicit seam input, as
   `ExecutorHostPhaseRunner` already does with its `_specFactory`.
6. **Auditor→runner binding and quota/placement routing**
   (`SelectAuditAgent`, `AuditorTimeoutAgentKind`,
   `OrderResolvedAuditorsForBatch`, `AcquireWorkPhaseSandboxAsync`'s
   placement inputs) needs execution detail (runner capabilities, live
   quota, host capacity) to decide. Per the item brief these are either
   contract gaps (surface runner capability/quota in the result) or
   genuinely control-plane concerns that take execution-typed inputs —
   either way they are recorded here, not forced across the line.

## Next steps

Split the auditor binding from the batch record (1), then move gate-run
normalisation; define contract fields for checkpoint/lease/stream
attribution (2, 4) and usage attribution (3) before moving turn
invocation; promote the sandbox spec factory to a seam input (5).
