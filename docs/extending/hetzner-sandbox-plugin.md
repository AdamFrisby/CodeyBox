# Hetzner Cloud sandbox provider plugin

`CodeyBox.HetznerSandboxPlugin` contributes a cloud-server sandbox backend
for Hetzner Cloud. Sandboxes are real servers — one per work item — booted in
the configured location with a per-sandbox SSH key, a per-sandbox firewall,
ownership labels, and cloud-init user data; exec and file transfer go over
SSH using the shared OpenSSH CLI transport seam. The OpenStack remote-VM
provider is the concrete analogue: same lifecycle shape, same SSH data plane,
same label-scoped leak cleanup — adapted to the Hetzner Cloud API.

**Provider kind:** `hetzner` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind.

## Isolation — read this before enabling

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee CodeyBox gives for local VMs — nftables
allowlist drops enforced in a kernel CodeyBox owns — **cannot apply here**.

The host therefore classifies this kind `NotEnforced`, unconditionally. The
plugin cannot promote itself; no option, label, or return value changes the
classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress and
  is refused for this kind (unplaceable, `enforced-egress`, naming
  `hetzner`). The provider additionally refuses a `ProfileName` that ever
  reaches it.
- Each sandbox gets a dedicated firewall that opens SSH ingress (TCP/22) from
  the orchestrator CIDRs plus DNS/NTP and the resolved `AllowedHosts` egress
  IPs. That is **best-effort defence in depth only** — vendor-managed filters
  on hardware you do not own are not a substitute for the host-enforced
  guarantee. An empty allowlist means no egress beyond DNS/NTP; hostnames that
  fail DNS narrow egress rather than failing the item, and resolutions can go
  stale.

Provider-specific network-filter limitations, stated plainly:

- The firewall rules are explicit for everything the sandbox needs (SSH
  ingress; DNS/NTP/allowlist egress). The vendor filter's exact
  default-deny/default-allow posture was not independently verified here, so
  the provider assumes nothing about implicit defaults — the plan is
  complete either way, but it is **not** an enforced-egress claim.
- A dedicated guest kernel does not imply host-enforced egress. No network
  policy is enabled and host recognition is unchanged by this provider.
- No price advantage is claimed: capacity planning is the operator's job
  (see Capacity and placement).

What the provider does give:

- A fresh server per work item, torn down on disposal — a real guest kernel,
  so the provider reports `SandboxIsolationLevel.DedicatedKernel` for
  workload-trust routing. This is a real isolation claim about the *guest
  boundary*, still independent of the egress classification.
- Genuine RAM-backed tmpfs mounts (including `/run/codeybox/creds`) via
  cloud-init, so file-backed agent credentials are supported — and refused
  anywhere outside tmpfs.
- Everything — server disks, staged files — lives on the cloud provider's
  storage, which CodeyBox does not control. Do not send workloads whose
  content or secrets you are unwilling to place on third-party
  infrastructure.

## Enabling the plugin

The plugin is disabled by default and declares no `SandboxClass` member. An
operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.hetzner-sandbox" ],
      "Allowlist": [ "codeybox.hetzner-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "hetzner-pool",
            "ProviderKind": "hetzner",
            "Capacity": 4,
            "PreferenceScore": 40
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under
`CodeyBox:Plugins:codeybox.hetzner-sandbox` (full sample with placeholder
values in the next section):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.hetzner-sandbox": {
        "Enabled": true,
        "ServerType": "cx23",
        "Image": "ubuntu-24.04",
        "Location": "fsn1"
      }
    }
  }
}
```

## Sample: Hetzner Cloud

Every value below is operator-chosen against the Hetzner Cloud console or
API — no value is account-specific in the provider itself. Placeholders only:
substitute your own names and CIDRs, and never put the API token in
configuration (see Credentials).

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.hetzner-sandbox": {
        "Enabled": true,
        "ApiBaseUrl": "https://api.hetzner.cloud/v1",
        "TokenEnvVar": "HCLOUD_TOKEN",
        "ServerType": "<hetzner-server-type-name>",
        "Image": "<approved-image-id-or-exact-name>",
        "Location": "<hetzner-location-name>",
        "NetworkId": 0,
        "EnablePublicIpv4": true,
        "EnablePublicIpv6": false,
        "FloatingIpHomeLocation": "",
        "SshUser": "root",
        "OwnerId": "<owner-tag-for-this-host>",
        "ServerNamePrefix": "codeybox-",
        "SshKeyNamePrefix": "codeybox-",
        "FirewallNamePrefix": "codeybox-fw-",
        "FloatingIpNamePrefix": "codeybox-fip-",
        "OrchestratorSshCidrs": ["<orchestrator-egress-cidr>"],
        "DnsServerIps": ["1.1.1.1", "8.8.8.8"],
        "NtpServerIps": ["1.1.1.1", "8.8.8.8"],
        "MaxFirewallRules": 128,
        "ReadyTimeoutSeconds": 600,
        "SshReadyTimeoutSeconds": 300,
        "ProvisioningRecheckSeconds": 60,
        "DnsTimeoutSeconds": 15,
        "SshBinary": "ssh",
        "SshKeygenBinary": "ssh-keygen",
        "SshPort": 22,
        "SshConnectTimeoutSeconds": 10
      }
    }
  }
}
```

Notes on the sample:

- `ServerType` resolves by exact (ordinal) server-type name and must not be
  deprecated; `Location` resolves by exact location name. Both fail loudly
  when missing or ambiguous — the vendor never chooses placement.
- `Image` accepts a numeric image id or an exact image name, which must match
  exactly one **available, non-deprecated** system image. A per-acquisition
  `ImageReference` overrides it with the same rules. There is no baseline
  bake/snapshot capability: every acquisition boots this approved image.
- `NetworkId` 0 (default) means no private network; otherwise the id (not a
  name) is attached at create time. SSH still travels over the public path.
- `FloatingIpHomeLocation` empty disables floating IPs: the sandbox is
  reached on its server public address. Set to a home location (e.g. the
  same value as `Location`) to allocate one floating IPv4 per sandbox,
  created already assigned to the server, and SSH to it.
- Disabling both `EnablePublicIpv4` and `EnablePublicIpv6` with no floating
  IP is refused: the provider would have no SSH path into the sandbox. IPv6
  reporting uses the vendor's `/64` network with the guest's first host
  address (`<network>::1`); anything unparseable is refused, not guessed.
- `OrchestratorSshCidrs` must name at least one CIDR — without it nobody
  could SSH in, so provisioning refuses to run.
- All `*NamePrefix` values must start with `codeybox-` so leak reaping can
  scope candidates — though a prefix alone never authorizes deletion: the
  ownership labels are re-verified at every deletion.
- All values are hot-reloadable options (re-read on every operation); the
  section carries no secret material.

## Credentials

The API token comes **only** from the host credential chain (environment):

- `HCLOUD_TOKEN` — the API token (no fallback: a missing variable fails
  loudly, naming the variable). Rename the variable via `TokenEnvVar`, never
  the value's location: the value stays out of configuration files.
- `HCLOUD_API_ENDPOINT` — fallback when `ApiBaseUrl` is empty (non-secret).

The token rides the `Authorization: Bearer` header only. It is never logged
(the credentials record redacts it in `ToString`), never persisted, never
copied into exception messages — which name the *variable*, never the
value — and never copied into guest user-data, logs, or prompts. Provision
it through the usual host secret path (vault agent, container secrets).
Rotation propagates without a restart because the value is read per call.
No signup, token creation, or grant change is ever performed by this
provider: the operator creates the token out of band (see Operator
prerequisites).

Every endpoint must be `https`. A cleartext `http` URL is refused unless the
dev-only `AllowUnsafeHttp` option is set, and even then only for loopback
hosts (`localhost` / `127.0.0.1` / `::1`): remote `http` URLs are refused
unconditionally, so the token can never ride a cleartext request to a remote
host because of one operator edit.

## Operator prerequisites (future work, not performed here)

Before enabling, the operator — outside CodeyBox — must have:

1. A Hetzner Cloud project with an API token scoped to it, exported as
   `HCLOUD_TOKEN` on the orchestrator host.
2. A chosen server type, location, and approved image id/name for the pins.
3. The orchestrator's egress CIDRs for `OrchestratorSshCidrs`.
4. (Optional) A private network id for `NetworkId`, and/or a floating-IP
   home location.

CodeyBox performs none of these: no account signup, no token creation, no
network/firewall pre-provisioning, no Codey runtime configuration change.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — a Hetzner
member never exceeds its cap, and queued acquisitions wait for headroom
rather than overflowing onto the cloud. Live load reaches placement through
the existing admission wrappers (held permits + `SandboxLiveCounter`), so
least-loaded member selection stays accurate. Configure capacity to match the
project's real Hetzner limits: the Cloud API exposes no pre-flight quota
endpoint this provider could consult, so quota exhaustion surfaces as a
deferred provisioning failure with a bounded recheck interval
(`ProvisioningRecheckSeconds`; longer for auth/quota classes so operators can
fix credentials or capacity). The provider never claims an unverified price
advantage — size `Capacity` from the project's actual budget and limits.

## What the provider implements

- **Lifecycle.** Resolve server type / image / location (all fail loudly
  when missing, ambiguous, or deprecated — operator configuration, never an
  item verdict) → generate an ephemeral ed25519 keypair (`ssh-keygen` via
  argv array, no shell) and register it as a labeled Hetzner SSH key → plan
  and create a labeled per-sandbox firewall (no attached resources yet) →
  boot the server with cloud-init user data, the SSH key, and the firewall
  attached, stamped with ownership labels (`codeybox-owned=true`,
  `codeybox-owner`, `codeybox-work-item`, `codeybox-request`,
  `codeybox-created`) → optionally allocate an assigned floating IP →
  bounded waits for `running` and SSH readiness. Disposal (graceful shutdown
  best-effort, then delete with bounded deletion confirm) removes the
  server, floating IP, firewall, and SSH key, idempotently; a 401/403/429/
  quota/5xx refusal deletes the half-built set and defers rather than
  failing the item.
- **Ambiguous creates reconcile, never duplicate.** The server-create call
  carries no vendor idempotency key, so when it fails ambiguously
  (transport cut, timeout, throttle, 5xx) the provider lists for the exact
  ownership triple including the per-attempt `codeybox-request` label and
  adopts the server it finds instead of resubmitting. Zero or several
  matches fail closed (rethrow / refuse to pick).
- **Exec.** Commands run over the shared OpenSSH CLI transport seam
  (`IRemoteHostTransport` / `OpenSshCliTransport` — no copy). Host keys are
  strict: the guest's ed25519 host key is generated on the orchestrator,
  injected via cloud-init `ssh_keys`, and pinned in a per-sandbox
  known_hosts file (`StrictHostKeyChecking=yes`, global known_hosts
  ignored). Cancelling a local SSH operation only proves the local transport
  stopped waiting — the remote command may still run — so disposal still
  shuts down and deletes the server as the termination fallback.
- **Files.** Host mounts are staged in at create over the same SSH transport.
  Writable host mounts sync back at `SyncStateToHostAsync` and — before the
  server is deleted and before the handle is marked disposed — once more at
  disposal. tmpfs mounts (including `/run/codeybox/creds`) are genuine
  RAM-backed tmpfs via cloud-init `mounts:`; file-backed agent credentials
  are supported and refused anywhere outside tmpfs.
- **Leak cleanup.** `ListAllManagedAsync` inventories owner-labeled servers
  and `DisposeLeakedAsync` deletes one leaked sandbox's full labeled set;
  the same pass sweeps this host's SSH keys, firewalls, and (unassigned)
  floating IPs whose request label matches no live owned server. Every
  deletion re-verifies the exact `codeybox-owned`/`codeybox-owner` labels —
  name prefixes gate candidacy but never authorize deletion — and other
  owners' resources are never touched. A deletion that cannot prove removal
  keeps its labels and is logged for the reaper; cleanup failures stay
  visible and recoverable, never silent.
- **Teardown.** `teardown` is declared. No other capability is declared: no
  baseline bake/snapshot, no suspend/resume, no disk guard, no cache
  seeding, no port publishing, no graphical flavor.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class and a recheck interval — `Retry-After` honored for
  429s; auth/quota classes wait longer so operators can fix credentials or
  capacity.
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Setup defects (unknown server type/image/location, deprecated pins, empty
  SSH CIDRs, bad prefixes, no SSH path) are deterministic errors —
  provisioning defects in the configuration, not service outages.
- A failed create attempts a best-effort delete; when the delete cannot prove
  removal the deferral names the leak risk and the labels retain the orphan
  identity for the reaper.

## What it does not implement

- **Baseline bake/snapshot.** Not implemented and not declared; acquisitions
  always boot the configured approved image.
- **Port publishing, disk guard, cache seeding, detached batch.** Not
  implemented; not declared.
- **Graphical flavor.** Refused: servers boot headless; there is no display
  server.

## Troubleshooting

- `environment variable 'HCLOUD_TOKEN' is not set` — export the token on the
  orchestrator host (or set `TokenEnvVar` to the variable you use). Never
  put the value in configuration files.
- `must use https://` — the API base URL is cleartext `http`. Fix the URL;
  `AllowUnsafeHttp` only permits loopback test URLs.
- `server type '…' not found` / image resolves to zero or several matches /
  `location '…' not found` / `is deprecated` — set `ServerType`, `Image`,
  and `Location` to the exact visible names (console or `hcloud` CLI); use a
  numeric image id when the name is ambiguous.
- Deferrals naming `quota-exhausted` — raise the project's Hetzner limits or
  lower the member `Capacity`; the provider rechecks after
  `ProvisioningRecheckSeconds`.
- SSH never becomes ready — check `OrchestratorSshCidrs` covers the
  orchestrator's egress address, and that the location's public path routes
  to it (or set `FloatingIpHomeLocation`).
- `uname -a` works but staging is slow — the server public address may
  hairpin through the cloud edge; a floating IP on the orchestrator's route
  is usually faster.

## Verification without a live service (and the live gate)

A live test needs a funded Hetzner Cloud project, creates billable servers,
and requires egress to the cloud — none available in CI, and no cloud API
call of any kind (including dry-run/list) is performed here. In its place,
`HetznerSandboxProviderTests.cs` drives the provider against a fake
in-process cloud (`FakeHetznerCloud`, an `HttpMessageHandler` mirroring the
recorded Hetzner Cloud request/response shapes) plus a fake SSH transport,
covering the full create-stage-exec-sync-dispose lifecycle, disabled/config
validation, explicit image and scope, host-key pinning, invalid tmpfs
credentials, cancellation at every provisioning boundary, remote transport
loss, output limits, partial and ambiguous creates, eventual consistency,
401/403/429/quota/5xx, malformed/truncated/oversized responses, restart
adoption and orphan cleanup, repeated disposal, sync-back before teardown,
retained cleanup failure, and ownership collisions. No test is skipped and no
unavailable coverage is presented as pass. A smoke test that needs a real
project is an explicit opt-in (see below) and never runs in CI.

## Smoke test

With the plugin enabled and the token present, acquire one sandbox, run
`uname -a` plus a file stage round-trip, and dispose it — printing timings,
and always cleaning up (including on Ctrl-C). The smoke entry point reads
the same `CodeyBox:Plugins:codeybox.hetzner-sandbox` options and the same
`HCLOUD_TOKEN` credential variable as the provider, so a green smoke run
means the configured project actually boots, answers SSH, and stages files.
It exits non-zero when any step fails, after disposing the sandbox.
