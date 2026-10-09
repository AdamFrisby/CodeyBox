# External builds (provider-neutral framework)

CodeyBox can build in generic sandboxes, but projects such as Unity need
authoritative external build environments with existing licenses/toolchains.
This framework provides the durable provider-neutral lifecycle; vendor
adapters (GitHub Actions, Unity Build Automation) are follow-on work and are
**not** implemented here.

> Feature default: **OFF**. `CodeyBox:ExternalBuilds:Enabled=false` unless the
> operator explicitly enables it and approves at least one target. No running
> configuration enables it. No real providers are contacted by this task;
> tests use deterministic fake providers through the real production paths.

## Lifecycle

`ExternalBuildService` (Orchestrator) over `IExternalBuildStore`
(in-memory or SQLite `external_builds` table, additive migration):

1. Sandbox calls `build/start` with a host-issued scoped capability.
2. Service validates: enabled, operator-approved target (exact name match —
   never arbitrary endpoints/commands), budgets (per-project/per-provider
   concurrency, queued cap).
3. Intent is persisted (`IntentRecorded`) **before** dispatch — crash windows
   before/after provider acceptance both recover.
4. Dispatch moves to `SubmitUncertain`; on acceptance to `Queued` with the
   provider run id. Uncertain submissions are **reconciled before any retry**:
   the provider is queried by request identity first, so no duplicate paid
   run follows a timeout/restart. When reconciliation is impossible the build
   moves to `ReconciliationBlocked` — never a silent pass.
5. Bounded polling maps provider phases to `Running` → `Collecting` →
   `Succeeded`/`Failed`/`Cancelled`, enforcing the deadline and poll bounds.
6. Callbacks (`HandleCallbackAsync`) are authenticated (provider match,
   freshness, signature hook) and treated as **wakeups**: the service always
   reconciles against authoritative provider status before acting.
7. Completion/cancel races: cancel from a non-terminal state moves to
   `Cancelled`; provider-confirmed cancellation is distinguished from user
   cancel, disconnect, and timeout. Reservations release exactly once
   (fence cleared on terminal transition).

Exclusive ownership uses compare-and-set on `(state, fence)` in
`TryClaimAsync`; concurrent writers cannot corrupt or cross-contaminate data.

## Candidate handoff (including uncommitted work)

`ExternalBuildSnapshotBuilder.Freeze` (Core, pure) captures a coherent
immutable snapshot while the host holds the work tree still, so concurrent
edit races surface as explicit duplicate-path errors rather than silent
mixing. Captured: tracked changes, deletions, file modes, and explicitly
intended untracked source. Excluded: credentials (`.env`, `secrets.json`),
host files, private agent scratchpads/transcripts, and caches (see
`ExternalBuildSnapshotPolicy`). Size/path bounds are enforced before
buffering; the snapshot digest (SHA-256 over ordered path+content+mode plus
deletions) is verified by the provider path.

LFS: only pinned pointer blobs are captured unless the policy materializes
them. Submodules: pinned commits are recorded; contents only for listed
submodules. When a provider requires Git, the host alone may publish a
temporary scoped candidate commit/ref under `refs/candidates/` per
`ExternalBuildGitPublicationPolicy` — never moving the work/base branch and
never publishing delivery PRs. Final delivery/audit authorization stays
independent. The agent may keep editing afterward; the result remains bound
to the frozen snapshot digest.

## Sandbox tools and transports

`ExternalBuildSandboxTools` exposes build/start, status, result, cancel,
bounded diagnostics, and artifact list/read. Caller identity is a
host-issued expiring/revocable capability scoped to
project/work item/phase/iteration/attempt; ownership is rechecked at each
sink and stale/foreign handles are rejected. Only operator-approved
targets/configurations can be selected. The host owns provider credentials
and publication; the sandbox never sees them. Majordomo credentials and
vocabulary are not reused.

`ExternalBuildRunnerTransport` negotiates capabilities: ordinary durable
tools are the baseline; the optional MCP `2026-07-28`
`io.modelcontextprotocol/tasks` extension is used only after negotiation
with server-directed task creation and `tasks/get` (final result inline) —
never a task handle to an unsupported client. Old `2025-11-25`
`tasks/result` is an explicitly versioned compat path. The scoped CLI bridge
(`ExternalBuildCliBridge`) is the fallback. The in-process transport
demonstrates real wiring; other runners declare fallback explicitly.

## Park/resume

`ExternalBuildParkPolicy` (Core, pure, clock-injected) predicts from the
last N comparable durations matched by provider/target/configuration/
toolchain/platform/cache class. It parks immediately only when the prediction
is **strictly greater than 10 minutes**. Defaults: N=8, median estimator,
minimum 3 samples; all configurable via `ExternalBuildOptions`. Stale
samples (>30 days), outliers (trimmed min/max with ≥4 samples), incomplete
runs (censored — never treated as short successes), and cold/warm cache
classes are handled distinctly. With insufficient history, the elapsed-time
fallback stays active until waiting exceeds 10 minutes.

`ExternalBuildParkCoordinator` checkpoints the turn, releases execution
capacity while parked (no model process or session slot is held to poll),
and delivers completion exactly once to the right
work item/phase/iteration/attempt via the durable outbox with acknowledged
delivery — surviving restarts, disconnected transports, TTL expiry, and
sandbox recovery. Completed-before-park never parks; completed-during-park
delivers once. Parked builds report as known waits, not stalled workers.
Audit/merge gates stay closed while evidence is outstanding.

## Evidence contract

`ExternalBuildEvidence` (Core, neutral — no .NET/MSBuild/Unity fields)
represents compile, test, and package/artifact outcomes independently, plus
source digest, provider run id, approved workflow/target, toolchain/platform/
configuration descriptors, and artifact digests. `ExternalBuildEvidenceGate`
passes only with sufficient authoritative evidence for the **exact
merge-result tree** and current expected base — never a model summary, never
a mutable-branch run, never compilation masquerading as tests, never absent
tests. Exploratory results are reusable only under exact identity/policy
match. Any edit/configuration change/rework invalidates prior evidence.

## Budgets, cleanup, artifacts

Concurrency, queued caps, deadlines, poll/retry bounds, and retention are
hot-reloadable options. `CleanupAsync` deletes terminal records past
retention; no ghost builds survive restart (non-terminal records reconcile
on next poll). Artifact ingestion (`ExternalBuildArtifactGuard`) checks
size/decompression/entry limits before buffering, rejects traversal and
symlinks, verifies digests, enforces https exact-host URL allowlists,
redacts secrets, and never executes downloaded artifacts.

## Provider contract (for adapter authors)

Implement `IExternalBuildProvider` (`ProviderId`, capability flags,
`SubmitAsync`/`GetStatusAsync`/`CancelAsync`/`ListArtifactsAsync`/
`ReadArtifactAsync`). Reuse the shared lifecycle — do not fork
submit/reconcile/cancel semantics. Honor idempotent request identity
(`ExternalBuildRecord.RequestId`): duplicate submits with the same request
id must return the same provider run, never a second paid run. Keep vendor
types out of Core. Toolchain-specific parsing belongs in the adapter.
