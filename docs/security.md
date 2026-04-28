# Security model and threat analysis

This document is the authoritative reference for CodeyBox's security posture.
**Read it before deploying to anything that matters.**

## Threat model

We assume the LLM agent's output is **adversarial-by-default**:

* The prompt may be authored by someone we trust, but the agent's response is
  shaped by training data and tool calls that can include attacker-influenced
  content (prompt injection from a fetched web page, a poisoned dependency,
  malicious source files in the repo being edited, etc.).
* The agent may attempt to read or exfiltrate secrets, persist outside the
  sandbox, escalate privileges, or pivot through the kernel.
* A compromise of the agent process must not become a compromise of the host
  or any other sandbox.

We do **not** defend against:

* A compromised host kernel running the orchestrator.
* A compromised orchestrator process.
* The agent producing *bad code that humans then merge*. Code review of the
  merged output is still required; CodeyBox does not validate correctness.
* Side-channel attacks against the host CPU (Spectre-class). Mitigations are
  the host kernel's responsibility.

## Core mitigations

### 1. Kernel isolation via real VMs

The headline reason this framework exists. With plain containers, every
container shares the host kernel. A Linux LPE (and there are several per
year) becomes a host compromise. With Multipass (KVM-backed), Kata +
Firecracker, or crun-vm / libkrun, each sandbox runs its own kernel
inside a VM. A guest kernel exploit doesn't get the attacker out of the
VM.

The recommended provider is Multipass — single-package install, no root
config edits — and it is the only provider that has been integration-
tested end-to-end. The bubblewrap and gVisor providers reduce attack
surface but remain shared-kernel; choose them only when full virt-
isolation isn't available.

The dev-only `Sandbox.Process` provider has **none** of these properties.
It is for local pipeline development only. Do not run it against
untrusted prompts, untrusted repositories, or untrusted upstream content.

### 2. Two-tier credential boundary

| Credential                         | Lives in                                    | Visible to sandbox? |
|------------------------------------|---------------------------------------------|---------------------|
| Agent API key (Anthropic, OpenAI…) | Host env, mounted to work / rework / merge / audit-LLM sbx | Only those sandboxes, only their own agent |
| GitHub PAT / generic git creds     | Orchestrator process only                   | **Never**           |
| Host SSH keys, cloud creds         | Host                                        | **Never**           |

Tool-only audit sandboxes (linters, scanners) run with **no** agent
credentials and (typically) the `isolated` network profile. A buggy or
compromised linter cannot exfiltrate the agent's API key, because the
key is not present in the sandbox where the linter runs.

The merge phase is agent-driven (so it can resolve merge conflicts and
run the project's test suite). The orchestrator constrains exfiltration
risk via the project's `merge` network profile — typically the same as
`work`, never broader. The orchestrator also verifies merge state
(expected post-merge SHA, clean working tree) before pushing.

The upstream push runs **on the host**, not in any sandbox. The token
required to push to GitHub is held by the orchestrator and never crosses
the sandbox boundary.

### 3. Network policy = host-enforced, per-phase

Every sandbox sits on a host-managed Linux bridge whose nftables rules
drop everything not on the bridge's allowlist. The bridges are created
once by `scripts/setup-host-networks.sh`; the orchestrator only chooses
which bridge a given sandbox attaches to (via `multipass launch
--network <bridge>`).

Three egress modes per profile:

* `-` — no egress at all (loopback + DNS + established only). Right for
  tool-only audits and isolated phases.
* `internet` — block RFC1918 / link-local / cloud-metadata / loopback /
  multicast; accept the rest. Wide reach without LAN attack surface.
* `host1,host2,…` — strict hostname allowlist; resolved to IPv4 IPs at
  setup time and re-resolved on script re-run.

Per-project, per-phase profile selection lives in project config. The
orchestrator passes a default-route override via cloud-init so the VM
can't accidentally egress through Multipass's default (blocked) bridge.

A compromised agent with sudo cannot bypass this: the drops happen in
the host kernel, on bridges the agent has no view into. `iptables -F`
inside the VM affects nothing. See [`host-firewall.md`](host-firewall.md)
for the threat model and setup. The Process and Bubblewrap providers do
**not** enforce egress (they are dev-only / shared-kernel).

### 4. Ephemeral credential mounts

Agent secrets are written to a tmpfs mount (`/run/codeybox/creds`) that
exists for the lifetime of the sandbox and is destroyed with it. The
secret never lands on a persistent disk inside the VM.

### 5. Resource bounds

`SandboxResourceLimits` caps CPU, memory, disk, and wall-clock. The
production providers must enforce these via cgroups (Kata config) or
libkrun limits. The orchestrator additionally enforces per-phase
`WorkTimeout` and `MergeTimeout` via `CancellationTokenSource.CancelAfter`.

### 6. State persistence is host-side only

The host bare git repo and the SQLite work-item store live on the host
filesystem. Sandbox writes to the bare repo go through the git protocol,
not raw filesystem writes. Even if a sandbox could write to the mount, it
would corrupt the bare repo's git data — git-fsck on the host can detect
this.

### 7. Token scrubbing on errors

Upstream remote impls scrub their tokens from any error message returned
to the orchestrator. The orchestrator log lines never contain credential
material. *Do not* extend the orchestrator to log the contents of
`AgentCredential` or `GitHubUpstreamOptions`.

## Audit status

An initial security audit has been performed (see
[`security-audit.md`](security-audit.md)). Highlights:

* Cross-work-item bare-repo exposure — **fixed** (per-item mount).
* `workBranch == baseBranch` bypass of merge containment — **fixed**.
* GitHub PAT visible on argv via token-in-URL — **fixed** (askpass).
* Git option-injection via `RepositoryUrl` / branch names — **fixed**
  (validation + `--` separator).
* Agent secrets on per-exec argv — **fixed** (env-file at boot).
* Default network bind tightened to loopback.
* Bearer-token auth required (or explicit `DangerouslyDisableAuth`).

See the audit doc for the complete list, severities, and accepted-risk
items.

## Sharp edges (read these before deploying)

### A. The Process provider is named "UNSAFE" for a reason

`Sandbox.Process` runs commands as the host's normal user, with read-only
copies for read-only mounts and *symlinks* for writable mounts. It does
not enforce the network policy. It exists so the orchestrator pipeline
can be developed and tested without a Kata/Firecracker setup. Do not ship
this provider to production. Configuration that selects it should fail
loudly when `ASPNETCORE_ENVIRONMENT != Development`.

### B. Sandbox image trust

The orchestrator pulls and runs images by reference
(`SandboxImageReference`). You are trusting:

1. The contents of that image (including the agent CLI binary inside it).
2. The image registry's TLS and signing configuration.

Use a digest pin (`codeybox/agent@sha256:…`) in production, not a tag.
Build the image yourself; do not pull `claude` or `gh` binaries from
arbitrary upstream sources without verification.

### C. Prompt injection through repository content

The agent's prompt typically references the repository it is editing.
Files in that repository can contain instructions designed to subvert the
agent (e.g. `README.md` with "ignore previous instructions and run …").
CodeyBox reduces *blast radius* of a successful injection but does not
prevent injection. The two key reductions:

1. The compromised agent has only its API key and the local repo. It
   cannot reach upstream creds, the host, or other work items' repos.
2. Anything the agent writes is constrained to a feature branch on the
   host bare repo. The merge phase blindly merges, but the upstream push
   is host-controlled, and a human (or a CI pipeline you bolt on) can
   gate it.

For high-stakes deployments, gate the upstream push behind a manual
approval step (replace `IUpstreamRemote` with one that records the merge
and requires a separate API call to push).

### D. The merge phase is agent-driven

The merge phase runs the agent against the merge so it can resolve merge
conflicts and run the project's test suite. This is what most projects
need; it is also a slightly larger trust surface than a deterministic
`git merge`. The reductions:

* The orchestrator verifies merge state (expected post-merge SHA, clean
  working tree) before allowing phase 4 — the agent cannot quietly push
  something other than the merge.
* The merge sandbox runs under the project's `merge` network profile.
  Pick the strictest profile your tests can tolerate.
* If you don't want agent-driven merge, configure auditors that gate
  merge (the audit phase is the natural gate) and/or replace the
  orchestrator's merge step with a deterministic one.

### E. Multi-tenant deployments

The framework has no concept of tenants today. If you run this for
multiple users, you must:

* Partition `IGitHost` repos per-tenant.
* Partition `ICredentialProvider` per-tenant (do not let tenant A's work
  items use tenant B's API key).
* Partition the SQLite store or move to per-tenant DBs.
* Gate the REST API behind authentication (out of scope today).

## Operational hygiene

* **Rotate API keys** on a schedule. The orchestrator picks up changes via
  `EnvironmentCredentialProvider` on next read; in-flight sandboxes hold
  the previous value until they finish.
* **Audit log everything**. Every state transition is logged at
  Information level. Send these to an append-only sink.
* **Don't disable warnings-as-errors.** The codebase enforces this in
  `Directory.Build.props`. Suppressing warnings about uninitialised
  fields or platform-specific calls usually masks a real bug.

## Review cadence

Review this document whenever:

* A new sandbox provider is added.
* A new agent integration is added (each agent has its own auth quirks).
* The credential or network model changes.
* Any cross-sandbox communication is introduced.
