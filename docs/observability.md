# Observability: OpenTelemetry export

CodeyBox emits **traces, metrics, and logs** via the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otlp/). This is **off by default** — operators opt in by setting `CodeyBox:Otel:Enabled=true`.

When disabled (the default), zero OTel types are registered and there is no runtime overhead: the `Meter`/`ActivitySource` instruments are always allocated but the SDK discards measurements and never starts spans when no provider is listening, the observable gauges are not registered, and logging stays on the Serilog-only path.

Structured agent event streams are stored separately from OTel under
`CodeyBox:AgentStreams`; see [`agent-streams.md`](agent-streams.md). OTel spans
carry phase and duration metadata, while agent streams preserve the raw
per-event stdout JSONL for later analysis.

---

## Configuration

All options live under the `CodeyBox:Otel` section.

| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | bool | `false` | Enable OTel OTLP push. Nothing OTLP-related is registered when `false`. The Prometheus scrape exporter (see [`Prometheus:Enabled`](#prometheus-scrape-exporter)) is independent — either or both can be on. |
| `ServiceName` | string | `"codeybox"` | OTel `service.name` resource attribute. |
| `ServiceVersion` | string? | `null` | OTel `service.version` — use a git SHA or release tag. |
| `OtlpEndpoint` | string | *(required if `Enabled`)* | OTLP collector endpoint, e.g. `http://localhost:4317`. |
| `OtlpHeaders` | string? | `null` | CSV of extra headers forwarded to the collector, e.g. `x-honeycomb-team=abc,x-dataset=prod`. |
| `ExportProtocol` | `"grpc"` \| `"httpprotobuf"` | `"grpc"` | OTLP wire format. |
| `ResourceAttributes` | `{ key: value }` | `{}` | Extra OTel resource attributes merged into every span, metric point, and log record. Applied last, so they override the auto-derived attributes on key collision. |
| `Prometheus:Enabled` | bool | `false` | In-process Prometheus scrape endpoint. See [Prometheus scrape exporter](#prometheus-scrape-exporter). |
| `Prometheus:Path` | string | `"/metrics"` | Path the scrape endpoint is mapped at. Must begin with `/`. |
| `Prometheus:RequireApiKey` | bool | `false` | When `false` (default), the scrape path is exempted from the API-key middleware (exact-path scope only). When `true`, the path requires the same `Authorization: Bearer` header as every other endpoint. |

### Standard `OTEL_*` environment variables

CodeyBox honors the conventional OpenTelemetry environment variables so a deployment can be configured with the standard env-only bootstrap (the same contract the paired JobTrack service follows). **Environment variables override the `CodeyBox:Otel` appsettings values** on collision:

| Env var | Overrides | Notes |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `OtlpEndpoint` | When set, telemetry can be enabled without an appsettings endpoint (`Enabled=true` alone suffices). The OTel SDK reads it directly, including the `httpprotobuf` path-append semantics. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `ExportProtocol` | When set, the SDK's own protocol selection is left in place (`grpc` / `http/protobuf`) rather than forcing the appsettings `ExportProtocol`, so an env-only deployment to an HTTP/protobuf collector exports correctly without also setting appsettings. |
| `OTEL_EXPORTER_OTLP_HEADERS` | `OtlpHeaders` | Same `key=value,key2=value2` format as the appsettings CSV. |
| `OTEL_SERVICE_NAME` | `ServiceName` | Sets the `service.name` resource attribute. |
| `OTEL_RESOURCE_ATTRIBUTES` | `ResourceAttributes` | `key=value,key2=value2` pairs; applied last so env pairs win over appsettings on key collision. |

### Resource attributes

Every signal (trace, metric, log) carries a shared resource so the three correlate on identical service identity:

| Attribute | Source |
|---|---|
| `service.name` | `CodeyBox:Otel:ServiceName` (default `codeybox`). |
| `service.version` | `CodeyBox:Otel:ServiceVersion`, falling back to the API assembly version. |
| `service.instance.id` | `<machine-name>:<process-id>`. |
| `deployment.environment` | The ASP.NET Core environment name (`Development` / `Production` / …) when available. |

### Startup validation

If `Enabled=true` and the configuration is invalid, the process refuses to start with a clear error message:

- `OtlpEndpoint` must be set (in appsettings **or** via `OTEL_EXPORTER_OTLP_ENDPOINT`); the appsettings value, when present, must parse as an absolute http/https URL.
- `ExportProtocol` must be exactly `"grpc"` or `"httpprotobuf"`.

---

## Supported protocols

| Protocol | Typical port | Notes |
|---|---|---|
| `grpc` (default) | 4317 | gRPC / HTTP/2, binary Protobuf. Best for local collectors (Jaeger, Tempo, OpenTelemetry Collector). |
| `httpprotobuf` | 4318 | HTTP/1.1, binary Protobuf. Use when your network blocks gRPC or when sending directly to Honeycomb, Datadog, etc. |

---

## Trace model

### Sources

Four `ActivitySource` instances produce spans:

| Source name | What it traces |
|---|---|
| `CodeyBox.Pipeline` | The work-item pipeline tree: a root `pipeline.run` span per work item → `phase.<name>` spans (pickup / work / rework / audit / merge / upstream) → `agent.invoke` spans per agent attempt → `agent.exec` / clone steps. |
| `CodeyBox.Sandbox` | Sandbox lifecycle — `git.clone_into_sandbox`, `git.commit`, `git.push_back_to_bare_repo`, sandbox start. |
| `CodeyBox.Audit` | Per-auditor invocation (`auditor.<name>` per iteration). |
| `CodeyBox.Upstream` | Upstream remote spans (reserved; upstream timing is currently captured by the `codeybox.upstream.*` metric and the `phase.upstream` span). |

Within one pipeline run the spans nest: `pipeline.run` is the root, each `phase.*` span is a child, and `agent.invoke` (plus the sandbox/exec spans) nest under the active phase. HTTP server spans (incoming API requests) and outbound HTTP spans (GitHub API, agent quota probes, webhooks) are captured automatically via `AspNetCore` and `Http` instrumentation.

### Span attributes

CodeyBox-produced spans carry these attributes (where applicable):

| Attribute | Description |
|---|---|
| `codeybox.work_item_id` | UUID of the work item |
| `codeybox.project_id` | Project id (root span) |
| `codeybox.phase` | Pipeline phase: `pickup`, `work`, `rework`, `audit`, `merge`, `upstream` |
| `codeybox.iteration` | Audit iteration number (audit/rework/invoke spans only) |
| `codeybox.agent` | Agent kind value, e.g. `claude`, `codex` |
| `codeybox.model` | Model id for the invocation (`(default)` when unset) |
| `codeybox.agent_class` | Agent class id driving routing (`(none)` when not class-routed) |
| `codeybox.state` | Work-item state at pipeline entry (root span) |
| `codeybox.outcome` | Agent-invocation outcome: `success` \| `error` \| `canceled` |

**PII and credential policy**: prompt bodies, agent stdout/stderr, and raw audit findings are never set as span attributes. Span attributes are limited to IDs and metadata (work_item_id, phase, agent.kind, etc.). Credential values are never included.

### W3C trace context propagation

Outbound HTTP calls (GitHub API, agent quota probes, webhooks) automatically receive W3C `traceparent` / `tracestate` headers via `AddHttpClientInstrumentation`. This allows distributed tracing across services that support W3C trace context.

---

## Metric model

### Counters

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.work_item.transitions` | `{transition}` | `to_state` | Incremented on every work-item state transition. |
| `codeybox.dispatch.count` | `{dispatch}` | — | Incremented when a work item is dispatched to a worker. |
| `codeybox.agent.invocations` | `{invocation}` | `agent.kind`, `model`, `agent_class`, `phase`, `outcome` (`success` \| `error` \| `canceled`) | One per agent invocation attempt. |
| `codeybox.agent.fallbacks` | `{fallback}` | `from_agent`, `to_agent` (`(none)` on class exhaustion), `kind` (`quota` \| `timeout`), `phase` | One per agent fallback / class-exhaustion event. |
| `codeybox.agent.tokens` | `{token}` | `agent.kind`, `model`, `token_type` (`input` \| `cached_input` \| `output`) | Tokens consumed, summed as cost rows are recorded. |
| `codeybox.agent.cost_usd` | `USD` | `agent.kind`, `model` | Estimated agent cost, summed as cost rows are recorded (aligned with the per-work-item cost rows — no double counting). |
| `codeybox.audit.iterations` | `{iteration}` | `outcome` (`passed` \| `reworking` \| `failed`) | Incremented once per completed audit iteration. |
| `codeybox.webhook.deliveries` | `{delivery}` | `endpoint`, `event`, `outcome` (`delivered` \| `failed`) | One per terminal webhook delivery outcome. |

### Histograms

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.audit.findings.blocking` | `{finding}` | `iteration` | Blocking-finding count per audit iteration. |
| `codeybox.auditor.duration_ms` | `ms` | `auditor.name`, `auditor.kind`, `iteration` | Wall-clock time per auditor invocation. |
| `codeybox.agent.duration_ms` | `ms` | `agent.kind`, `phase` | Agent execution time per phase. |
| `codeybox.phase.duration_ms` | `ms` | `phase` (`pickup` \| `work` \| `rework` \| `audit` \| `merge` \| `upstream`) | Whole-phase wall-clock duration. |
| `codeybox.sandbox.lifecycle.duration_ms` | `ms` | `step` (`start` \| `clone`) | Sandbox step durations. |
| `codeybox.sandbox.resource.peak_ram_mb` | `MB` | `phase`, `network_profile` | Peak guest RAM captured at VM-provider teardown. |
| `codeybox.sandbox.resource.avg_cpu_pct` | `%` | `phase`, `network_profile` | Lifetime-average guest CPU utilisation captured at VM-provider teardown. |
| `codeybox.sandbox.resource.net_rx_mb` | `MB` | `phase`, `network_profile` | Cumulative guest receive traffic on the data interface captured at VM-provider teardown. |
| `codeybox.sandbox.resource.net_tx_mb` | `MB` | `phase`, `network_profile` | Cumulative guest transmit traffic on the data interface captured at VM-provider teardown. |
| `codeybox.upstream.api_call.duration_ms` | `ms` | `endpoint`, `status_code` | Upstream forge API call durations. |

### Observable gauges

Polled at collection time; registered only when OTel is enabled.

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.work_item.active` | `{work_item}` | `state` | Work items currently persisted in each state (refreshed on a 15 s background cadence so the collection thread never blocks on SQLite). |
| `codeybox.workers.in_use` | `{worker}` | — | Worker slots currently occupied by an in-flight pipeline run. |
| `codeybox.workers.max` | `{worker}` | — | Configured `MaxConcurrentWorkers` ceiling. |
| `codeybox.sandbox.active` | `{sandbox}` | `provider` | Currently admitted sandbox leases when the provider is wrapped by the global admission gate, including create/provisioning and startup-resume leases; otherwise lifecycle-aware providers report `IActiveSandboxProvider.SnapshotActiveSandboxes()` and ephemeral providers report `SandboxLiveCounter.Active`. |
| `codeybox.sandbox.max` | `{sandbox}` | — | Configured `MaxConcurrentSandboxes` admission ceiling. |
| `codeybox.agent.quota.available_pct` | `%` | `agent.kind`, `model` | Most-recent subscription quota headroom observed per agent/model during routing (`-1` = unknown). |

In addition, `.NET` runtime metrics (GC, thread pool, memory) are emitted automatically via `AddRuntimeInstrumentation`.

---

## Sandbox Resource Usage

When `CodeyBox:MultipassSandbox:CaptureResourceMetrics=true` or
`CodeyBox:Incus:CaptureResourceMetrics=true`, the selected VM provider performs
one bounded, best-effort in-guest read before stop/delete and persists a
per-work-item row with phase, VM lifetime, average CPU, peak RAM, rx/tx MB,
baseline ref, network profile, load average, and capture time. Incus uses
`Incus:ResourceMetricsCaptureTimeout` for this read. The admin read surface is:

```text
GET /admin/sandbox-resource-usage?n=100
```

It returns recent p50/p95 peak RAM, average/p95 CPU, and rx/tx/total network
planning stats from the SQLite-backed `sandbox_resource_usage` table.

Peak RAM comes from a boot-time systemd sampler baked into each provider's
cloud-init baseline. The sampler reads `/proc/meminfo` periodically and updates
one small file under `/run` when `MemTotal - MemAvailable` exceeds the previous
maximum. Incus's interval is configured with
`Incus:ResourceMetricsSampleInterval` (10 seconds by default). Measured command
cost on the Ubuntu baseline is a single `/proc/meminfo` read plus one shell loop
wakeup per tick; steady-state storage is one integer file and no per-process
history.

---

## Prometheus scrape exporter

CodeyBox can additionally expose its existing metric instruments as a Prometheus scrape endpoint, so operator scrapers (Prometheus, conky, a KDE widget, curl-from-cron) can read fleet state directly without an OTLP collector in the path. The Prometheus exporter is a **peer** of the OTLP push exporter: enabling one does not require the other. Both can run side by side against the same metric provider — no double instrumentation cost.

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `CodeyBox:Otel:Prometheus:Enabled` | bool | `false` | When `false`, the exporter is not registered with the meter provider and the scrape endpoint is not mapped at all (the route is invisible — `404`, not `401`). Restart required to toggle. |
| `CodeyBox:Otel:Prometheus:Path` | string | `"/metrics"` | Path to expose. Must begin with `/`. |
| `CodeyBox:Otel:Prometheus:RequireApiKey` | bool | `false` | Whether the scrape path requires the API-key middleware. See [Authentication](#authentication-for-the-scrape-endpoint). |

The Prometheus exporter and the OTLP push exporter are independent — enabling Prometheus does **not** require `CodeyBox:Otel:Enabled=true`. Setting `Prometheus:Enabled=true` alone wires up the metric provider (and the observable gauges) with the Prometheus exporter only; tracing, log forwarding, and OTLP push stay off until `Otel:Enabled=true`.

### Series shape

Exposed series are the **same** instruments documented in [Metric model](#metric-model), rendered in Prometheus exposition format. The OTel → Prometheus name conversion is:

- Dots become underscores: `codeybox.work_item.active` → `codeybox_work_item_active`.
- Tag keys become labels: `state="Queued"`, `agent_kind="claude"`, etc.
- Counter instruments get a `_total` suffix per Prometheus convention.

Examples:

```
# HELP codeybox_work_item_active Work items currently persisted in each state.
# TYPE codeybox_work_item_active gauge
codeybox_work_item_active{state="Queued"} 31
codeybox_work_item_active{state="Working"} 4
codeybox_work_item_active{state="Done"} 1812

# HELP codeybox_workers_in_use Worker slots currently occupied by an in-flight pipeline run.
# TYPE codeybox_workers_in_use gauge
codeybox_workers_in_use 4

# HELP codeybox_workers_max Configured MaxConcurrentWorkers ceiling for the worker pool.
# TYPE codeybox_workers_max gauge
codeybox_workers_max 16

# HELP codeybox_sandbox_active Currently admitted live or provisioning sandboxes/VMs.
# TYPE codeybox_sandbox_active gauge
codeybox_sandbox_active{provider="incus"} 4

# HELP codeybox_agent_quota_available_pct Most-recent subscription quota headroom...
# TYPE codeybox_agent_quota_available_pct gauge
codeybox_agent_quota_available_pct{agent_kind="claude",model="claude-opus-4-7"} 71.4
```

The endpoint also includes the runtime / HTTP-client instrumentation series already registered (`process_runtime_dotnet_gc_*`, `http_server_request_duration_seconds`, etc.).

### Authentication for the scrape endpoint

Prometheus scrapers typically cannot send an `Authorization: Bearer ...` header. CodeyBox makes the auth posture for `/metrics` (or whatever `Path` resolves to) configurable:

- **`RequireApiKey=false` (default)** — the configured path is exempted from the API-key middleware. The exemption is **exact-path only**: it does not cascade to descendants (`/metrics/leak` still requires the bearer token) or any other route. The default assumes the deployment binds the API to localhost (or a private network) and treats the fleet/quota gauges as non-sensitive operational data.
- **`RequireApiKey=true`** — the scrape path requires the same `Authorization: Bearer <key>` header as every other endpoint. Use this when the API is reachable from a network you do not control end-to-end.

When `Enabled=false`, the route is not mapped at all — there is no 401 to bypass, just a 404 from the routing table.

### Example configurations

Scrape from a Prometheus instance running on the same host:

```json
{
  "CodeyBox": {
    "Otel": {
      "Prometheus": { "Enabled": true }
    }
  }
}
```

```yaml
# prometheus.yml
scrape_configs:
  - job_name: codeybox
    metrics_path: /metrics
    static_configs:
      - targets: ['127.0.0.1:5000']
```

Run both OTLP push and Prometheus scrape side by side:

```json
{
  "CodeyBox": {
    "Otel": {
      "Enabled": true,
      "OtlpEndpoint": "http://otel-collector:4317",
      "Prometheus": { "Enabled": true, "Path": "/metrics", "RequireApiKey": false }
    }
  }
}
```

Locked-down scrape (token-protected):

```json
{
  "CodeyBox": {
    "Otel": {
      "Prometheus": { "Enabled": true, "RequireApiKey": true }
    }
  }
}
```

```yaml
scrape_configs:
  - job_name: codeybox
    metrics_path: /metrics
    bearer_token_file: /etc/prometheus/codeybox.token
    static_configs:
      - targets: ['codeybox.internal:5000']
```

---

## Log model

When OTel is enabled, the existing `ILogger` output is **also** routed through the OpenTelemetry logging provider — the Serilog console/file sinks are unchanged. Serilog forwards each event to the OTel provider (`writeToProviders`), which exports `LogRecord`s over OTLP stamped with the active span's `TraceId`/`SpanId` for log↔trace correlation. Scopes, formatted messages, and structured state values are all included. No logging call sites change; the OTel provider is purely additive and is not registered when OTel is disabled.

The same credential/PII redaction enricher that protects the file logs runs before events reach the OTel provider.

---

## Example configurations

### Local — Jaeger all-in-one

```json
{
  "CodeyBox": {
    "Otel": {
      "Enabled": true,
      "OtlpEndpoint": "http://localhost:4317",
      "ExportProtocol": "grpc",
      "ServiceName": "codeybox",
      "ServiceVersion": "main"
    }
  }
}
```

Start Jaeger:
```
docker run --rm -p 16686:16686 -p 4317:4317 jaegertracing/all-in-one:latest
```

Open `http://localhost:16686` and search for service `codeybox`.

### Honeycomb

```json
{
  "CodeyBox": {
    "Otel": {
      "Enabled": true,
      "OtlpEndpoint": "https://api.honeycomb.io",
      "ExportProtocol": "httpprotobuf",
      "OtlpHeaders": "x-honeycomb-team=YOUR_API_KEY,x-honeycomb-dataset=codeybox",
      "ServiceName": "codeybox",
      "ServiceVersion": "1.2.3"
    }
  }
}
```

### Grafana Tempo (via OpenTelemetry Collector)

```json
{
  "CodeyBox": {
    "Otel": {
      "Enabled": true,
      "OtlpEndpoint": "http://otel-collector:4317",
      "ExportProtocol": "grpc",
      "ServiceName": "codeybox",
      "ResourceAttributes": {
        "deployment.environment": "production",
        "host.name": "worker-1"
      }
    }
  }
}
```

### Datadog

```json
{
  "CodeyBox": {
    "Otel": {
      "Enabled": true,
      "OtlpEndpoint": "http://localhost:4317",
      "ExportProtocol": "grpc",
      "ServiceName": "codeybox"
    }
  }
}
```

Point the Datadog Agent's OTLP ingest at port 4317 (`DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT=0.0.0.0:4317`).

---

## Relationship to the timings database

The SQLite timings database (see [`timings.md`](timings.md)) and OTel are complementary:

| Capability | Timings DB | OTel |
|---|---|---|
| Per-work-item drill-in | ✓ | ✓ |
| Fleet-scale aggregation (p95, trends) | ✗ | ✓ |
| Works offline / no external dependency | ✓ | ✗ (needs collector) |
| Queryable via REST API | ✓ | ✗ (query via Jaeger/Grafana/etc.) |
| Dashboards | Admin UI timing tab | Jaeger, Honeycomb, Tempo, Datadog, Grafana |

OTel is strictly additive — the timings database always runs regardless of whether OTel is enabled.
