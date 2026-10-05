# CodeyBox Tart sandbox provider plugin

Sandbox provider plugin (`kind: tart`, plugin id `codeybox.tart-sandbox`)
that runs CodeyBox work items in [Tart](https://tart.run) VMs: macOS (and
Linux) guests on Apple Silicon hosts via Apple's Virtualization.framework,
driven over the local `tart` CLI with guest access over SSH. One project,
one plugin, contributed through the plugin trust model. **Off unless an
operator enables it.** This is the first credible path to native macOS and
Xcode workers.

## Containment posture — read this first

CodeyBox ships Linux-only precisely because the egress guarantee depends
on nftables, which macOS cannot provide. This provider therefore has
**no host-enforced egress filtering**, and the host classifies every
plugin kind `NotEnforced` — this plugin cannot promote itself, and nothing
below changes that classification. Do not claim enforced egress.

What isolation this provider **does** give:

- Each work item gets a fresh VM (separate guest kernel via Apple's
  Virtualization.framework) with no shared filesystem with the
  orchestrator host or with other VMs. Host mounts are staged into the
  guest file-by-file over SSH — never shared by reference.
- The guest SSH password travels in the SSH child-process environment
  (`SSHPASS`, via `sshpass -e`) or is replaced by key auth (`-i`); it
  never appears in argv, configuration files, logs, or the remote command
  string. Secret-bearing exec environments are staged as a root-only
  guest file over an stdin pipe and sourced-then-deleted; a staging
  failure fails the exec instead of falling back to inline transport.
- No process is ever launched through a shell string: `tart`, `ssh`,
  and `sshpass` are spawned as argv arrays, and every guest path or
  variable that reaches a remote shell is single-quote escaped.
- Guest SSH server authentication: `StrictHostKeyChecking=accept-new`
  against a provider-owned known_hosts file (`SshKnownHostsPath`), never
  `/dev/null`. The first contact trusts the guest key into that file and
  later contacts verify it. Clones from one image share that image's host
  keys until rotated — prefer images that regenerate host keys on first
  boot, and key auth over password auth.

What it **does not** give:

- No static host-enforced egress filtering. In the default `nat` mode,
  guest egress follows the Mac host's network, not any CodeyBox network
  profile. `softnet` mode (below) adds a per-VM Softnet packet filter, but
  the host still classifies the kind `NotEnforced` — statically this never
  becomes an enforced network profile. The provider refuses any sandbox
  naming a network profile, and placement never routes profiled work here
  **unless** the operator opts `tart` into host-owned per-sandbox canary
  verification (`CodeyBox:EgressVerification`, below): then each sandbox is
  handed over only after its own canary passes, and the verified grant is
  deliberately never stronger than orchestrator-host nftables enforcement.
  An operator choosing this provider for unprofiled work is accepting
  host-default egress (NAT) or a guest filter without a passing canary,
  not a CodeyBox allowlist.
- Suspend is stop/start: a suspended VM keeps its clone directory but
  running processes do not survive; the pipeline replays from its
  checkpoint, so this is expected, not data loss.
- No tmpfs semantics. Credential tmpfs mounts are refused outright (use
  environment variables); other tmpfs mounts need
  `AllowPersistentTmpfsDowngrade` and become persistent guest
  directories.
- No baseline bake, disk guard, cache seeding, or port publishing. The
  declared capabilities say exactly this, so placement refuses work that
  needs them instead of failing deep inside a phase.

## What an operator must configure

1. An Apple Silicon Mac with Tart installed and `sshpass` on the `PATH`
   (password auth), or an SSH key (key auth). Any other OS refuses with
   an infrastructure deferral naming `tart`.
2. Guest SSH credentials **from the host credential chain, never from
   configuration files**: `TART_SSH_PASSWORD` in the orchestrator process
   environment (the options type has no password property — configuration
   cannot carry one), or `SshPrivateKeyPath` to a host-owned key file.
3. Allowlist **and** enable the plugin (both gates must pass; changing
   `Enabled` requires a host restart):

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.tart-sandbox"],
      "Enabled": ["codeybox.tart-sandbox"]
    },
    "Plugins:codeybox.tart-sandbox": {
      "Enabled": true,
      "DefaultImage": "ghcr.io/cirruslabs/macos-sequoia-xcode:latest"
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          { "MemberId": "tart-mac", "ProviderKind": "tart", "Capacity": 2 }
        ]
      }
    ]
  }
}
```

4. Size `Capacity` for the host's RAM/CPU (1–2 per Mac): every sandbox
   is a full hardware-virtualized guest, and macOS images are tens of
   GB. The provider honours its cap, reports live load for least-loaded
   placement, and defers rather than overcommits.

### Full knob reference (`CodeyBox:Plugins:codeybox.tart-sandbox`)

All values are hot-reloadable: the provider re-reads the section on every
operation. Operational values are config knobs, never literals in source.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch; the provider refuses to provision until `true`. |
| `TartBinaryPath` | `tart` | Tart CLI binary (absolute path recommended). |
| `DefaultImage` | `ghcr.io/cirruslabs/macos-sequoia-base:latest` | Image cloned when the spec names none. Use an `-xcode` image for Xcode workers. |
| `NamePrefix` | `codeybox-` | VM name prefix; managed-VM filtering uses it. |
| `DefaultCpuCount` | `4` | Applied via `tart set` when the spec leaves CPU unset (1–32). |
| `DefaultMemoryGiB` | `8` | Applied via `tart set` when the spec leaves memory unset (1–128 GiB). |
| `SshUsername` | `admin` | Guest SSH username (non-secret). |
| `SshPasswordEnvVar` | `TART_SSH_PASSWORD` | Env var holding the guest SSH password. Only the *name* is configured. |
| `SshPrivateKeyPath` | unset | Host path to an SSH private key; key auth wins over password auth. |
| `SshKnownHostsPath` | `~/.ssh/codeybox-tart-known_hosts` | Provider-owned known_hosts file for guest server authentication (`accept-new`); never `/dev/null`. Clones from one image share that image's host keys until rotated, so trust stays scoped to this file. |
| `SshPort` | `22` | Guest SSH port. |
| `SshConnectTimeoutSeconds` | `10` | One SSH connect attempt. |
| `ReadyTimeoutSeconds` | `300` | Bound on waiting for guest SSH after `tart run`; refused credentials fail fast as `unauthorised`. |
| `TransitionTimeoutSeconds` | `180` | Bound on stop/delete transitions. |
| `PollIntervalMilliseconds` | `2000` | Guest-reachability poll cadence. |
| `CliTimeoutSeconds` | `60` | Per-invocation timeout for `tart` CLI calls. |
| `SetupCommands` | unset | Operator-trusted guest provisioning commands, run after boot before staging. |
| `MaxStageFileBytes` | `48 MiB` | Largest single staged file or sync-back file. |
| `AllowPersistentTmpfsDowngrade` | `false` | Downgrade non-secret tmpfs mounts to persistent guest dirs; credential tmpfs is always refused. |
| `ProvisioningRecheckSeconds` | `60` | Backoff floor on provisioning-deferred failures; throttling suggests a longer wait. |
| `Network:Mode` | `nat` | Guest-network backend: `nat` (today's behaviour) or `softnet` (per-VM packet filter; see below). |
| `Network:GatewayCidr` | `192.168.64.1/32` | vmnet gateway CIDR the guest needs for DHCP/DNS; always allowed in Softnet mode, even with an empty acquisition allowlist. Override to match this Mac's Softnet subnet. Never `@host` or the LAN. |
| `Network:MaxAllowCidrs` | `64` | Cap on resolved allowlist CIDRs (1–4096, gateway extra); overflows fail the create. |
| `Network:DnsTimeoutSeconds` | `5` | Per-host DNS bound in seconds (1–30) for allowlist resolution. |
| `Network:SoftnetBinaryPath` | `softnet` | Softnet helper binary probed at preflight (absolute path recommended). |

## Softnet egress filtering (`Network:Mode=softnet`, default off)

Tart supports [Softnet](https://github.com/cirruslabs/softnet): a userspace
packet filter that runs on the Mac host, outside the guest, giving each VM
its own vmnet network with a private `/30` subnet. In Softnet mode every
`tart run` is launched as an argv array with `--net-softnet`,
`--net-softnet-block=0.0.0.0/0`, and
`--net-softnet-allow=<gateway>,<resolved /32s>`, where the allowlist is the
acquisition's `AllowedHosts` resolved to IPv4 at create time (bounded per-host
DNS timeout; unresolvable hosts are logged and skipped while block-all holds
for them) plus the vmnet gateway (DHCP/DNS). An empty allowlist means no
egress beyond DNS. The effective policy (mode + exact CIDRs) is recorded on
the sandbox (`ITartSoftnetPolicyReport`, readable via `SandboxCapability`)
and in the create log, so the host verifier and operators see exactly what
each VM may reach.

Rules the provider enforces: preflight (macOS only) probes the Softnet binary
before any clone and refuses the create with a typed
`TartSoftnetUnavailableException` naming the setup step when Softnet is
missing or unprivileged — never a silent NAT fallback. Resume reinstalls the
stored create-time allowlist; a VM whose policy is unknown (provider
restarted) refuses resume fail-closed: delete it and let the pipeline
provision a fresh sandbox.

Prerequisites (Mac host only): Softnet installed with its setuid bit or a
passwordless-sudoers entry (it drops privileges after initialisation).

### Operator verification (needs a real Mac — not verified in CI)

No CI or dev machine here is a Mac, so run this scripted check on the target
Mac after enabling Softnet mode:

```bash
# 1. Probe the helper the provider preflights.
softnet --version || sudo -n softnet --version
# 2. Confirm the vmnet gateway matches Network:GatewayCidr.
tart run --help | grep -A2 softnet
# 3. Launch one sandbox and confirm the filter flags in the log
#    ("Softnet egress policy installed (block 0.0.0.0/0, allow [...])").
# 4. From inside the guest: allowed host reaches, other hosts time out, and
#    DNS resolves (gateway DNS) while TCP to a non-allowlisted IP hangs.
# 5. Negative: point Network:SoftnetBinaryPath at a missing binary and confirm
#    creates fail with TartSoftnetUnavailableException (no NAT fallback).
```

### Serving profiled work: per-sandbox canary verification (opt-in)

Softnet alone never changes the static `NotEnforced` classification. To let
profiled work run on `tart` sandboxes, the operator additionally opts the
kind into host-owned canary verification. The decision is host code, not
plugin code: after placement creates the sandbox (with the named profile
translated to the Softnet allowlist — `AllowedHosts`, never a host bridge)
and before any work phase runs, the host probes the sandbox through the
normal exec path:

- a TCP connect to `AllowedHost` (which must be on the allowlist) succeeds;
- a TCP connect to `BlockedHost` — a dedicated canary IP the operator
  guarantees is NOT on any allowlist (use a TEST-NET address or an
  operator-owned sink, never a third-party host) — fails within the bound;
- a TCP connect to a global IPv6 address (`Ipv6Host`, default a TEST-NET-6
  documentation address) fails — Softnet documents IPv4 only, so IPv6 must
  be proven blocked;
- a TCP connect to the Mac host's own LAN address (`LanHost`) fails.

```json
{
  "CodeyBox": {
    "EgressVerification": {
      "Kinds": ["tart"],
      "AllowedHost": "192.0.2.10",
      "AllowedPort": 443,
      "BlockedHost": "198.51.100.7",
      "BlockedPort": 443,
      "LanHost": "192.168.1.2",
      "LanPort": 22,
      "PerCheckTimeout": "00:00:05",
      "Cooldown": "00:15:00",
      "ReverifyInterval": "00:00:00",
      "MaxProbeOutputBytes": 4096
    }
  }
}
```

(`Ipv6Host`/`Ipv6Port` default to `2001:db8::1:443`.) All values are
hot-reloadable. Every check needs its endpoint configured — a missing
endpoint fails the canary closed, and the kind is then treated as
`NotEnforced` until configured. `ReverifyInterval` `00:00:00` disables
periodic re-verification; set it (for example `00:05:00`) on long-lived
sandboxes so a filter that dies mid-run is caught. A failed canary
disposes the sandbox, emits a critical alert event, demotes `tart` to
`NotEnforced` for `Cooldown`, and re-places the item on an enforced
provider. The sandbox also exposes its filter liveness
(`IEgressFilterHealth`, backed by the `tart run` process hosting the
filter): a dead filter fails verification without running guest probes.

Extend the Mac procedure above with these canary steps (also Mac-only,
also not CI-verified):

```bash
# 6. Configure CodeyBox:EgressVerification as above (BlockedHost a TEST-NET
#    address, LanHost this Mac's LAN IP on the guest network). Queue a
#    profiled item and confirm the "egress canary passed" event with timings.
# 7. Negative: set BlockedHost to an allowlisted host and confirm the item
#    is re-placed on an enforced provider while "tart" stays demoted until
#    the cool-down lapses (critical alert in the log).
# 8. IPv6: confirm the guest has no global IPv6 route reaching past the
#    filter (the canary's IPv6 probe must fail); re-run step 6 after any
#    network change on the Mac.
```

## What it costs

Every `CreateAsync` clones a full VM image (tens of GB disk; macOS images
download ~25 GB on first pull) and runs a hardware-virtualized guest:
size `Capacity` for the host's RAM/CPU, not just disk. Stopped
(suspended) VMs keep their clone directory on disk until deleted. Apple
Silicon hosts only — there is no Linux-hosted path.

## What it cannot do

- Static enforced-egress network profiles (refused at placement and at
  create). Profiled work reaches this provider only through the host-owned
  per-sandbox canary above — never by static classification.
- Graphical sandboxes and recovery-lease adopt (refused explicitly,
  naming `tart`, rather than provisioning something different).
- Baseline bake, cache seeding, disk guard, port publishing (not
  declared; placement refuses work requiring them).
- Live processes across suspend (stop/start; processes restart from the
  pipeline checkpoint).

## Verification

- Unit and recorded-shape tests: `dotnet test --filter
  "FullyQualifiedName~CodeyBox.Tests.Tart"` (kind resolution,
  `NotEnforced` classification, capability honesty, failure taxonomy,
  capacity accounting, `tart list` JSON/text fixtures) plus
  `FullyQualifiedName~EgressVerificationTests` (host-owned canary,
  demotion cool-down, re-placement, periodic re-verification — all against
  fakes, no Mac needed).
- Live integration (`TartIntegrationTests`, tagged `requires_tart`,
  skipped unless `CODEYBOX_RUN_TART_INTEGRATION=1` with
  `TART_SSH_PASSWORD` on a macOS host): create → exec → file round-trip
  → suspend → resume → exec. Leaked `codeybox-live-*` VMs sweep with
  `tart list` / `tart delete`.
