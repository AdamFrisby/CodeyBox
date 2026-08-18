# Cost, Telemetry, And Streams Manual UAT

Run these procedures against disposable
projects, credentials, collectors, and webhook receivers. Do not preserve real
secrets in the UAT record.

## Real Invoice And Usage Reconciliation

1. Configure a disposable project with cost reporting enabled and known agent
   models for Claude, Codex, and Gemini as available.
2. Run a small fixed prompt through work, audit, and merge paths that produce
   structured token usage.
3. Call `GET /workitems/{id}/costs`, `GET /projects/{id}/costs`, and
   `GET /projects/{id}/budget`.
4. Compare input, cached-input, output token counts, model IDs, and estimated USD
   against the vendor invoice or account usage page for the same time window.
5. Confirm missing or unmapped model pricing produces the documented fallback
   estimate without failing the work item.
6. Set warning and hard-cap budget thresholds on the disposable project, then
   verify warning, exceeded, auto-pause, recovery, and optional auto-resume
   behavior with the configured webhook receiver.

Pass criteria: API totals reconcile directionally with vendor usage, rolling
30-day project totals exclude older rows, and budget webhooks/queue state match
the configured thresholds.

## Real OpenTelemetry Collector Inspection

1. Start an OTLP collector or observability backend reachable from the CodeyBox
   host.
2. Configure `CodeyBox:Otel:Enabled=true`, the collector endpoint, protocol, and
   deployment resource attributes.
3. Run one work item through pipeline, sandbox, audit, merge, and upstream paths
   that are available in the disposable environment.
4. Inspect exported spans for `CodeyBox.Pipeline`, `CodeyBox.Sandbox`,
   `CodeyBox.Audit`, and `CodeyBox.Upstream` activity sources.
5. Inspect metrics for `codeybox.work_item.transitions`,
   `codeybox.agent.duration_ms`, `codeybox.audit.iterations`,
   `codeybox.auditor.duration_ms`, and upstream/sandbox duration instruments.
6. Stop or block the collector and run another small item.

Pass criteria: spans and metrics contain work item, phase, iteration, agent,
auditor, endpoint, and resource tags where applicable; collector unavailability
does not stop the pipeline.

## Browser Live Stdout Against A Real Agent

1. Enable agent stream capture and start the admin dashboard against a disposable
   project.
2. Queue a long-running real-agent work item that prints periodic stdout and at
   least one known fake secret-shaped value.
3. Open the work item detail page before the agent starts and leave the live
   stdout panel connected.
4. Open the same work item detail page in a second browser after the process has
   already emitted output.
5. Let the item complete, then call the stdout-tail endpoint and stream download
   endpoint for the same work item.

Pass criteria: both browser sessions show batched live stdout without duplicate
or mixed-phase chunks, late joiners receive the recent tail, completion is
visible, and raw stream/stdout output is redacted before display or download.
