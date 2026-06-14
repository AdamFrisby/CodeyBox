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
6. [Adding further metric streams](#adding-further-metric-streams)
7. [Migrating off the standalone poller](#migrating-off-the-standalone-poller)

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
        "MaxQueryRows": 50000
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
