# Host platform support

CodeyBox 0.7 ships **Linux only** for local VM sandboxes. The orchestrator
also runs on macOS and Windows, but only in the **remote-executor topology**:
VMs execute on a Linux executor host while the orchestrator runs locally.
This page records why, and what is supported where.

## The egress question

Sandbox isolation is enforced **on the host, by nftables, on per-profile
Linux bridges** (`scripts/setup-host-networks.sh`). A sandbox VM attaches to
a bridge whose host-side rules drop everything not on that profile's
allowlist. The drops happen in the host kernel, on bridges the guest cannot
see — an agent with sudo inside the VM cannot switch them off.

That mechanism is Linux-only, and the assessed alternatives do not reach
equivalence:

| Candidate | Verdict | Reason |
|---|---|---|
| macOS `pf` anchor per VM | Rejected | Multipass on macOS uses Apple's hypervisor framework with its own NAT network; there is no per-VM bridge attachment point in the host kernel where an unbypassable per-profile drop path can be installed. `pf` rules also require disabling SIP-protected defaults or installer-owned anchors that OS upgrades can reset. |
| Windows Filtering Platform (WFP) callout per VM | Rejected | Multipass on Windows runs on Hyper-V with a virtual switch owned by the provider. Per-VM allowlist enforcement would need a custom WFP callout driver plus signed-driver install — an unsigned script cannot install an unbypassable host-kernel drop path, and Hyper-V NAT networks give no stable per-VM L2 hook. |
| In-guest filtering (iptables/nftables inside the VM) | Rejected | The threat model assumes a root agent. Anything enforced inside the guest is flushable from inside the guest (`iptables -F`). Enforcement must live outside the attacker-controlled kernel. |
| Per-provider user-space proxy on the host | Rejected | A proxy only constrains traffic that goes through the proxy. A root agent in a bridged/NAT VM can route around it. It is allowlist-shaped logging, not isolation. |
| Remote-executor topology (orchestrator on macOS/Windows, VMs on a Linux executor) | **Supported** | Enforcement stays exactly where it is today: nftables on Linux bridges on the executor host. No new mechanism is needed and no isolation claim is weakened. |

"Only the remote-executor topology is supported on non-Linux hosts" is the
staged answer: it reuses the proven enforcement instead of inventing a weaker
one per platform.

## Supported matrix

| Orchestrator host | `incus` | `multipass` (local) | `multipass-remote` | `sprites` | `bubblewrap` | `process` (dev-only) |
|---|---|---|---|---|---|---|
| Linux | ✅ enforced on host | ✅ enforced on host | ✅ enforced on executor | ✅ enforced on executor | ⚠️ shared kernel, no egress | ⚠️ no isolation, dev only |
| macOS | ❌ | ❌ | ✅ enforced on executor | ✅ enforced on executor | ❌ | ❌ |
| Windows | ❌ | ❌ | ✅ enforced on executor | ✅ enforced on executor | ❌ | ❌ |

Legend: ✅ = supported with network isolation; ⚠️ = runs but must never be
described as isolated (see `concepts/security.md` sharp edges); ❌ =
rejected at startup with a message pointing here.

Running the orchestrator on a platform does **not** imply guest sandboxes for
it: guests are always Linux VMs. The matrix is about where the orchestrator
runs, not what the agent edits.

The matrix is executable, not just prose:
`CodeyBox.Core.HostPlatformSupport` declares it, the API startup path
(`SelectSandboxProvider`) and options validation reject unsupported
combinations, and `HostPlatformSupportTests` asserts the documented matrix
matches what the code registers.

## Quickstart per host OS

### macOS / Windows (remote-executor only)

1. Provision a **Linux executor host** (any KVM-capable Linux box or VM) and
   run `sudo scripts/setup-host-networks.sh` there. Verify with
   `nft list table inet codeybox` on the executor.
2. From the executor, confirm Multipass is reachable over SSH from your
   machine (`ssh user@executor multipass list`).
3. Build the orchestrator locally:
   - macOS/Linux: `./build.sh`
   - Windows (PowerShell 7+): `./build.ps1`
4. Configure `CodeyBox:SandboxProvider` to `multipass-remote` (or `sprites`)
   with the executor's `SshTarget`, and run the normal
   `getting-started.md` flow from step 4 onward. Queue a trivial work item;
   it executes in a VM on the executor, under the executor's allowlist.
5. `GitRootDirectory` and `StateDatabasePath` default per OS
   (`%PROGRAMDATA%\CodeyBox\...` on Windows, `/var/lib/codeybox/...`
   elsewhere); override in config if desired.

### Linux (full support)

Follow `getting-started.md` as written, including
`sudo scripts/setup-host-networks.sh` on the orchestrator host itself.

## Egress verification procedure

Isolation is claimed only where the enforcement mechanism has been
exercised. To demonstrate the block on any host claiming isolation:

**Linux orchestrator or Linux executor host:**

```bash
sudo scripts/setup-host-networks.sh /etc/codeybox/networks.conf
nft list table inet codeybox   # allowlist rules present per bridge
# Launch a sandbox on the `isolated` profile, then from inside it:
curl -m 10 https://api.anthropic.com -o /dev/null -w '%{http_code}\n'  # expect timeout/drop
getent hosts api.anthropic.com  # DNS resolves, TCP does not establish
```

Expected result: DNS may resolve but no TCP connection outside the profile
allowlist establishes; `nft` counters on the bridge chain increment on drop.
Record the `nft` output with the deployment — that is the isolation evidence,
not the config file alone.

**macOS/Windows orchestrator:** run the same procedure on the Linux executor
host. No isolation is claimed for anything enforced (or unenforced) on the
macOS/Windows machine itself.

## Build packaging note: the ACP bridge

`src/CodeyBox.Agents.Claude/Resources/acp-bridge` is a statically-linked
Linux ELF (built via `scripts/publish-acp-bridge.sh`, which needs musl,
clang, lld, and a Multipass verification VM). It runs **inside Linux
guests**, so a macOS/Windows orchestrator host never executes it — but only a
Linux host can rebuild it. The prebuilt resource is checked in, so
`./build.sh` / `./build.ps1` on any host consume it without rebuilding; do
not attempt to regenerate it from macOS/Windows.
