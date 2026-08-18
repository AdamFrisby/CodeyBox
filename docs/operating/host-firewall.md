# Host-side egress enforcement (Multipass/Incus + nftables)

For sandbox providers that give kernel isolation (Multipass and Incus), egress
filtering belongs on the **host**, not inside the VM. An agent in a VM
with sudo could flush any in-guest iptables rule, so the orchestrator
deliberately installs **no** in-VM firewall — egress enforcement lives
entirely in the host kernel via nftables on per-profile bridges, where
the agent has no view and no privileged access.

This document describes how to set up the host-side enforcement.

## How it works

1. **Operator runs `scripts/setup-host-networks.sh` once** (with sudo). The
   script creates a Linux bridge per network profile, assigns each bridge
   its own subnet with a built-in DHCP server (via systemd-networkd), and
   writes nftables rules that:
   - Drop all forward traffic on Multipass's default bridge (`mpqemubr0`),
     so Multipass's control-plane NIC has no path to the internet. Incus VMs
     are created with `--no-profiles` and never receive a default NAT NIC.
   - Apply per-bridge allowlists: loopback + ESTABLISHED/RELATED + DNS +
     the resolved IPs of the configured allowed hosts. Everything else
     dropped.
2. **CodeyBox is configured with a profile→bridge map** in
   `appsettings.json` (`SandboxNetworkProfiles`).
3. **At Multipass sandbox creation**, the orchestrator passes
   `--network <bridge>` to `multipass launch` based on the profile the
   work-item phase needs. That VM ends up with two NICs:
   - First NIC on `mpqemubr0` — used by Multipass's control plane
     (host → guest agent for `exec`, `mount`, `transfer`). Forward
     traffic on this bridge is dropped by host nftables, so it can't
     reach the internet.
   - Second NIC on the chosen `cb-*` bridge — the only viable path
     out, with the bridge's host-side filtering applied.
4. **At Incus sandbox creation**, the provider uses `--no-profiles`, then adds
   exactly one bridged NIC. It does not attach Incus's default profile or NAT
   network. The equivalent CLI operation is:

   ```bash
   incus --project codeybox config device add <vm> codeybox-net nic \
     nictype=bridged parent=<cb-profile-bridge> name=eth0
   ```

   The selected `cb-*` bridge is therefore the VM's only network path; no
   in-guest route swap is needed. This command documents what CodeyBox runs—an
   operator does not add the device manually. Replace `codeybox` when a
   different restart-only `Incus:ProjectName` is configured.
5. **Multipass cloud-init runs a one-shot route swap at first
   boot** that detects which interface has an IP in the `10.99.0.0/16`
   profile-bridge range and sets it as the default route. Without this,
   Linux defaults to the first NIC (mpqemubr0 → blocked) and the agent's
   traffic dies before reaching our filtered bridge.

There is nothing in the VM for a compromised agent to flush — the
drops happen in the host kernel, on bridges the agent has no view
into. A compromised Multipass agent restoring the default route to `mpqemubr0`
(`sudo ip route ...`) just self-DOSes because that traffic still hits the host
nftables drop. An Incus agent has no alternate NAT NIC to select.

### IPv4 only

The `setup-host-networks.sh` script writes `ip daddr ...` rules — IPv4
only. Traffic to IPv6 destinations is currently blocked at the host
because the `cb-*` chains have no `ip6 daddr accept` rules and end with
`drop`. That's the safe-default, but it has a visible cost: clients
that try IPv6 first (curl with happy-eyeballs, `getent hosts`) take
several seconds to fall back to IPv4. If you need IPv6 reachability,
extend the script to resolve `ahostsv6` and emit `ip6 daddr ... accept`
rules per allowed host.

## Profile modes

The `allowed-hosts` column in `networks.conf` accepts three forms:

| Value                  | Semantics                                                                 | Use case                                                |
|------------------------|---------------------------------------------------------------------------|---------------------------------------------------------|
| `-`                    | No egress at all (only DNS, loopback, established/related).               | Tool-only audits, isolated merge phases, gitops sandboxes. |
| `internet`             | Block RFC1918 / link-local / loopback / multicast / cloud-metadata; accept everything else. | "Wide reach but no LAN attacks" — agent can hit any external service but can't pivot to your home/office network or cloud-metadata endpoints. |
| `host1,host2,…`        | Specific hostname allowlist; the script resolves each to IPv4 IPs at setup time and emits accept rules for those IPs.       | Production agents bound to known APIs (api.anthropic.com etc.). Strict but requires re-running setup if CDN IPs rotate. |

The `internet` mode drops these explicitly:

- **IPv4**: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (all RFC1918);
  `100.64.0.0/10` (CGNAT); `169.254.0.0/16` (link-local + AWS/GCP metadata
  IP `169.254.169.254`); `127.0.0.0/8` (loopback); `224.0.0.0/4` (multicast);
  `240.0.0.0/4` (reserved).
- **IPv6**: `fc00::/7` (ULA); `fe80::/10` (link-local); `::1`, `::`,
  `ff00::/8` (multicast).

The bridge subnet itself (e.g. `10.99.5.0/24`) is in `10.0.0.0/8`, but that's
fine: VM↔bridge-gateway and VM↔VM-on-same-bridge traffic is L2-bridged and
never traverses the `forward` chain. The drops only apply to traffic the host
would ROUTE between interfaces — exactly the case where the agent tries to
reach LAN hosts via the host's other interfaces.

## Operator setup

Once per host:

```bash
# 1. Install the selected VM provider first. See docs/concepts/sandboxes.md;
#    Incus also requires its pre-created ZFS/Btrfs pool.

# 2. Create the network profile config.
sudo mkdir -p /etc/codeybox
sudo tee /etc/codeybox/networks.conf > /dev/null <<'EOF'
# name           bridge                       subnet         allowed-hosts
isolated         cb-iso          10.99.1.0/24   -
claude           cb-claude       10.99.2.0/24   api.anthropic.com
internet-only    cb-net          10.99.5.0/24   internet
codex            cb-codex           10.99.3.0/24   api.openai.com
multi-llm        cb-multi-llm       10.99.4.0/24   api.anthropic.com,api.openai.com,api.githubcopilot.com
graphical        cb-graphical       10.99.6.0/24   internet
EOF

# 3. Run the setup script (creates bridges, applies nftables, persists rules).
sudo /path/to/codeybox/scripts/setup-host-networks.sh

# 4. Verify provider-independent host state.
ip link show cb-iso                  # each configured bridge should exist
nft list table inet codeybox         # rules should be loaded
```

For Multipass, `multipass networks` should list the bridges. For a running
Incus sandbox, verify the one-NIC invariant directly:

```bash
incus --project codeybox config show <vm> --expanded
# In the expanded devices: stanza, the only type: nic entry must be:
#   codeybox-net:
#     name: eth0
#     nictype: bridged
#     parent: cb-claude
#     type: nic
```

There must be no inherited `eth0`/NAT device in the output. CodeyBox enforces
this by creating every Incus VM with `--no-profiles` before adding
`codeybox-net`.

Re-run the script after editing the config or to refresh resolved IPs
(useful when CDN endpoints rotate; the script re-resolves and rewrites
rules).

## CodeyBox configuration

Two layers of config:

**1. The orchestrator-wide profile→bridge map** in `appsettings.json`:

```json
{
  "CodeyBox": {
    "SandboxProvider": "incus",
    "SandboxNetworkProfiles": {
      "isolated":  "cb-iso",
      "claude":    "cb-claude",
      "codex":     "cb-codex",
      "multi-llm": "cb-multi-llm",
      "graphical": "cb-graphical"
    }
  }
}
```

These keys are *logical profile names* — labels the orchestrator uses
internally. The values are the host bridge names from `networks.conf`. The same
mapping is used by Multipass and Incus; changing the example provider to
`multipass` does not change the map.

**2. Per-project, per-phase profile selection** in each project's config
(see [`projects.md`](../concepts/projects.md)):

```yaml
networkProfiles:
  work:        claude
  rework:      claude
  auditAgent:  claude
  auditTool:   isolated
  merge:       claude
```

Each phase picks a profile by name. The orchestrator looks up the
matching bridge from `SandboxNetworkProfiles` at sandbox creation.

**Why per-project, per-phase**: a project whose tests need network
access at merge time can grant it via `merge: claude-with-tests`
(assuming you've defined that profile); a strict project keeps merge
isolated. The merge agent runs `git merge` plus whatever the project's
test suite does — the merge phase is no longer purely deterministic, so
its egress needs are project-specific.

If a profile referenced in project config isn't in
`SandboxNetworkProfiles`, the provider fails loudly at sandbox
creation — never silently degrades to "no enforcement."

### Graphical VNC exposure

Graphical Multipass sandboxes run `x11vnc` on the conventional graphical
VNC port (`SandboxConventions.GraphicalVncPort`, currently `5900`), bound
to the VM's `10.99.x.x` profile-bridge address. The server uses a per-sandbox
VNC password and only allows the host bridge gateway address.

For human access, use the loopback-only helper installed by
`setup-host-networks.sh`:

```bash
codeybox-vnc-loopback <multipass-vm-name> 5901
```

Then connect your VNC client to `127.0.0.1:5901`. Programmatic screenshots
and input use `multipass exec` (`scrot`/`xdotool`); VNC is only an
operator-facing inspection path. The helper binds host loopback and proxies
to the guest bridge VNC listener.

## Sandbox staging directory hardening

### Multipass

Multipass-snap reads cloud-init files and bind-mount sources from
`~/snap/multipass/common/codeybox-staging/`. Each sandbox gets its own
subdirectory under there. To prevent cross-sandbox visibility at the
host filesystem level:

* The staging root and each sandbox subdirectory are created with mode
  `0700` (operator-only). Other host users cannot list or read either.
* Multipass bind-mounts are scoped to a single per-sandbox subdirectory;
  virtio-fs prevents `..`-traversal past the mount root, so VM A cannot
  walk into VM B's staging through the mount.
* The orchestrator process owns all staging dirs. It is itself a trust
  boundary — a compromised orchestrator process can read every sandbox's
  data, but that's the same trust assumption as everything else the
  orchestrator does (selecting profiles, transferring credentials, etc).

A regression test (`MultipassStagingPermsTests`) verifies the staging
root's permissions don't drift back to default 0755.

### Incus

Incus mount snapshots and grouped read-only file mounts are staged under
`CodeyBox:Incus:StagingDirectory`. When it is unset, CodeyBox uses the
persistent `incus-staging` directory beside `StateDatabasePath`; it never
defaults API-created providers to a shared `/tmp` path. The root and each
per-sandbox directory are mode `0700`. The staging root's canonical parent must
already exist without symlink traversal. Prefer leaving the root absent so
CodeyBox can create it exclusively. An existing root is accepted only when it
has the service UID/GID, exact mode `0700`, and the provider-owned
`.codeybox-incus-staging-v1` marker with exact mode `0600` and expected content.

Direct virtiofs sources must resolve beneath `GitRootDirectory`, the enabled
shared upstream mirror directory, or one of the explicit canonical
`Incus:AllowedHostMountRoots`. CodeyBox resolves symlinks at
the attachment boundary and rejects sources that escape those roots. Keep the
explicit list narrow: membership in `incus-admin` is root-equivalent, and each
allowed root is data the daemon may expose to a VM when CodeyBox requests it.
`SnapshotForIsolation` sources are authorized first and then copied with the
configured byte bound into the private staging tree.

Incus VM virtiofs does not support the container-only `shift` mapping. The
configured `Incus:GuestUserId`/`GuestGroupId` therefore reaches the host as the
same numeric identity. Whenever a host path is mounted, the provider requires
those IDs to exactly match the CodeyBox process's effective host UID/GID. That
identity must be able to traverse and access each allowed root and owns the
mode-`0700` staging root. Use narrow ACLs only to grant any additional required
read access, never as a substitute for the identity match.

## What this does not protect against

- **Compromised orchestrator process.** The orchestrator decides which
  profile each VM gets. If an attacker can change the orchestrator's
  decision (e.g. via a config injection), they pick a more-permissive
  profile. Operator authn on the API and least-privileged orchestrator
  process are the answer there — out of scope for this document.
- **DNS rebinding past resolved IPs.** The script resolves hostnames at
  setup time and writes IP rules. If a CDN rotates IPs to ones we
  didn't capture, those connections fail (closed-fail) — that's the
  right direction. The reverse is also possible: an IP we allowlisted
  for `api.anthropic.com` could later be reassigned to a different
  service. For high-stakes use, swap the per-profile chain for an
  allowlist-aware proxy (squid, mitmproxy with hostname allowlist).
- **Inbound exploits.** This is purely egress filtering. Defense
  against inbound attacks is the VM's responsibility (and standard
  reduced attack surface — the VM has nothing listening that the
  agent's adversaries can reach).

## What this does protect against

- **Agent + sudo running anything inside the VM.** The orchestrator no
  longer relies on any in-guest firewall, so there's nothing in the VM
  to flush — host rules drop the traffic regardless of what the agent
  does inside.
- **Agent installing arbitrary packages, running arbitrary code,
  attempting outbound connections to attacker-controlled hosts.** Drops
  at the host's bridge nftables before the packet reaches the host's
  external interface.
- **Stored prompt injection telling the agent to exfiltrate the API
  key.** The agent can run any command but can only reach the
  allowlisted destinations.

## Troubleshooting

- **`multipass networks` doesn't show our bridges.** Check
  `networkctl status <bridge>` — it should be "configured" and "routable".
  If not, `sudo systemctl restart systemd-networkd` and
  `sudo networkctl reconfigure <bridge>`.
- **An Incus VM has a default NAT NIC or more than one NIC.** It was not
  created through the CodeyBox Incus provider's `--no-profiles` path. Inspect
  `incus --project codeybox config show <vm> --expanded` and treat the instance as
  unmanaged; do not rely on the `cb-*` bridge policy until the extra device is
  removed or the VM is recreated.
- **VM's secondary NIC has no IP.** The bridge's DHCP server (in
  systemd-networkd) needs to be up. Confirm with
  `journalctl -u systemd-networkd | grep DHCP`.
- **Agent can't reach the allowed host.** Check rules with
  `nft list table inet codeybox`. Resolve the hostname yourself and
  see if the IP is in the rules. Re-run the setup script if the IP
  has rotated.
- **Agent reaches an unexpected host.** Either the IP was allowlisted
  (check the resolved IPs against your config) or the rule isn't being
  hit (check `nft -a list ruleset` for counters). If forward traffic on
  `mpqemubr0` is being accepted, the script's `cb_default_blocked`
  chain isn't loaded — re-run.

## Tests

`MultipassNetworkProfileTests` and `IncusCommandBuilderTests` (fast,
unit-level) verify that the shared profile→bridge mapping becomes the expected
provider-specific argv, including Incus's `nictype=bridged`, selected parent,
and `--no-profiles`. The Incus integration path separately verifies virtiofs
devices and the absence of 9p. Actual host-side packet enforcement is verified
by configuring real bridges and running the orchestrator end-to-end
(operator-side verification, not in CI).
