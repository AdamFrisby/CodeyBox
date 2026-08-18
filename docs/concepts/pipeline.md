# The pipeline in git terms

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

On every `EnsureRepositoryAsync` call with a non-null upstream seed URL,
an existing host bare repo refreshes the configured base branch from that
upstream before the sandbox clone. If no base branch is configured, the
host resolves the upstream-advertised default branch and refreshes that
branch instead of assuming `main`. The fetch updates only the selected
base branch ref, so per-work-item refs such as `codeybox/<id>` are
preserved. Before the host fetch, the orchestrator replaces the
sandbox-writable bare-repo config with a minimal host-controlled config
so repo-local credential helpers, SSH commands, and URL rewrites cannot
influence the host command. Host git commands set `core.hooksPath` to an
empty host-controlled directory, so hooks written under the bare repo are
not executed during ref updates.
If the upstream fetch exits non-zero, the orchestrator logs a redacted
warning and continues with the previous local tip instead of deleting the
bare repo.

After that refresh, pickup performs a sandboxed work-branch freshness check
before any work, audit, or merge agent sees the repository. If the work branch
already exists, the sandbox fetches the refreshed base and work refs, checks
whether the refreshed base is already an ancestor of the work branch, and when
needed runs:

```bash
git checkout -B <workBranch> origin/<workBranch>
git rebase --keep-empty origin/<baseBranch>
git push --force-with-lease=refs/heads/<workBranch>:<oldTip> origin HEAD:refs/heads/<workBranch>
```

First pickup is a no-op because there is no work-branch ref yet. Pickup rebase
is only allowed to force-push the server-owned per-item
`codeybox/<work-item-id-prefix>` branch. Explicit work branches created by the
API or replay flow, including other `codeybox/*` names, are not rewritten by
pickup rebase. Items resumed from `Merged` for upstream-push recovery also skip
pickup rebase because they will not enter work, audit, or merge. Rebase
conflicts are read through bounded, canonical in-worktree file reads, abort the
in-sandbox rebase before any push on failure, and move the work item to
`MergeConflictResolutionFailed`, the same operator-facing failure state used by
scope-fenced merge conflict resolution.

## Phase 1: Work

```bash
# Inside the work sandbox:
git clone /repos/<id>.git /work
cd /work
git checkout -B codeybox/<id> origin/<baseBranch>      # first pickup
# or, on retry after the pickup rebase above:
git checkout -B codeybox/<id> origin/codeybox/<id>

# Identity resolution (precedence: project > host global git config > fallback)
git config user.email <resolved-email>
git config user.name  <resolved-name>

# (the agent runs here — claude / copilot / codex / gemini)
# It edits files in /work and may or may not commit.

git add -A
# Strip suggestions file before committing — it is advisory metadata,
# not part of the work branch history. Exit code ignored if not staged.
git rm --cached -- .codeybox/suggestions.json
git diff --cached --quiet && exit 1      # fail if no changes
git commit -m "codeybox: <title>

Co-Authored-By: CodeyBox <noreply@codeybox.invalid>"
git push origin codeybox/<id>:codeybox/<id>
```

If the sandbox push of the work branch is rejected as non-fast-forward,
the orchestrator performs exactly one in-sandbox reconcile attempt:

```bash
git fetch --no-tags origin +refs/heads/<workBranch>:refs/remotes/origin/<workBranch>
git rebase origin/<workBranch>
git push origin <workBranch>:<workBranch>
```

This is Approach B from the retry design: preserve the prior attempt's
reachable commits and replay the new sandbox commits on top. The rejected
pre-clean alternative would delete or rename the stale bare-repo ref before
the retry; that is simpler, but it makes attempt history handling a separate
observability concern and does not match the host-side upstream-push recovery.
Rebase conflicts fail the work item with a clear manual-resolution error.

### Git identity propagation

By default the orchestrator reads the host's global git config at startup
(`git config --global user.name` / `user.email`) and uses those values for all
sandbox commits. This lets `git blame` / `git log --author=alice` show the real
operator who triggered the work item.

Resolution order (first match wins):

1. **`Project.GitAuthorName` / `Project.GitAuthorEmail`** — set both fields on a
   project to override for that project only.
2. **Host global git config** — read once at orchestrator startup via
   `git config --global`. Requires `git` on PATH and `~/.gitconfig` configured.
3. **Synthetic fallback** — `CodeyBox <codeybox@local>`. Used when no global git
   config is found (fresh containers, CI runners without a configured identity).
   A warning is logged at startup.

### Co-Authored-By trailer

Every commit produced by the orchestrator or instructed to the agent includes:

```
Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
```

The `.invalid` TLD (per RFC 2606) signals "not a real email" while GitHub still
renders the trailer in the PR conversation view. The operator's email never
appears in this trailer — only `noreply@codeybox.invalid`.

The orchestrator then opens a PR record via `IPullRequestService`. With the
default in-memory impl this is just metadata; with a Gitea/Forgejo backend
it would be a real PR on a self-hosted forge.

## Phase 2: Audit + rework loop

Skipped if no auditors are registered. See [`audit.md`](../quality/audit.md) for
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
2. Generate PR description (LLM or static fallback — see below)
3. POST https://api.github.com/repos/{owner}/{repo}/pulls
       { "title": "<pr title>", "head": "<workBranch>", "base": "<baseBranch>",
         "body": "<generated description>" }
4. [if AutoMerge=true]
   PUT  https://api.github.com/repos/{owner}/{repo}/pulls/{n}/merge
       { "merge_method": "<merge|squash|rebase>" }
```

The PAT is set as a per-request `Authorization: token <PAT>` header; it
never appears on argv, in config files, or in log output (scrubbed from
any error message). The named `HttpClient "github-upstream"` carries the
`User-Agent: codeybox` header required by the GitHub API.

#### LLM-generated PR descriptions

When an `IPullRequestDescriptionGenerator` is wired up (default in production)
and `Upstream.PrDescription.Enabled = true`, step 2 generates a narrative PR
body from:

- `git diff --stat` (compact change summary)
- Full `git diff` between base and work branches, capped at `MaxDiffBytes`
  (default 32 KB) and **truncated from the middle** — equal portions from
  the start and end are preserved and a `[… N bytes truncated …]` marker
  is inserted — so the LLM sees both the first and last diff hunks of a
  large changeset.
- The original work-item prompt (truncated to 2 KB).
- Titles of audit findings addressed during rework iterations.
- Last 2 KB of agent stdout (the agent's concluding reasoning).

The generator runs the configured agent (`GeneratorAgent`, default `"claude"`)
inside a minimal sandbox. Its output is sanitised through
`RawOutputRedactor` before use, so accidentally-committed tokens in the
diff or echoed back by the LLM are replaced with `***`.

**Fallback semantics** — the generator is non-blocking:

| Condition | Behaviour |
|---|---|
| `Enabled = false` | Static template used immediately; no LLM call |
| Generator succeeds | LLM body used as PR description prefix |
| Generator times out (`Timeout`, default 30 s) | Warning logged; static template used |
| Generator throws | Warning logged; static template used |

The standard footer (`Co-Authored-By: CodeyBox <noreply@codeybox.invalid>`
plus a generated-with link) is appended to the PR body in all cases.

**Configuration** (`appsettings.json`, under `Upstream` for a GitHub project):

```json
"Upstream": {
  "Kind": "github",
  ...
  "PrDescription": {
    "Enabled": true,
    "GeneratorAgent": "claude",
    "GeneratorModelId": null,
    "MaxDiffBytes": 32768,
    "Timeout": "00:00:30",
    "SandboxImageReference": "ghcr.io/myorg/codeybox-agent:latest",
    "AgentAllowedHosts": ["api.anthropic.com"]
  }
}
```

**Cost** — each PR description adds approximately 5 K input tokens and
500 output tokens. This appears in the per-work-item cost report as a
separate `phase=upstream` row. Operators on tight quotas can set
`Enabled: false`.

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
