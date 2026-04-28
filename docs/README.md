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
7. [`agents.md`](agents.md) — built-in agents and how to add new ones.
8. [`audit.md`](audit.md) — opt-in audit phase between work and merge,
   capability-grouped sandboxes, rework loop.
9. [`git-workflow.md`](git-workflow.md) — what the work, audit, rework,
   and merge phases actually do at the git level.
10. [`api.md`](api.md) — REST endpoints, configuration, and authentication.
11. [`operations.md`](operations.md) — running, logs, restarts, failure modes.
