# Quota And Routing

## Agent quota probes - Reads per-agent and per-model availability snapshots

1. Configure live OAuth credentials for Claude, Codex, and Gemini subscription probes.
   - Expected: application starts without logging OAuth tokens or account identifiers.
2. Call `GET /quota`.
   - Expected: response includes one entry per registered live probe with `latestSnapshot`, `wouldAllow`, `defaultModelWouldAllow`, and any `perModelWouldAllow` values.
3. Compare each reported availability value against the corresponding vendor account usage page or CLI quota status.
   - Expected: overall and model-specific availability are directionally consistent with the vendor view.
4. Temporarily remove one probe credential and call `GET /quota` again.
   - Expected: that probe reports unknown availability with a diagnostic note, and other probes still return snapshots.

## Agent class router - Chooses an agent/model using score, quota, and observed-failure gates

1. Configure the production operator-tuned agent class catalog with at least two subscription members and one PayPerApi fallback member.
   - Expected: startup validation accepts the catalog and logs active time-of-day score modifiers.
2. Queue a work item with an `AgentClassId` whose highest-ranked subscription member has live quota available.
   - Expected: the item is picked up by the highest viable member after `MinModelScore`, time-of-day, and quota gates.
3. Queue a work item for a project that sets `DefaultAgentClass` and omits per-item `AgentClassId`.
   - Expected: the item inherits the project default class and routes through the same gates.
4. Repeat while the preferred subscription member is under real quota pressure.
   - Expected: routing skips exhausted subscription members, waits if all subscriptions are exhausted, or selects the PayPerApi fallback when it is the highest viable remaining member.
5. Queue an item with a deliberately unknown `AgentClassId`.
   - Expected: router logs the unknown class diagnostic and falls through to direct agent selection.

## Quota failure classification and auto-retry - Persists quota failures and retries after reset

1. Enable quota auto-retry with a nonzero `ClockDriftSafetyMargin`.
   - Expected: startup logs that quota auto-retry is enabled and re-arms any persisted timers.
2. Trigger a real vendor CLI quota exhaustion event for a test work item.
   - Expected: item transitions to `Failed`, `FailureKind` is `quota`, and `QuotaResetAt` plus `NextQuotaRetryAt` are populated.
3. Leave the queue and project running until after the vendor reset time plus safety margin.
   - Expected: scheduler retries the item through the shared retrier and emits `work_item.auto_retry`.
4. Pause the global queue or the item project before the reset timer fires.
   - Expected: scheduler skips retry without changing item state or incrementing `QuotaRetryAttempts`.
5. Set the item at `MaxAutoRetriesPerWorkItem` and run the retry sweep.
   - Expected: scheduler leaves the item failed and does not enqueue another attempt.
