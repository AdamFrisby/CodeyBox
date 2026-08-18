# Getting started

From a clean host to a merged change, in about half an hour of setup. This runs
CodeyBox against a scratch repository with no upstream, so nothing you do here
can push to GitHub.

Before you put it near a repository that matters, read
[`concepts/security.md`](concepts/security.md).

## 1. Prerequisites

- **Linux with KVM** (`/dev/kvm` present). Without it you are limited to the
  shared-kernel `bubblewrap` provider or the isolation-free `process` one.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** and **git** on the host.
- **A sandbox provider.** Incus 6.3+ with an existing ZFS or Btrfs pool for a
  persistent headless box; Multipass (`sudo snap install multipass`) for the
  simplest install. [`concepts/sandboxes.md`](concepts/sandboxes.md) covers the
  trade-offs.
- **At least one agent CLI, already authenticated on the host** — Claude Code,
  Codex, Gemini, Cursor, Copilot, opencode, Antigravity, or Crock. Its
  credential is read from a host environment variable and injected into the
  sandbox; see [`concepts/agents.md`](concepts/agents.md) for the variable names.

The agent CLI also has to exist **inside** the sandbox image. CodeyBox never
installs it for you — that is operator config, and forgetting it is the most
common first-run failure (`exit 127`). See
[`reference/sandbox-baselines.md`](reference/sandbox-baselines.md).

## 2. Build

```bash
git clone https://github.com/AdamFrisby/CodeyBox.git
cd CodeyBox
dotnet build CodeyBox.slnx
```

## 3. Set up host networking

For Multipass and Incus, every sandbox attaches to a host bridge whose nftables
rules drop anything not on its allowlist. Create them once, with sudo:

```bash
sudo scripts/setup-host-networks.sh
```

This is the layer an agent cannot switch off from inside its VM, so it is not
optional for anything but local experiments —
[`operating/host-firewall.md`](operating/host-firewall.md).

## 4. Write a project config

One project, one repository, no upstream:

```json
{
  "CodeyBox": {
    "SandboxProvider": "incus",
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://github.com/owner/my-app.git",
        "BaseBranch": "main",
        "Agent": "claude",
        "Upstream": { "Kind": "noop" }
      }
    ]
  }
}
```

`Upstream.Kind: "noop"` keeps every result local: the merge lands in a per-item
bare repository on the host and goes no further. Swap it for `github` once you
trust the setup.

The file is hot-reloaded — editing it does not need a restart. Full schema in
[`concepts/projects.md`](concepts/projects.md) and
[`reference/configuration.md`](reference/configuration.md), or generate a first
draft with the wizard in [`reference/cli.md`](reference/cli.md).

## 5. Run it

```bash
export CODEYBOX_API_KEY=$(openssl rand -hex 32)
export CODEYBOX_EXTRA_CONFIG=/path/to/codeybox.json
export CODEYBOX_CLAUDE_API_KEY=...          # whichever agent you configured
dotnet run --project src/CodeyBox.Api
```

The API binds to `http://localhost:5036` under `dotnet run` (production defaults
to `127.0.0.1:5000`). Every endpoint except `/healthz` needs
`Authorization: Bearer $CODEYBOX_API_KEY`.

## 6. Queue something small

```bash
cd tools/CodeyBox.Cli && dotnet run -- configure   # stores API URL + token
```

```bash
ID=$(dotnet run --project tools/CodeyBox.Cli -- queue add \
       --project my-app \
       --title "Add a hello file" \
       --prompt "Add hello.txt containing the word hello." --quiet)

dotnet run --project tools/CodeyBox.Cli -- queue watch "$ID"
```

`queue watch` streams state transitions over SSE. Expect
`Queued → Working → WorkComplete → Auditing → AuditPassed → Merging → Merged → Done`,
with `Auditing → Reworking → Auditing` loops whenever an auditor blocks.

Keep the first task trivially small. A one-file change proves the whole
pipeline; a large prompt on a first run mostly proves that debugging is slow.

## 7. Look at what happened

```bash
dotnet run --project tools/CodeyBox.Cli -- queue show "$ID"   # state, agent, errors
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/workitems/$ID/costs                   # token spend by phase
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/workitems/$ID/timings                 # where the time went
```

The merged commit lives in that item's bare repository under
`GitRootDirectory`, carrying a `CodeyBox-WorkItem` trailer. Nothing was pushed
anywhere else, because the upstream is `noop`.

For a UI, run the Blazor dashboard — queue, diffs, findings, cost and timing
charts:

```bash
CodeyBoxAdmin__ApiBaseUrl=http://localhost:5036 \
  dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

The dashboard's default `ApiBaseUrl` is `http://localhost:5050`, which matches
neither the `dotnet run` port (5036) nor the production default (5000) — set it
explicitly or the dashboard will show nothing.

## When it does not work

| Symptom | Cause |
|---|---|
| `exit 127`, `<binary>: No such file or directory` | the agent CLI is not installed in the sandbox image |
| Item fails with "Agent produced no changes to commit" | prompt too narrow, or the agent decided nothing was needed |
| `AuditFailed` | auditors never converged within `MaxIterations` — read the findings before re-queueing |
| Sandbox cannot reach the internet | the phase's network profile, or a rotated CDN IP; check `nft list table inet codeybox` |
| Nothing dispatches at all | quota gate or a paused project — check `GET /quota` and `GET /queue/status` |

More in [`operating/running.md`](operating/running.md).

## Next

- [`concepts/architecture.md`](concepts/architecture.md) — how the pieces fit.
- [`concepts/security.md`](concepts/security.md) — the threat model, before production.
- [`quality/audit.md`](quality/audit.md) — choosing which auditors gate a merge.
- [`concepts/agent-classes.md`](concepts/agent-classes.md) — routing across several agents with quota fallback.
