# CodeyBox

C#/.NET orchestration framework that runs LLM coding agents (Claude Code,
GitHub Copilot CLI, OpenAI Codex CLI, ...) inside VM-isolated sandboxes and
merges their output through a controlled git workflow. The framework itself
is implemented in .NET, but the projects it works on can be Python, Node, Go,
Rust, C#, or other stacks through custom auditors. The parent orchestrator
runs **no LLMs** - its only job is to schedule sandboxes and shepherd state.

## Why

Plain containers share the host kernel; an agent that finds a Linux LPE
escapes into the host. CodeyBox runs each agent inside a real VM — the
recommended provider is **Multipass** (KVM-backed, single-package install)
— so a guest kernel exploit doesn't reach the host. Egress is enforced on
the *host* via per-profile nftables bridges, so a compromised agent with
sudo cannot disable its own network policy. Credentials are tiered: each
sandbox sees only what it needs, and upstream remote credentials (e.g. a
GitHub PAT) live only in the orchestrator process and are never visible to
any sandbox.

## Pipeline

For each work item, the orchestrator resolves the project's per-phase
network profile and spawns a fresh sandbox:

1. **Work sandbox** clones the host bare repo, runs the agent, commits, and
   pushes a feature branch.
2. **Audit + rework loop** (skipped if no auditors are registered) runs
   tool auditors in a credential-free sandbox and LLM auditors in a
   sandbox with agent credentials. On failure the agent reworks; on
   convergence we proceed.
3. **Merge sandbox** runs the agent against the merge — `git merge` is the
   nominal path, but the agent can resolve conflicts and run the project's
   tests if the project's `merge` network profile allows it. The
   orchestrator verifies merge state before pushing.
4. **Upstream push** (host, no sandbox) replicates the target branch to
   GitHub (or any git URL).

Phases 1–3 together are the atomic unit: failure of any of them marks the
item failed. Phase 4 is retried independently.

## Commit-message trailers

Every commit the orchestrator produces stamps a trailer block so attribution
survives a DB wipe — `git log --grep 'CodeyBox-Agent: gemini'` is the
source of truth.

```
codeybox: <subject>

CodeyBox-WorkItem: <work-item id>
CodeyBox-Agent: <agent>[/<model>]
CodeyBox-Fallbacks: <from>→<to> (×N <reason>); …      # only if fallbacks happened
Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
```

`CodeyBox-Fallbacks` summarises the work item's `AgentFallbackRecord`
events grouped by `from→to` agent, count, and most-common reason; it is
omitted when no fallback occurred. All trailers are valid RFC-5322 single
lines. See `CodeyBox.Core.CodeyBoxTrailers.Compose` for the canonical
producer.

## Status

Built and building clean:

* `CodeyBox.Core` — interfaces and domain types
* `CodeyBox.Sandbox.Process` — dev-only sandbox provider (UNSAFE)
* `CodeyBox.Git` — host bare-repo manager + in-memory PR records
* `CodeyBox.Agents.{Claude,Copilot,Codex}` — agent runners
* `CodeyBox.Upstream{,.GitHub}` — upstream remotes
* `CodeyBox.Orchestrator` — pipeline runner + worker pool + SQLite store
* `CodeyBox.Api` — REST host wiring everything together

Three sandbox providers — pick by `CodeyBox.SandboxProvider`:

| Provider     | Setup                       | Status                                             |
|--------------|-----------------------------|----------------------------------------------------|
| `process`    | None                        | UNSAFE; dev only                                   |
| `bubblewrap` | `apt install bubblewrap`    | **Working, integration-tested** (shared kernel)    |
| `multipass`  | `snap install multipass`    | **Working, integration-tested (kernel isolation)** |

See `docs/sandbox-providers.md` for the full setup and trade-offs of
each.

Projects that need GUI build/test plumbing can set `GraphicalSandbox: true`.
With Multipass this routes work and rework through the conventional `graphical`
network profile, uses the `cb-baseline-graphical` baseline, and starts an
XFCE/Xvfb desktop with screenshot and input synthesis exposed through the
sandbox API. Audit sandboxes use the graphical flavor when the auditor declares
`AuditCapabilities.Graphical`.

See [`docs/`](docs/README.md) for the full write-up. **Read
[`docs/security.md`](docs/security.md) before deploying.**

## Build

```bash
dotnet build CodeyBox.slnx
```

## Test Host Prerequisites

The test suite creates host-side file watchers for hot-reload and credential
rotation coverage. On Linux CI hosts, set a higher inotify watch limit before
running the full suite:

```bash
sudo sysctl fs.inotify.max_user_watches=524288
sudo sysctl fs.inotify.max_user_instances=1024
```

The test assembly also prints this guidance at startup when it detects lower
Linux inotify limits.

For managed projects, configure the audit language that matches the repo:
`"Languages": ["python"]`, `"Languages": ["node"]`, or
`"Languages": ["csharp"]` all use the same preset mechanism.

## Run (dev)

The default DI wiring uses `Sandbox.Process`, which is **not safe** for
real prompts. It exists to develop and test the orchestrator pipeline.

```bash
export CODEYBOX_CLAUDE_API_KEY=...
dotnet run --project src/CodeyBox.Api
```

POST a work item (project must be configured first — see
[`docs/projects.md`](docs/projects.md)):

```bash
curl -X POST http://localhost:5000/workitems \
  -H 'authorization: Bearer <CODEYBOX_API_KEY>' \
  -H 'content-type: application/json' \
  -d '{
    "projectId": "my-app",
    "title": "demo",
    "prompt": "Add a hello.txt file with the word hello.",
    "agent": "claude"
  }'
```

## Host-side egress enforcement (recommended)

For Multipass, egress filtering belongs on the host — an in-VM firewall
would be voluntary (a compromised agent with sudo could flush it), so
the orchestrator installs none. CodeyBox ships
`scripts/setup-host-networks.sh` which (with sudo, once) creates a Linux
bridge per network profile and writes nftables rules that drop
everything not on the profile's allowlist. Profiles support three modes:
no egress, "internet" (block RFC1918/link-local/cloud-metadata), or a
specific hostname allowlist. Per-project, per-phase profile selection
lives in project config. See [`docs/host-firewall.md`](docs/host-firewall.md).
