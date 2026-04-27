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
                └─────┬───────────────────┬──────┘
                phase 1 (clone+push)  phase 2 (clone+merge+push)
                      ▼                   ▼
              ┌─────────────────┐  ┌─────────────────┐
              │ Work sandbox    │  │ Merge sandbox   │
              │ (agent + creds) │  │ (no agent creds)│
              └─────────────────┘  └─────────────────┘
```

The host bare repo is the *source of truth*. The work and merge sandboxes
each clone and push to it. The orchestrator pushes from it to upstream.
Sandboxes never see the upstream URL or creds.

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

## Phase 2: Merge

The merge sandbox is a **fresh** sandbox — different filesystem, different
network policy (no agent endpoints), no credentials.

```bash
# Inside the merge sandbox:
git clone /repos/<id>.git /work
cd /work
git checkout <baseBranch>
git config user.email codeybox@local
git config user.name CodeyBox
git merge --no-ff -m "codeybox: merge codeybox/<id>" origin/codeybox/<id>
git rev-parse HEAD                       # → captured as merge SHA
git push origin <baseBranch>:<baseBranch>
```

The merge is `--no-ff` so the work-branch is preserved in the history.

## Phase 3: Upstream push (host)

The orchestrator runs on the host:

```bash
# Pseudo: actual impl uses git via Process and never logs the token
git -C /var/lib/codeybox/repos/<id>.git push <upstream> <baseBranch>:<baseBranch>
```

For GitHub: `IUpstreamRemote` impl rebuilds the URL with
`x-access-token:<PAT>` once, in memory, for the single push. The token never
hits a config file, never touches argv, and is scrubbed from any error
message returned to the orchestrator.

If push fails (network, branch protection, conflict), it is retried
`UpstreamPushMaxAttempts` times with `UpstreamPushBackoff` between
attempts. After exhaustion the work item is marked Failed; the local merge
is still in place and an operator can retry by re-queuing.

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

Currently: phase 2's `git merge` fails the merge sandbox if the work branch
has conflicts with `baseBranch`. The work item flips to Failed.

Future: a conflict-resolution phase that re-runs the agent with the
conflict context. This is a natural place to add — it's just a third
sandbox spawn before phase 2 retries.
