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
* **No in-VM firewall.** The provider deliberately installs no
  in-guest iptables rules — they would be voluntary (an agent with
  sudo could flush them) and pretending otherwise was misleading. The
  host bridge filtering is the only egress boundary, and that's what
  we treat as load-bearing.
* **IPv4-only enforcement.** The host's `cb-*` chains accept by
  IPv4 destination IP only. IPv6 traffic on the `cb-*` bridges falls
  through to `drop`. This is safe (default-deny) but causes a
  several-second delay when clients try IPv6 first then fall back —
  curl, `getent hosts`, etc. If you need IPv6 reachability, extend
  setup-host-networks.sh to resolve and emit `ip6 daddr` rules.
* **Image bring-up.** First launch downloads the default Ubuntu image
  (~600 MB) to the multipass cache. Subsequent launches reuse it.

### Graphical Multipass sandboxes

Projects that need to build or test GUI applications can opt in with:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "desktop-app",
        "RepositoryUrl": "https://example.com/desktop-app.git",
        "GraphicalSandbox": true
      }
    ]
  }
}
```

When enabled, the work and rework phases use `SandboxProfileFlavor.Graphical`
and the conventional `graphical` network profile (`cb-graphical` in the default
operator examples). Audit sandboxes use
the graphical flavor only when an auditor declares `AuditCapabilities.Graphical`;
ordinary tool auditors keep their configured `auditTool` profile. LLM audit and
merge phases keep their normal headless profiles. The Multipass
graphical flavor installs a lightweight XFCE session on Xvfb, starts `x11vnc`
on the VM's `10.99.x.x` profile-bridge address on the conventional graphical VNC
port (`SandboxConventions.GraphicalVncPort`, currently `5900`), and preinstalls
`xdotool`, `scrot`, and `ffmpeg`. The VNC server is password-protected and
allows only the host bridge gateway. For operator viewing, run
`codeybox-vnc-loopback <multipass-vm-name> 5901` and connect to
`127.0.0.1:5901`; the helper binds host loopback and proxies to the guest bridge
listener. The CodeyBox
screenshot/input APIs call `scrot` and `xdotool` through `multipass exec`;
no additional LLM network surface is required.

Operator setup needs the dedicated graphical profile bridge:

```text
# /etc/codeybox/networks.conf
graphical        cb-graphical    10.99.6.0/24   internet
```

Use `internet` or an allowlist that covers the Ubuntu package sources while
the graphical baseline is baked. A no-egress graphical bridge is only viable
with a custom image that already contains the desktop, VNC, screenshot, and
input tooling.

Map it in CodeyBox config if you override `SandboxNetworkProfiles`:

```json
"SandboxNetworkProfiles": {
  "graphical": "cb-graphical"
}
```

With `MultipassUseBaselineImages=true`, the provider bakes
`cb-baseline-graphical` the first time a graphical project runs. Delete that
baseline to force a rebuild after changing graphical tooling or
`MultipassExtraRuncmd`.

**Integration-tested**: end-to-end on a real Ubuntu 25.10 host. The
shipped test verifies VM launch, native bind-mount visibility,
env-from-file (with explicit no-argv-leak verification via in-VM `ps`
grep), stdin piping, and working-directory enforcement. Egress
filtering is verified separately by `local/verify-host-firewall.sh`
and `local/verify-internet-only.sh` against the real host bridges.

**Putting your project's tooling in the VM.** The default Ubuntu image
is bare — no agent CLI, no language toolchain, no auditor binaries.
Three ways to install what your project needs:

1. **`CodeyBox.MultipassExtraRuncmd` (recommended)** — a list of shell
   commands spliced into the generated cloud-init `runcmd` on ordinary
   launches, and rendered into the baseline user-data as a diagnostic
   install-command manifest before being run once via `multipass exec` during
   baseline bakes. Use this for anything that needs a runcmd-style invocation.
   Example for a project
   whose work agent is Claude Code and whose audit policy uses gitleaks:
   ```json
   "CodeyBox": {
     "MultipassExtraRuncmd": [
       "set -eux\nexport DEBIAN_FRONTEND=noninteractive\napt-get update\napt-get install -y nodejs npm\nnpm install -g @anthropic-ai/claude-code\ncurl -fsSL https://github.com/gitleaks/gitleaks/releases/download/v8.21.2/gitleaks_8.21.2_linux_x64.tar.gz | tar -xzC /usr/local/bin gitleaks"
     ]
   }
   ```
   Each entry is one shell command; the orchestrator preserves their order.
   Package downloads go out via the profile's host bridge, so their
   destinations need to be on the bridge's allowlist (or use a profile in
   `internet` mode).
2. **`CodeyBox.MultipassExtraCloudInit`** — extra cloud-init YAML for
   directives CodeyBox does not generate (`packages:`, `apt:`, etc.).
   Don't use this to add `runcmd:` or `write_files:` blocks — cloud-init's
   PyYAML parser keeps only one duplicate top-level key, so the provider
   rejects those fragments before launch rather than letting user-data get
   partially dropped.
3. **Custom multipass image** — pre-bake everything (`multipass launch`
   + customise + snapshot) and reference via `SandboxSpec.ImageReference`.
   Faster startup, no bring-up reachability requirements. Useful when
   your install set is heavy enough that the per-profile baseline bake
   gets long.

## `multipass-remote` — same kernel isolation, VM execution off-box

Drives `multipass` on a REMOTE host over SSH while the orchestrator
brain — work-item DB, dispatch loop, agent stream capture — stays local.
This is "CHEAP-PATH distributed VMs, step 1 of 2": one orchestrator
process, one SQLite database, sandboxes elsewhere. Lets you scale VM
throughput by adding a beefy remote host without re-architecting the
orchestrator into a multi-process service.

**Provider name:** `multipass-remote`.

**Architecture.** Every `multipass` command (launch / exec / mount /
stop / delete / info / list) is issued through an `IRemoteHostTransport`
seam. The default implementation is OpenSSH — the orchestrator already
ships with `ssh` on every supported OS, no managed dependency required,
and the existing `IProcessRunner` infrastructure already streams the
child process's stdout/stderr line-by-line. That's exactly what
`AgentStreamCapture` needs so the agent CLI's output is visible on the
orchestrator host in real time, not after the remote command exits.

**Bind mounts.** Host-side bind-mount sources (e.g. the per-item bare
git repo from `LocalGitHost`) are staged to a per-sandbox directory
under `RemoteStagingRoot` via `tar | ssh tar`, then attached to the
remote VM with `multipass mount`. Writable mounts are synced BACK on
disposal (host ← remote) so the merge phase on the orchestrator host
sees commits the in-VM agent pushed. Read-only mounts skip the sync-back.

**Git remotes.** The orchestrator's `IGitHost` still issues a
`CloneUrlInsideSandbox` of `/repo`; that's the same shape local multipass
sees. The per-item bare repo is what gets staged across to the remote
host, so the in-VM `git clone /repo /work` works identically. After the
work phase, the bare repo is synced back to the host before the staging
dir is deleted. No remote git-daemon or SSH reverse tunnel is required
in this iteration.

**Failure classification.** OpenSSH reserves exit code 255 for "the SSH
client itself failed before the remote command even ran" — connection
refused, auth rejected, network partition, key permission error. The
transport raises `RemoteSshTransportException` on that code; the
orchestrator maps it to a sandbox-level failure (recoverable: re-pickup
the work item) rather than an agent crash. A remote command running and
returning non-zero (an agent CLI failure, a build error, etc.) is
returned as a normal `SandboxExecResult.ExitCode`, exactly like local
multipass.

**Setup.**
1. Install OpenSSH on the orchestrator host (almost always already
   there).
2. On the remote host: `snap install multipass`. Same install command as
   for local multipass, same `MultipassExtraRuncmd` baseline-bake
   workflow when you switch in step 2.
3. Provision an SSH key for the orchestrator that authorizes a
   non-interactive user on the remote host. Use a key dedicated to
   CodeyBox so revocation has clean blast radius.
4. Add to the host's `~/.ssh/known_hosts` (or leave `AcceptUnknownHostKeys=true`
   on first contact — see config below).
5. Set the config block:

   ```jsonc
   {
     "CodeyBox": {
       "SandboxProvider": "multipass-remote",
       "MultipassRemoteSandbox": {
         "SshTarget": "codeybox@remote.example.com",
         "SshKeyPath": "/etc/codeybox/ssh/id_ed25519",
         "RemoteMultipassPath": "/snap/bin/multipass",
         "RemoteStagingRoot": "/home/codeybox/snap/multipass/common/codeybox-remote-staging",
         "DefaultImage": "24.04"
       }
     }
   }
   ```

**Hot reload.** Every field on `MultipassRemoteSandbox` is read fresh on
each `CreateAsync` via `IOptionsMonitor`, so rotating an SSH key or
re-pointing at a different remote host takes effect on the next sandbox
launch without an orchestrator restart.

**Scope (step 1 of 2).** This provider deliberately does NOT implement:
baseline image bake/clone, suspend/resume, host-shutdown teardown,
disk-guard preflight, package-cache seeding. Those host-side concerns
either don't translate cleanly to a remote host without further design
(suspend/resume needs network-stable VM identity across orchestrator
restarts; baselines need a per-remote-host cache) or are operator-tuning
concerns deferred until the basic distributed-VM path is working
end-to-end. Step 2 picks those up.

## Choosing

| Use case                                                    | Pick                |
|-------------------------------------------------------------|---------------------|
| Local development of the orchestrator itself                | `process`           |
| Pre-prod / trusted prompts / "just give me a sandbox"       | `bubblewrap`        |
| **Production on Ubuntu / kernel isolation**                 | **`multipass`**     |
| Production where VM throughput needs a separate host        | `multipass-remote`  |

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
