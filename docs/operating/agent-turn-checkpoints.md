# Agent-turn checkpoints

Dead-worker recovery re-runs a phase from its last durable boundary. This page
covers the exception: recovering a *partial* agent turn — edits an agent made
before it was killed, which no phase commit has captured yet.

Read [`recovery.md`](recovery.md) first; this is the deep end.

## When a checkpoint is taken

A work or rework agent can fail after making useful edits but before the
orchestrator creates the normal phase commit. When the remaining failure is a
recognised quota failure, transient-network failure, infrastructure failure
carrying the sandbox provider's explicit `ExecutionUnavailable` signal, or
process exit `137` (SIGKILL/OOM), CodeyBox attempts a durable checkpoint.
Exhausting a live CLI resume budget does not by itself make another generic
failure eligible for durable re-dispatch.

The dirty source tree and private CLI state have separate persistence
boundaries. Git receives only the source commit at
`refs/heads/codeybox/preempt/<work-item-id>/<source-commit>-<archive-sha256>`.
The bounded archive and its restore manifest are captured under the private
`/run/codeybox/agent-turn` tmpfs, removed before source staging, and saved as an
immutable host-private SQLite BLOB capped at 32 MiB and keyed by the exact ref.
The ref binds the work item, source commit, and archive SHA-256; restore verifies
all three before exposing the archive to the exact route. Clearing checkpoint
metadata deletes its private archives through a SQLite trigger. Startup
reconciliation removes orphaned or no-longer-referenced BLOB rows, and a
successfully committed replacement checkpoint removes older generations.

The immutable checkpoint is preferred because it is safe to replay in a fresh
sandbox. There is one provider-specific fallback: if an Incus
`ExecutionUnavailable` incident also prevents the capture commands from
running, CodeyBox can retain the exact stopped VM that contains the dirty
`/work` tree. The provider prepares an opaque recovery token and immutable
private manifest while the VM is healthy; only their hashes are bound into the
Incus instance configuration. On failure, Incus durably records the exact
interrupted exec and returns the pre-created token without depending on the
unavailable daemon.

The work-item store publishes the retained lease and its typed turn metadata in
one lifecycle compare-and-set. That same SQL statement enforces the global
`CodeyBox:PipelineTuning:MaxRetainedAgentTurnSandboxes` cap (16 by default,
valid range 1–256), including across concurrent orchestrator processes. If the
cap or lifecycle comparison rejects publication, sandbox preservation is
disarmed and the original agent failure remains authoritative. A lease is
internal capability data and is never exposed in public work-item JSON or log
formatting.

## Retained Incus adoption and conversion

A retry of a retained boundary must use the exact recorded agent route and
lease provider. Before any VM mutation, the pipeline atomically records a
`Preparation` claim. Unlike a `Dispatched` claim, it does not increment the
agent-turn attempt count because no agent CLI is allowed to run in mutable
recovery evidence. The provider-cutover router sends the request to the
lease-named provider even if the selected backend changed after the outage.

Incus additionally takes an exclusive lock in the sandbox's private staging
tree. It validates the exact project, instance and token/manifest hashes, then
checks the creation-time sandbox specification, storage and guest identities,
network and effective device topology, inode-pinned host mount sources, and
recorded guest links. VM start is authorized again at each lifecycle sink.
Recovery uses an isolated boot with host devices detached, validates guest
paths, restores the exact devices, non-persistent tmpfs mounts and links, waits
for mount readiness, and removes the recorded exec control files. Any
uncertainty refuses recovery, keeps the lease preserved, and makes a
best-effort authoritative force-stop when a start may have occurred.

After adoption, the pipeline validates the work branch and isolated Git origin,
captures the CLI scratchpad, commits and pushes the dirty tree, and publishes
the content-bound private archive. SQLite atomically replaces the lease with
that immutable checkpoint. Only after publication does CodeyBox disarm
preservation and delete the retained VM. The item is then enqueued
automatically; its next pickup performs the ordinary immutable resumed-agent
dispatch. A failed adoption or conversion keeps the lease, releases the
preparation claim, and remains retryable after infrastructure is repaired. If
the post-conversion queue write fails, the immutable checkpoint remains paired
with an infrastructure-shaped `Failed` item for a later retry; the lease and VM
are not falsely restored.

The typed Git or retained-lease boundary stays paired with the item while it is
`Working`, `Reworking`, `WaitingForQuotaReset`, `WaitingForTransientRetry`,
`WaitingForAgentResume`, `NeedsOperatorInput`,
`AbandonedAfterRecoveryAttempts`, or an infrastructure-shaped `Failed`. The
last two states deliberately keep typed evidence available for an operator;
legacy Git-only preempt records do not gain that broader persistence. Once
quota/network/provider conditions recover, the corresponding automatic
scheduler—or a manual retry with no explicit phase—restores the original
`Working`/`Reworking` boundary. The first agent dispatch is pinned to the saved
route. That route receives the private archive through `RunResumedAsync`, and
Claude/Codex resume the exact saved session when an id was captured. A later
agent-class fallback receives only the source tree; because the archive was
never committed to Git, another route cannot recover it from the checkout,
history, or bare origin.

If a resumed invocation exits cleanly without creating a new diff, CodeyBox
accepts it only when the checkpoint already contains meaningful source changes
relative to the pre-turn work-branch tip. An allow-empty checkpoint followed by
a no-op resume uses the normal initial-work/rework no-diff failure path.

Durable re-dispatches are bounded by
`CodeyBox:PipelineTuning:AgentSessionResumeMaxAttempts`. The attempt is claimed
atomically immediately before the resume hook rather than at enqueue time, so
manual, scheduler, startup, and dead-worker paths share the same cap. If typed
resume preparation fails while restoring private state or prerequisites before
the agent CLI starts, the exact claim is released and that attempt is refunded;
an outage after CLI dispatch remains consumed. A value of `0` refuses an
agent-turn redispatch. This is independent of `DeadWorker.MaxRecoveryAttempts`
and of legacy suspend/preempt recovery. A retained-VM `Preparation` claim is
also independent: it consumes no agent attempt and is released when conversion
does not publish. Changing the prompt or explicitly choosing a different retry
phase discards an immutable checkpoint; a retained lease cannot be discarded by
selecting another phase because provider cleanup must remain authoritative. If
private archive capture/storage, source commit/push, or content verification
fails outside the bounded retained-Incus path, the original error remains
authoritative and normal phase recovery applies.

The item-stale watchdog does not release a live dispatch claim merely because
`UpdatedAt` is old. It must recovery-cancel a pipeline registered in this
process and observe that registration become inactive within
`WorkerProgressWatchdog.PostAgentTransitionTimeout`; the row is then re-read
and changed through its state/`UpdatedAt` compare-and-set. A claim owned by
another process on the same host remains untouched for confirmed
process-death/startup recovery. A claim owned by another host fails closed
until that host is externally fenced; heartbeat expiry alone cannot prove the
remote agent has stopped writing.

Once a resumed agent's resulting tree is pushed and synced to the work branch,
CodeyBox clears the older turn checkpoint before required-build and other
post-agent verification. A later verification outage therefore retries from
the published branch phase boundary instead of restoring stale pre-turn source
or provider session state.

`SandboxLeakReaper` treats every provider-scoped lease still referenced by a
work item as protected, including leases in operator and abandoned-recovery
states. When cancellation or another lifecycle transition clears the lease,
the VM is no longer protected and normal provider inventory cleanup can remove
it. This couples database authority and resource cleanup without relying on a
process-local handle.
