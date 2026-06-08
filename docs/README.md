# CodeyBox documentation

Start here if you're new:

1. [`architecture.md`](architecture.md) — the system at a glance, plugin
   points, state machine.
2. [`security.md`](security.md) — threat model, mitigations, sharp edges.
   **Read before deploying.**
3. [`security-audit.md`](security-audit.md) — initial-pass audit, all
   findings with severities and status.
4. [`projects.md`](projects.md) — multi-project config, per-project
   upstream/auditors, defaults inheritance, language + audit-type presets.
5. [`sandbox-providers.md`](sandbox-providers.md) — Multipass,
   Bubblewrap, and Process trade-offs and host setup.
6. [`host-firewall.md`](host-firewall.md) — host-side egress enforcement
   for the Multipass provider (operator setup, profile model, what it
   protects against).
7. [`AGENTS.md`](../AGENTS.md) — built-in agents and how to add new ones.
8. [`audit.md`](audit.md) — opt-in audit phase between work and merge,
   capability-grouped sandboxes, rework loop.
9. [`git-workflow.md`](git-workflow.md) — what the work, audit, rework,
   and merge phases actually do at the git level.
10. [`api.md`](api.md) — REST endpoints, configuration, and authentication.
11. [`operations.md`](operations.md) — running, logs, restarts, failure modes.
12. [`webhooks.md`](webhooks.md) — outbound webhook events, payload shape, HMAC signing, and configuration.
13. [`audit-logging.md`](audit-logging.md) — structured audit log: location, format, all event names and
    properties. Start here when writing SIEM rules or log-query dashboards.

## Plugin SDK

14. [`plugins.md`](plugins.md) — plugin author guide. Covers project setup,
    `[CodeyBoxPlugin]` attribute, allowlist configuration, configuration
    scoping, the API-version contract, threat model, and NuGet publishing
    pattern. Start here if you want to ship a custom auditor, upstream remote,
    or credential provider without forking CodeyBox.

## Admin dashboard

A Blazor Server web UI lives at [`tools/CodeyBox.Admin/`](../tools/CodeyBox.Admin/README.md).
Run with `dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web`.
It speaks to the orchestrator over REST only — no shared code with the orchestrator.

## CLI (operator tools)

A typed command-line client lives at [`tools/CodeyBox.Cli/`](../tools/CodeyBox.Cli/README.md).
Run with `dotnet run --project tools/CodeyBox.Cli -- <command>`, or publish as a self-contained AOT binary.

For sandbox tool installation examples across C#, Python, Node, Go, and Rust,
see [`baseline-bake-examples.md`](baseline-bake-examples.md).

```bash
codeybox configure                          # set API URL + token
codeybox queue add --project myapp --title "healthz" --prompt-file ./prompt.md
codeybox queue ls --state Queued,Working
codeybox queue watch <id>
```

Speaks to the orchestrator over REST only — no shared code with the orchestrator.
