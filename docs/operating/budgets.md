# Spend limits

Two independent caps sit on top of [cost tracking](costs.md), protecting
different things:

| | Agent budgets | Project budget alerts |
|---|---|---|
| Keyed by | (agent, model) | project |
| Windows | rolling hours, ISO week, calendar month | rolling 30 days |
| Effect at the cap | the router stops dispatching to that agent/model | the project queue auto-pauses |
| Config | `CodeyBox:AgentBudgets` | `Project.Budget.MonthlyCostBudgetUsd` |

Use agent budgets to stay inside a provider's plan. Use project budget alerts
to stop one project burning the month's money.

Both read the spend recorded by cost tracking, so neither works until cost rows
are being written. `BudgetAlertService` logs a warning and skips its sweep when
the `work_item_costs` table is absent.

## Agent budgets

The orchestrator sums locally-accounted spend per (agent, model) window, turns
it into a synthetic quota snapshot, and feeds that to the same router gate that
consumes real quota probes. Dispatch is gated *before* the provider's hard limit
is reached.

That matters most for providers with no quota API at all — an opencode-go
subscription, say — where the alternative is HTML scraping or nothing. It works
equally for the Claude, OpenAI, and Gemini APIs: give it a budget per (agent,
model, window) and it gates.

How a dispatch decision is reached:

1. Each completed agent invocation writes a row to `agent_usage_events` with
   agent, model, work item, phase, timestamps, elapsed ms, and cost in
   `cost_microcents` — **USD × 1,000,000**, despite the column name, so divide
   by a million for dollars. Invocations whose tokens could not be extracted
   still write a zero-cost row, keeping run counts honest.
2. `AgentBudgetCalculator` sums `cost_microcents` per window and computes
   `percentRemaining = 100 − (used / limit × 100)`.
3. Windows collapse into one `AgentQuotaSnapshot`: `AvailablePct` is the
   minimum across windows, `ResetAt` the earliest window reset.
4. The router applies `MIN(real probe AvailablePct, budget AvailablePct)`. When
   the probe reading is unknown, the budget figure stands alone.

`agent_usage_events` has no foreign key to `work_item_costs` on purpose:
deleting a work item must not rewrite budget history.

### Configuration

Bind under `CodeyBox:AgentBudgets`; edits hot-reload and apply on the next
dispatch.

```json
"AgentBudgets": {
  "RetentionDays": 90,
  "Members": {
    "opencode": {
      "Models": {
        "opencode-go/deepseek-v4-pro": {
          "Windows": [
            { "Kind": "Rolling", "Hours": 5, "LimitCents": 200 },
            { "Kind": "Weekly",              "LimitCents": 2000 },
            { "Kind": "Monthly",             "LimitCents": 8000 }
          ]
        }
      }
    },
    "claude": {
      "Models": {
        "claude-opus-4-7": {
          "Windows": [ { "Kind": "Monthly", "LimitCents": 50000 } ]
        }
      }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `RetentionDays` | Days of usage events kept; default 90, `0` disables pruning. An hourly sweep prunes older rows but never one still inside an active window, so short retention cannot fail a cap open. |
| `Kind: "Rolling"` | Sliding window of `Hours` hours. |
| `Kind: "Weekly"` | ISO calendar week, Monday 00:00 UTC. |
| `Kind: "Monthly"` | Calendar month, 1st 00:00 UTC. |
| `LimitCents` | Cap for that window, in cents. Multiple windows per model are combined with MIN(remaining). |

Budgets key on a **specific** model id. A class member that routes to its
default model without naming one is not gated by a model-keyed budget.

### Sizing them

- **Only this orchestrator's spend counts.** If the same subscription is also
  driven from a laptop CLI, that spend is invisible here and the budget
  under-estimates real usage.
- **Leave headroom.** Against a $100/month provider cap, set ~$90; the gap
  absorbs the difference between our accounting and theirs.
- **Expect a soft first cycle.** `agent_usage_events` starts empty, so the
  first rolling, weekly, and monthly cycles under-count until history builds.

Every failure mode fails **closed**, on the principle that a broken cap should
block spend rather than silently permit it:

| Condition | Result |
|---|---|
| Usage store unreachable, or a window query fails | that window reads 0% remaining; `/quota` shows `percentRemaining: 0` and sets `budgetsError` |
| `LimitCents` ≤ 0 | 0% remaining — a typo reads as an over-tight cap, not an open door |
| `Rolling` with missing or non-positive `Hours` | 0% remaining, rather than collapsing to an hour and overstating headroom |
| Unknown window kind | 0% remaining |

Both the dispatch gate and the `/quota` summary recompute against the live
store on every call — no cached snapshot — so an outage starting after a
healthy read reads as exhausted immediately, and the gate recovers on the first
call after accounting comes back.

### Visibility

`/quota` returns a `budgets` array beside `probes`:

```json
{
  "probes": [ ],
  "budgets": [
    {
      "agent": "opencode",
      "model": "opencode-go/deepseek-v4-pro",
      "windows": [
        { "kind": "Rolling", "hours": 5, "usedCents": 16, "limitCents": 200,  "percentRemaining": 92, "resetAt": "..." },
        { "kind": "Weekly",              "usedCents": 60, "limitCents": 2000, "percentRemaining": 97, "resetAt": "..." },
        { "kind": "Monthly",             "usedCents": 80, "limitCents": 8000, "percentRemaining": 99, "resetAt": "..." }
      ]
    }
  ]
}
```

An invocation counts only once it completes — there is no in-flight cost
streaming — and accounting is per orchestrator, with no cross-instance
coordination.

## Project budget alerts

Set a monthly ceiling per project. The orchestrator fires a webhook at the
warning percentage and pauses that project's queue at the hard cap.

```json
{
  "CodeyBox": {
    "BudgetAlerts": { "CheckInterval": "00:05:00" },
    "Projects": [
      {
        "Id": "my-app",
        "Budget": {
          "MonthlyCostBudgetUsd": 500.00,
          "CostWarningThresholdPct": 80,
          "CostHardCapPct": 100,
          "AutoResumeOnRecovery": false
        }
      }
    ]
  }
}
```

| Field | Default | Meaning |
|---|---|---|
| `MonthlyCostBudgetUsd` | `0` | Ceiling over a rolling 30-day window. `0` disables alerts entirely — thresholds set alongside it do nothing. |
| `CostWarningThresholdPct` | `80` | Percentage that fires `project.budget_warning`. `0` disables the warning. |
| `CostHardCapPct` | `100` | Percentage that fires `project.budget_exceeded` and auto-pauses. `0` keeps the webhook but skips the pause. |
| `AutoResumeOnRecovery` | `false` | Resume the queue automatically once spend falls back below the warning threshold. Off by default so an operator acknowledges the alert. |
| `BudgetAlerts.CheckInterval` | `00:05:00` | Sweep period. |

Each tick, for every project with a positive budget, `BudgetAlertService` sums
`estimated_usd` from `work_item_costs` joined to `work_items` over the last 30
days, computes `pct = total / budget × 100`, and compares it with the band from
the previous tick. One indexed aggregation per project; a sweep stays well under
100 ms even with months of history.

Bands are `ok` (below warning), `warning` (at or above warning, below hard cap)
and `exceeded` (at or above hard cap). Events fire on **boundary crossings**
only:

| Transition | Result |
|---|---|
| `ok → warning` | `project.budget_warning` |
| `ok → exceeded` in one tick | `project.budget_warning`, then `project.budget_exceeded`, then pause |
| `warning → exceeded` | `project.budget_exceeded` + pause |
| `warning → ok` or `exceeded → ok` | `project.budget_recovered` (+ resume if `AutoResumeOnRecovery`) |
| `exceeded → warning` | nothing — still above the warning threshold |

All three events carry the same details payload:

```json
{
  "projectId": "my-app",
  "currentSpendUsd": 432.18,
  "budgetUsd": 500.00,
  "pct": 86.4,
  "thresholdPct": 80
}
```

Band state lives in memory, so **the first sweep after a restart re-evaluates
from scratch and re-fires whatever applies** — a project left in `warning` gets
another `project.budget_warning`, one left in `exceeded` gets another
`project.budget_exceeded` and another (idempotent) pause. Webhook receivers must
de-duplicate; keying on `(projectId, thresholdState)` is enough.

### What pausing does

`PauseProjectAsync` is called with a reason such as
`"budget-exceeded: $432.18 of $500.00 (86.4%)"`. The pause is a **pickup gate**:
no new items start for that project, in-flight items run to completion, and
other projects are untouched. Repeated ticks above the cap do not re-pause.

Per-project pause and the global `POST /queue/pause` are independent gates —
either one blocks pickup — and neither cancels in-flight work. Both persist
across restarts.

| Endpoint | Purpose |
|---|---|
| `GET /projects/{id}/budget` | spend, budget, pct, band, window bounds, queue state |
| `POST /projects/{id}/queue/pause` | manual pause, `{"reason":"..."}` |
| `POST /projects/{id}/queue/resume` | clear the pause |

Payloads are in [`../reference/api.md`](../reference/api.md); event shapes in
[`../reference/webhooks.md`](../reference/webhooks.md).
