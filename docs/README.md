# CodeyBox documentation

CodeyBox runs coding agents in throwaway VMs, reviews what they produce, and
merges it. These pages describe how that works and how to run it.

**New here?** [`getting-started.md`](getting-started.md) takes you from a clean
host to a merged change. Then read
[`concepts/architecture.md`](concepts/architecture.md) for the shape of the
system, and [`concepts/security.md`](concepts/security.md) before you point it
at anything that matters.

## Concepts — how it works

| Page | What it covers |
|---|---|
| [architecture](concepts/architecture.md) | components, trust boundaries, the state machine, plugin points |
| [pipeline](concepts/pipeline.md) | what work, audit, rework, merge and push do at the git level |
| [work items](concepts/work-items.md) | dependencies, cancellation, check-and-act, refactor items, replay |
| [projects](concepts/projects.md) | per-project repository, auditors, upstream, budgets, credentials |
| [agents](concepts/agents.md) | the agent CLI contract, the built-in eight, adding a ninth |
| [agent classes](concepts/agent-classes.md) | routing across agents, quality scores, quota-aware fallback |
| [sandboxes](concepts/sandboxes.md) | Incus, Multipass, remote Multipass, Sprites, Bubblewrap, Process |
| [security](concepts/security.md) | threat model, mitigations, sharp edges, known gaps |
| [questions and suggestions](concepts/agent-feedback.md) | how an agent asks you something, or flags adjacent work |

## Quality — the gate before merge

| Page | What it covers |
|---|---|
| [audit](quality/audit.md) | the audit phase, capability-grouped sandboxes, the rework loop |
| [audit reports](quality/audit-reports.md) | how findings are stored, queried, and de-duplicated |
| [presets](quality/presets.md) | language detection and audit-type prompts, and who may override them |
| [mutation rigor](quality/mutation-rigor.md) | the per-item gate that checks tests actually catch bugs |
| [test cases](quality/test-cases.md) | test cases as a first-class artifact on a work item |
| [E2E execution](quality/e2e-execution.md) | replaying committed E2E artifacts on cheap CPU-only VMs |

## Operating — running it

| Page | What it covers |
|---|---|
| [running](operating/running.md) | host setup, starting the service, the failure modes you will meet |
| [host firewall](operating/host-firewall.md) | host-side nftables egress enforcement, profiles, troubleshooting |
| [worker pool](operating/worker-pool.md) | concurrency sizing, stuck-agent detection, queue and per-agent pause |
| [recovery](operating/recovery.md) | crash recovery, where each state resumes, the restart window |
| [agent-turn checkpoints](operating/agent-turn-checkpoints.md) | resuming a *partial* agent turn — the deep end of recovery |
| [sandbox reliability](operating/sandbox-reliability.md) | leaked VMs, smoke probes, surviving suspend/resume |
| [quota](operating/quota.md) | probes, floors, the burn gate, the observed-failure breaker |
| [costs](operating/costs.md) | what each run cost, and where the rates come from |
| [spend limits](operating/budgets.md) | per-agent budgets and per-project budget alerts |
| [logging](operating/logging.md) | the structured audit log: files, common properties, event names |
| [observability](operating/observability.md) | OpenTelemetry traces, metrics, Prometheus scrape |
| [pipeline metrics](operating/pipeline-metrics.md) | per-step timings and the transition-health score |
| [agent streams](operating/agent-streams.md) | capturing agent stdout as NDJSON, and what the analyser derives |
| [supervision](operating/supervision.md) | watching and injecting into a live agent session |
| [releases](operating/releases.md) | release branches with a deep audit, and changelog automation |

## Reference — look it up

| Page | What it covers |
|---|---|
| [configuration](reference/configuration.md) | every `CodeyBox:*` key, defaults, hot-reload behaviour |
| [API](reference/api.md) | REST endpoints, auth, the work-item record, SignalR |
| [webhooks](reference/webhooks.md) | outbound events, payloads, HMAC signing, delivery semantics |
| [events](reference/events.md) | the versioned event envelope and its evolution rules |
| [CLI](reference/cli.md) | the `codeybox` client and the project-config wizard |
| [knobs](reference/knobs.md) | per-item directives and how to add one |
| [external IDs](reference/external-ids.md) | addressing work items by your tracker's identifier |
| [agent quirks](reference/agent-quirks.md) | per-CLI binary names, auth layouts, flags, traps |
| [agent sessions](reference/agent-sessions.md) | the opt-in session contract and the Claude session worker |
| [sandbox baselines](reference/sandbox-baselines.md) | bake recipes for C#, Python, Node, Go, Rust, agent CLIs, security tools |

## Extending — plugins

| Page | What it covers |
|---|---|
| [plugin SDK](extending/plugins.md) | the plugin contract, allowlist, API-version rules, threat model |
| [auditor plugins](extending/auditor-plugins.md) | shipping a custom auditor |
| [upstream plugins](extending/upstream-plugins.md) | shipping a forge integration |
| [credential plugins](extending/credential-plugins.md) | shipping a credential provider |
| [statistics plugin](extending/statistics-plugin.md) | the bundled quota-history and capacity plugin |
| [file-size-limits auditor](extending/file-size-limits-auditor.md) | a small worked example of a deterministic auditor |

## Developing CodeyBox itself

| Page | What it covers |
|---|---|
| [build environment](development/build-environment.md) | provisioning prerequisites that break the build when missing |
| [manual UAT](development/manual-uat/) | operator checklists for what automated tests cannot cover |
| [`AGENTS.md`](../AGENTS.md) | the engineering contract every change is graded against |

## The other clients

The **admin dashboard** ([`tools/CodeyBox.Admin/`](../tools/CodeyBox.Admin/README.md))
is a Blazor Server UI — queue, diffs, findings, cost and timing charts:

```bash
dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

The **CLI** ([`tools/CodeyBox.Cli/`](../tools/CodeyBox.Cli/README.md)) is a typed
client for the same API:

```bash
codeybox configure
codeybox queue add --project myapp --title "healthz" --prompt-file ./prompt.md
codeybox queue ls --state Queued,Working
codeybox queue watch <id>
```

Both talk to the orchestrator over REST only, and share no code with it.
