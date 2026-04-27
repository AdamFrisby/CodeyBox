# CodeyBox

C#/.NET orchestration framework that runs LLM coding agents (Claude Code,
GitHub Copilot CLI, OpenAI Codex CLI, …) inside VM-isolated sandboxes and
merges their output through a controlled git workflow. The parent
orchestrator runs **no LLMs** — its only job is to schedule sandboxes and
shepherd state.

## Why

Plain containers share the host kernel; an agent that finds a Linux LPE
escapes into the host. CodeyBox runs each agent inside a Firecracker
microVM (via Kata Containers or crun-vm/libkrun) so a guest kernel exploit
doesn't reach the host. Credentials are tiered: each sandbox sees only what
it needs, and upstream remote credentials (e.g. a GitHub PAT) live only in
the orchestrator process and are never visible to any sandbox.

## Pipeline

For each work item:

1. **Work sandbox** clones the host bare repo, runs the agent, commits, and
   pushes a feature branch.
2. **Merge sandbox** (separate VM, no agent credentials) clones, merges the
   feature branch, and pushes the target branch.
3. **Upstream push** (host, no sandbox) replicates the target branch to
   GitHub (or any git URL).

Phases 1 and 2 together are the atomic unit: failure of either marks the
item failed. Phase 3 is retried independently.

## Status

Built and building clean:

* `CodeyBox.Core` — interfaces and domain types
* `CodeyBox.Sandbox.Process` — dev-only sandbox provider (UNSAFE)
* `CodeyBox.Git` — host bare-repo manager + in-memory PR records
* `CodeyBox.Agents.{Claude,Copilot,Codex}` — agent runners
* `CodeyBox.Upstream{,.GitHub}` — upstream remotes
* `CodeyBox.Orchestrator` — pipeline runner + worker pool + SQLite store
* `CodeyBox.Api` — REST host wiring everything together

Five sandbox providers — pick by `CodeyBox.SandboxProvider`:

| Provider          | Setup                                   | Status                          |
|-------------------|-----------------------------------------|---------------------------------|
| `process`         | None                                    | UNSAFE; dev only                |
| `bubblewrap`      | `apt install bubblewrap`                | **Working, integration-tested** |
| `gvisor`          | install runsc + 1 line user config      | Code-reviewed                   |
| `kata` (QEMU)     | install kata + `usermod -aG kvm`        | Code-reviewed                   |
| `kata` (Firecracker) | as above + edit `/etc/kata-containers/configuration.toml` | Code-reviewed |
| `crun-vm`         | install crun-vm + register OCI runtime  | Code-reviewed                   |

See `docs/sandbox-providers.md` for the full setup and trade-offs of each.

See [`docs/`](docs/README.md) for the full write-up. **Read
[`docs/security.md`](docs/security.md) before deploying.**

## Build

```bash
dotnet build CodeyBox.slnx
```

## Run (dev)

The default DI wiring uses `Sandbox.Process`, which is **not safe** for
real prompts. It exists to develop and test the orchestrator pipeline.

```bash
export CODEYBOX_CLAUDE_API_KEY=...
dotnet run --project src/CodeyBox.Api
```

POST a work item:

```bash
curl -X POST http://localhost:5000/workitems \
  -H 'content-type: application/json' \
  -d '{
    "title": "demo",
    "prompt": "Add a hello.txt file with the word hello.",
    "repositoryUrl": "/path/to/some/local/seed.git",
    "agent": "claude"
  }'
```
