# Agent Budgets

Operator-configurable, multi-window spend budgets per (agent, model). The
orchestrator turns locally-accounted spend into a **synthetic quota snapshot**
and feeds it to the same router gate that consumes real quota probes — so it can
gate dispatch *proactively*, before a provider's hard limit is hit.

This is the first proactive visibility we have for providers with no quota API
(e.g. opencode-go subscriptions): no HTML scraping, no cookie capture, no
upstream feature request. It generalises to every paid provider — bring your own
budget per (agent, model, window) and it works for the Claude / OpenAI / Gemini
APIs too.

## How it works

1. Every completed agent invocation writes one row to the `agent_usage_events`
   SQLite table (the same site that records `work_item_costs`). Cost is stored
   in **microcents** (`1 cent = 10000 microcents`, `1 USD = 1_000_000 microcents`).
2. `AgentBudgetCalculator` sums `cost_microcents` over each configured window and
   computes `percentRemaining = 100 − (used / limit × 100)`.
3. The per-window figures combine into one synthetic `AgentQuotaSnapshot`:
   `AvailablePct = MIN(percentRemaining across windows)` and
   `ResetAt = earliest window reset`.
4. The router takes `MIN(real probe AvailablePct, budget AvailablePct)` — the
   stronger constraint gates. When the real probe reading is unknown, the budget
   percentage stands alone.

`agent_usage_events` is intentionally **independent** of `work_item_costs` (no
foreign key): deleting a work item must not corrupt budget accounting.

## Configuration

Bind under `CodeyBox:AgentBudgets`. Hot-reloadable — edits take effect on the
next dispatch without a restart.

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
            { "Kind": "Monthly",            "LimitCents": 8000 }
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
| `RetentionDays` | Days of usage events to keep. Default 90. An hourly sweep prunes older rows. `0` disables pruning. The sweep never deletes rows still inside an active Weekly/Monthly/Rolling window even if `RetentionDays` is shorter than the window span, so a configured cap can never be fail-opened by aggressive retention. |
| `Kind: "Rolling"` | Sliding window of `Hours` hours. |
| `Kind: "Weekly"` | Calendar ISO week (Monday 00:00 UTC → next Monday). |
| `Kind: "Monthly"` | Calendar month (1st 00:00 UTC → 1st of next month). |
| `LimitCents` | Spend cap for the window, in cents. A model may have multiple windows; the router uses MIN(remaining) across them. |

Budgets are keyed by a **specific** model id. A member with no model id (default
model) and a window list keyed only by concrete models has no budget gate.

## Visibility — `/quota`

The `/quota` JSON response gains a `budgets` array alongside `probes`:

```json
{
  "probes": [ ... ],
  "budgets": [
    {
      "agent": "opencode",
      "model": "opencode-go/deepseek-v4-pro",
      "windows": [
        { "kind": "Rolling", "hours": 5, "usedCents": 16, "limitCents": 200,  "percentRemaining": 92, "resetAt": "..." },
        { "kind": "Weekly",              "usedCents": 60, "limitCents": 2000, "percentRemaining": 97, "resetAt": "..." },
        { "kind": "Monthly",            "usedCents": 80, "limitCents": 8000, "percentRemaining": 99, "resetAt": "..." }
      ]
    }
  ]
}
```

## Operator notes — read before sizing budgets

- **Only this orchestrator's spend is counted.** The budget tracker sees only
  what *this* orchestrator dispatched. If the same opencode-go subscription is
  also used from a CLI elsewhere, those costs are invisible here and the budget
  will **under-estimate** true usage.
- **Size budgets below the provider's real cap** for safety margin. If
  opencode-go's monthly cap is $100, set the budget to ~$90 — the 10% headroom
  absorbs the gap between our accounting and theirs, so we gate before the
  provider hard-limits.
- **Bootstrap undercount.** When the feature first turns on the
  `agent_usage_events` table is empty, so the first window cycle reports near-100%
  remaining and will keep reporting low usage until enough history accumulates.
  Expect the first rolling/weekly/monthly cycle to under-count.
- **A configured budget fails *closed*.** If the usage store cannot be reached or
  a window query fails, the affected window is treated as fully exhausted (0%
  remaining) so the cap keeps gating dispatch instead of silently disabling
  protection during an outage. Both the dispatch gate and the `/quota` visibility
  summary recompute against the live store on every call — neither serves a cached
  snapshot — so an outage that begins after a healthy read still reads as exhausted
  immediately, and the gate recovers on the next call once accounting comes back.
  On `/quota` a degraded budget shows `percentRemaining: 0` and a `budgetsError`
  flag surfaces a summarisation failure.
- **A non-positive `LimitCents` fails closed too.** A window with `LimitCents`
  ≤ 0 means a zero budget; it reports 0% remaining (blocking dispatch) rather
  than disabling the gate, so a typo surfaces as an over-tight cap, not an open
  door.
- **A `Rolling` window with missing or non-positive `Hours` fails closed.** It
  reports 0% remaining rather than silently collapsing to a 1-hour window (which
  would narrow the cap and overstate remaining budget), so a misconfiguration
  (`Hours: 0`, omitted `Hours`) blocks dispatch until the operator corrects it —
  consistent with the fail-closed handling of unknown window kinds and zero
  limits.

## Out of scope

- Real-time / in-flight cost streaming (an invocation counts only after it
  completes).
- Cross-orchestrator coordination (one orchestrator, one DB).
- Auto-tuning budgets from observed historical spend (manual config for now).
