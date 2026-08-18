# Manual UAT checklists

Procedures a human runs against a non-production deployment to cover what the
automated suite cannot: real VMs, real agent CLIs, real forge credentials, real
operator UIs.

Each checklist states its pass criteria. Keep the evidence — work item IDs,
`/plugins` and report payloads, log excerpts, screenshots — with the run
record.

| Area | Checklist |
|---|---|
| Pipeline and worker lifecycle | [pipeline-and-worker-lifecycle.md](pipeline-and-worker-lifecycle.md) |
| Sandbox providers | [sandbox-providers.md](sandbox-providers.md) |
| Quota and routing | [QuotaAndRouting.md](QuotaAndRouting.md) |
| Agent runners and credentials | [agent-runners-and-credentials.md](agent-runners-and-credentials.md) |
| Auditing and reports | [auditing-and-reports.md](auditing-and-reports.md) |
| Plugins | [plugins.md](plugins.md) |
| Work-item APIs and queue controls | [work-item-apis-and-queue-controls.md](work-item-apis-and-queue-controls.md) |
| Projects and configuration | [projects-and-configuration.md](projects-and-configuration.md) |
| Persistence and recovery | [persistence-and-recovery.md](persistence-and-recovery.md) |
| Upstream, webhooks, and releases | [upstream-webhooks-and-releases.md](upstream-webhooks-and-releases.md) |
| Cost, telemetry, and streams | [cost-telemetry-and-streams.md](cost-telemetry-and-streams.md) |
| Operator clients | [operator-clients.md](operator-clients.md) |
