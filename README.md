# CodeyBox

**An autonomous coding orchestrator.** Hand it a task — a title and a prompt
against one of your repos — and CodeyBox picks a coding agent, runs it inside
a throwaway VM, reviews the result, resolves merge conflicts, and lands the
change on your branch (and on GitHub, if you point it there). You stay in the
loop for product decisions; it handles the delivery grind.

It drives a *fleet* of agent CLIs — Claude Code, OpenAI Codex, GitHub Copilot,
Cursor, Gemini, opencode — and routes each task to whichever one is best and
available, falling back automatically when a provider hits a rate limit. The
orchestrator itself runs **no LLMs**: it schedules sandboxes, gates quality,
tracks spend, and keeps state durably across restarts.

And because every agent is boxed in a real VM behind a host-enforced firewall,
it's one of the few orchestrators of this kind designed to be **safe to
actually leave running** — see [Security: defense in depth](#security-defense-in-depth).

> Built in C#/.NET 10. Managed repos can be any stack — Python, Node, Go,
> Rust, C#, or your own — through config-driven auditors.

---

## Why you might want this

- **You have more coding work than reviewer attention.** Queue it. CodeyBox
  works items in parallel, runs the same audit gate a human reviewer would,
  and only bothers you when it genuinely needs a decision.
- **You don't trust an LLM agent with `sudo` on your machine.** Every agent
  runs in a real VM with kernel isolation and a host-enforced firewall — a
  compromised agent can't reach your host or exfiltrate past its allowlist.
- **You pay for several coding subscriptions.** CodeyBox pools them: one task
  queue, automatic routing across agents, quota-aware fallback, and per-agent
  cost tracking so you can see where the money goes.
- **You want it to be hackable.** Every subsystem sits behind an interface;
  add an agent, an auditor, a forge, or a credential backend without forking.

## How it works

```
   POST /workitems  ──►  queue  ──►  worker pool
                                        │  (one fresh VM per phase)
                                        ▼
        ┌──────────────────────────────────────────────────────┐
        │  1. Work     run the agent, commit, push a branch     │
        │  2. Audit    tool + LLM review → rework until it passes│
        │  3. Merge    resolve conflicts in-VM, verify on host  │
        │  4. Push     replicate to GitHub / any git remote     │
        └──────────────────────────────────────────────────────┘
                                        │
                                        ▼
                         a reviewed, merged change
```

Phases 1–3 are atomic — the change lands cleanly or not at all. Push is a
separate retryable tier, so a flaky remote never corrupts your local result.
The full state machine is in [`docs/architecture.md`](docs/architecture.md).

## Security: defense in depth

Most agent orchestrators run the model in a container or straight on the
host. CodeyBox is built to be **one of the few you can reasonably leave
running unattended**, with several independent layers between an agent and
your machine — so a prompt-injected or actively malicious agent has to defeat
all of them, not one:

- **Real VMs, not containers.** Each agent runs in a KVM-backed microVM. A
  container shares the host kernel — one Linux privilege-escalation bug and
  the agent is on your host. A guest-kernel exploit inside a VM isn't.
- **Host-enforced egress.** The firewall is nftables rules on the *host*, not
  inside the guest. An agent that gains `sudo` in its sandbox still can't
  reach your LAN, cloud-metadata endpoints, or anything off its allowlist —
  it can't flush a firewall it can't see.
- **Least-privilege credentials.** Audit-tool sandboxes get no agent secrets
  at all. Your upstream/GitHub credentials never leave the orchestrator
  process. An injected agent has nothing to exfiltrate beyond its own scoped
  token.
- **No host-side provider HTTP.** The orchestrator never makes raw model API
  calls; all model work goes through agent CLIs *inside* sandboxes, so
  there's no token-bearing request path to hijack on the host.
- **A deterministic merge fence.** Conflict resolutions are accepted by a
  host-side, non-LLM scope check — changed lines must fall within the actual
  conflict spans — so a model can't smuggle edits outside the conflict under
  cover of "resolving" it.
- **A review gate before merge.** The audit phase runs secret scanning, SAST,
  and LLM security review, catching a class of malicious or low-quality output
  before it ever lands.

**Honest caveat:** this is defense in depth, not a guarantee. A determined
adversary — especially one targeting a weaker coding agent you've installed —
may still find a path, and a misconfigured egress profile or an over-broad
project setup weakens the model. The goal is to be meaningfully harder to
abuse than comparable tools, not unbreakable. Read
[`docs/security.md`](docs/security.md) before you trust it with anything that
matters.

## Quickstart

The fastest way to watch it work end-to-end, on your own machine:

> The default sandbox is `process` — it runs the agent **directly on your
> host with no isolation**. Great for kicking the tires on a throwaway repo,
> **not** safe for untrusted prompts. For real use, switch to Multipass
> (see [Going to production](#going-to-production)).

**1. Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download),
git, and at least one agent CLI installed and logged in (e.g. `claude`).

**2. Build:**

```bash
git clone https://github.com/AdamFrisby/CodeyBox.git
cd CodeyBox
dotnet build CodeyBox.slnx
```

**3. Configure a project.** Drop a JSON file somewhere and point
`CODEYBOX_EXTRA_CONFIG` at it (it hot-reloads on change):

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://github.com/you/my-app.git",
        "BaseBranch": "main",
        "Agent": "claude"
      }
    ]
  }
}
```

**4. Run:**

```bash
export CODEYBOX_API_KEY=pick-any-bearer-token      # auth for the REST API
export CODEYBOX_CLAUDE_API_KEY=...                 # the agent's own credential
export CODEYBOX_EXTRA_CONFIG=/path/to/your.json
dotnet run --project src/CodeyBox.Api              # http://localhost:5036
```

**5. Queue a task:**

```bash
curl -X POST http://localhost:5036/workitems \
  -H "authorization: Bearer $CODEYBOX_API_KEY" \
  -H 'content-type: application/json' \
  -d '{
    "projectId": "my-app",
    "title": "Add a hello file",
    "prompt": "Add a hello.txt file containing the word hello.",
    "agent": "claude"
  }'
```

Watch it move through the pipeline with the CLI:

```bash
dotnet run --project tools/CodeyBox.Cli -- queue watch <work-item-id>
```

See [`docs/projects.md`](docs/projects.md) for the full project schema
(auditors, per-phase network profiles, upstream config) and
[`docs/configuration.md`](docs/configuration.md) for everything tunable.

## Features

- **Agent fleet with quota-aware routing.** Group agents into a *class* with
  quality scores and concurrency caps; CodeyBox routes each task to the best
  available member and **falls back mid-task** when one hits a quota wall, so
  a single provider's 5-hour limit never stalls the queue.
  → [`docs/agent-classes.md`](docs/agent-classes.md)
- **VM isolation with host-enforced egress.** Each agent runs in a fresh
  microVM with least-privilege credentials; network policy lives on the host
  as nftables profiles a guest can't flush.
  → [`docs/host-firewall.md`](docs/host-firewall.md)
- **Quality gates you stack.** Compose exactly which auditors must pass before
  a merge — tool checks (format/build/test, gitleaks, semgrep) and LLM reviews
  (security, architecture, quality, completeness, anti-cheating) — and nothing
  lands until it clears all of them. → [Quality gates you control](#quality-gates-you-control)
- **Per-item cost tracking.** Every work item's token spend is tracked by phase
  and agent, so you know what each bugfix or feature actually cost to run.
  → [Know what every change costs](#know-what-every-change-costs)
- **Agentic conflict resolution.** The agent resolves merge conflicts inside
  its own sandbox through its normal CLI, then a deterministic host-side scope
  fence verifies the result before the push is accepted.
- **Quota governance.** Per-agent/per-model pricing, budgets, alerts, and a
  burn-rate-aware quota gate that routes around exhausted providers.
  → [`docs/quota-gate.md`](docs/quota-gate.md)
- **Durable and restartable.** SQLite-backed state, crash/restart tolerance,
  sandbox suspend-resilience, and deterministic replay.
  → [`docs/restart-tolerance.md`](docs/restart-tolerance.md)
- **Three ways to drive it.** A REST API, a typed CLI, and a Blazor admin
  dashboard — plus HMAC-signed outbound webhooks.
  → [`docs/api.md`](docs/api.md), [`docs/webhooks.md`](docs/webhooks.md)
- **Pluggable everything.** Ship custom auditors, upstream remotes, credential
  providers, or sandbox backends as NuGet plugins — no fork.
  → [`docs/plugins.md`](docs/plugins.md)

## Quality gates you control

Auditors stack. You choose exactly which checks gate a merge — pick from
built-in tool auditors (formatting, build, the full test suite, gitleaks
secret scanning, semgrep SAST) and LLM reviewers (security, architecture,
quality, completeness, anti-cheating, test coverage), or bring your own. Each
runs in its own capability-scoped sandbox.

And the gate is hard: when any auditor fails, its findings go straight back to
the agent, which reworks and resubmits — the loop repeats until **every** gate
passes or it hits the iteration cap (at which point the item is flagged
`AuditFailed` and is *not* merged). Nothing lands until it clears the bar you
set. The auditor set, the failing-severity threshold, and the iteration cap
are all per-project config. → [`docs/audit.md`](docs/audit.md)

## Know what every change costs

CodeyBox tracks **token usage and estimated spend for every work item**, broken
down by phase (work, each rework, each audit iteration, merge) and by
agent/model. So you can answer "what did this bugfix actually cost to run?" —
and build a real feel for the economics of automated work before you scale it
up.

Costs are normalised to pay-per-API list prices — even on subscription plans,
and accounting for cached tokens — so they're comparable across agents and
over time. Query per item or per project:

```bash
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/workitems/<id>/costs       # one item, broken out by phase
curl -H "authorization: Bearer $CODEYBOX_API_KEY" \
  http://localhost:5036/projects/my-app/costs      # the whole project
```

The admin dashboard's Costs tab charts the same data.
→ [`docs/cost-reporting.md`](docs/cost-reporting.md)

## Drive it from the CLI

`codeybox` is a typed client for the whole API — no more `curl + jq`. Run it
from source (`dotnet run --project tools/CodeyBox.Cli -- <command>`) or publish
a self-contained binary:

```bash
dotnet publish tools/CodeyBox.Cli -c Release -r linux-x64 -o ./bin/codeybox
codeybox configure          # save API URL + token to ~/.config/codeybox
```

Everyday use:

```bash
# Queue a task (inline, --prompt-file, or piped in) and follow it live
ID=$(codeybox queue add --project my-app --title "Add /healthz" \
       --prompt "Add a /healthz endpoint returning 200." --quiet)
codeybox queue watch "$ID"                    # streams state transitions over SSE

codeybox queue ls --state Working,Auditing    # what's in flight
codeybox queue show <id>                       # full detail for one item
codeybox queue retry <id> --from audit         # re-drive a failed item
codeybox queue cancel <id>
```

`queue add` also takes `--agent`, `--work-branch`, `--push-upstream`, and
`--depends-on` (to chain dependent items); `--json` / `--quiet` make every
command pipe-friendly. → [`docs/cli.md`](docs/cli.md)

## The agent fleet

| Agent          | Add a new one by implementing `IAgentRunner` in… |
|----------------|--------------------------------------------------|
| Claude Code    | `CodeyBox.Agents.Claude`                         |
| OpenAI Codex   | `CodeyBox.Agents.Codex`                          |
| GitHub Copilot | `CodeyBox.Agents.Copilot`                        |
| Cursor         | `CodeyBox.Agents.Cursor`                         |
| Gemini         | `CodeyBox.Agents.Gemini`                         |
| opencode       | `CodeyBox.Agents.Opencode`                       |

Agents are interchangeable. A class lists members with quality scores; the
router prefers the highest-scoring one that's within quota and under its
concurrency cap. Every fallback is recorded in the commit trailer. Aider,
Goose, or anything else is just a new `IAgentRunner` —
see [`docs/agents.md`](docs/agents.md).

## Sandbox providers

Pick with `CodeyBox.SandboxProvider`:

| Provider     | Setup                    | Isolation                                       |
|--------------|--------------------------|-------------------------------------------------|
| `process`    | none                     | **none — dev only**, shares your host           |
| `bubblewrap` | `apt install bubblewrap` | namespaces, shared kernel; integration-tested   |
| `multipass`  | `snap install multipass` | **KVM kernel isolation** — recommended for real use |
| `graphical`  | Multipass + XFCE/Xvfb    | kernel isolation **with a desktop**, for GUI build/test |

The `graphical` flavor exposes screenshots and input synthesis through the
sandbox API for projects that need a display.
See [`docs/sandbox-providers.md`](docs/sandbox-providers.md).

## Going to production

1. **Install Multipass** and set `"SandboxProvider": "multipass"`.
2. **Set up host egress** once, with sudo: `scripts/setup-host-networks.sh`
   creates a Linux bridge per network profile and writes nftables rules that
   drop anything not on the profile's allowlist. A compromised agent with
   `sudo` can't disable this because it lives on the host, not in the guest.
   → [`docs/host-firewall.md`](docs/host-firewall.md)
3. **Read [`docs/security.md`](docs/security.md)** — the threat model, the
   trust boundaries, and the sharp edges. This is not optional.

Credentials are tiered: tool-only audit sandboxes hold **no** agent secrets,
and upstream remote credentials (e.g. a GitHub PAT) live **only** in the
orchestrator process and never cross into a sandbox.

## Provenance

Every commit CodeyBox produces carries a trailer block, so attribution
survives even a full database wipe — `git log` is the source of truth:

```
codeybox: <subject>

CodeyBox-WorkItem: <id>
CodeyBox-Agent: <agent>[/<model>]
CodeyBox-Fallbacks: claude→codex (×2 quota); …       # only if fallbacks happened
Co-Authored-By: CodeyBox <noreply@codeybox.invalid>
```

## Documentation

The [`docs/`](docs/README.md) tree is the full reference. Good entry points:

- [`architecture.md`](docs/architecture.md) — the system, plugin points, state machine
- [`security.md`](docs/security.md) — threat model (**read before deploying**)
- [`projects.md`](docs/projects.md) — project, auditor, and upstream config
- [`agent-classes.md`](docs/agent-classes.md) — routing, quotas, and fallback
- [`plugins.md`](docs/plugins.md) — the Plugin SDK
- [`api.md`](docs/api.md) — the full REST reference

## Status

CodeyBox is under active development and builds clean against .NET 10. The
`process` sandbox is for development only; the Multipass path is the
integration-tested, isolation-providing configuration. Issues and
contributions are welcome.
