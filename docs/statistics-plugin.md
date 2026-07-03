# Statistics Plugin

The statistics plugin (`codeybox.statistics`) is a first-class plug-in built
on the [Plugin SDK](plugins.md). It ships with one metric stream — a per-agent
**quota snapshot time-series** — and is structured so further streams
(throughput, audit pass/fail rates, agent fallback frequency, cost-over-time)
can plug into the same `IMetricSampler` extension point without changing the
host.

The plugin replaces the standalone external `codey-quota-logger.sh` poller
historically used to track subscription quota burn-down. CodeyBox already
persists the *consumption* side via `agent_usage_events` and
`work_item_costs`; the statistics plugin closes the gap by also persisting
the *availability* side (the data the `/quota` endpoint exposes, sampled
on a cadence) so operators can correlate tokens-consumed against
quota-burned.

---

## Contents

1. [How it works](#how-it-works)
2. [Enabling the plugin](#enabling-the-plugin)
3. [Configuration reference](#configuration-reference)
4. [Storage layout](#storage-layout)
5. [REST: `GET /quota/history`](#rest-get-quotahistory)
6. [REST: `GET /quota/reset-credits`](#rest-get-quotareset-credits)
7. [REST: `GET /quota/reset-advice`](#rest-get-quotareset-advice)
8. [Adding further metric streams](#adding-further-metric-streams)
9. [Migrating off the standalone poller](#migrating-off-the-standalone-poller)

---

## How it works

```
Orchestrator process
        │
        │ (one BackgroundService per host)
        ▼
MetricSamplerHost ─┬─► IMetricSampler "quota"   ◄── StatisticsQuotaPlugin
                   │       │
                   │       │ (reuses the host's IAgentQuotaProbe set —
                   │       │  no self-HTTP against /quota)
                   │       ▼
                   │   QuotaTimeSeriesSqliteStore (codeybox-stats.db)
                   │       │
                   │       └─► quota_sample (normalised) + quota_raw (JSON)
                   │
                   └─► (future) IMetricSampler "throughput" / "audit-pass-rate" / …

API host: GET /quota/history → resolves IQuotaTimeSeriesStore from DI
                                → returns rows / raw JSON
                                → 503 when no implementation is registered
```

The sampler does NOT self-HTTP against the orchestrator's own `/quota`
endpoint. It takes `IEnumerable<IAgentQuotaProbe>` and `IAgentQuotaGate` as
DI dependencies and invokes them directly — the snapshot data is
authoritative (probe → snapshot → row) and the plugin remains usable when
the API is degraded.

---

## Enabling the plugin

The plugin ships as a separate assembly (`CodeyBox.StatisticsPlugin.dll`).
Like every other plugin it must be allowlisted before the loader registers
it. In `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.statistics"]
    }
  }
}
```

After enabling, restart the orchestrator. The plugin's quota sampler starts
on its first interval and writes both normalised and raw rows on every
tick.

---

## Configuration reference

Bind from `CodeyBox:Plugins:codeybox.statistics` in `appsettings.json`. All
keys are hot-reloadable — changes to the config file are observed via the
plugin's own change-token registration and take effect on the next tick /
next prune cycle.

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.statistics": {
        "QuotaSamplerEnabled": true,
        "QuotaSamplerIntervalSeconds": 900,
        "RetentionHours": 720,
        "DatabasePath": "/var/lib/codeybox/codeybox-stats.db",
        "MaxQueryRows": 50000,
        "ResetCreditExpiry": {
          "Agent": "codex",
          "ExpiryPeriodDays": 30,
          "SafetyBufferHours": 24,
          "LookbackDays": 60,
          "Seeds": [
            { "EstimatedExpiresAt": "2026-07-16T00:00:00Z", "Label": "credit A — burn within 2 weeks" },
            { "EstimatedExpiresAt": "2026-08-01T00:00:00Z", "Label": "credit B — just arrived (~30d)" }
          ]
        }
      }
    }
  }
}
```

| Key | Type | Default | Description |
|---|---|---|---|
| `QuotaSamplerEnabled` | `bool` | `true` | Master switch. When false the loop keeps running but never invokes probes. |
| `QuotaSamplerIntervalSeconds` | `int` | `900` (15 min) | Delay between snapshots. Floor 10s. Matches the cadence of the stopgap external poller. |
| `RetentionHours` | `int` | `720` (30 days) | Rows older than this are pruned hourly. Long enough to span two weekly resets. Floor 1h. Set to 0 to disable pruning. |
| `DatabasePath` | `string` | unset → `codeybox-stats.db` next to `CodeyBox:StateDatabasePath` | Absolute path to the stats SQLite file. |
| `MaxQueryRows` | `int` | `50000` | Hard ceiling on rows returned by a single `QueryAsync` call (clamps the REST `limit` parameter). |
| `ResetCreditExpiry:Agent` | `string` | `codex` | Agent whose banked-reset-credit count series is tracked. Codex is the only provider that exposes reset credits today. |
| `ResetCreditExpiry:ExpiryPeriodDays` | `double` | `30` | Provider-published credit lifetime (Codex publishes 30 days). Floor 1h. |
| `ResetCreditExpiry:SafetyBufferHours` | `double` | `24` | Margin subtracted from a credit's raw expiry to produce its advised spend-by moment. |
| `ResetCreditExpiry:LookbackDays` | `double` | `60` | How far back the count series is read when a query supplies no `from`. Bounded in practice by `RetentionHours`. |
| `ResetCreditExpiry:Seeds` | array | `[]` | Operator estimates for pre-observation credits — see [Reset-credit expiry](#rest-get-quotareset-credits). Entries without a parseable `EstimatedExpiresAt` are dropped. |
| `ResetOptimality:Agents` | array | `["codex"]` | Agents the reset advisor covers. A present-but-empty array means "advise for none". Codex today; add claude later. |
| `ResetOptimality:PlanEndsAt` | RFC 3339 | unset | When the subscription plan ends. Caps the decision deadline — quota past this is worthless. |
| `ResetOptimality:CadenceAnchor` | RFC 3339 | unset | A known instant on the natural-reset schedule (e.g. a recent Monday 06:00 UTC boundary). **Unset disables spend advice.** Phase-refined from the logger when `RefineAnchorFromLogger` is on. |
| `ResetOptimality:CadencePeriodDays` | `double` | `7` | Natural-reset period. Codex resets weekly. Floor 1h. |
| `ResetOptimality:DustThresholdPct` | `double` | `1` | Usable-quota % at/below which the current window counts as spent (burn-first satisfied). Clamped to 0–100. |
| `ResetOptimality:TimeToleranceHours` | `double` | `6` | Slack around the deadline-vs-natural-reset comparison; the natural reset must land later than the deadline by more than this before a spend is advised. |
| `ResetOptimality:RefineAnchorFromLogger` | `bool` | `true` | Phase-refine `CadenceAnchor` from observed weekly resets in the logged series (self-calibration). When false the configured anchor is used verbatim. |

---

## Storage layout

The plugin owns its own SQLite file — independent of `state.db` so the
stats workload never competes for the orchestrator's hot-path write gate.

**`quota_sample`** — one row per snapshot expansion:

| Column | Type | Notes |
|---|---|---|
| `snapshot_id` | TEXT | UUID linking back to `quota_raw` for fidelity. |
| `sampled_at` | TEXT | ISO-8601 UTC. |
| `agent` | TEXT | Probe `Kind` value (e.g. `claude`). |
| `model_id` | TEXT NULL | Set when the row is a per-model expansion. |
| `overall_pct` | REAL | Either the snapshot's aggregated `AvailablePct`, or the per-model `AvailablePct` for model rows. |
| `would_allow` | INTEGER | Result of `IAgentQuotaGate.Allows` at sample time. |
| `notes` | TEXT NULL | Mirrors `AgentQuotaSnapshot.Notes`. |
| `window_name` | TEXT NULL | NULL for the aggregated row; provider window name (e.g. `five_hour`) otherwise. |
| `window_pct` | REAL NULL | Per-window `AvailablePct`. |
| `window_reset_at` | TEXT NULL | When this window resets. |
| `is_known` | INTEGER | Mirrors `AgentQuotaSnapshot.IsKnown`. |
| `unknown_reason` | TEXT NULL | `QuotaUnknownReason` when the snapshot was not a real reading. |

**`quota_raw`** — one row per probe call, keyed by the same `snapshot_id`,
carrying the full `AgentQuotaSnapshot` serialised as JSON. Useful for
back-fill, debugging, or fields the normalised schema does not yet expose.

---

## REST: `GET /quota/history`

Returns the normalised quota time-series. The endpoint resolves
`IQuotaTimeSeriesStore` from DI; when the plugin is not loaded it returns
`503 Service Unavailable` with a problem body — the route exists, the data
backend just isn't online.

### Query parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `agent` | string | (none) | Filter by agent kind (case-insensitive). |
| `window` | string | (none) | Provider window name (`five_hour`, `seven_day`, …). Special value `overall` matches rows whose `window_name` is NULL (the aggregated reading). |
| `model` | string | (none) | Per-model filter (case-insensitive). |
| `from` | RFC 3339 | (none) | Lower bound on `sampled_at` (inclusive). |
| `to` | RFC 3339 | (none) | Upper bound on `sampled_at` (exclusive). |
| `limit` | int | 1000 | Max rows. Clamped to `MaxQueryRows` (50 000 by default). |
| `raw` | bool | false | When true, returns rows from `quota_raw` with the raw `AgentQuotaSnapshot` JSON instead of the normalised columns. |

### Example: a day's overall availability for Claude

```sh
curl 'http://orchestrator/quota/history?agent=claude&window=overall&from=2026-06-13T00:00:00Z&to=2026-06-14T00:00:00Z'
```

```json
{
  "count": 96,
  "rows": [
    {
      "sampledAt": "2026-06-13T00:00:00+00:00",
      "agent": "claude",
      "modelId": null,
      "overallPct": 88,
      "wouldAllow": true,
      "notes": null,
      "windowName": null,
      "windowPct": null,
      "windowResetAt": null,
      "isKnown": true,
      "unknownReason": null
    }
  ]
}
```

### Example: raw snapshots

```sh
curl 'http://orchestrator/quota/history?agent=claude&raw=true&limit=10'
```

Each row carries the same `AgentQuotaSnapshot` JSON the `/quota` endpoint
produces, anchored to its sample time.

---

## REST: `GET /stats/capacity`

Returns subscription capacity estimates — for each (agent, window) pair, how
many tokens / requests one percent of the window holds and the implied
capacity of a full 100 % window, derived by joining the captured quota
time-series against `agent_usage_events` consumption.

Algorithm: for each pair of consecutive quota snapshots, the calculator
reads the percent drop in the chosen window and sums the
`agent_usage_events` whose `time_utc` falls in the interval. Intervals
where the percent went UP (window reset) are flagged and excluded from
the burn-rate average. Intervals with a percent drop below the noise
floor (default 0.25 %) are also excluded so a 0.01 % drop with a stray
million-token call does not pollute the average.

The endpoint resolves `ICapacityCalculator` from DI; when the plugin is
not loaded it returns `503 Service Unavailable` with a problem body.

### Query parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `agent` | string | (none) | Filter by agent kind (case-insensitive). |
| `window` | string | (none) | Provider window name (`five_hour`, `seven_day`, …). |
| `model` | string | (none) | When set, narrows the entry to a single per-model quota bucket; when omitted the entry aggregates the agent's overall reading across all models. |
| `from` | RFC 3339 | now − 7 d | Lower bound on `sampled_at` (inclusive). |
| `to` | RFC 3339 | now | Upper bound on `sampled_at` (exclusive). Max horizon 60 days. |
| `minDeltaPct` | float | 0.25 | Minimum percent drop between consecutive samples for an interval to count toward the burn-rate average. |
| `includeIntervals` | bool | true | Carry the per-interval burn-rate series in the response (set false to halve payload size). |

### Example: seven-day capacity for Claude

```sh
curl 'http://orchestrator/stats/capacity?agent=claude&window=seven_day'
```

```json
{
  "generatedAt": "2026-06-14T15:00:00+00:00",
  "fromUtc":     "2026-06-07T15:00:00+00:00",
  "toUtc":       "2026-06-14T15:00:00+00:00",
  "entries": [
    {
      "agent": "claude",
      "windowName": "seven_day",
      "modelId": null,
      "sampleIntervals": 96,
      "totalDeltaPct": 41.2,
      "totalInputTokens": 41200000,
      "totalCachedInputTokens": 18800000,
      "totalOutputTokens": 12400000,
      "totalRequests": 188,
      "inputTokensPerPercent": 1000000,
      "estimatedFullWindowInputTokens": 100000000,
      "requestsPerPercent": 4.56,
      "estimatedFullWindowRequests": 456,
      "currentPct": 58.8,
      "resetAt": "2026-06-21T15:00:00+00:00",
      "estimatedExhaustionAt": "2026-06-15T03:24:00+00:00",
      "confidence": "High",
      "notes": [
        "Cached input tokens are billed at a different rate than fresh input — both buckets are reported separately so totals stay meaningful."
      ],
      "intervals": [ /* one per consecutive-sample pair, with from/to/deltaPct/tokens/requests/isWindowReset */ ]
    }
  ]
}
```

### Caveats

- **Cached vs billable input.** The aggregator surfaces cache-read input
  tokens separately from fresh-input tokens (`cachedInputTokensPerPercent`
  vs `inputTokensPerPercent`). The two are billed at different rates and
  conflating them would understate the value of cache hits.
- **Rolling windows never reset.** Codex `5h-rolling` and similar rolling
  windows oscillate continuously; the burn-rate represents amortised
  consumption across the rolling horizon, not a discrete fill-and-empty
  cycle. The entry includes a note flagging this case.
- **Estimates improve with more samples.** Confidence is bucketed by
  surviving-interval count: `Low` < 3, `Medium` 3-9, `High` 10+, `None`
  when no intervals survived filtering.
- **Provider-side accounting drift.** The quota probe reads the provider's
  own % remaining, but provider counters can lag actual ingestion by
  seconds-to-minutes. A short measurement window can show a misalignment
  that washes out over longer horizons.

---

## REST: `GET /quota/reset-credits`

Derives, from the sampled `rate_limit_reset_credits.available_count`
time-series, when each banked quota-**reset credit** was granted and when it
expires — so an operator can spend a credit before the provider silently
expires it. The endpoint resolves `IResetCreditExpiryEstimator` from DI; when
the plugin is not loaded it returns `503 Service Unavailable`.

No manual per-credit expiry is entered. The grant instant of each credit is
inferred from *when the count stepped up*, and the provider's fixed expiry
period (Codex publishes a 30-day credit lifetime) is added to it.

### Derivation algorithm

The tracker replays the ordered `available_count` series and maintains a FIFO
queue of credit grant-times:

- **On an increment** of `available_count` by *N*, it records *N* new grants,
  each pinned to the timestamp of the **last sample at the previous (lower)
  count** — the *earliest-possible* grant instant, not the first higher
  reading. Taking the earlier bound yields the earliest-possible expiry (the
  safe direction: warn to spend a credit sooner, never later) and is immune to
  the orchestrator being **down** across the grant. A measurement gap can only
  push the inferred grant earlier, so it can never under-estimate a credit's
  age.
- **On a decrement**, it retires the **oldest** grant first (FIFO —
  closest-to-expiry first), mirroring how the provider spends the
  soonest-expiring credit. A decrement below what is tracked is a safe no-op.
- **`nextCreditExpiresAt`** = min over queued grants of
  `grant_time + expiryPeriod − safetyBuffer`. The companion
  **`nextCreditIsEstimated`** is `true` when the credit driving that headline is
  a seeded operator estimate (below) rather than an observed grant — a consumer
  reading only the headline must then render it as an estimate, never as a
  precise provider deadline.

A sample whose count is **absent** (older provider / probe failure) is treated
as a *gap*, not a decrement to zero, so it never spuriously retires a credit.

### Pre-observation credits

Credits already banked before the count series began have no observed
increment, so their age cannot be inferred. Seed them under
`ResetCreditExpiry:Seeds` with an **estimated** expiry; the report flags each
seeded credit `isEstimated: true` so it is never presented as precise. Seeds
are treated as the oldest credits (retired before any observed grant on a
decrement) and are sorted by estimated expiry so the soonest-expiring is
retired first. Seeds belong to the single configured agent
(`ResetCreditExpiry:Agent`, default `codex`); a request for a different `agent`
reads that agent's own series and never inherits the configured agent's seeds.

### Query parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `agent` | string | `codex` (from config) | Agent whose reset-credit series to derive. |
| `from` | RFC 3339 | now − `LookbackDays` | Lower bound on the count series (inclusive). |
| `to` | RFC 3339 | now | Upper bound on the count series (exclusive). |

### Example

```sh
curl 'http://orchestrator/quota/reset-credits?agent=codex'
```

```json
{
  "credits": [
    {
      "grantedAt":       "2026-06-16T00:00:00+00:00",
      "expiresAt":       "2026-07-16T00:00:00+00:00",
      "advisedSpendByAt":"2026-07-15T00:00:00+00:00",
      "isEstimated":     true,
      "label":           "credit A — burn within 2 weeks"
    },
    {
      "grantedAt":       "2026-06-20T12:00:00+00:00",
      "expiresAt":       "2026-07-20T12:00:00+00:00",
      "advisedSpendByAt":"2026-07-19T12:00:00+00:00",
      "isEstimated":     false,
      "label":           null
    }
  ],
  "nextCreditExpiresAt": "2026-07-15T00:00:00+00:00",
  "nextCreditIsEstimated": true,
  "latestObservedCount": 2,
  "expiryPeriod": "30.00:00:00",
  "safetyBuffer": "1.00:00:00"
}
```

`latestObservedCount` is the provider's own count; when it differs from
`credits.length` the seed list does not exactly cover the pre-observation
baseline — a signal to adjust `Seeds`.

---

## REST: `GET /quota/reset-advice`

Composes the live quota snapshot (from the probe) and the derived banked-credit
expiry (above) into a single **report-only** verdict: *should I spend a banked
quota-reset credit now?* It never notifies and never triggers a reset — it only
reports. Resolves `IResetOptimalityAdvisor` from DI and degrades to `503` when
the statistics plugin is not loaded.

### Decision algorithm

The advisor encodes the operator's reset-optimality rules, with the corrections
established from real Codex data:

1. **Burn-first.** Never advise spending while usable quota is above the dust
   threshold. Applying a reset re-anchors the current window, so any quota left
   in it at the reset moment is forfeited — burn it down first.
2. **Re-anchor model.** A banked reset sets the window to `now + period` and
   **destroys** the upcoming natural reset (which would have refilled the window
   for free). So only spend when the natural reset would land *too late* to
   help; otherwise wait for the free reset and keep the credit.
3. **Predicted natural reset.** Codex's real reset is a fixed weekly boundary
   (~Monday 06:00 UTC). The provider's `reset_at` field **over-predicts** and is
   not used — the boundary is predicted from `CadenceAnchor` + `CadencePeriodDays`,
   optionally phase-refined from observed weekly resets in the logged series
   (`RefineAnchorFromLogger`).
4. **Decision deadline.** `min(PlanEndsAt, nextCreditExpiresAt)` — the latest
   moment at which spending still has value AND is still possible. Spend only
   when the natural reset lands after this deadline (beyond `TimeToleranceHours`).

The `reason` field is a stable string code: `NotApplicableAgent`,
`ConfigurationInvalid`, `QuotaReadingUnavailable`, `NoBankedCredit`, `BurnFirst`,
`DeadlinePassed`, `NaturalResetArrivesInTime`, or `SpendBeforeDeadline`.

### Query parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `agent` | string | first of `ResetOptimality:Agents` | Agent to advise on. |
| `from` | RFC 3339 | now − `LookbackDays` | Lower bound on the credit-count series used to derive expiries. |
| `to` | RFC 3339 | now | Upper bound on the credit-count series. |

### Example

```sh
curl 'http://orchestrator/quota/reset-advice?agent=codex'
```

```json
{
  "agent": "codex",
  "evaluatedAt": "2026-07-03T12:00:00+00:00",
  "shouldSpend": true,
  "reason": "SpendBeforeDeadline",
  "rationale": "Quota is exhausted and the natural reset at 2026-07-08 06:00:00Z lands after the deadline 2026-07-05 00:00:00Z — spend a banked credit before then, else the plan ends or the credit expires unused.",
  "predictedNaturalReset": "2026-07-08T06:00:00+00:00",
  "decisionDeadline": "2026-07-05T00:00:00+00:00",
  "planEndsAt": "2026-07-05T00:00:00+00:00",
  "nextCreditExpiresAt": "2026-07-19T12:00:00+00:00",
  "nextCreditIsEstimated": false,
  "usableQuotaPct": 0.0,
  "dustThresholdPct": 1.0,
  "optimalWindow": { "opensAt": "2026-07-03T12:00:00+00:00", "closesAt": "2026-07-05T00:00:00+00:00" }
}
```

`optimalWindow` is present only when `shouldSpend` is true. When
`nextCreditIsEstimated` is true the deadline is driven by an operator-seeded
estimate (not an observed grant) and must not be rendered as a precise provider
deadline.

---

## Adding further metric streams

Implement `IMetricSampler` and decorate the class with `[CodeyBoxPlugin]`
(or add it to an existing plugin alongside the quota sampler). The host
discovers it through the standard plugin loader and runs it on its own loop.

```csharp
[CodeyBoxPlugin(
    id: "myorg.throughput-stats",
    displayName: "Throughput sampler",
    minHostApiVersion: "1.2")]
public sealed class ThroughputSampler : IMetricSampler, IPluginInitializer
{
    public string Kind => "throughput";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public bool Enabled => true;

    public Task SampleOnceAsync(CancellationToken ct)
    {
        // Read whatever you need from DI deps and write your sample.
        return Task.CompletedTask;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

Best practice — mirror the quota plugin's shape:
- Own your own SQLite (or other) storage.
- Expose a Core-level read interface for queries (`IThroughputTimeSeriesStore`,
  …) so the API can map a REST endpoint that gracefully degrades to 503
  when the plugin is not loaded.
- Read all knobs from `CodeyBox:Plugins:<your-plugin-id>` and re-bind on
  the scoped config's reload token so hot-reload works.

---

## Migrating off the standalone poller

The stopgap external poller wrote to `~/codeybox-quota-history.db` with
loosely-defined columns (`quota_sample`, `token_snapshot`, `quota_raw`).
The plugin's `quota_sample` / `quota_raw` schemas are intentionally
similar shapes so a one-time SQL migration (column mapping +
ISO-8601 timestamp conversion) is sufficient for operators who want to
preserve historical data. New deployments can simply start fresh — the
plugin begins capturing on its first interval.

Once the plugin is reporting rows, stop the external poller, archive its
SQLite file, and remove the cron entry. The `/quota` endpoint continues
to expose point-in-time availability; `/quota/history` exposes the
sampled time-series.
