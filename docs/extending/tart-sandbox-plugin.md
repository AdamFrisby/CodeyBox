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

CodeyBox ships Linux-only precisely because the egress guarantee depends
on nftables, which macOS cannot provide. This item states plainly what
containment is and is not available on macOS rather than assuming parity:
**there is no statically enforced egress on this provider**, and there
cannot be without an in-tree host mechanism that does not exist.

The host therefore classifies this kind `NotEnforced`, unconditionally. The
plugin cannot promote itself; no option, label, or return value changes the
classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress and
  is refused for this kind (unplaceable, naming `tart` and the missing
  `baseline`/`network-egress-enforcement` capability) — unless the operator
  opts `tart` into host-owned per-sandbox canary verification
  (`CodeyBox:EgressVerification`), in which case each sandbox is handed over
  only after its own canary passes, and the verified grant is never stronger
  than orchestrator-host enforcement. The provider
  additionally refuses a `ProfileName` that ever reaches it directly.
- Guest egress follows the Mac host's network (NAT/shared with the host).
  An operator choosing this provider for unprofiled work is accepting
  host-default egress, not a CodeyBox allowlist. Do not claim enforced
  egress.

What the provider does give:

- A fresh VM per work item (separate guest kernel via Apple's
  Virtualization.framework), torn down on disposal. No shared filesystem
  with the host or other VMs: host mounts are staged file-by-file over
  SSH, never shared by reference.
- Guest SSH credentials from the host credential chain
  (`TART_SSH_PASSWORD`, or key auth via `SshPrivateKeyPath`) — never from
  configuration files. Secret-bearing exec environments travel a
  stdin-piped staging file that is sourced then deleted; staging
  failures fail the exec instead of falling back to inline transport.

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

## Softnet egress filtering (opt-in, default off)

`Network:Mode=softnet` attaches Tart's Softnet userspace packet filter to
every `tart run` (block `0.0.0.0/0`, allow the vmnet gateway for DHCP/DNS
plus the acquisition's `AllowedHosts` resolved to IPv4 `/32`s). Preflight
refuses creates with a typed error when Softnet is missing or unprivileged
— never a silent NAT fallback — and each sandbox reports its effective
policy for the host verifier. This does **not** change the host's static egress
classification: the kind stays `NotEnforced`, and profiled work reaches it
only through the host-owned per-sandbox canary
(`CodeyBox:EgressVerification`, operator opt-in plus a passing canary per
sandbox — dispose, demote, alert, and re-place on failure). See the plugin
README for knobs, the canary reference, and the scripted Mac-only operator
verification procedure.

## Failure handling

Every host-side failure — missing `tart` binary, non-macOS host,
unreachable guest, refused SSH credentials, rejected clone — is an
**infrastructure** failure (`SandboxProvisioningDeferredException` with
a bounded recheck, or `SandboxExecutionUnavailableException` for exec),
never a verdict on the work item's diff. A non-zero guest exit code is
an ordinary exec result.
