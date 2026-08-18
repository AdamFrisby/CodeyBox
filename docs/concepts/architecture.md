# CodeyBox architecture

CodeyBox is a C#/.NET orchestration framework that runs LLM coding agents
(Claude Code, GitHub Copilot CLI, OpenAI Codex CLI, ...) inside VM-isolated
sandboxes and merges their output through a controlled git workflow. Managed
projects are language-agnostic; Python, Node, Go, Rust, C#, and custom stacks
all enter through the same project and auditor configuration.

No coding agent ever runs on the host: every model call that touches a
repository happens through an agent CLI inside a sandbox. The orchestrator's job
is to schedule those sandboxes and shepherd state. It does make a small, fixed
set of its own HTTP calls — quota and smoke probes, model listing, changelog
summarisation, and the deliberately tool-free text-only calls used for plan
review — none of which hand a repository to a model.

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
   │     │   mechanical fixers → credential-free sandbox     │
   │     │   tool auditors     → credential-free sandbox     │
   │     │   LLM auditors      → sandbox with agent creds    │
   │     │   on fail: rework sandbox → loop until pass       │
   │     ▼   or AuditFailed at MaxIterations                 │
   │  Phase 3: Merge sandbox     ───► merge → host bare repo │
   │     ▼   (Phase 1+2+3 atomic unit)                       │
   │  Phase 4: Upstream push     ───► GitHub etc. (host-only)│
   │           (independently retried)                       │
   └─────────────────────────────────────────────────────────┘
```

The audit phase is covered in [`../quality/audit.md`](../quality/audit.md).
Language detection, audit-type prompts, and the LLM review frame all come from
config-driven presets — schema and override rules in
[`../quality/presets.md`](../quality/presets.md).

## Auditor profiles

Projects can define named auditor profiles under `Audit.Profiles`. The top-level
`Audit` object *is* the `default` profile, so a config with no `Profiles` block
needs no changes. `Audit.Profile` picks the project's default profile; there is
no per-work-item override.

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
| Work / Rework sandbox        | VM (Multipass or Incus/KVM) | Nothing    | No                    | Yes (only its own)    |
| Mechanical-edit sandbox      | VM (Multipass or Incus/KVM) | Nothing    | No                    | **No**                |
| Audit-tool sandbox           | VM (Multipass or Incus/KVM) | Nothing    | No                    | **No**                |
| Audit-LLM / clean-merge sandbox | VM (Multipass or Incus/KVM) | Nothing | No                    | Yes (only its own)    |
| Conflict resolver            | Work-item sandbox    | In-sandbox repo CLI | Yes (resolver only) | Yes (resolver only) |
| Host git server              | Host (or sidecar)  | Sandbox network    | No                    | No                    |
| Upstream remote (e.g. GitHub)| External           | —                  | —                     | —                     |

The merge phase is host-verified. Before accepting an agent-produced merge,
the orchestrator runs host-side `git merge-tree --write-tree --no-messages`
against the pre-merge main commit and the work tip.

For clean merges, the agent commit tree must exactly match the host
`merge-tree` result, and the accepted commit must keep both the pre-merge main
commit and the work tip in its ancestry. For conflicted merges, the orchestrator
runs the configured agent's normal CLI shape inside the work-item sandbox
through `IAgentRunner.RunAsync` with a conflict-resolution prompt. The agent
reads conflicted files directly off the sandbox working tree, writes resolutions,
and `git add`'s them; the orchestrator then verifies (no unmerged paths, no
conflict markers in any of the originally-conflicted files) and commits. This
is the same CLI shape the agent uses for any normal pickup, so subscription
credentials (Claude OAuth, Cursor session) are exercised through the supported
client surface rather than a hand-rolled host-side request. The resolver iterates
through a candidate chain (the work item's primary
runner plus its class fallback chain, with at-cap agents deprioritised) until
one candidate produces a clean working tree.

After the in-VM resolver commits the merge, the orchestrator applies a
deterministic scope fence before updating main. The final
conflict-baseline-to-resolved changed-file set must exactly equal the
conflicted file set, and each conflict-baseline-to-resolved changed line in
those files is checked in conflicted-baseline coordinates. Every changed old-side
line must fall inside a conflict marker span plus
`Audit.MergeScopeBufferLines` context lines. The default buffer is 5. New files,
deletes, renames, edits to non-conflicted files, missing conflicted-file edits,
and whitespace-only edits outside the allowed ranges are rejected and the work
item enters `MergeConflictResolutionFailed`.

Because the agent reads files directly off the working tree inside the sandbox,
there is no per-file or per-hunk payload cap: a conflicted file of several
hundred KiB is handled exactly like a small one.

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
Queued ── plan=on ─→ Planning → PlanReview → PlanApproved ┐
   │                                                      │
   └──────────────── plan=off ────────────────────────────┘
                                                          ↓
Working → WorkComplete ─┬─→ Auditing* ─pass─→ AuditPassed ─→ Merging ─→ Merged ─→ UpstreamPushing ─→ Done
                        │      │
                        │      └─fail─→ Reworking ─→ Auditing* (loop)
                        │                  │
                        │                  └─no-changes─→ Failed
                        │      │
                        │      └─maxIters─→ AuditFailed (terminal)
                        │
                        └─(no auditors registered)─→ Merging ─→ ...

Merging can also terminate as `MergeConflictResolutionFailed` when host-side
merge verification or the scope fence rejects a conflict resolution.

When the merge phase fails with a conflict the preventive pickup-time rebase
and the merge-phase agent could not resolve, the orchestrator falls through
to a focused conflict-rework iteration: `Merging` → `ReworkingForConflict` →
back to `Merging`. The original work agent is re-engaged on the existing
work branch (commits intact) with a prompt that explains the rebase-in-progress
state, forbids destructive actions, and documents the `SEMANTIC_INCOMPATIBLE:`
escape hatch. Capped at one iteration per merge attempt; the second failure
parks at `MergeConflictResolutionFailed`. Cost rows for this iteration use the
phase key `conflict_rework` so operators can measure how much budget the
third-line fallback is burning.

Cancelled (via DELETE /workitems/{id}) is reachable from any non-terminal state.
```

`Planning` is optional and gated by the `plan` knob, which defaults to `off`.
The planning turn runs in a discardable sandbox and persists only the PLAN
artifact on the work item.

`PlanReview` is a review loop analogous to the audit loop. Auditors declare a
multi-valued `IAuditor.Targets` set (default `{ Code }`); the composer selects
`{ Plan }`-target reviewers for this phase (respecting the same
config-driven active set as code audit) and evaluates the PLAN artifact.
Blocking findings send the plan back for a plan-rework turn (the planning agent
receives only bounded categories, severities, counts, and stable finding IDs,
never model-authored reviewer prose) and re-review, up to
the hot-reloadable `CodeyBox:PipelineTuning:MaxPlanReviewIterations`; the plan must pass before
`PlanApproved` flows into implementation. `CodeyBox:PlanReview:UseAuditors`
is compatibility-only and ignored; Plan-target auditors are always composed by
the pipeline, and canonical PLAN parsing occurs at the artifact boundary before
the selected auditor panel is authoritative. Plan-target reviewers run through
the shared auditor credential, quota, persistence, and post-processing path,
with `AuditContext.Target == Plan` and the PLAN artifact carried on the context.
LLM plan reviewers additionally require a host-side text-only provider call
that places trusted review instructions in the system channel and the untrusted
task/PLAN data in a separate user channel. Moving subjective / architectural review to the cheap PLAN
artifact lets the code-stage audit stay objective and low-cycle.

The planning loop is closed on the implementation side by three cooperating
pieces. First, `PlanApproved` feeds the reviewed plan into the work-phase prompt,
so the agent implements against it. Second, a code-target **`plan:adherence`**
reviewer (config-gated by `CodeyBox:PlanAdherence`, enabled by default) compares
the diff to the approved plan and blocks only on *unjustified* deviations from
the agreed approach; it self-limits to planned items (no plan artifact → no-op),
so unplanned items are unaffected. Third, the code audit is **rebalanced** for
planned items: because the subjective/architectural judgement already happened at
the plan stage, blocking findings from the configured approach reviewer(s)
(default `architecture:llm-review`, via
`CodeyBox:PipelineTuning:PlannedItemAdvisoryAuditors`) are demoted to advisory —
still recorded, no longer forcing a rework cycle — while the objective gates
(build, tests, security, cheating, completeness, plan-adherence) keep full
blocking authority. No auditor is removed; only which findings block is
reweighted, and only for planned items. The thesis — subjective review on a small
plan plus objective + adherence review on the code — is measured by the
`planned` tag on the `codeybox.audit.iterations` and
`codeybox.audit.first_audit.outcome` instruments, which lets dashboards compare
code-stage iteration count and first-audit pass-rate for planned vs unplanned
items and prove (or disprove) that planning net-reduces cycles.

`*` Before each `Auditing` run, configured mechanical fixers may run in a
credential-free sandbox and commit deterministic normalizations. This is a
phase action, not a durable work-item state.

Work + Audit + Merge are the atomic unit: failure of any of them flips the
item to `Failed` (or `AuditFailed` for the specific case of audit not
converging). UpstreamPushing is a separate retryable step — its failures
are bounded by `UpstreamPushMaxAttempts` and don't roll back the local merge.

## Plugin points

Every meaningful behaviour is behind an interface in `CodeyBox.Core`. The
intent is that you can swap any of these without touching the orchestrator:

| Interface                | Default impl                         | Replace when…                          |
|--------------------------|--------------------------------------|----------------------------------------|
| `ISandboxProvider`       | Configured provider (`process` is UNSAFE) | Production VM isolation → Multipass or Incus |
| `IGitHost`               | `LocalGitHost`                       | You need a remote git daemon model     |
| `IAgentRunner`           | `Claude` / `Copilot` / `Codex`       | Adding a new agent (Aider, Goose, …)   |
| `IAgentRegistry`         | `AgentRegistry`                      | Multi-tenant routing                   |
| `IAuditor`               | (none — opt-in)                      | Adding code-quality / security review  |
| `IAuditorRegistry`       | `AuditorRegistry`                    | Custom ordering or filtering           |
| `IMechanicalFixer`       | `dotnet-format`                      | Adding deterministic no-model normalizers |
| `ICredentialProvider`    | `EnvironmentCredentialProvider`      | Vault, AWS Secrets Manager, etc.       |
| `IPullRequestService`    | `InMemoryPullRequestService`         | You want PR records in SQLite/Gitea    |
| `IUpstreamRemote`        | `Noop` / `GitGeneric` / `GitHub`     | New forge integration (Gitea, Forgejo) |
| `IWorkItemStore`         | `SqliteWorkItemStore`                | External Postgres etc.                 |
| `ITaskQueue`             | `InMemoryTaskQueue`                  | Multi-process orchestration            |

## Why this shape

* **Loose coupling.** Every subsystem is swappable. Concrete types never
  appear in cross-component method signatures; everything is the Core
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

## Known limits

* `scripts/setup-host-networks.sh` resolves hostnames at setup time and
  writes IP rules. CDN rotation past resolved IPs fails closed (correct
  direction); for high-stakes use, swap the per-profile chain for an
  allowlist-aware proxy (squid, mitmproxy with hostname allowlist).
