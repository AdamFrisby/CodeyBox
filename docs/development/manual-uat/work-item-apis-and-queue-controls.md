# Work Item APIs And Queue Controls Manual UAT

Run these procedures against a disposable CodeyBox deployment with authentication enabled and a non-production repository. Preserve request/response payloads, dashboard screenshots, audit log excerpts, and affected work item IDs with the UAT run record.

## API Smoke And Admin UI

1. Create a work item through the API with title, prompt, project, base branch, work branch, agent or agent class, external ID, and at least one dependency.
2. Open the admin work item list and detail views.
3. Patch the queued item title or prompt through the API and refresh the UI.
4. Cancel the item, then uncancel a parent-cascaded dependent item if one exists.
5. Retry one terminal failed item from `work`, `audit`, `merge`, and `upstream` where the state and repository make that retry legal.

Pass criteria: API responses and dashboard fields agree on state, dependency status, replay links, quota fields, release fields, agent routing, and failure/retry metadata.

## Diagnostics Drill-Down

1. Run a real work item through at least one audit iteration.
2. Open the work item detail page and inspect diff, timeline, stdout tail, audit reports, timings, costs, and agent stream artifacts.
3. Request the same diagnostics through the API and compare the displayed values with the JSON or text responses.
4. Delete or move one noncritical stream artifact in the disposable environment and request it again.

Pass criteria: each diagnostic panel loads independently, missing artifacts return a bounded `404` or empty response, and raw auditor output is available only for the requested iteration and auditor.

## Batch Queue Workflow From External IDs

1. Queue a parent item with an external ID that matches the operator's ticket system.
2. Queue multiple dependent child items using that external ID rather than the UUID.
3. Reorder the queued set through the admin UI.
4. Complete or cancel the parent and verify dependent state changes or pickup eligibility.

Pass criteria: external IDs resolve within the intended project only, queue order remains stable after refresh, and parent cancellation cascades only to queued dependents.

## Cross-Agent Replay Review

1. Complete or fail a source work item.
2. Create at least two replays with different agent or agent-class overrides.
3. Open the comparison page for the source and replays.
4. Cancel the source after at least one replay exists and refresh the comparison view.

Pass criteria: replay items keep the source prompt and base branch, show their override routing, continue independently, and display understandable orphaning behavior if the source link is cleared.

## Operator Question Flow

1. Configure a disposable project with agent questions enabled.
2. Run an agent task that emits a valid `<codeybox-question>` block.
3. Answer one question and dismiss another through the dashboard.
4. Repeat with a malformed question block and with questions disabled for the project.

Pass criteria: valid questions park the item in `NeedsOperatorInput`, answer and dismiss actions resume the item when all questions are resolved, and malformed or disabled-question output does not crash the run.

## Suggestions Triage

1. Run a work item whose agent writes `.codeybox/suggestions.json` with one root-array suggestion and one wrapped `suggestions` object in separate runs.
2. Open the suggestions page and filter by project, category, and severity.
3. Promote one suggestion with extra instructions and inspect the created work item's prompt.
4. Dismiss another suggestion with a reason.

Pass criteria: valid suggestions persist without automatically queueing work, promoted prompts keep advisory content fenced and escaped, and dismissed suggestions disappear from the default open list.

## Queue Controls During Active Workload

1. Start one long-running item and leave another item queued.
2. Pause the global queue and confirm in-flight work continues while no new item starts.
3. Resume the global queue and confirm pending work starts.
4. Repeat with only one project paused while another project remains runnable.
5. Inspect `/queue/status`, `/workers/status`, and the dashboard queue controls during each state.

Pass criteria: global and project pause state is persisted and visible, resume is a safe no-op when already running, and no queued item is lost when a pause races with pickup.
