# Host-side egress enforcement (Multipass + nftables)

For a sandbox provider that gives kernel isolation (Multipass), egress
filtering belongs on the **host**, not inside the VM. An agent in the VM
with sudo can flush iptables and undo any in-VM rule. The host's nftables
runs in the host kernel and is unreachable from inside the guest.

This document describes how to set up the host-side enforcement.

## How it works

1. **Operator runs `scripts/setup-host-networks.sh` once** (with sudo). The
   script creates a Linux bridge per network profile, assigns each bridge
   its own subnet with a built-in DHCP server (via systemd-networkd), and
   writes nftables rules that:
   - Drop all forward traffic on Multipass's default bridge (`mpqemubr0`),
     so the default NIC every VM gets has no path to the internet.
   - Apply per-bridge allowlists: loopback + ESTABLISHED/RELATED + DNS +
     the resolved IPs of the configured allowed hosts. Everything else
     dropped.
2. **CodeyBox is configured with a profile→bridge map** in
   `appsettings.json` (`SandboxNetworkProfiles`).
3. **At sandbox creation**, the orchestrator passes
   `--network <bridge>` to `multipass launch` based on the profile the
   work-item phase needs. The VM ends up with two NICs:
   - `eth0` on `mpqemubr0` — used by Multipass's control plane (host →
     guest agent for `exec`, `mount`, `transfer`). Forward traffic on
     this bridge is dropped by host nftables, so it can't reach
     internet.
   - `eth1` on the chosen `codeybox-net-*` bridge — the only viable path
     to the outside, with the bridge's host-side filtering applied.

A compromised agent doing `sudo iptables -F` inside the VM affects
nothing — the drops happen in the host kernel, on bridges the agent
has no view into.

## Operator setup

Once per host:

```bash
# 1. Install Multipass.
sudo snap install multipass

# 2. Create the network profile config.
sudo mkdir -p /etc/codeybox
sudo tee /etc/codeybox/networks.conf > /dev/null <<'EOF'
# name           bridge                       subnet         allowed-hosts
isolated         codeybox-net-isolated        10.99.1.0/24   -
claude           codeybox-net-claude          10.99.2.0/24   api.anthropic.com
codex            codeybox-net-codex           10.99.3.0/24   api.openai.com
multi-llm        codeybox-net-multi-llm       10.99.4.0/24   api.anthropic.com,api.openai.com,api.githubcopilot.com
EOF

# 3. Run the setup script (creates bridges, applies nftables, persists rules).
sudo /path/to/codeybox/scripts/setup-host-networks.sh

# 4. Verify.
multipass networks                   # bridges should appear
nft list table inet codeybox        # rules should be loaded
```

Re-run the script after editing the config or to refresh resolved IPs
(useful when CDN endpoints rotate; the script re-resolves and rewrites
rules).

## CodeyBox configuration

In `appsettings.json`:

```json
{
  "CodeyBox": {
    "SandboxProvider": "multipass",
    "SandboxNetworkProfiles": {
      "isolated":  "codeybox-net-isolated",
      "claude":    "codeybox-net-claude",
      "codex":     "codeybox-net-codex",
      "multi-llm": "codeybox-net-multi-llm"
    }
  }
}
```

The keys are logical profile names; the orchestrator picks them by name
based on what each phase needs (work phase = "claude" if the agent is
Claude, merge phase could be "multi-llm" for the agent-driven merge,
tool-only audit phases = "isolated"). The values are the host bridge
names from `networks.conf`.

If the orchestrator asks for a profile that isn't in the map, the
provider throws at launch — never silently falls back to "no
enforcement."

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

- **Agent + sudo flushing in-VM iptables.** Doesn't matter — host rules
  still drop the traffic.
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
  `mpqemubr0` is being accepted, the script's `cb-default-blocked`
  chain isn't loaded — re-run.

## Tests

`MultipassNetworkProfileTests` (fast, unit-level) verifies the
profile→bridge mapping turns into the correct `multipass launch
--network <bridge>` argv. The actual host-side enforcement is verified
by configuring real bridges and running the orchestrator end-to-end
(operator-side verification, not in CI).
