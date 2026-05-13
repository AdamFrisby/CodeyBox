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

See [`audit.md`](audit.md) for the audit phase in detail. Built-in language
presets are config-driven YAML resources; see [`languages.md`](languages.md)
for the schema and override rules. LLM audit focus prompts and the review frame
are also config-driven; see [`audit-types.md`](audit-types.md).

## Auditor Profiles

Projects can define named auditor profiles under `Audit.Profiles`. The
top-level `Audit` object remains the backwards-compatible `default` profile;
configs without `Profiles` behave as before. `Audit.Profile` selects the
project-default profile for all work items in that project. Per-work-item
profile overrides are intentionally out of scope today.

A profile is a complete audit bundle: languages, audit types, custom auditors,
iteration limits, agent routing, and excluded auditor names. The composer
resolves the selected profile before expanding language and audit-type presets,
then removes any exact auditor-name exclusions.

CodeyBox ships a built-in `uat` profile for UAT/test-plan generation work. It
keeps C# format/build/test checks, `security:gitleaks`, `security:semgrep`,
`security:llm-review`, and `cheating:deterministic-patterns`; it omits
`completeness:llm-review` and `cheating:llm-review` because those reviewers
were repeatedly blocking on the meta-shape of UAT lists rather than substantive
code-quality signals. The profile sets `MaxIterations` to 5 because UAT audit
cycles tend to plateau quickly.

## Process / trust boundaries

| Component                    | Lives in           | Trusts             | Holds upstream creds? | Holds agent API keys? |
|------------------------------|--------------------|--------------------|-----------------------|-----------------------|
| Orchestrator (REST + workers)| Host               | Host OS only       | **Yes**               | Yes (to inject)       |
| Work / Rework sandbox        | VM (Multipass/KVM) | Nothing            | No                    | Yes (only its own)    |
| Audit-tool sandbox           | VM (Multipass/KVM) | Nothing            | No                    | **No**                |
| Audit-LLM / clean-merge sandbox | VM (Multipass/KVM) | Nothing         | No                    | Yes (only its own)    |
| Conflict resolver            | Text-only VM sandbox | Conflict text only | No                 | Yes (resolver only) |
| Host git server              | Host (or sidecar)  | Sandbox network    | No                    | No                    |
| Upstream remote (e.g. GitHub)| External           | —                  | —                     | —                     |

The merge phase is host-verified. Before accepting an agent-produced merge,
the orchestrator runs host-side `git merge-tree --write-tree --no-messages`
against the pre-merge main commit and the work tip.

For clean merges, the agent commit tree must exactly match the host
`merge-tree` result, and the accepted commit must keep both the pre-merge main
commit and the work tip in its ancestry. For conflicted merges, the normal
repository-mounted merge agent runner is not invoked. The host first creates the
conflicted working tree, reads only the conflicted file contents, records each
`<<<<<<<` ... `>>>>>>>` hunk as the marker span in the conflicted merge-tree
file, and sends that text through `ITextOnlyAgentRunner`. That call is pure text-in/text-out:
no repository checkout, shell, filesystem, agent tools, writable result file, or
model-controlled network is exposed to the untrusted conflict text. The resolver
can only return complete replacement contents for exactly the conflicted paths;
the host applies those contents to the merge worktree.

After the host writes those returned contents and creates the merge commit, it
applies a deterministic scope fence before updating main. The final
conflict-baseline-to-resolved changed-file set must exactly equal the
conflicted file set, and each conflict-baseline-to-resolved changed line in
those files is checked in conflicted-baseline coordinates. Every changed old-side
line must fall inside a conflict marker span plus
`Audit.MergeScopeBufferLines` context lines. The default buffer is 5. New files,
deletes, renames, edits to non-conflicted files, missing conflicted-file edits,
and whitespace-only edits outside the allowed ranges are rejected and the work
item enters `MergeConflictResolutionFailed`.

This deterministic scope fence is the security boundary. The optional merge
security review is an LLM text review over the resolved conflict diff in a
pure text-in/text-out call with no repository checkout, shell, filesystem, agent
tools, writable result file, or model-controlled network. It is advisory-only:
it has no authority to fail the merge because it reads the same untrusted
conflict content as the resolver. Findings are logged for operator review, but
review failures and finding-persistence failures do not block the merge; only
host git verification and the scope fence gate the push.

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

Merging can also terminate as `MergeConflictResolutionFailed` when host-side
merge verification or the scope fence rejects a conflict resolution.

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
  non-null upstream seed URL before reuse; if no base branch is configured,
  it resolves and refreshes the upstream-advertised default branch instead
  of assuming `main`. The refresh preserves work-branch refs and warns
  rather than failing if the upstream fetch exits non-zero. It first
  replaces sandbox-writable bare-repo config with a minimal host-controlled
  config, so repo-local credential helpers, SSH commands, and URL rewrites
  cannot influence host git. Host git commands also set `core.hooksPath`
  to an empty host-controlled directory, so sandbox-written bare-repo hooks
  cannot run during ref updates. After refresh and before any work, audit,
  or merge agent invocation, pickup rebases the item's configured work branch
  onto the refreshed base inside that item's sandboxed bare repository and
  force-pushes it back with a lease. This includes explicit work-branch names
  supplied through the API or replay flow; the branch still must differ from
  the base branch. First pickup skips this because no work branch exists yet.
  Rebase conflicts use the same constrained text-only conflict resolver
  contract as merge conflicts: conflict hunks are extracted from bounded,
  canonicalized in-worktree file reads, the +/- buffer scope fence is verified
  deterministically, and advisory security review is reused when audit
  reporting is configured. The rebase force-push is limited to the
  server-owned per-item `codeybox/<work-item-id-prefix>` branch; explicit API
  or replay branches outside that exact ref are left untouched. Items resumed
  from `Merged` for upstream-push recovery skip pickup rebase because no work,
  audit, or merge phase will run. If resolution fails, the rebase is aborted
  before pushing and the item transitions to `MergeConflictResolutionFailed`.
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
