# Agent Pricing Defaults

CodeyBox ships a bundled per-(agent, model) pricing table — used by the cost
calculator (`AgentCostCalculator`) and downstream budget tracking — so new
installations get useful cost reporting out of the box without operators
having to research and hand-populate provider price tables.

This page covers:

- Where the bundled table lives and how it merges with operator config
- The `GET /agent-pricing` endpoint
- The update workflow (when prices change or new models ship)
- Operator overrides when you can't wait for a release

For the broader cost-reporting framework, see [cost-reporting.md](cost-reporting.md).

## File: `src/CodeyBox.Api/agent-pricing-defaults.json`

Shipped next to the API binary (`CopyToOutputDirectory=PreserveNewest`,
`CopyToPublishDirectory=PreserveNewest`). Loaded at startup from
`IHostEnvironment.ContentRootPath`, which resolves correctly in both
`dotnet run` and published deployments.

Schema:

```jsonc
{
  "_meta": {
    "lastUpdated": "YYYY-MM-DD",
    "sources": { "<agentKey>": "<doc URL>", ... },
    "notes":   { "<agentKey>": "<free-text caveat>", ... }
  },
  "Rates": {
    "<agentKey>": {
      "<modelId>": {
        "inputPerMillion":       <number>,
        "cachedInputPerMillion": <number>,
        "outputPerMillion":      <number>
      },
      ...
    },
    ...
  }
}
```

`<agentKey>` matches `AgentKind.Value` (`claude`, `codex`, `gemini`, etc.).
`<modelId>` matches the model identifier the provider's CLI reports back
in its structured-stream usage events.

### What ships today

| Agent     | Bundled defaults? | Why |
|-----------|-------------------|-----|
| `claude`  | Yes               | Anthropic publishes per-token rates. |
| `codex`   | Yes               | OpenAI publishes per-token rates. |
| `gemini`  | Yes               | Google publishes per-token rates. |
| `opencode` | No (notes)       | opencode-go is a flat-rate subscription ($10/mo). No per-token rate is published for its bundled models. |
| `cursor`  | No (notes)        | Cursor is a flat-rate subscription. No per-token rate is published. |
| `copilot` | No (notes)        | Copilot is a flat-rate subscription. No per-token rate is published. |

Subscription-only agents are deliberately excluded from the bundled table
— any value would be a guess. Operators who want USD-equivalent
attribution for those agents can override per (agent, modelId) (see
below).

The bundled `_meta.notes` block captures these caveats in machine-readable
form; the `/agent-pricing` endpoint echoes them so operators see the same
reasoning at runtime.

## Merge rules

At startup (and on hot-reload of `CodeyBox:AgentPricing`):

1. Load `agent-pricing-defaults.json` from `ContentRoot`.
2. Bind operator config from `CodeyBox:AgentPricing` as before.
3. Merge by (agentKind, modelId). **Operator config wins per key.**
4. `DefaultRates` (agent-level fallback) is operator-only; the bundled
   file does not carry it.
5. Per-provider `IAgentCostExtractor.DefaultPricing` remains the
   final-fallback when neither the merged map nor `DefaultRates` covers a
   model.

The merge produces an `INFO` startup log of the form:

```
AgentPricing loaded: bundled=N, operator-overrides=M, total=N+M-overlap (bundled lastUpdated=YYYY-MM-DD)
```

so you can confirm at a glance how many entries are active and how
stale the bundled file is.

## `GET /agent-pricing`

Returns the merged table plus the bundled `_meta`:

```jsonc
{
  "meta": {
    "lastUpdated": "2026-05-28",
    "sources": { "claude": "https://...", ... },
    "notes":   { "opencode": "subscription-only — no per-token rates published", ... },
    "sourcePath": "<absolute path the bundle was loaded from>",
    "counts": { "bundled": 11, "operatorOverrides": 0, "total": 11, "overlap": 0 }
  },
  "rates":        { "claude": { "claude-opus-4-7": { ... }, ... }, ... },
  "defaultRates": { ... }
}
```

`lastUpdated` is the load-bearing field for staleness — if it's months
old, the bundled rates are probably out of date.

## Update workflow

When a provider changes prices, deprecates a model, or ships a new one:

1. Edit `src/CodeyBox.Api/agent-pricing-defaults.json` — add/remove model
   entries under the relevant `<agentKey>`.
2. Bump `_meta.lastUpdated` to today's UTC date.
3. If a source URL has moved, update `_meta.sources` to match.
4. Open a PR; mention which provider's price page you cross-checked.
5. File a [CodeyBox work item](https://github.com/codeybox/codeybox/issues/new/choose)
   when you need tracking outside the PR (provider-wide refresh, new agent
   kind, or operator-reported cost mismatch).

The bundled file is **not** auto-refreshed from provider docs — they're
HTML pages with no machine-readable feed, and provider model id ↔ pricing
mapping is opinionated enough that we want a human in the loop.

Per the standing rule on vendor API drift, refresh **when a real
discrepancy surfaces** (a cost-report mismatch, a new model that has no
attributed cost, an operator question), not preemptively.

## Operator overrides

Operators who can't wait for a release — or who route under a
subscription-only agent and want a USD-equivalent rate — set the same
shape under their `codeybox-extra.json`:

```jsonc
{
  "CodeyBox": {
    "AgentPricing": {
      "Rates": {
        "opencode": {
          "opencode-go/deepseek-v4-pro": {
            "inputPerMillion":       0.27,
            "cachedInputPerMillion": 0.07,
            "outputPerMillion":      1.10
          }
        }
      }
    }
  }
}
```

Use the same model id the provider CLI reports in usage events (for
opencode-go, `opencode-go/<model-id>` per
[opencode.ai/docs/go](https://opencode.ai/docs/go)).

Operator entries override bundled entries for the same (agentKind,
modelId). Hot-reload re-merges on each edit; the `AuditLog.ConfigReloaded`
trail records the operator-visible diff only (the bundled defaults are
static between deploys).

## Out of scope

- Auto-refresh from provider docs (HTML pages; no feed).
- Cursor / Copilot / opencode-go per-call pricing (subscription-only;
  intentionally absent).
- Historical pricing for back-attributing past invocations against
  rates-at-the-time. The cost calculator always uses *current* rates.
