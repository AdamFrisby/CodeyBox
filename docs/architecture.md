# CodeyBox architecture

CodeyBox is a C#/.NET orchestration framework that runs LLM coding agents
(Claude Code, GitHub Copilot CLI, OpenAI Codex CLI, ...) inside VM-isolated
sandboxes and merges their output through a controlled git workflow. Managed
projects are language-agnostic; Python, Node, Go, Rust, C#, and custom stacks
all enter through the same project and auditor configuration. The parent
orchestrator runs **no LLMs** - its only job is to schedule sandboxes and
shepherd state.

## High-level flow

```
                                    ┌─────────────────────────┐
                                    │   REST API (ASP.NET)    │
                                    │  POST /workitems …      │
                                    └────────────┬────────────┘
                                                 │ enqueue
                                                 ▼
┌─────────────┐   ┌──────────────┐    ┌─────────────────────┐
│ SQLite      │◀──│  Worker pool │◀───│   ITaskQueue        │
│ (state)     │   │  (Concurrency│    │   (channel-backed)  │
└─────────────┘   │   workers)   │    └─────────────────────┘
                  └──────┬───────┘
                         │ for each work item
                         ▼
   ┌──────────────────── PipelineRunner ─────────────────────┐
   │                                                         │
   │  Phase 1: Work sandbox      ───► branch + agent commits │
   │     ▼                                                   │
   │  Phase 2: Audit + rework loop (skipped if no auditors)  │
   │     │   tool auditors  → credential-free sandbox        │
   │     │   LLM auditors   → sandbox with agent creds       │
   │     │   on fail: rework sandbox → loop until pass       │
   │     ▼   or AuditFailed at MaxIterations                 │
   │  Phase 3: Merge sandbox     ───► merge → host bare repo │
   │     ▼   (Phase 1+2+3 atomic unit)                       │
   │  Phase 4: Upstream push     ───► GitHub etc. (host-only)│
   │           (independently retried)                       │
   └─────────────────────────────────────────────────────────┘
```

See [`audit.md`](audit.md) for the audit phase in detail.

## Process / trust boundaries

| Component                    | Lives in           | Trusts             | Holds upstream creds? | Holds agent API keys? |
|------------------------------|--------------------|--------------------|-----------------------|-----------------------|
| Orchestrator (REST + workers)| Host               | Host OS only       | **Yes**               | Yes (to inject)       |
| Work / Rework sandbox        | VM (Multipass/KVM) | Nothing            | No                    | Yes (only its own)    |
| Audit-tool sandbox           | VM (Multipass/KVM) | Nothing            | No                    | **No**                |
| Audit-LLM / Merge sandbox    | VM (Multipass/KVM) | Nothing            | No                    | Yes (only its own)    |
| Host git server              | Host (or sidecar)  | Sandbox network    | No                    | No                    |
| Upstream remote (e.g. GitHub)| External           | —                  | —                     | —                     |

The merge phase is **agent-driven**: it gets agent credentials so the
agent can resolve merge conflicts and run the project's test suite. The
orchestrator verifies merge state (head matches the expected post-merge
SHA, working tree is clean) before allowing phase 4. The egress reduction
that protects the merge phase against exfiltration is the project's
`merge` network profile — typically the same as `work`, or stricter.

## State machine

```
Queued → Working → WorkComplete ─┬─→ Auditing ─pass─→ AuditPassed ─→ Merging ─→ Merged ─→ UpstreamPushing ─→ Done
                                 │      │
                                 │      └─fail─→ Reworking ─→ Auditing (loop)
                                 │                  │
                                 │                  └─no-changes─→ Failed
                                 │      │
                                 │      └─maxIters─→ AuditFailed (terminal)
                                 │
                                 └─(no auditors registered)─→ Merging ─→ ...

Cancelled (via DELETE /workitems/{id}) is reachable from any non-terminal state.
```

Work + Audit + Merge are the atomic unit: failure of any of them flips the
item to `Failed` (or `AuditFailed` for the specific case of audit not
converging). UpstreamPushing is a separate retryable step — its failures
are bounded by `UpstreamPushMaxAttempts` and don't roll back the local merge.

## Plugin points

Every meaningful behaviour is behind an interface in `CodeyBox.Core`. The
intent is that you can swap any of these without touching the orchestrator:

| Interface                | Default impl                         | Replace when…                          |
|--------------------------|--------------------------------------|----------------------------------------|
| `ISandboxProvider`       | `ProcessSandboxProvider` (UNSAFE)    | Going to production → Multipass        |
| `IGitHost`               | `LocalGitHost`                       | You need a remote git daemon model     |
| `IAgentRunner`           | `Claude` / `Copilot` / `Codex`       | Adding a new agent (Aider, Goose, …)   |
| `IAgentRegistry`         | `AgentRegistry`                      | Multi-tenant routing                   |
| `IAuditor`               | (none — opt-in)                      | Adding code-quality / security review  |
| `IAuditorRegistry`       | `AuditorRegistry`                    | Custom ordering or filtering           |
| `ICredentialProvider`    | `EnvironmentCredentialProvider`      | Vault, AWS Secrets Manager, etc.       |
| `IPullRequestService`    | `InMemoryPullRequestService`         | You want PR records in SQLite/Gitea    |
| `IUpstreamRemote`        | `Noop` / `GitGeneric` / `GitHub`     | New forge integration (Gitea, Forgejo) |
| `IWorkItemStore`         | `SqliteWorkItemStore`                | External Postgres etc.                 |
| `ITaskQueue`             | `InMemoryTaskQueue`                  | Multi-process orchestration            |

## Why this shape

* **Loose coupling.** Adam wants every subsystem swappable. Concrete types
  never appear in cross-component method signatures; everything is the Core
  interface.
* **Atomic Work+Merge.** Mirrors how a human reviewer reasons: either the
  feature lands cleanly on the integration branch or it doesn't. Half-applied
  state is the worst of all worlds.
* **Upstream is a second tier.** Pushing to GitHub failing shouldn't poison
  the local result. The local bare repo is the source of truth; upstream is
  replication. When a per-work-item bare repo already exists,
  `EnsureRepositoryAsync` refreshes the configured base branch from a
  non-null upstream seed URL before reuse, preserving work-branch refs and
  warning rather than failing if the refresh cannot reach upstream. The
  refresh first replaces sandbox-writable bare-repo config with a minimal
  host-controlled config, so repo-local credential helpers, SSH commands,
  and URL rewrites cannot influence host git.
* **Credentials follow least privilege.** Each sandbox sees only the
  minimum it needs. Tool-only audit sandboxes see no agent secrets.
  Upstream creds live only in the orchestrator process and never cross
  the sandbox boundary.

## What's not built yet

* The Gitea / GitHub-PR upstream variant currently pushes the merged
  branch directly; opening a real upstream PR is a future enhancement.
* `scripts/setup-host-networks.sh` resolves hostnames at setup time and
  writes IP rules. CDN rotation past resolved IPs fails closed (correct
  direction); for high-stakes use, swap the per-profile chain for an
  allowlist-aware proxy (squid, mitmproxy with hostname allowlist).
