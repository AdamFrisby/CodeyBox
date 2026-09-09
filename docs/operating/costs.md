# Cost tracking

Every agent invocation writes a row to the `work_item_costs` table with token
counts, elapsed time, and an estimated USD figure, so you can answer "what did
this work item actually cost to run?" broken down by phase and by agent.

Read the numbers back with:

```bash
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/workitems/<id>/costs      # one item, per phase and iteration
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/projects/my-app/costs     # project rollup
```

The admin dashboard's Costs tab (`/work-items/{id}/costs`) charts the same data:
cost by phase, a per-phase token table with per-iteration sub-rows, and a
by-agent breakdown.

Cost writes are best-effort. A failure to extract or store a cost is logged at
Warning and never aborts the phase.

## What gets a row

| Phase | Invocation |
|---|---|
| `work` | initial work agent run |
| `rework` | work agent runs after a failed audit |
| `check` | the read-only run of a CheckAndAct item |
| `post-act-recheck` | CheckAndAct follow-up validation after remediation |
| `audit` | each LLM-backed auditor invocation (one row per auditor) |
| `merge` | merge agent run |

Tool-only auditors — formatters, gitleaks, semgrep, the test suite — call no
LLM and are not instrumented.

When no extractor is registered for the agent, the extractor finds no tokens,
or it throws, the pipeline still writes a zero-token row carrying the start and
end timestamps and `raw_metadata_json.source = "elapsed_fallback"`. Elapsed
agent time and invocation counts therefore stay accurate even where token
counts are unavailable.

## How the dollar figure is computed

Costs are **subscription-equivalent pay-per-API prices**: what those token
counts would have cost on a pay-per-token plan, even when the run was covered
by a subscription. That keeps agents comparable to each other and to their own
past runs, and stops a cached token from looking free.

```
estimated_usd = (input_tokens        / 1e6) × input_rate_per_m
              + (cached_input_tokens / 1e6) × cached_rate_per_m
              + (output_tokens       / 1e6) × output_rate_per_m
```

Extractors normalise provider usage before storage: `input_tokens` is the
non-cached bucket billed at the normal input rate, `cached_input_tokens` the
bucket billed at the cached rate.

## Pricing table

CodeyBox ships a per-(agent, model) rate table at
`src/CodeyBox.Api/agent-pricing-defaults.json`, copied next to the API binary
and loaded at startup from `IHostEnvironment.ContentRootPath`, so a new
install reports useful costs without any operator research.

```jsonc
{
  "_meta": {
    "lastUpdated": "YYYY-MM-DD",
    "sources": { "<agentKey>": "<doc URL>" },
    "notes":   { "<agentKey>": "<caveat>" }
  },
  "Rates": {
    "<agentKey>": {
      "<modelId>": {
        "inputPerMillion":       0.0,
        "cachedInputPerMillion": 0.0,
        "outputPerMillion":      0.0
      }
    }
  }
}
```

`<agentKey>` is `AgentKind.Value` (`claude`, `codex`, `gemini`, …).
`<modelId>` is the model identifier the provider's CLI reports in its usage
events.

| Agent | Bundled rates | Why |
|---|---|---|
| `claude`, `codex`, `gemini` | yes | the provider publishes per-token list prices |
| `opencode` | yes, estimated | OpenCode Go is subscription-priced. Bundled rates are a single subscription-equivalent USD/M per model (same value for input, cached, and output), derived from the $12/5h budget, each model's requests-per-5h limit, and the token mix documented at [opencode.ai/docs/go](https://opencode.ai/docs/go). Keys are `opencode-go/<model-id>`. |
| `cursor`, `copilot` | no | flat-rate subscriptions with no published per-token price |

`_meta.notes` carries those caveats in the file, and `GET /agent-pricing`
echoes them, so the reasoning is visible at runtime rather than only here.

### Overrides and lookup order

Operators set the same shape under `CodeyBox:AgentPricing` in
`codeybox-extra.json` — useful to price a subscription-only agent, or to
correct a rate before the next release:

```jsonc
{
  "CodeyBox": {
    "AgentPricing": {
      "Rates": {
        "opencode": {
          "opencode-go/deepseek-v4-pro": {
            "inputPerMillion":       0.0419,
            "cachedInputPerMillion": 0.0419,
            "outputPerMillion":      0.0419
          }
        }
      }
    }
  }
}
```

A rate is resolved in this order, first hit wins:

1. operator entry for that exact (agent, model);
2. bundled entry for that (agent, model);
3. operator `DefaultRates[agent]` — operator-only, the bundled file has none;
4. the rate of `AgentDefaults[agent]`'s model, used when an invocation
   reported tokens but no model id;
5. the provider's `IAgentCostExtractor.DefaultPricing` constants.

The merge re-runs on hot-reload and logs
`AgentPricing loaded: bundled=N, operator-overrides=M, total=… (bundled lastUpdated=…)`
at Information. Negative rates fail startup; a missing agent entry logs a
Warning and falls through to the built-in constants.

`GET /agent-pricing` returns the merged table with `_meta`, the resolved
`sourcePath`, and counts. Its `lastUpdated` is the staleness signal — if it is
months old, so are the rates.

### Refreshing the bundled rates

Edit `agent-pricing-defaults.json`, bump `_meta.lastUpdated` to today's UTC
date, fix `_meta.sources` if a price page moved, and say in the PR which page
you cross-checked. Refresh when a discrepancy actually surfaces — a mismatched
cost report, a new model with no attributed cost — not on a schedule. Provider
price pages are HTML with no machine-readable feed, and mapping model ids to
prices needs a human, so this file is deliberately not auto-updated.

## Per-agent extractors

Each agent kind has an `IAgentCostExtractor` that parses the CLI's stdout and
stderr. Every extractor has a structured primary path and a human-readable
fallback.

**Claude.** Reads the `result` event from the NDJSON stream:

```json
{"type":"result","usage":{"input_tokens":1234,"output_tokens":567,"cache_read_input_tokens":890,"cache_creation_input_tokens":250},"model":"claude-sonnet-4-6"}
```

Anthropic splits prompt input three ways: `input_tokens` (novel content),
`cache_creation_input_tokens` (written to cache this turn, priced above fresh
input), and `cache_read_input_tokens` (cheap). The extractor stores
`input_tokens + cache_creation_input_tokens` in `input_tokens` so both are
billed at the normal rate, and only `cache_read_input_tokens` as
`cached_input_tokens`. Fallback: the `Input: 1,234 tokens, Output: 567 tokens`
footer emitted without `--output-format stream-json`, including its
`N cached tokens` clause when present.

**Codex.** Reads the terminal event from `codex exec --json`:

```json
{"type":"turn.completed","usage":{"input_tokens":10546,"cached_input_tokens":2432,"output_tokens":5}}
```

It also accepts the OpenAI usage shape (`prompt_tokens`,
`completion_tokens`, `prompt_tokens_details.cached_tokens`). In both shapes
the prompt total includes cached tokens, so stored `input_tokens` is
`prompt total − cached`. Fallback: `Prompt tokens: … / Cached input tokens: … /
Completion tokens: …`.

**Gemini.** Reads `{"promptTokenCount":1234,"candidatesTokenCount":567}`;
falls back to `Total tokens: input=1234 output=567`.

**Copilot** emits no token counts, so `CopilotCostExtractor` returns `null` and
Copilot-backed phases get elapsed-time rows only. Any agent without a
registered extractor behaves the same way, and startup logs a Warning naming
those agent kinds.

Two limits worth knowing: counts are per agent run, never per tool call; and
actual subscription billing will not match `estimated_usd`, by design. Rates
are always applied at *current* prices — there is no back-attribution of old
invocations against the rates in force at the time.

## Table shape

```sql
CREATE TABLE IF NOT EXISTS work_item_costs (
    id                  TEXT PRIMARY KEY,
    work_item_id        TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
    phase               TEXT NOT NULL,          -- work | rework | check | post-act-recheck | audit | merge
    iteration           INTEGER,                -- audit iteration, NULL for work/merge
    agent_kind          TEXT NOT NULL,
    model_id            TEXT,                   -- from agent output when reported
    input_tokens        INTEGER NOT NULL,
    cached_input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens       INTEGER NOT NULL,
    estimated_usd       REAL NOT NULL DEFAULT 0,
    started_at          TEXT NOT NULL,          -- ISO-8601 with offset
    ended_at            TEXT NOT NULL,
    raw_metadata_json   TEXT NOT NULL DEFAULT '{}'
);
```

It shares the SQLite file and WAL settings (`journal_mode=WAL`,
`busy_timeout=30000`) with `work_items`, and rows cascade-delete with their
work item.

To turn collection off entirely, drop the `IWorkItemCostStore`,
`AgentCostCalculator`, and `IAgentCostExtractor` registrations from
`Program.cs`; every cost path null-checks the store and skips.

Endpoint payloads are in [`../reference/api.md`](../reference/api.md).
Spend caps and alerting build on this data — see [`budgets.md`](budgets.md).
