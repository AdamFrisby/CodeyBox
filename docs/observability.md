# Observability: OpenTelemetry export

CodeyBox emits traces and metrics via the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otlp/). This is **off by default** — operators opt in by setting `CodeyBox:Otel:Enabled=true`.

When disabled (the default), zero OTel types are registered and there is no runtime overhead.

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
| `ResourceAttributes` | `{ key: value }` | `{}` | Extra OTel resource attributes merged into every span and metric point. |

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
| `CodeyBox.Pipeline` | Agent execution (`agent.exec` per phase) |
| `CodeyBox.Sandbox` | Sandbox lifecycle — `git.clone_into_sandbox`, `git.commit`, `git.push_back_to_bare_repo` |
| `CodeyBox.Audit` | Per-auditor invocation (`auditor.<name>` per iteration) |
| `CodeyBox.Upstream` | *(Reserved for upstream remote spans in a future release)* |

HTTP server spans (incoming API requests) and outbound HTTP spans (GitHub API, agent quota probes, webhooks) are captured automatically via `AspNetCore` and `Http` instrumentation.

### Span attributes

All CodeyBox-produced spans carry these attributes (where applicable):

| Attribute | Description |
|---|---|
| `codeybox.work_item_id` | UUID of the work item |
| `codeybox.phase` | Pipeline phase: `work`, `rework`, `audit`, `merge`, `upstream` |
| `codeybox.iteration` | Audit iteration number (audit/rework spans only) |
| `codeybox.agent` | Agent kind value, e.g. `claude`, `codex` |

**PII and credential policy**: prompt bodies, agent stdout/stderr, and raw audit findings are never set as span attributes. Span attributes are limited to IDs and metadata (work_item_id, phase, agent.kind, etc.). Credential values are never included.

### W3C trace context propagation

Outbound HTTP calls (GitHub API, agent quota probes, webhooks) automatically receive W3C `traceparent` / `tracestate` headers via `AddHttpClientInstrumentation`. This allows distributed tracing across services that support W3C trace context.

---

## Metric model

### Counters

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.work_item.transitions` | `{transition}` | `to_state` | Incremented on every work-item state transition. |
| `codeybox.audit.iterations` | `{iteration}` | `outcome` (`passed` \| `reworking` \| `failed`) | Incremented once per completed audit iteration. |

### Histograms

| Instrument | Unit | Tags | Description |
|---|---|---|---|
| `codeybox.audit.findings.blocking` | `{finding}` | `iteration` | Blocking-finding count per audit iteration. |
| `codeybox.auditor.duration_ms` | `ms` | `auditor.name`, `auditor.kind`, `iteration` | Wall-clock time per auditor invocation. |
| `codeybox.agent.duration_ms` | `ms` | `agent.kind`, `phase` | Agent execution time per phase. |
| `codeybox.sandbox.lifecycle.duration_ms` | `ms` | `step` (`start` \| `clone`) | Sandbox step durations. |

In addition, `.NET` runtime metrics (GC, thread pool, memory) are emitted automatically via `AddRuntimeInstrumentation`.

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
