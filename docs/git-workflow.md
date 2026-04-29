# Git workflow

How a work item flows through the git topology, and why each step looks the
way it does.

## Topology

```
                 ┌──────────────────────────────┐
                 │  Upstream remote (GitHub)    │  ← orchestrator pushes here
                 │   (optional, host-only)      │     using its own creds
                 └────────────┬─────────────────┘
                              │ phase 3
                ┌─────────────▼──────────────────┐
                │ Host bare repo  (per work item)│  ← source of truth
                │ /var/lib/codeybox/repos/<id> │
                └─┬──────────┬───────────────┬───┘
       phase 1   │  phase 2 │      phase 3   │
       (clone+   ▼  (audit+ ▼     (clone+    ▼
        push)              rework)            agent merge+push)
        ┌─────────────┐  ┌─────────────────┐  ┌─────────────────┐
        │ Work sbx    │  │ Audit sandboxes │  │ Merge sbx       │
        │(agent+creds)│  │ tool: no creds  │  │ (agent+creds,   │
        │             │  │ llm: agent creds│  │  merge profile) │
        └─────────────┘  └─────────────────┘  └─────────────────┘
```

The host bare repo is the *source of truth*. Work, audit, rework, and
merge sandboxes each clone and push to it. The orchestrator pushes from
it to upstream. Sandboxes never see the upstream URL or creds.

## Phase 1: Work

```bash
# Inside the work sandbox:
git clone /repos/<id>.git /work
cd /work
git checkout -B codeybox/<id>          # work branch off baseBranch
git config user.email codeybox@local
git config user.name CodeyBox

# (the agent runs here — claude / copilot / codex)
# It edits files in /work and may or may not commit.

git add -A
git diff --cached --quiet && exit 1      # fail if no changes
git commit -m "codeybox: <title>"
git push origin codeybox/<id>:codeybox/<id>
```

The orchestrator then opens a PR record via `IPullRequestService`. With the
default in-memory impl this is just metadata; with a Gitea/Forgejo backend
it would be a real PR on a self-hosted forge.

## Phase 2: Audit + rework loop

Skipped if no auditors are registered. See [`audit.md`](audit.md) for
the full breakdown. Tool auditors run in a credential-free sandbox; LLM
auditors run in a sandbox with agent credentials. On failure the agent
reworks (pushing further commits onto the same `codeybox/<id>` branch)
and the loop reruns. On `MaxIterations` without convergence the work
item flips to `AuditFailed`.

## Phase 3: Merge (agent-driven)

The merge sandbox is a **fresh** sandbox under the project's `merge`
network profile. The agent is invoked to perform the merge — it can
resolve `git merge` conflicts and run the project's test suite if the
profile allows the necessary egress. The orchestrator then verifies
merge state before pushing:

```bash
# The orchestrator verifies that, post-agent:
#   1. The current branch is <baseBranch>
#   2. HEAD's parents include the workBranch's tip
#   3. Working tree is clean
# Failures here flip the item to Failed; the agent cannot push something
# other than the merge.

git push origin <baseBranch>:<baseBranch>
```

The merge is `--no-ff` so the work-branch is preserved in the history.

## Phase 4: Upstream push (host)

The orchestrator calls `IUpstreamRemote.CompleteAsync` with the work branch
name, base branch, and the merge SHA from phase 3. The implementation
decides what "complete" means for its upstream type.

### git-generic

```bash
git -C /var/lib/codeybox/repos/<id>.git push <url> <baseBranch>:<baseBranch>
```

Pushes the merged base branch. No PR concept.

### GitHub

```
1. git push origin <workBranch>:<workBranch>   # token via GIT_ASKPASS
2. POST https://api.github.com/repos/{owner}/{repo}/pulls
       { "title": "<pr title>", "head": "<workBranch>", "base": "<baseBranch>" }
3. [if AutoMerge=true]
   PUT  https://api.github.com/repos/{owner}/{repo}/pulls/{n}/merge
       { "merge_method": "<merge|squash|rebase>" }
```

The PAT is set as a per-request `Authorization: token <PAT>` header; it
never appears on argv, in config files, or in log output (scrubbed from
any error message). The named `HttpClient "github-upstream"` carries the
`User-Agent: codeybox` header required by the GitHub API.

**Soft failures** are handled gracefully without retrying:

| Status | Scenario | Behaviour |
|---|---|---|
| 422 on POST /pulls | Branch already has an open PR | Log warning, return BranchPushed=true, PR fields null |
| 405 on PUT /pulls/N/merge | Branch protection prevents merge | Log warning, return PullRequestUrl set, MergedSha null |

All other errors throw, triggering the orchestrator retry loop.

If every attempt fails (network outage, unexpected HTTP errors), the item
is marked Failed after `UpstreamPushMaxAttempts` retries with
`UpstreamPushBackoff` between them. The local merge in the host bare repo
is unaffected; an operator can re-queue to retry phase 4 independently.

## Why bare repos per work item

* **Isolation.** Work item A's branch can never end up in work item B's
  bare repo, so a misbehaving agent on A can't pollute B.
* **Cleanup.** Disposing the bare repo is `rm -rf` — no hunting for orphan
  branches.
* **Concurrency.** Two work items on the same upstream can run in parallel
  without sharing any git state until they hit upstream.

The trade-off is more disk usage. For deployments with many concurrent
items, future work could share a single bare repo across items keyed by
upstream URL — `IGitHost` is the abstraction to evolve.

## Conflict handling

The agent-driven merge phase resolves conflicts directly: the agent is
invoked with the merge state and can edit files, run tests, and commit.
The orchestrator's post-merge verification catches the case where the
agent didn't actually produce a valid merge and flips the item to
`Failed` rather than letting it push.
