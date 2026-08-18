# Pipeline And Worker Lifecycle Manual UAT

These procedures cover the spec-only checks from `docs/uat/00-plan.md` for the
Pipeline And Worker Lifecycle section. Run them against a disposable project and
repository; do not use a production queue or upstream.

## Full Sandboxed Real-Agent Pipeline

1. Create a disposable git repository with a `main` branch and one queued work
   item that makes a small, reviewable file change.
2. Configure the project to use the intended real agent CLI, the `uat` auditor
   profile, and a real sandbox provider.
3. Start CodeyBox and let the item run without operator intervention.
4. Verify the item transitions through work, audit, merge, and either upstream
   push or local `Done` according to `PushUpstream`.
5. Inspect the bare repo: the work branch exists, the base branch contains the
   merged change, and commits include the required `Co-Authored-By` trailer.

## Retry Entry Points From UI/API

1. Force one item into each terminal state that supports retry: work failure,
   audit failure, merge conflict failure, upstream failure, and cancelled.
2. Retry each item from `work`, `audit`, `merge`, and `upstream` where allowed.
3. Verify the API or UI rejects unsupported/missing-repo resumes with a clear
   error.
4. Verify successful retries re-enter the expected durable state: `Queued`,
   `WorkComplete`, `AuditPassed`, or `Merged`.

## Adversarial Merge-Conflict Session

1. Create a conflict between `main` and the work branch in a disposable repo.
2. Run a merge-resolution item with a real CLI agent.
3. Prompt or configure the agent so it attempts to modify content outside the
   allowed conflict hunk buffer.
4. Verify scope-fence or host merge verification rejects the unsafe merge and
   surfaces a merge-conflict-resolution failure.

## Host Shutdown And Preemption Drill

1. Start a long-running real-agent work item in a sandbox provider that supports
   preemption.
2. Send `SIGTERM` to the orchestrator process while the work phase is running.
3. Restart CodeyBox against the same state database.
4. Verify the work item is recovered or requeued according to checkpoint
   availability, and inspect logs for checkpoint metadata or the fallback path.

## Stuck-Agent Drill

1. Configure a disposable project with stuck detection enabled and a short test
   threshold.
2. Run a CLI agent command that remains idle without CPU or network activity.
3. Verify CodeyBox emits stuck-agent diagnostics and either retries the same
   phase or fails after the configured retry cap.
4. Repeat with stuck detection disabled and confirm the probe does not kill the
   phase.

## Multi-Process Dead-Worker Drill

1. Start two CodeyBox processes pointed at the same disposable state database,
   with worker registry and dead-worker reaping enabled.
2. Start a long-running item and identify the process that owns its worker
   registry row.
3. Kill that process abruptly.
4. Let the surviving process run a reaper sweep.
5. Verify the stale worker row is claimed once, the item maps back to the correct
   recoverable state, and duplicate workers do not process the same item.
