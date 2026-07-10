# Sandbox providers

CodeyBox ships several additive providers. Pick one with `CodeyBox.SandboxProvider`
in `appsettings.json` (or `CodeyBox__SandboxProvider` env). Setup ranges
from "single package" to nothing — pick the one whose security and
operational trade-off matches your deployment.

## Comparison

| Provider          | Isolation                                    | Setup                                                            | Status                          |
|-------------------|----------------------------------------------|------------------------------------------------------------------|---------------------------------|
| `process`         | None (UNSAFE)                                | nothing                                                          | Working — dev only              |
| `bubblewrap`      | Linux namespaces + seccomp; shared kernel    | `apt install bubblewrap` — no daemon, no /etc edits              | **Working, integration-tested** |
| **`multipass`**   | **Real Ubuntu VM (separate guest kernel)**   | **`snap install multipass` — single command, no /etc edits**     | **Working, integration-tested** |
| `incus`           | Real VM with COW ZFS/Btrfs roots and virtiofs | Incus 7.0 LTS, `incus-admin`, and a ZFS or Btrfs storage pool    | Opt-in; `requires_incus` tested |
| `multipass-remote`| Real Multipass VM on a dedicated SSH host    | Multipass host plus SSH                                          | Working                         |
| `sprites`         | Hosted Firecracker microVM                   | sprites.dev account and token                                    | Working                         |

CodeyBox previously shipped Kata, gVisor, and crun-vm provider scaffolds.
Those were code-reviewed but never runtime-validated, so they were
removed — running unverified isolation code is worse than acknowledging
the gap. Multipass remains available throughout the Incus cutover; selecting
Incus is explicit and does not inherit Multipass configuration or lifecycle.

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
  host nftables rules or a userspace proxy. Pick Multipass or Incus for that.
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

## `incus` — COW VMs with virtiofs

Incus runs each sandbox as a VM with a separate guest kernel. A lazily baked,
content-addressed baseline is stopped and snapshotted; ordinary sandboxes are
created from that immutable snapshot with `incus copy`. On a snapshot-capable
ZFS or Btrfs pool, those copies share unchanged blocks and write only their
deltas. ZFS is strongly recommended for VM workloads. Host
directories are attached as Incus `disk` devices with
`io.bus=virtiofs` explicitly selected—`auto` is not used, so the provider never
falls back to 9p.

Only host-backed paths use virtiofs. Incus 7.0's `source=tmpfs:` disk-device
form is container-only, so `SandboxMount.Tmpfs` paths such as `/work` and the
credential directory are mounted after VM boot with the guest kernel's real
`tmpfs`. Per-device and aggregate logical sizes are bounded by
`MaxTmpfsDeviceBytes` and `MaxAggregateTmpfsBytes`.

Generated cloud-init is sent to Incus through `user.user-data` over stdin.
`ExtraRuncmd` is likewise executed as a bounded stdin script after cloud-init;
it is never placed in process argv. Before the immutable `ready` snapshot is
created, the provider replaces user-data with an empty cloud config and runs
`cloud-init clean --logs --machine-id`, preventing bake logs and first-boot
state from entering every clone. `ExtraCloudInit` is still operator-visible
configuration and may be retained on a full-launch instance: never put secrets
in it.

Incus settings and provisioning are independent from Multipass. Switching the
provider does not reuse `MultipassExtraRuncmd`, `MultipassExtraCloudInit`,
or Multipass binaries. Incus lifecycle actions go only to Incus; the cutover
wrapper merely aggregates the two providers' separately owned inventories.

The current Incus provider covers headless work, audit, and merge sandboxes.
A graphical request fails explicitly; it is never rerouted to Multipass.

### Host prerequisites

- Incus 6.3 or newer on Linux kernel 5.6 or newer; Incus 7.0 LTS is
  recommended. The native filesystem `io.bus=virtiofs` selector was added in
  6.3, and kernel 5.6 supplies the `openat2` confinement used by Incus for
  restricted-project disk paths. Preflight rejects a server that does not
  report both capabilities.
- The CodeyBox service identity in the `incus-admin` group. This group is
  effectively host-root-equivalent because Incus can attach host paths and
  devices; restrict membership accordingly and restart the service after
  changing it.
- Numeric host/guest ownership aligned for virtiofs. Incus VM disk devices do
  not support the container-only `shift` mapping. If a sandbox has any
  host-backed mount, `Incus:GuestUserId` and `GuestGroupId` must exactly match
  the CodeyBox process's effective host UID/GID; creation otherwise fails
  closed. The provider never writes an identity marker into a caller-owned
  source: it pins and rechecks the directory's Linux device/inode identity
  before and after device attachment and after VM start, and additionally
  hashes an existing bounded regular file through the guest when one is
  available. Empty and host-read-only directories therefore remain valid mount
  sources. Do not make repository roots world-writable.
- A dedicated non-default Incus project. When absent, the provider creates it
  with `features.images=false`, Incus-required `features.profiles=true`, exact ownership
  markers `user.codeybox.managed=true` and
  `user.codeybox.project-schema=1`, and with `restricted=true`,
  `restricted.devices.disk=allow`, a nonempty exact
  `restricted.devices.disk.paths` list, `restricted.devices.nic=allow`, and
  `restricted.snapshots=allow`. It also sets
  `restricted.virtual-machines.lowlevel=block` and
  and rejects per-instance VM nesting before start. CodeyBox refuses to claim or
  mutate an existing project unless both ownership/schema markers and both
  feature flags already match exactly. Every VM still uses `--no-profiles`, and
  effective profiles/devices are verified before start. After that adoption check, it
  applies all restriction keys in one operation and reads them back before
  creating a VM. Prefer leaving the configured project absent and allowing
  CodeyBox to create it; do not add the markers to a shared project merely to
  bypass the guard. Incus then opens
  each local disk source beneath the matched allowed parent with
  `openat2(RESOLVE_BENEATH|RESOLVE_NO_MAGICLINKS)` and passes the resulting
  descriptor to virtiofsd. This is the daemon-side atomic containment guard;
  CodeyBox's canonical allowlist and device/inode checks remain narrower
  defense in depth. The default project is rejected.
- The canonical parent of `Incus:StagingDirectory` must already exist and must
  not traverse a symbolic link. Normally leave the staging root itself absent;
  CodeyBox creates it exclusively with service ownership, mode `0700`, and a
  mode-`0600` `.codeybox-incus-staging-v1` ownership marker. To adopt an
  existing root, it must already have that exact ownership/mode/marker shape;
  an ordinary pre-created empty directory is intentionally rejected.
- KVM, QEMU, `virtiofsd`, host `setsid` from util-linux, and the
  userspace/kernel support for the selected storage driver. CodeyBox uses a
  dedicated host process group to make Incus CLI cancellation tear down every
  descendant. The Incus packages supply the VM runtime on supported Ubuntu
  installations.
- A cloud-init-enabled VM image with the Incus guest agent, systemd,
  `/usr/bin/setpriv`, and `/usr/bin/setsid` (both from util-linux). The default
  Ubuntu cloud image provides these; the provider verifies both executables
  before admitting a sandbox. Its root-owned control wrapper starts each agent
  command in a separate session and drops it to the configured numeric UID/GID.
- An existing ZFS or Btrfs Incus storage pool. The provider validates the
  snapshot-capable driver and, for ZFS, rejects any explicitly configured
  `zfs.clone_copy` mode other than `true`; it never creates, reformats, or
  destroys storage. Btrfs is supported but emits an
  operator warning because ZFS has stronger VM-volume isolation and is the
  recommended production choice.
- The `cb-*` Linux bridges and nftables policy described in
  [`host-firewall.md`](host-firewall.md). Incus's default NAT bridge is not a
  substitute for CodeyBox's host-enforced profile policy.

Follow the official [Incus installation guide](https://linuxcontainers.org/incus/docs/main/installing/)
and the [ZFS](https://linuxcontainers.org/incus/docs/main/reference/storage_zfs/)
or [Btrfs](https://linuxcontainers.org/incus/docs/main/reference/storage_btrfs/)
storage-driver documentation.
For a non-destructive development pool backed by a loop file:

```bash
incus storage create codeybox-zfs zfs size=50GiB
```

That demonstrates genuine ZFS snapshots and COW clones, but its I/O still
lands on the filesystem containing `/var/lib/incus`. For production, give the
pool a dedicated empty fast device or an existing dedicated ZFS dataset so VM
scratch I/O can avoid the encrypted system disk. Selecting a block device for
pool creation is destructive; verify it independently before running any
storage-create command.

### Configuration

```json
{
  "CodeyBox": {
    "SandboxProvider": "incus",
    "Incus": {
      "BinaryPath": "incus",
      "ProjectName": "codeybox",
      "StoragePoolName": "codeybox-zfs",
      "DefaultImage": "images:ubuntu/24.04/cloud",
      "InstanceNamePrefix": "codeybox-",
      "BaselineNamePrefix": "cb-incus-baseline-",
      "UseBaselineImages": true,
      "IncludeMultipassCutoverInventory": false,
      "ExtraRuncmd": []
    },
    "SandboxNetworkProfiles": {
      "isolated": "cb-iso",
      "claude": "cb-claude"
    }
  }
}
```

Incus operational settings other than the restart-only `ProjectName` and
effective `StagingDirectory`, plus the shared network-profile map, are read for
subsequent provider operations. Existing sandbox handles retain the option
snapshot with which they were created. A process started with `multipass` or
`incus` may hot-switch between those two providers: in-progress creations
continue on their original provider and existing handles keep their owner.
Each new creation invokes only the currently selected backend; a failure is
propagated and never retried through the other provider. Selecting any other
provider still requires a restart. See
[`configuration.md`](configuration.md#incus) for every key and bound.

If a cutover spans a process restart and preserved/leaked Multipass resources
still exist after Incus becomes the startup selection, set
`Incus:IncludeMultipassCutoverInventory=true` until those resources are gone.
Leave it false on an Incus-only host; dormant Multipass is then neither invoked
nor required. Once both providers have been activated, inventory fails closed
rather than reporting a partial list if either backend becomes unavailable.

The default baseline root is 8 GiB, matching CodeyBox's default sandbox disk
limit. Keeping the baseline at the smallest supported root matters because a
ZFS clone volume can be grown but cannot be shrunk after copying. CodeyBox
verifies virtiofs readiness, cloud-init, snapshot/COW preconditions, and bounded
CLI output before admitting the sandbox.

Virtiofs preserves numeric ownership. The defaults use guest UID/GID `1000`,
but those values are not translated to the host. For any host-backed mount,
the provider requires an exact match with the CodeyBox process's effective
UID/GID before it attaches the path. Provider staging remains mode `0700` and
is owned by that same service identity.

A VM's root user can still access the contents of every path intentionally
attached read-write through virtiofs; numeric identity matching is not a root
containment boundary. Keep `AllowedHostMountRoots` narrow, attach only
per-sandbox repositories/work directories read-write, and never attach host
system or executable-search paths.

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
       "set -eux\nexport DEBIAN_FRONTEND=noninteractive\napt-get update\napt-get install -y curl ca-certificates nodejs npm\nnpm install -g @anthropic-ai/claude-code\nGITLEAKS_TGZ=/tmp/gitleaks_8.29.0_linux_x64.tar.gz\ncurl -fsSL -o \"$GITLEAKS_TGZ\" https://github.com/gitleaks/gitleaks/releases/download/v8.29.0/gitleaks_8.29.0_linux_x64.tar.gz\nprintf '%s  %s\\n' 39e07ad810336fd0ae80d0bd61c60d0521f628173e7583583b5df4a38738522c \"$GITLEAKS_TGZ\" | sha256sum -c -\ntar -xzf \"$GITLEAKS_TGZ\" -C /usr/local/bin gitleaks\nrm \"$GITLEAKS_TGZ\""
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
| incus        | Host-side nftables on the same per-profile bridges; one filtered NIC and no NAT NIC    |

The Multipass and Incus paths provide real per-host enforcement, configured
once via `scripts/setup-host-networks.sh` and described in
[`host-firewall.md`](host-firewall.md). Incus instances are created with
`--no-profiles` and receive only the selected `cb-*` bridge NIC, so they cannot
route around that host policy through an Incus NAT network.
