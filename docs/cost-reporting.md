# Cost Reporting

Per-invocation LLM token and estimated cost tracking for the CodeyBox
orchestrator, designed to answer "what did this work item actually cost
to run?" with per-phase and per-agent breakdowns.

## Overview

Every agent invocation (work, rework, audit LLM auditors, merge) attempts
to extract token usage from the agent CLI's output and store a cost row in
the `work_item_costs` SQLite table. When a registered extractor cannot find
tokens, the pipeline still writes a zero-token, $0 row with the invocation
timestamps so elapsed agent time and run count remain visible. Costs are
surfaced via two REST endpoints and an admin dashboard "Costs" tab.

Cost writes are **best-effort**: any failure during extraction or storage
is logged at Warning level and does not abort the pipeline phase.

## Instrumented phases

| Phase | Description |
|---|---|
| `work` | Initial work agent run |
| `rework` | Subsequent work agent runs after an audit failure |
| `audit` | LLM-backed auditors (`needsCredentials == true`); one row per auditor invocation |
| `merge` | Merge agent run |

Tool-only auditors (those that do not call an LLM) are not instrumented
because they do not incur API costs.

## Estimated USD

All cost figures are computed as **subscription-equivalent pay-per-API
costs** — i.e., what the same token counts would cost on a pay-per-API
plan — even when the agent is run under a subscription plan.  This makes
costs comparable across agents and over time, and ensures that a "free"
cached token still shows as a fraction of its list-price value.

The formula is:

```
billable_input  = input_tokens - cached_input_tokens
estimated_usd   = (billable_input / 1_000_000) × input_rate_per_m
                + (cached_input_tokens / 1_000_000) × cached_rate_per_m
                + (output_tokens / 1_000_000) × output_rate_per_m
```

## Database schema

```sql
CREATE TABLE IF NOT EXISTS work_item_costs (
    id                TEXT PRIMARY KEY,
    work_item_id      TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
    phase             TEXT NOT NULL,          -- work | rework | audit | merge
    iteration         INTEGER,                -- audit iteration number, NULL for work/merge
    agent_kind        TEXT NOT NULL,          -- claude | codex | gemini | copilot
    model_id          TEXT,                   -- model string from agent output, if available
    input_tokens      INTEGER NOT NULL,
    cached_input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens     INTEGER NOT NULL,
    estimated_usd     REAL NOT NULL DEFAULT 0,
    started_at        TEXT NOT NULL,          -- ISO-8601 with offset
    ended_at          TEXT NOT NULL,          -- ISO-8601 with offset
    raw_metadata_json TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_costs_work_item
    ON work_item_costs(work_item_id, phase, iteration);
CREATE INDEX IF NOT EXISTS idx_costs_project_time
    ON work_item_costs (work_item_id, started_at);
```

The table lives in the same SQLite file as `work_items` and uses WAL mode
(`journal_mode=WAL`, `busy_timeout=30000`) for safe concurrent access.  Rows
are deleted automatically when the parent work item is deleted (CASCADE).

## Pricing configuration

Rates live in `appsettings.json` under `CodeyBox.AgentPricing`:

```json
"AgentPricing": {
  "Rates": {
    "claude": {
      "claude-opus-4-7":  { "inputPerMillion": 15.0, "cachedInputPerMillion": 1.50, "outputPerMillion": 75.0 },
      "claude-sonnet-4-6": { "inputPerMillion": 3.0,  "cachedInputPerMillion": 0.30, "outputPerMillion": 15.0 }
    },
    "codex": {
      "codex-5.5": { "inputPerMillion": 5.0, "cachedInputPerMillion": 0.50, "outputPerMillion": 25.0 }
    },
    "gemini": {
      "gemini-3.0-pro": { "inputPerMillion": 7.0, "cachedInputPerMillion": 0.70, "outputPerMillion": 21.0 }
    }
  },
  "DefaultRates": {
    "claude": { "inputPerMillion": 3.0, "cachedInputPerMillion": 0.30, "outputPerMillion": 15.0 }
  }
}
```

Rate lookup is three-level: **model-specific** → **agent default** →
**built-in fallback constants** (Claude Opus rates).

Rules:
- Negative rates cause a startup error (`InvalidOperationException`);
  the API will not start.
- A completely missing agent entry logs a Warning at startup but does not
  prevent the API from starting.  Any invocations for that agent will use
  the built-in fallback constants.

## Parser behaviour

Each supported agent kind has a dedicated `IAgentCostExtractor`
implementation that parses the agent CLI's stdout and stderr.

If an extractor is registered for an agent kind but returns `null`, the
pipeline records an elapsed-time fallback row: `input_tokens = 0`,
`cached_input_tokens = 0`, `output_tokens = 0`, `estimated_usd = 0`, and
`raw_metadata_json.source = "extractor_null_elapsed_fallback"`. The row's
`started_at` / `ended_at` still feed `usageTotal.elapsedMs`, the cost API's
`elapsedMs`, and invocation counts. If no extractor is registered at all, no
cost row is written.

### Claude (`ClaudeCostExtractor`)

Primary: scans the agent's NDJSON stdout for a `result` event with a
`usage` field:

```json
{"type":"result","subtype":"success","usage":{"input_tokens":1234,"output_tokens":567,"cache_read_input_tokens":890,"cache_creation_input_tokens":250},"model":"claude-sonnet-4-6"}
```

Anthropic splits prompt input into three buckets: `input_tokens` (truly
novel content), `cache_creation_input_tokens` (tokens written to cache
this turn — priced higher than fresh on the API), and
`cache_read_input_tokens` (read from cache — cheap). The extractor sums
all three into `input_tokens` (the column / `AgentCostSnapshot.InputTokens`)
so the calculator's `billable_input = input_tokens - cached_input_tokens`
formula bills both the fresh portion and `cache_creation` at the fresh
input rate. `cached_input_tokens` is `cache_read_input_tokens` only.

Fallback: scans stderr/stdout for the human-readable footer line emitted
when `--output-format stream-json` is not used:

```
Input: 1,234 tokens, Output: 567 tokens
```

The fallback captures cached token counts when the output includes the "N cached tokens" pattern.

### Codex (`CodexCostExtractor`)

Primary: scans stdout for a JSON object with a `usage` key:

```json
{"usage":{"prompt_tokens":1234,"completion_tokens":567}}
```

Fallback: scans stdout for a human-readable line like:

```
Tokens used: prompt=1234 completion=567
```

### Gemini (`GeminiCostExtractor`)

Primary: scans stdout for a JSON object with `promptTokenCount` and
`candidatesTokenCount`:

```json
{"promptTokenCount":1234,"candidatesTokenCount":567}
```

Fallback: scans stdout for a human-readable line like:

```
Total tokens: input=1234 output=567
```

### Parser limitations

- **No per-tool-call granularity**: token counts are aggregated for the
  full agent run.  There is no breakdown by individual tool call.
- **Subscription plan variance**: actual billing under a subscription plan
  may differ from the computed `estimated_usd` figure.
- **Missing model in fallback mode**: the fallback regex paths do not emit
  a model string; `model_id` will be `null` for those rows, and rate
  lookup falls back to the agent's `DefaultRate`.
- **Copilot**: GitHub Copilot does not emit token counts in its CLI output.
  No extractor is registered for Copilot; cost rows will not be written for
  Copilot-backed work items.
- **Unknown agents**: any agent kind without a registered extractor is
  silently skipped.  A Warning is logged at startup listing agent kinds
  without extractors.

## REST API

See [`api.md`](api.md) for `GET /workitems/{id}/costs` and
`GET /projects/{id}/costs`.

## Admin dashboard

The admin UI exposes a per-work-item costs page at
`/work-items/{id}/costs` (linked from the work item detail page as the
"Costs" button).

The page shows:
- Total estimated cost and token summary (with cache-hit percentage when
  applicable)
- Stacked horizontal bar chart of cost by phase (work/rework/audit/merge)
- Token breakdown table by phase, with per-iteration sub-rows for rework
  and audit phases
- By-agent breakdown table

## Disabling cost collection

Cost collection is enabled by default when the API starts.  To disable it,
remove or comment out the `IWorkItemCostStore`, `AgentCostCalculator`, and
`IReadOnlyDictionary<AgentKind, IAgentCostExtractor>` registrations in
`Program.cs`.  All cost code paths check for null stores and silently skip
collection.
