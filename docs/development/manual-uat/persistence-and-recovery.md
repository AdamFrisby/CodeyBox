# Persistence And Recovery Manual UAT

These procedures cover the spec-only checks from `docs/uat/00-plan.md` for the
Persistence And Recovery section. Run them against disposable state databases and
repositories only.

## Real State Database Upgrade

1. Stop CodeyBox and make a backup copy of an older real `state.db`.
2. Start the current CodeyBox build with `StateDatabasePath` pointed at the copy.
3. Verify startup succeeds without SQLite migration errors.
4. Inspect the copied database and confirm recent additive columns exist on
   `work_items`, including `failure_kind`, `quota_reset_at`, `release_id`,
   `recovery_attempts`, `preempted_at`, and `preempt_checkpoint`.
5. Queue and complete one small work item against the upgraded database.
6. Verify existing historical work items still load in the API and the new work
   item records questions, suggestions, timings, audit reports, costs, and stream
   summaries when those features are enabled.

## Kill During Each Pipeline Phase

1. Configure a disposable project with a real repository, real sandbox provider,
   and the `uat` auditor profile.
2. For each phase, start a work item and wait until the database shows the target
   state: `Working`, `Auditing`, `Reworking`, `Merging`, and `UpstreamPushing`.
3. Send `kill -9` to the orchestrator process.
4. Restart CodeyBox with the same `state.db`.
5. Verify startup replay maps the item to the expected durable state:
   `Working` without a preempt checkpoint becomes `Failed`, `Auditing` and
   `Reworking` become `WorkComplete`, `Merging` becomes `AuditPassed`, and
   `UpstreamPushing` becomes `Merged`.
6. Repeat the interrupted recovery until `MaxRecoveryAttempts` is exceeded and
   verify the item reaches `AbandonedAfterRecoveryAttempts`.
