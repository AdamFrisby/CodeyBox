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
| `Enabled` | bool | `false` | Enable OTel export. Nothing is registered when `false`. |
| `ServiceName` | string | `"codeybox"` | OTel `service.name` resource attribute. |
| `ServiceVersion` | string? | `null` | OTel `service.version` — use a git SHA or release tag. |
| `OtlpEndpoint` | string | *(required if enabled)* | OTLP collector endpoint, e.g. `http://localhost:4317`. |
| `OtlpHeaders` | string? | `null` | CSV of extra headers forwarded to the collector, e.g. `x-honeycomb-team=abc,x-dataset=prod`. |
| `ExportProtocol` | `"grpc"` \| `"httpprotobuf"` | `"grpc"` | OTLP wire format. |
| `ResourceAttributes` | `{ key: value }` | `{}` | Extra OTel resource attributes merged into every span, metric point, and log record. Applied last, so they override the auto-derived attributes on key collision. |

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

- `OtlpEndpoint` must be set and parse as an absolute URL.
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
| `CodeyBox.Pipeline` | The work-item pipeline tree: a root `pipeline.run` span per work item → `phase.<name>` spans (work / rework / audit / merge / upstream) → `agent.invoke` spans per agent attempt → `agent.exec` / clone steps. |
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
| `codeybox.phase` | Pipeline phase: `work`, `rework`, `audit`, `merge`, `upstream` |
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
| `codeybox.phase.duration_ms` | `ms` | `phase` (`work` \| `rework` \| `audit` \| `merge` \| `upstream`) | Whole-phase wall-clock duration. |
| `codeybox.sandbox.lifecycle.duration_ms` | `ms` | `step` (`start` \| `clone`) | Sandbox step durations. |
| `codeybox.upstream.api_call.duration_ms` | `ms` | `endpoint`, `status_code` | Upstream forge API call durations. |

### Observable gauges

Polled at collection time; registered only when OTel is enabled.

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.work_item.active` | `{work_item}` | `state` | Work items currently persisted in each state (refreshed on a 15 s background cadence so the collection thread never blocks on SQLite). |
| `codeybox.workers.in_use` | `{worker}` | — | Worker slots currently occupied by an in-flight pipeline run. |
| `codeybox.workers.max` | `{worker}` | — | Configured `MaxConcurrentWorkers` ceiling. |
| `codeybox.sandbox.active` | `{sandbox}` | `provider` | Sandboxes/VMs the process is actively tracking (0 for the ephemeral process/bubblewrap providers, which have no persistent VM lifecycle). |
| `codeybox.agent.quota.available_pct` | `%` | `agent.kind`, `model` | Most-recent subscription quota headroom observed per agent/model during routing (`-1` = unknown). |

In addition, `.NET` runtime metrics (GC, thread pool, memory) are emitted automatically via `AddRuntimeInstrumentation`.

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
