# Budget Alerts

Monthly cost-budget alerts give operators a safety net against runaway spend. Set `Budget.MonthlyCostBudgetUsd` on a project; the orchestrator fires webhooks at a configurable warning percentage and auto-pauses the project queue when the hard cap is hit.

---

## Depends on cost-reporting

Budget alerts require the cost-reporting feature (work-item costs stored in `work_item_costs`). If that table doesn't exist yet, the `BudgetAlertService` logs a warning and skips the sweep without crashing. Deploy cost-reporting first, or ensure the database migration has run.

---

## Configuration

```json
{
  "CodeyBox": {
    "BudgetAlerts": {
      "CheckInterval": "00:05:00"
    },
    "Projects": [
      {
        "Id": "my-app",
        "Budget": {
          "MaxItemsPerHour": 10,
          "MaxItemsPerDay": 50,
          "MaxConcurrentForProject": 3,

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

### Fields

| Field | Type | Default | Description |
|---|---|---|---|
| `MonthlyCostBudgetUsd` | `decimal` | `0` | Max USD spend over a rolling 30-day window. **0 = unlimited** (alerts disabled). |
| `CostWarningThresholdPct` | `int` | `80` | Percentage at which `project.budget_warning` fires. Set to `0` to disable the warning event. |
| `CostHardCapPct` | `int` | `100` | Percentage at which `project.budget_exceeded` fires and the project queue auto-pauses. Set to `0` to disable auto-pause (webhook still fires). |
| `AutoResumeOnRecovery` | `bool` | `false` | When `true`, the project queue auto-resumes when spend drops below `CostWarningThresholdPct`. Default is `false` — operator manually unpauses to acknowledge. |
| `BudgetAlerts.CheckInterval` | `TimeSpan` | `00:05:00` | How often the sweep runs. |

**A misconfigured project (`MonthlyCostBudgetUsd=0` with thresholds set) is a no-op**: thresholds only apply when the budget is positive.

---

## How it works

A `BudgetAlertService` background task runs every `CheckInterval` (default 5 minutes).

For each project with `MonthlyCostBudgetUsd > 0`:

1. **Query**: `SUM(estimated_usd)` from `work_item_costs` joined to `work_items` where `project_id = X` and `started_at >= now() - 30d`.
2. **Compute**: `pct = total / budget * 100`.
3. **Edge-trigger**: compare against previous tick's state (in-memory dictionary). Fire events only on **boundary crossings**, not on every tick.

The sweep is cheap — a single indexed aggregation query per project. Expected runtime under 100ms with months of data.

---

## Threshold states

| State | Condition |
|---|---|
| `ok` | `pct < CostWarningThresholdPct` |
| `warning` | `pct >= CostWarningThresholdPct` AND `pct < CostHardCapPct` |
| `exceeded` | `pct >= CostHardCapPct` |

---

## Edge-trigger semantics

Events fire **once when crossing a threshold boundary** and do not repeat while the project remains above the threshold. Events fire again only after the spend drops back to `ok` and rises again.

| Transition | Events fired |
|---|---|
| `ok → warning` | `project.budget_warning` |
| `ok → exceeded` (single tick jump) | `project.budget_warning` + `project.budget_exceeded` + auto-pause |
| `warning → exceeded` | `project.budget_exceeded` + auto-pause |
| `warning → ok` | `project.budget_recovered` (+ auto-resume if `AutoResumeOnRecovery=true`) |
| `exceeded → ok` | `project.budget_recovered` (+ auto-resume if `AutoResumeOnRecovery=true`) |
| `exceeded → warning` | No event (not below the warning threshold) |
| Same state twice | No event |

---

## Restart-replay behaviour

**On orchestrator restart, the first sweep tick re-evaluates every project from scratch** and re-fires whatever events apply at that moment. This means:

- If a project was in `warning` when the orchestrator stopped, the next tick fires `project.budget_warning` again.
- If a project was `exceeded`, the next tick fires `project.budget_exceeded` and calls `PauseProjectAsync` again (which is idempotent).

**Webhook receivers must be idempotent.** The event payload includes `pct` and the threshold band, making de-duplication straightforward: key on `(projectId, thresholdState)`.

---

## Auto-pause behaviour

When spend crosses `CostHardCapPct`:

- `PauseProjectAsync(projectId, reason)` is called with a reason like `"budget-exceeded: $432.18 of $500.00 (86.4%)"`.
- **Per-project pause** blocks new work-item pickup for that project only. In-flight items continue to completion.
- Auto-pause is **idempotent** — successive ticks above the hard cap do not call pause repeatedly.
- The global queue is unaffected; other projects keep running.

When spend drops back below `CostWarningThresholdPct`:

- `project.budget_recovered` fires.
- If `AutoResumeOnRecovery=true`, `ResumeProjectAsync` is called automatically.
- **Default is `false`**: the operator should manually unpause to acknowledge the alert. The queue stays paused even after recovery until manual intervention.

Per-project pause does **not** cancel in-flight work items. It is a pickup gate: the orchestrator will not pick up new queued items for a paused project.

---

## Webhook events

See [webhooks.md](webhooks.md) for full event payload shape.

| Event | Fired when |
|---|---|
| `project.budget_warning` | Spend crosses `CostWarningThresholdPct` going up |
| `project.budget_exceeded` | Spend crosses `CostHardCapPct` going up; auto-pause attached |
| `project.budget_recovered` | Spend crosses back below `CostWarningThresholdPct` going down |

All three events carry the same `details` payload:

```json
{
  "projectId": "my-app",
  "currentSpendUsd": 432.18,
  "budgetUsd": 500.00,
  "pct": 86.4,
  "thresholdPct": 80
}
```

---

## API

| Endpoint | Description |
|---|---|
| `GET /projects/{id}/budget` | Current spend, budget, pct, threshold state, window bounds, project queue state |
| `POST /projects/{id}/queue/pause` | Manual per-project pause (`{"reason":"..."}`) |
| `POST /projects/{id}/queue/resume` | Clear per-project pause |

See [api.md](api.md) for full request/response shapes.

---

## Per-project queue pause vs global pause

| | Global pause | Per-project pause |
|---|---|---|
| Scope | All projects | Single project |
| Endpoint | `POST /queue/pause` | `POST /projects/{id}/queue/pause` |
| Persisted | Yes | Yes |
| Cancels in-flight | No | No |
| Auto-triggered by | — | Budget hard cap |
| Auto-cleared by | — | Recovery + `AutoResumeOnRecovery=true` |

A **globally-paused queue** OR a **project-paused project** both block pickup for that project. The two gates are independent.
