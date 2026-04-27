# Sandbox providers

CodeyBox ships five providers. Pick one with `CodeyBox.SandboxProvider` in
`appsettings.json` (or `CodeyBox__SandboxProvider` env). Setup difficulty
ranges from "single package" to "needs a Linux config session" — pick the
one whose security/operational trade-off matches your deployment.

## Comparison

| Provider          | Isolation                               | Setup                                                                     | Status      |
|-------------------|-----------------------------------------|---------------------------------------------------------------------------|-------------|
| `process`         | None (UNSAFE)                           | nothing                                                                   | Working     |
| `bubblewrap`      | Linux namespaces + seccomp; shared kernel | `apt install bubblewrap` — no daemon, no /etc edits                     | **Working, integration-tested** |
| `gvisor`          | User-space kernel (syscall interception)| install runsc + one line in `~/.config/containers/containers.conf`        | Code-reviewed |
| `kata` (default QEMU) | Microvm with separate guest kernel      | install kata + add user to kvm group + lines in user containers.conf      | Code-reviewed |
| `kata` (Firecracker)  | Microvm with separate guest kernel      | as above + edit `/etc/kata-containers/configuration.toml` (root config)   | Code-reviewed |
| `crun-vm`         | Microvm via libkrun                     | install crun-vm + register OCI runtime                                    | Code-reviewed |

The *Working / Code-reviewed* status is honest: only the Process and
Bubblewrap providers have been runtime-tested on the dev host. The others
are written against well-documented OCI / podman interfaces but need
runtime validation on a properly-configured host.

## `process` (dev only — refuses to load in production)

Runs sandbox commands as plain host processes in a temp directory.
**No isolation.**

Allowed only when `ASPNETCORE_ENVIRONMENT=Development`, or with explicit
`CodeyBox.DangerouslyAllowProcessSandbox=true` (don't). The startup will
refuse to load this provider in any other environment with a clear error
pointing at the alternatives.

## `bubblewrap` — recommended for "easy and safe-ish"

Single package, no daemon, no /etc edits, no podman. The orchestrator
invokes `bwrap` directly with full namespace unsharing (mount, PID, IPC,
UTS, user, cgroup, optionally network) plus a tmpfs `/tmp`.

**What you get:**
- Process tree visibility blocked (the agent can't see other host processes)
- Filesystem visibility blocked (only what we explicitly bind is reachable;
  user homes are not bound)
- Network isolation when `Audit.AuditTypes` doesn't include LLM-based
  reviewers and the work item doesn't need network — `--unshare-net` gives
  the sandbox loopback only
- Identical sandbox-side filesystem layout to the host (we replicate
  symlinks like `/bin -> usr/bin` so PATH resolution behaves the same)
- HOME isolation: the sandbox's `$HOME` is `/work`; the host user's home
  isn't reachable

**What you don't get:**
- Separate guest kernel — a Linux LPE in the agent reaches the host kernel
- Hostname allowlisting on egress — when network is allowed, the sandbox
  shares the host's network namespace. Per-host filtering would require
  host nftables rules or a userspace proxy. Pick gVisor or Kata for that.
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

## `gvisor` — recommended for production with minimal config

Runs containers under `runsc`, gVisor's user-space kernel that intercepts
syscalls instead of forwarding them to the host kernel. A kernel exploit
in the agent has to escape gVisor's much smaller, Go-implemented kernel
before it can touch the Linux host kernel.

**Setup:**
```bash
# Add Google's apt repo and install runsc:
sudo apt install runsc            # see gvisor.dev/docs/user_guide/install/

# Register the runtime (USER-level config — no /etc edits):
mkdir -p ~/.config/containers
cat >> ~/.config/containers/containers.conf <<'EOF'
[engine.runtimes]
runsc = ["/usr/bin/runsc"]
EOF
```
And in `appsettings.json`:
```json
{ "CodeyBox": { "SandboxProvider": "gvisor" } }
```
No `usermod -aG kvm` needed (runsc is a user-space kernel; KVM isn't
involved). No /etc edits.

**Trade-off vs Kata:** smaller attack surface than Linux kernel + much
faster start-up; some exotic syscalls aren't supported (rare for git +
CLI agents). Used in production by Google App Engine / Cloud Run for
similar threat models.

## `kata` (default = QEMU) — strongest isolation, no /etc edits

Each sandbox is a microvm with its own guest kernel. Default Kata setup
ships QEMU as the hypervisor — same security property as Firecracker
(separate kernel) just slower to start (~2-3s) and heavier on RAM.

**Setup (no /etc edits required):**
```bash
sudo apt install kata-containers
sudo usermod -aG kvm $USER          # one-time; for /dev/kvm access
# ... log out and back in, or `newgrp kvm`

# User-level podman runtime registration:
mkdir -p ~/.config/containers
cat >> ~/.config/containers/containers.conf <<'EOF'
[engine.runtimes]
kata = ["/usr/bin/kata-runtime"]
EOF
```
And in `appsettings.json`:
```json
{ "CodeyBox": { "SandboxProvider": "kata" } }
```

The `usermod` is a one-time sudo command, not a config-file edit.

## `kata` with Firecracker — advanced

Firecracker has faster cold-start (~125ms vs ~2-3s) and lower memory
overhead than QEMU. Same security guarantees. Requires editing
`/etc/kata-containers/configuration.toml` to switch the hypervisor —
this is the only path that needs root config.

If you don't want the /etc edit, stick with `kata` (QEMU) and accept the
slower start. The security property — separate guest kernel — is the
same.

```toml
# /etc/kata-containers/configuration.toml
[hypervisor.firecracker]
path = "/usr/bin/firecracker"

[runtime]
hypervisor = "firecracker"
```

(Or use a Firecracker-pre-configured `kata-fc` runtime if your distro
ships one.)

## `crun-vm` — alternative VM runtime

libkrun-backed microvm OCI runtime. Lighter dependency footprint than
Kata, smaller community. Same security property (separate guest kernel).

```bash
sudo apt install crun-vm           # availability varies by distro
mkdir -p ~/.config/containers
cat >> ~/.config/containers/containers.conf <<'EOF'
[engine.runtimes]
crun-vm = ["/usr/bin/crun-vm"]
EOF
```
```json
{ "CodeyBox": { "SandboxProvider": "crun-vm" } }
```

## Choosing

| Use case                                            | Pick           |
|-----------------------------------------------------|----------------|
| Local development of the orchestrator itself        | `process`      |
| Pre-prod / trusted prompts / "just give me a sandbox" | `bubblewrap` |
| Production / untrusted prompts / minimal config     | `gvisor`       |
| Production / strongest isolation / OK with `usermod` | `kata` (QEMU) |
| Production / fastest VMs / OK with /etc edit        | `kata` (Firecracker) |
| You already run libkrun                             | `crun-vm`      |

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

| Provider     | Enforcement mechanism                                                       |
|--------------|------------------------------------------------------------------------------|
| process      | None (dev only)                                                              |
| bubblewrap   | Binary on/off (`--unshare-net` or `--share-net`); no per-host filtering      |
| gvisor       | Container netns + podman CNI network; operator adds nftables rules to filter |
| kata         | Container netns + podman CNI network; operator adds nftables rules to filter |
| crun-vm      | Container netns + podman CNI network; operator adds nftables rules to filter |

For provider-level allowlisting, configure host nftables on the
`codeybox-egress` CNI network. Sample drop-all-except-allowed rules can
be auto-generated from the allowed-hosts list — that's an operator-
facing helper, not orchestrator code.
