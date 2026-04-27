# Sandbox providers

CodeyBox ships three providers. Pick one at composition time by changing
the DI registration in `CodeyBox.Api/Program.cs`. The `ISandboxProvider`
contract is identical across all three.

## Comparison

| Provider           | Isolation           | Setup difficulty | Status             | Use for         |
|--------------------|---------------------|------------------|--------------------|-----------------|
| `Sandbox.Process`  | None (UNSAFE)       | Trivial          | Working            | Dev/CI of the orchestrator itself |
| `Sandbox.Kata`     | Microvm (Firecracker)| High            | Skeleton           | Production      |
| `Sandbox.CrunVm`   | Microvm (libkrun)   | Medium           | Skeleton           | Production-lite |

## `Sandbox.Process` (dev only)

Runs sandbox commands as plain host processes in a temp directory. Read-only
mounts are file copies; writable mounts are symlinks (so the sandbox's git
push to the bare repo actually lands). The network policy is **not** enforced.

Use only for developing the orchestrator pipeline itself. Never ship.

## `Sandbox.Kata` (production target)

Each sandbox is a Firecracker microVM under Kata Containers, launched via
podman or containerd with the `kata` runtime.

### Host setup checklist

1. **KVM available.** `kvm-ok` or `[ -e /dev/kvm ]`. Required for Firecracker.
2. **Kata Containers installed.** `kata-runtime --version`. The
   `configuration.toml` must set:
   ```toml
   [hypervisor.firecracker]
   path = "/usr/bin/firecracker"
   ```
   and the runtime selected via `[runtime] hypervisor = "firecracker"`.
3. **Container engine.** podman 4+ with `containers.conf` registering
   `kata` as a runtime, or containerd with the kata-containerd shim.
4. **Sandbox image built.** A minimal OCI image containing:
   * `git`
   * The agent CLI binaries you intend to support (claude, gh + copilot
     extension, codex, …). Pin by digest.
   * No package manager credentials, no SSH keys, no user secrets.
5. **nftables table for egress policy.**
   ```
   nft add table inet codeybox
   nft add chain inet codeybox egress { type filter hook output priority 0\; policy drop\; }
   ```
   The provider implementation is responsible for adding per-sandbox accept
   rules keyed on the VM's tap interface, then removing them on teardown.
6. **virtio-fs mount for the bare-repos directory** OR a host-side
   `git-daemon` reachable from the VM network. virtio-fs is preferred because
   it avoids running a network service on the host.

### What the provider needs to implement

The current skeleton (`KataSandboxProvider.CreateAsync`) throws
`NotImplementedException` and lists the steps in a comment. The orchestrator
side is complete — when this provider is filled in, no other code changes.

## `Sandbox.CrunVm` (alternative)

Same security properties (separate guest kernel) using libkrun via the
`crun-vm` OCI runtime. Lighter weight than Kata at the cost of a smaller
ecosystem. Same skeleton status.

### Host setup checklist (abbreviated)

1. KVM available.
2. `crun-vm` installed and registered as an OCI runtime in
   `containers.conf` or `containerd` config.
3. Same image, network, and mount requirements as Kata.

## Choosing between Kata and crun-vm

* Pick **Kata** if you already run Kata for other workloads, or if you want
  the more battle-tested runtime.
* Pick **crun-vm** if you want minimal new moving parts and you're comfortable
  with libkrun's smaller community.

Both terminate at the same `ISandboxProvider` interface, so you can prototype
on one and switch later.

## Adding a new provider

1. New project: `CodeyBox.Sandbox.<Name>` referencing `CodeyBox.Sandbox`
   and `CodeyBox.Core`.
2. Implement `ISandboxProvider` and a corresponding `ISandbox`. Honour:
   * `SandboxSpec.Mounts` — including tmpfs and the read-only flag.
   * `SandboxSpec.Network` — default deny, explicit allowlist.
   * `SandboxSpec.Limits` — CPU, memory, disk, wall-clock.
   * `SandboxConventions.WorkDir` and `CredentialsDir` paths inside the sandbox.
3. Document the host setup in this file.
4. Update `docs/security.md` with any provider-specific caveats.
