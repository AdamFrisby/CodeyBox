# Sandbox providers

CodeyBox ships three providers. Pick one with `CodeyBox.SandboxProvider`
in `appsettings.json` (or `CodeyBox__SandboxProvider` env). Setup ranges
from "single package" to nothing — pick the one whose security and
operational trade-off matches your deployment.

## Comparison

| Provider          | Isolation                                    | Setup                                                            | Status                          |
|-------------------|----------------------------------------------|------------------------------------------------------------------|---------------------------------|
| `process`         | None (UNSAFE)                                | nothing                                                          | Working — dev only              |
| `bubblewrap`      | Linux namespaces + seccomp; shared kernel    | `apt install bubblewrap` — no daemon, no /etc edits              | **Working, integration-tested** |
| **`multipass`**   | **Real Ubuntu VM (separate guest kernel)**   | **`snap install multipass` — single command, no /etc edits**     | **Working, integration-tested** |

CodeyBox previously shipped Kata, gVisor, and crun-vm provider scaffolds.
Those were code-reviewed but never runtime-validated, so they were
removed — running unverified isolation code is worse than acknowledging
the gap. Multipass is the recommended kernel-isolated path; the others
can be re-added later if a real deployment needs them.

## `process` (dev only — refuses to load in production)

Runs sandbox commands as plain host processes in a temp directory.
**No isolation.**

Allowed only when `ASPNETCORE_ENVIRONMENT=Development`, or with explicit
`CodeyBox.DangerouslyAllowProcessSandbox=true` (don't). The startup will
refuse to load this provider in any other environment with a clear error
pointing at the alternatives.

## `bubblewrap` — single-package, shared-kernel

Single package, no daemon, no /etc edits, no podman. The orchestrator
invokes `bwrap` directly with full namespace unsharing (mount, PID, IPC,
UTS, user, cgroup, optionally network) plus a tmpfs `/tmp`.

**What you get:**
- Process tree visibility blocked (the agent can't see other host processes)
- Filesystem visibility blocked (only what we explicitly bind is reachable;
  user homes are not bound)
- Network isolation when the work item doesn't need network —
  `--unshare-net` gives the sandbox loopback only
- Identical sandbox-side filesystem layout to the host (we replicate
  symlinks like `/bin -> usr/bin` so PATH resolution behaves the same)
- HOME isolation: the sandbox's `$HOME` is `/work`; the host user's home
  isn't reachable

**What you don't get:**
- Separate guest kernel — a Linux LPE in the agent reaches the host kernel
- Hostname allowlisting on egress — when network is allowed, the sandbox
  shares the host's network namespace. Per-host filtering would require
  host nftables rules or a userspace proxy. Pick Multipass for that.
- Resource caps — bubblewrap doesn't enforce CPU / memory limits. Wrap
  with `systemd-run` for that, or pick a different provider.

**Setup:**
```bash
sudo apt install bubblewrap     # or dnf, pacman; bwrap is in every distro
```
Then in `appsettings.json`:
```json
{ "CodeyBox": { "SandboxProvider": "bubblewrap" } }
```
That's it. No daemon to start, no config to edit.

## `multipass` — recommended for kernel isolation

Real Ubuntu VMs via Canonical's snap. Each sandbox is a VM with its own
guest kernel — a kernel exploit in the agent escapes into a VM that
gets purged when the sandbox is disposed, never reaching the host.

**Setup (one command on Ubuntu):**
```bash
sudo snap install multipass
```
That's it. No daemon configuration, no /etc edits, no podman, no KVM
group dance (multipass handles its own KVM access). Confirm with
`multipass version`.

**In `appsettings.json`:**
```json
{ "CodeyBox": { "SandboxProvider": "multipass" } }
```

**Trade-offs:**

* **Slow.** VM launch is ~30-45 seconds. A work item with audit phases
  spawns multiple VMs in sequence; expect ~1-2 minutes of pure VM-launch
  overhead per work item.
* **Snap-confined.** Multipass-as-snap can read paths under
  `~/snap/multipass/common/` only. CodeyBox auto-stages cloud-init and
  bind-mount sources there. **Important**: set
  `CodeyBox.GitRootDirectory` to a path under `~/snap/multipass/common/`
  too (e.g. `~/snap/multipass/common/codeybox-repos`), otherwise the
  per-work-item bare repo can't be bind-mounted into the VM.
* **Egress enforcement is host-side via nftables on per-profile bridges**
  (see [`host-firewall.md`](host-firewall.md)). Operator runs
  `scripts/setup-host-networks.sh` once, defining one bridge per
  profile (e.g. `claude`, `multi-llm`, `isolated`). The orchestrator's
  `SandboxNetworkProfiles` config maps profile names to bridges; the
  provider passes `--network <bridge>` at launch. Forward traffic on
  Multipass's default `mpqemubr0` is dropped by the host, so the only
  viable internet path is via the chosen bridge — and that bridge's
  egress is filtered in the host kernel, where the agent (even with
  sudo inside the VM) cannot touch it.
* **Cloud-init route swap.** The provider's cloud-init pushes the VM's
  default route over to the secondary NIC (the chosen `cb-*` bridge)
  at first boot. Without this, Linux defaults to the first NIC
  (mpqemubr0 → blocked at host) and traffic times out. The route swap
  itself runs in-VM and is therefore reversible by a privileged agent;
  reverting it just sends traffic back to mpqemubr0 where the host's
  drop rule kills it (self-DOS, not a bypass).
* **In-VM advisory firewall** is also installed as defence-in-depth.
  An agent with sudo can disable it; the host bridge filtering is the
  real boundary.
* **IPv4-only enforcement.** The host's `cb-*` chains accept by
  IPv4 destination IP only. IPv6 traffic on the `cb-*` bridges falls
  through to `drop`. This is safe (default-deny) but causes a
  several-second delay when clients try IPv6 first then fall back —
  curl, `getent hosts`, etc. If you need IPv6 reachability, extend
  setup-host-networks.sh to resolve and emit `ip6 daddr` rules.
* **Image bring-up.** First launch downloads the default Ubuntu image
  (~600 MB) to the multipass cache. Subsequent launches reuse it.

**Integration-tested**: end-to-end on a real Ubuntu 25.10 host. Two
shipped tests verify VM launch, native bind-mount visibility, env-from-
file (with explicit no-argv-leak verification via in-VM `ps` grep),
stdin piping, working-directory enforcement, and the firewall actually
blocking outbound traffic when `AllowedHosts` is empty.

**Putting agents in the VM.** The default Ubuntu image doesn't include
Claude Code, Codex, etc. Two options:

1. **Cloud-init at first boot** — set `CodeyBox.MultipassExtraCloudInit`
   to install agents:
   ```yaml
   packages:
     - nodejs
     - npm
   runcmd:
     - npm install -g @anthropic-ai/claude-code
   ```
   Note: extra-cloud-init runs after the egress firewall is enabled, so
   package downloads need their destinations on the AllowedHosts list.
2. **Custom multipass image** — build a base image with agents
   pre-installed (`multipass launch` + customise + snapshot), then
   reference via `SandboxSpec.ImageReference`. Faster startup, no
   firewall race.

## Choosing

| Use case                                                    | Pick           |
|-------------------------------------------------------------|----------------|
| Local development of the orchestrator itself                | `process`      |
| Pre-prod / trusted prompts / "just give me a sandbox"       | `bubblewrap`   |
| **Production on Ubuntu / kernel isolation**                 | **`multipass`**|

## Adding a new provider

1. New project: `CodeyBox.Sandbox.<Name>` referencing `CodeyBox.Core` and
   `CodeyBox.Sandbox`.
2. Implement `ISandboxProvider` and a corresponding `ISandbox`. Honour:
   - `SandboxSpec.Mounts` — including tmpfs and the read-only flag.
   - `SandboxSpec.Network` — default deny, explicit allowlist.
   - `SandboxSpec.Limits` — CPU, memory, disk, wall-clock.
   - `SandboxConventions.WorkDir` and `CredentialsDir` paths inside the sandbox.
3. Document the new provider in this file.
4. Register it in the switch in `Program.cs`.
5. Update `docs/security.md` with any provider-specific caveats.

## Network allowlist enforcement

`SandboxSpec.Network.AllowedHosts` is the policy surface. **Enforcement
varies by provider:**

| Provider     | Enforcement mechanism                                                                  |
|--------------|----------------------------------------------------------------------------------------|
| process      | None (dev only)                                                                        |
| bubblewrap   | Binary on/off (`--unshare-net` or `--share-net`); no per-host filtering                |
| multipass    | Host-side nftables on per-profile Linux bridges; agent inside the VM cannot bypass it  |

The Multipass path is the only one with real per-host enforcement —
configured once via `scripts/setup-host-networks.sh` and described in
[`host-firewall.md`](host-firewall.md).
