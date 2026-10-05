# Tart sandbox provider plugin

`CodeyBox.TartSandboxPlugin` contributes a local-VM sandbox backend for
[Tart](https://tart.run): macOS (and Linux) guests on Apple Silicon hosts
via Apple's Virtualization.framework, driven over the local `tart` CLI
with guest access over SSH. This is the only provider in the batch that
targets a non-Linux guest, and the first credible path to native macOS
and Xcode workers.

**Provider kind:** `tart` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind.

## Isolation — read this before enabling

Tart VMs give **VM isolation**: each work item gets a fresh VM with its own
guest kernel via Apple's Virtualization.framework (`DedicatedKernel` — the
primary boundary), torn down on disposal, with no shared filesystem with the
host or other VMs (host mounts are staged file-by-file over SSH, never shared
by reference). Egress enforcement is a further, separate layer — and on a Mac
host it is **not enforced by default**.

The host classifies the `tart` kind `NotEnforced`, unconditionally and
statically. The plugin cannot promote itself; no option, label, or return
value changes the classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress and
  is refused for this kind (unplaceable, naming `tart` and the missing
  `baseline`/`network-egress-enforcement` capability) — unless Softnet mode is
  on, the operator opts `tart` into host-owned per-sandbox canary verification
  (`CodeyBox:EgressVerification`), and that sandbox's own canary passes. The
  resulting grant is `EnforcedOnProviderHostVerified`, deliberately never
  stronger than orchestrator-host nftables enforcement. The provider
  additionally refuses a `ProfileName` that ever reaches it directly.
- In `nat` mode (default), guest egress follows the Mac host's network
  (NAT/shared with the host). An operator choosing this provider for unprofiled
  work is accepting host-default egress, not a CodeyBox allowlist. Do not claim
  enforced egress.

Guest SSH credentials come from the host credential chain
(`TART_SSH_PASSWORD`, or key auth via `SshPrivateKeyPath`) — never from
configuration files. Secret-bearing exec environments travel a
stdin-piped staging file that is sourced then deleted; staging
failures fail the exec instead of falling back to inline transport.

## Softnet mode — the verified egress path (opt-in, default off)

[Softnet](https://github.com/cirruslabs/softnet) is a userspace packet filter
that runs **on the Mac host, outside the guest**: each VM gets its own vmnet
network with a private `/30` subnet, and Softnet applies an IPv4 policy of
allow/block CIDR lists. `Network:Mode=softnet` attaches it to every
`tart run` (launched with `--net-softnet --net-softnet-block=<cidrs>
--net-softnet-allow=<cidrs>`, with the policy updatable at runtime over its
JSON-RPC control socket). Softnet documents IPv4 only.

### Installation (Mac host only)

Softnet needs privilege to attach to vmnet: either the setuid bit on the
helper binary or a passwordless-sudoers entry. It drops privileges after
initialisation. Install per the
[Softnet README](https://github.com/cirruslabs/softnet), then confirm the
provider preflight sees it:

```bash
softnet --version || sudo -n softnet --version
```

Point `Network:SoftnetBinaryPath` at the helper (absolute path recommended).
Preflight probes the binary before any clone and refuses the create with a
typed `TartSoftnetUnavailableException` naming the setup step when Softnet is
missing or unprivileged — never a silent NAT fallback.

### Config

```json
{
  "CodeyBox": {
    "Plugins:codeybox.tart-sandbox": {
      "Enabled": true,
      "Network": {
        "Mode": "softnet",
        "GatewayCidr": "192.168.64.1/32",
        "MaxAllowCidrs": 64,
        "DnsTimeoutSeconds": 5,
        "SoftnetBinaryPath": "/usr/local/bin/softnet"
      }
    }
  }
}
```

### What is enforced, and what is not

- **Enforced (per VM):** the acquisition's `AllowedHosts` are resolved to
  IPv4 `/32`s **at create time** (bounded per-host DNS timeout; unresolvable
  hosts are logged and skipped while block-all holds for them), plus the vmnet
  gateway (DHCP/DNS). Every `tart run` carries `--net-softnet-block=0.0.0.0/0`
  with `--net-softnet-allow=<gateway>,<resolved /32s>`. An empty allowlist
  means no egress beyond the gateway. Resume reinstalls the stored create-time
  allowlist; a VM whose policy is unknown (provider restarted) refuses resume
  fail-closed. The effective policy (mode + exact CIDRs) is recorded on the
  sandbox (`ITartSoftnetPolicyReport`) and in the create log.
- **Not enforced:** anything outside IPv4. DNS still resolves through the
  gateway (as on Linux, where DNS resolves but TCP does not establish), and
  the IPv6 posture is unverified until the operator procedure below proves the
  guest cannot reach IPv6 destinations on that Mac. Host-LAN reachability is
  likewise proven only by the canary's `LanHost` probe, not assumed.
- **Static classification unchanged:** Softnet mode alone never changes the
  host's `NotEnforced` classification. Profiled work reaches `tart` only
  through the host-owned per-sandbox canary, and the verified grant is never
  stronger than orchestrator-host enforcement.

### The canary (host-owned, per sandbox)

With `tart` opted in (`CodeyBox:EgressVerification:Kinds: ["tart"]`), the host
probes each sandbox through the normal exec path after create and before any
work phase: TCP to an allowlisted destination must succeed; connects to a
configured canary destination, to a global IPv6 address, and to the Mac host's
LAN address must all fail within the per-check bound. On failure the sandbox
is disposed, a loud alert is emitted, the kind is demoted to `NotEnforced` for
the cool-down, and the item is re-placed on an enforced provider — never run
unverified. Long-lived sandboxes can re-verify on `ReverifyInterval`, and a
dead filter process (`IEgressFilterHealth`) fails the sandbox closed. See the
plugin README for the full knob reference and canary JSON.

## Operator verification (unverified on real hardware)

No CI or dev machine here is a Mac, so the two properties below cannot be
proven in CI. They are established only by the scripted operator procedure
`scripts/verify-tart-softnet.sh`, run on a real Apple Silicon Mac with Softnet
mode on:

- **fail-closed:** kill the Softnet process for a running VM and confirm the
  guest loses ALL network, rather than falling back to open NAT;
- **IPv6:** confirm the guest cannot reach any IPv6 destination.

Until an operator has run that script on real hardware and recorded the
results here, this path is documented as **unverified on real hardware**. See
the plugin README's verification section for the script's steps.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly on a
macOS Apple Silicon host with Tart (and `sshpass` for password auth)
installed:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.tart-sandbox" ],
      "Allowlist": [ "codeybox.tart-sandbox" ]
    },
    "Plugins:codeybox.tart-sandbox": {
      "Enabled": true,
      "DefaultImage": "ghcr.io/cirruslabs/macos-sequoia-xcode:latest"
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "tart-mac",
            "ProviderKind": "tart",
            "Capacity": 2
          }
        ]
      }
    ]
  }
}
```

Size `Capacity` for the host's RAM/CPU: every sandbox is a full
hardware-virtualized guest, and macOS images are tens of GB. Keep
`Capacity` small (1–2 per Mac mini) and let the pipeline queue rather
than overcommit the host.

## Capabilities

The provider declares `suspend-resume` (stop preserves the clone
directory; resume re-runs it — running processes do not survive, the
pipeline replays from its checkpoint) and `teardown`
(stop-and-preserve, delete). It deliberately does **not** declare
baseline bake, cache seeding, disk guard, or port publishing: placement
refuses work requiring them rather than failing deep inside a phase.

## Failure handling

Every host-side failure — missing `tart` binary, non-macOS host,
unreachable guest, refused SSH credentials, rejected clone — is an
**infrastructure** failure (`SandboxProvisioningDeferredException` with
a bounded recheck, or `SandboxExecutionUnavailableException` for exec),
never a verdict on the work item's diff. A non-zero guest exit code is
an ordinary exec result.
