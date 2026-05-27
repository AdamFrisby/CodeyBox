# Restart tolerance

CodeyBox runs as a single ASP.NET process owning one port (default `5036`).
Binary swaps (kill old → start new) and operator-triggered restarts produce a
short window — typically 5–30 seconds — during which TCP connections to the API
are refused. We have **explicitly chosen not to implement blue/green port
handover**; the design accepts the brief refused-connection window and relies on
callers to retry.

This document records that decision and lists every external HTTP caller of the
CodeyBox API together with the retry behaviour it must exhibit for the
assumption to hold. Pair this with `docs/operations.md` ("Restarting the
orchestrator safely"), which covers in-flight pipeline recovery — the
complementary half of the restart story.

## The downtime window

| Phase | Duration (typical) | Caller-visible symptom |
|---|---|---|
| Old process draining (`HostOptions.ShutdownTimeout`, default **60 s**) | up to `CodeyBox:Shutdown:GraceSeconds` | Process still bound; new requests accepted until the listener stops, then `connection refused` |
| Port unbound → new process listening | 5–30 s | TCP `connection refused` |
| Warm-up before first request served | < 1 s | First request may take longer (cold path) |

The grace window above is the worker-drain timeout, not a listener delay; in
practice the HTTP listener stops accepting almost immediately on SIGTERM.
Callers should plan for **30 s of `connection refused`** as the worst-case
delivery delay during a restart.

## External HTTP callers

### 1. JobTrack → CodeyBox (Quartz polling job)

* **Direction.** JobTrack runs a Quartz job that periodically polls CodeyBox
  endpoints (`GET /workitems`, `GET /workitems/{id}`, `GET /quota`, etc.) to
  reconcile its local mirror of work-item state.
* **Expected retry behaviour.** Polling cadence is the implicit retry. If a
  poll fails with `connection refused` or HTTP `5xx`, the next scheduled tick
  re-runs the same query — no data is lost because the queries are read-only
  and the next iteration recomputes the delta from the live CodeyBox state.
* **Verification.** See `jobtrack/src/JobTrack.Infrastructure/`
  (`HttpClient` configuration, Quartz job classes). Confirm:
  * `HttpClient` for the CodeyBox base URL has a finite-attempt
    Polly retry policy (recommended: 3–5 attempts, exponential backoff
    starting at ~1 s) or relies on the next Quartz tick as the retry.
  * Polling interval ≤ 5 minutes so a single missed tick is recovered within
    one polling cycle.
  * Mutating calls (PATCH, POST) carry an `Idempotency-Key` header — see
    `src/CodeyBox.Api/IdempotencyMiddleware.cs`. The middleware caches the
    `(method, path, body)` response for 24 h, so a retry that arrives after
    the server has already processed the original request returns the cached
    `2xx` instead of double-applying the mutation.
* **Verdict.** Tolerant of the 30-s downtime window provided the polling
  cadence is shorter than the time to next reconciliation requirement and
  mutating calls send `Idempotency-Key`. Live verification belongs in the
  JobTrack repo; this repo cannot inspect it directly.

### 2. JobTrack → CodeyBox (webhook receiver target)

* **Direction.** This caller is *inverted* — CodeyBox dispatches webhooks to
  JobTrack, not the other way around. Restarting CodeyBox does not break
  JobTrack's webhook receiver; restarting JobTrack would, but CodeyBox's
  outbound dispatcher handles that:
  * `src/CodeyBox.Webhooks/HttpWebhookDispatcher.cs` retries each delivery
    `MaxAttempts` times with exponential back-off
    (`InitialBackoffSeconds`, doubling each attempt).
  * Default config: `MaxAttempts=3`, `InitialBackoffSeconds=1` → attempt
    schedule of `t+0`, `t+1`, `t+3` seconds. Operators dispatching to a
    JobTrack instance that may itself take 30 s to restart should raise
    `MaxAttempts` to ≥ 5 in `appsettings.json` so the last attempt falls
    well outside the restart window.
  * The dispatcher runs on a background channel that survives webhook
    receiver failures; the work-item pipeline is never blocked on delivery.
  * On graceful shutdown the dispatcher drains for up to 30 s — see
    `HttpWebhookDispatcher.DisposeAsync`.
* **Verdict.** Tolerant by construction; no JobTrack-side change required.

### 3. GitHub → CodeyBox (`POST /webhooks/github/release`)

* **Direction.** GitHub delivers release-published webhooks to
  `src/CodeyBox.Api/ChangelogEndpoints.cs:HandleGitHubReleaseAsync`.
* **GitHub retry policy.** GitHub retries undeliverable webhook deliveries up
  to **8 times over ~3.5 days** with exponential back-off whenever the
  endpoint returns a non-2xx response or refuses the connection. A 30-s
  `connection refused` window is comfortably absorbed by the first 1–2
  retries.
* **Handler idempotency under retry.** The handler:
  * Validates the `X-Hub-Signature-256` HMAC over the raw body. Replays of
    the same event carry the same body + signature, so HMAC validation is
    deterministic and replay-safe.
  * Looks up the project by repository URL; returns `202 Accepted` if no
    match (does no work).
  * Returns `202 Accepted` early for non-`published`/`released` actions and
    for projects with changelog automation disabled.
  * Calls `IPullRequestEnumerator.ListMergedBetweenAsync` and the changelog
    generator. These are read-only against GitHub and idempotent.
  * Calls `IWorkItemStore.CreateAsync` with `WorkItemId.New()` and enqueues
    the new item.
* **Known idempotency gap.** The work-item creation step is **not** keyed
  by GitHub `X-GitHub-Delivery` or by the release tag. A duplicate delivery
  that the server fully processed (e.g. crash *after* `CreateAsync` but
  *before* the 202 response reaches GitHub) would create a duplicate
  changelog work item on the next retry. This is **outside the scope of the
  brief-restart window** (where the request never reaches the handler at
  all) but operators should be aware. Tracked as a suggestion in
  `.codeybox/suggestions.json` for follow-up.
* **Verdict.** Tolerant of the 30-s downtime window. A future hardening
  pass should add delivery-ID deduplication to close the broader retry
  idempotency hole.

### 4. Operator scripts / `curl` / dashboard

Human-driven calls are out of scope: an operator who sees `connection
refused` or a 503 page during a restart will simply retry.

### 5. Internal CodeyBox → CodeyBox calls

**None.** The orchestrator, audit, merge and upstream-push paths use
in-process services (`IWorkItemStore`, `IGitHost`, `ISandboxProvider`, etc.)
rather than the HTTP API. No background service or hosted job calls the API
on `127.0.0.1:5036`.

Verification: `grep -r 'HttpClient' src/` returns only callers to GitHub,
Anthropic, OpenAI, Google, OpenCode and the outbound webhook dispatcher;
none target the local API surface.

## Summary

| Caller | Direction | Restart-tolerance? |
|---|---|---|
| JobTrack Quartz polling | inbound | Yes — next tick re-polls; mutations use `Idempotency-Key` |
| JobTrack webhook receiver | outbound (CodeyBox → JobTrack) | Yes — `HttpWebhookDispatcher` retries with back-off |
| GitHub release webhook | inbound | Yes — GitHub retries 8 × over 3.5 d |
| Operator scripts | inbound | Yes — human in the loop |
| CodeyBox self-calls | n/a | None exist |

All inbound callers either retry on transient failures or are polling-shaped,
and outbound deliveries already use the dispatcher's built-in retry. The
30-second refused-connection window is therefore absorbed by existing
behaviour without code changes.

## Verification runbook

Use the steps below to demonstrate the 30-s downtime scenario completes
without manual intervention. This is the operator-side check that the
assumptions above hold in a live deployment.

### Prerequisites

* A running CodeyBox instance bound to a known port (default `5036`).
* JobTrack (or any HTTP poller) configured against that instance.
* `curl`, `ss` (or `lsof`), and a working clock.

### Procedure

1. **Record the steady state.**
   ```bash
   curl -s http://localhost:5036/healthz
   curl -s http://localhost:5036/workitems | jq 'length'
   ```
   Note the count.

2. **Simulate a 30-second outage.** From the host running CodeyBox:
   ```bash
   PID=$(pgrep -f 'CodeyBox.Api')
   kill -SIGTERM "$PID"
   # wait for the port to free
   while ss -ltn 'sport = :5036' | grep -q :5036; do sleep 1; done
   sleep 30
   # restart
   dotnet run --project src/CodeyBox.Api &
   ```

3. **Observe caller behaviour during the window.**
   * `curl http://localhost:5036/healthz` should fail with
     `Connection refused`. The JobTrack Quartz job logs should show a
     transient failure and re-schedule.
   * GitHub deliveries queued during the window should appear in the
     GitHub "Recent deliveries" panel with a non-2xx response and a
     scheduled retry.

4. **Wait for recovery.** Once the port is bound again:
   ```bash
   until curl -sf http://localhost:5036/healthz > /dev/null; do sleep 1; done
   ```

5. **Verify no data loss.**
   * Re-run `curl http://localhost:5036/workitems | jq 'length'` — count
     should be unchanged (no items created or lost by the outage).
   * Confirm JobTrack's next poll succeeds (check JobTrack logs for the
     successful 200 after the failure burst).
   * Confirm any GitHub delivery queued during the window now shows a
     successful retry in the "Recent deliveries" panel.
   * Inspect the new CodeyBox log for the recovery banner described in
     `docs/operations.md` ("Restarting the orchestrator safely") — any
     in-flight work items should be reset to their safe restart point.

### Pass criteria

* No caller produced an unrecoverable error.
* No duplicate work items were created by webhook retries during the
  window (note the known idempotency gap above — duplicates only arise
  if the handler ran to completion *before* the crash, which the
  refused-connection scenario does not trigger).
* JobTrack's mirror converges back to CodeyBox's authoritative state on
  the next reconciliation cycle.

If any of these fail, the assumption that "brief downtime is fine"
breaks down and the caller in question needs explicit retry logic
(via `IHttpClientFactory.AddPolicyHandler` with a Polly retry policy)
before the next planned restart.
