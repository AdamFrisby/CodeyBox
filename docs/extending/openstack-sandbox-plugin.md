# OpenStack sandbox provider plugin

`CodeyBox.OpenStackSandboxPlugin` contributes a cloud-VM sandbox backend for
standard OpenStack deployments (first target: Infomaniak Public Cloud).
Sandboxes are real Nova VMs — one per work item — booted on the configured
network with a per-sandbox SSH keypair, a per-sandbox Neutron security group,
and cloud-init user data; exec and file transfer go over SSH using the shared
OpenSSH CLI transport seam.

**Provider kind:** `openstack` — name it from a
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
  `openstack`). The provider additionally refuses a `ProfileName` that ever
  reaches it.
- Each sandbox gets a dedicated Neutron security group that starts deny-all
  and then opens SSH ingress from the orchestrator CIDRs plus DNS/NTP and the
  resolved `AllowedHosts` egress IPs. That is **best-effort defence in depth
  only** — service-side controls on hardware you do not own are not a
  substitute for the host-enforced guarantee. An empty allowlist means no
  egress beyond DNS/NTP; hostnames that fail DNS narrow egress rather than
  failing the item, and resolutions can go stale.

What the provider does give:

- A fresh Nova VM per work item, torn down on disposal — a real guest kernel,
  so the provider reports `SandboxIsolationLevel.DedicatedKernel` for
  workload-trust routing. This is a real isolation claim about the *guest
  boundary*, still independent of the egress classification.
- Genuine RAM-backed tmpfs mounts (including `/run/codeybox/creds`) via
  cloud-init, so file-backed agent credentials are supported — and refused
  anywhere outside tmpfs.
- Everything — VM disks, baked Glance images, snapshots, staged files — lives
  on the cloud provider's storage, which CodeyBox does not control. Do not
  send workloads whose content or secrets you are unwilling to place on
  third-party infrastructure.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.openstack-sandbox" ],
      "Allowlist": [ "codeybox.openstack-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "openstack-pool",
            "ProviderKind": "openstack",
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
`CodeyBox:Plugins:codeybox.openstack-sandbox` (full Infomaniak sample with
placeholder values in the next section):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.openstack-sandbox": {
        "Enabled": true,
        "AuthUrl": "https://api.pub1.infomaniak.cloud:5000",
        "Region": "dc4-a",
        "Interface": "public"
      }
    }
  }
}
```

## Sample: Infomaniak Public Cloud

Every value below comes from your `openrc` file or the Horizon dashboard —
no value is Infomaniak-specific in the provider itself; any standard
OpenStack cloud works by pointing the same keys at its own endpoints.
Placeholders only: substitute your own ids, names, and CIDRs, and never put
the application-credential secret in configuration (see Credentials).

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.openstack-sandbox": {
        "Enabled": true,
        "AuthUrl": "https://api.<region-endpoint-from-your-openrc>:5000/v3",
        "Region": "<region-from-your-openrc>",
        "Interface": "public",
        "ImageName": "<image-name-from-your-openrc-or-horizon>",
        "FlavorName": "<flavor-name-from-your-openrc-or-horizon>",
        "NetworkId": "<neutron-network-id-from-your-openrc>",
        "FloatingNetworkId": "<floating-ip-network-id-from-your-openrc>",
        "SshUser": "<image-default-user-from-your-openrc>",
        "OwnerId": "<owner-tag-from-your-openrc>",
        "ServerNamePrefix": "codeybox-",
        "KeypairNamePrefix": "codeybox-",
        "SecurityGroupNamePrefix": "codeybox-sg-",
        "OrchestratorSshCidrs": ["<orchestrator-egress-cidr-from-your-openrc>"],
        "DnsServerIps": ["1.1.1.1", "8.8.8.8"],
        "NtpServerIps": ["1.1.1.1", "8.8.8.8"],
        "MaxEgressRules": 128,
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

- `ImageName` accepts an image id or an exact image name (exactly one match).
  `FlavorName` resolves by exact name to a flavor id; both fail loudly when
  missing or ambiguous.
- `FloatingNetworkId` empty disables floating IPs: the sandbox is reached on
  its fixed address.
- `OrchestratorSshCidrs` must name at least one CIDR — without it nobody
  could SSH in, so provisioning refuses to run.
- `ServerNamePrefix` / `SecurityGroupNamePrefix` must start with `codeybox-`
  so leak reaping never touches unrelated resources.
- All values are hot-reloadable options (re-read on every operation); the
  section carries no secret material.

## Credentials

The application-credential id and secret come **only** from the host
credential chain (environment), using the standard OpenStack names:

- `OS_AUTH_URL` — fallback when `AuthUrl` is empty (non-secret).
- `OS_APPLICATION_CREDENTIAL_ID` — the credential id (no fallback: a missing
  variable fails loudly, naming the variable).
- `OS_APPLICATION_CREDENTIAL_SECRET` — the credential secret, never logged
  and never persisted. The credentials record redacts it in `ToString`, and
  exception messages name the *variable*, never the value.
- `OS_REGION_NAME` — fallback when `Region` is empty (non-secret).
- `OS_INTERFACE` — optional; `public` when unset (non-secret).

Provision them through the usual host secret path (vault agent, container
secrets). Rotation propagates without a restart because the values are read
per call. A missing variable fails `CreateAsync` loudly, naming the variable
— never silently falling back to another provider.

Every endpoint — the auth URL and each catalog URL — must be `https`. A
cleartext `http` URL is refused unless the dev-only `AllowUnsafeHttp` option
is set, and even then only for loopback hosts (`localhost` / `127.0.0.1` /
`::1`): remote `http` URLs are refused unconditionally, so the secret can
never ride a cleartext request to a remote host because of one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — an
OpenStack member never exceeds its cap, and queued acquisitions wait for
headroom rather than overflowing onto the cloud. Live load reaches placement
through the existing admission wrappers (held permits + `SandboxLiveCounter`),
so least-loaded member selection stays accurate. Configure capacity to match
the project's real Nova quota: the provider additionally checks Nova
quota headroom (limits API) before booting and defers provisioning as
`SandboxProvisioningDeferredException` when the cloud is full instead of
failing items, but it cannot see quota consumed outside CodeyBox, and quota
exhaustion still surfaces as a deferred provisioning failure with a bounded
recheck interval (`ProvisioningRecheckSeconds`; longer for auth/quota classes
so operators can fix credentials or capacity).

## What the provider implements

- **Lifecycle.** Resolve flavor/image → check quota headroom → generate an
  ephemeral ed25519 keypair (`ssh-keygen` via argv array, no shell) and
  register it as a Nova keypair → create a per-sandbox security group with the
  planned rules → boot the server with cloud-init user data tagged `codeybox`
  plus owner/work-item/created metadata → optionally attach a floating IP →
  bounded waits for ACTIVE and SSH readiness. Disposal deletes the server,
  floating IP, keypair, and security group, idempotently; a 409/429/quota
  refusal or a build ERROR state deletes the half-built server and defers
  rather than failing the item.
- **Exec.** Commands run over the shared OpenSSH CLI transport seam
  (`IRemoteHostTransport` / `OpenSshCliTransport` — no copy). Host keys are
  strict: the guest's ed25519 host key is generated on the orchestrator,
  injected via cloud-init `ssh_keys`, and pinned in a per-sandbox
  known_hosts file (`StrictHostKeyChecking=yes`, global known_hosts ignored).
- **Files.** Host mounts are staged in at create over the same SSH transport.
  Writable host mounts sync back at `SyncStateToHostAsync` and once more at
  disposal. tmpfs mounts (including `/run/codeybox/creds`) are genuine
  RAM-backed tmpfs via cloud-init `mounts:`; file-backed agent credentials are
  supported and refused anywhere outside tmpfs.
- **Baseline images.** When `UseBaselineImages` (default true) the provider
  boots work sandboxes from content-hashed Glance images carrying the same
  toolchain as the local Incus baseline instead of the bare base cloud image:
  `ExtraRuncmd`, `ExecutableProvisions` (hashed by file content, never host
  paths), and `BaselineVerificationCommands` join the same shared hash the
  Incus provider uses, so identical inputs hash identically on both
  providers. The first acquisition for a new hash boots an ephemeral builder
  (`<ServerNamePrefix>bake-*`), provisions it over SSH, runs the verification
  probes, powers it off, and snapshots it to
  `<BaselineImagePrefix>tc-<12-hex-hash>`; concurrent acquisitions share one
  in-process bake bounded by `BaselineBakeTimeoutSeconds`. Work items pin the
  provider-scoped ref `openstack/tc-<hash>/<image>`; cross-provider pins
  resolve by hash. `BaselineRetainedImageCount` (default 3) keeps the newest N
  baked images plus every image pinned by a non-terminal item; anything older
  and unpinned is pruned. The builder needs outbound access to fetch
  toolchains, so its security group opens full egress while baking unless
  `BaselineBuilderOpenEgress` is false (the builder is deleted afterwards and
  work sandboxes keep their locked-down groups either way).
- **Teardown.** `teardown` is declared: `StopAndPreserveAsync` stops without
  deleting, `DisablePreserveOnDispose` reverts to delete-on-dispose.
- **Leak cleanup.** `ListAllManagedAsync` inventories owner-tagged servers
  and `DisposeLeakedAsync` deletes one leaked sandbox's server, floating IP,
  keypair, and security group; the leak reaper sweeps orphaned keypairs,
  security groups, and floating IPs the same way. Name prefixes must stay on
  `codeybox-*` so reaping never touches unrelated cloud resources.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class and a recheck interval — `Retry-After` honored for
  429s; auth/quota classes wait longer so operators can fix credentials or
  capacity.
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Setup defects (unknown flavor/image, empty SSH CIDRs, bad prefixes) are
  deterministic errors — provisioning defects in the configuration, not
  service outages.
- A failed create attempts a best-effort delete; when the delete cannot prove
  removal the deferral names the leak risk.

## What it does not implement

- **Port publishing.** Not declared: there is no async-friendly floating-IP
  handshake inside the synchronous provider contract.
- **Disk guard / cache seeding / detached batch.** Not implemented; not
  declared.
- **Graphical flavor.** Refused: Nova VMs boot headless; there is no display
  server.

## Troubleshooting

- `application-credential ... environment variable ... is not set` — export
  `OS_APPLICATION_CREDENTIAL_ID` and `OS_APPLICATION_CREDENTIAL_SECRET` on
  the orchestrator host (or set `CredentialIdEnvVar` /
  `CredentialSecretEnvVar` to the variables you use). Never put the values in
  configuration files.
- `must use https://` — the auth URL or a catalog endpoint is cleartext
  `http`. Fix the URL; `AllowUnsafeHttp` only permits loopback test URLs.
- `flavor '…' not found` / image resolves to zero or several matches — set
  `FlavorName` / `ImageName` to the exact visible names (Horizon or
  `openstack flavor list` / `openstack image list`).
- Quota deferrals naming `quota-exhausted` — raise the project's Nova quota
  or lower the member `Capacity`; the provider rechecks after
  `ProvisioningRecheckSeconds`.
- SSH never becomes ready — check `OrchestratorSshCidrs` covers the
  orchestrator's egress address, and that `NetworkId` routes to it (or set
  `FloatingNetworkId` and check the floating pool has addresses).
- `uname -a` works but staging is slow — the fixed address may hairpin
  through the cloud edge; a floating IP on the same external network as the
  orchestrator's route is usually faster.

## Verification without a live service (and the live gate)

A live test needs a funded OpenStack project, creates billable cloud VMs, and
requires egress to the cloud — none available in CI. In its place,
`OpenStackSandboxProviderTests.cs` drives the provider against a fake
in-process cloud (`FakeOpenStackCloud`, an `HttpMessageHandler` mirroring the
recorded Keystone/Nova/Neutron/Glance request/response shapes) plus a fake
SSH transport, covering lifecycle, exec, staging, disposal idempotence,
quota-headroom deferral, leak-inventory scoping, ERROR cleanup, and tmpfs
credential containment. A smoke test that needs a real cloud is an explicit
opt-in (see below) and never runs in CI.

## Smoke test

With the plugin enabled and credentials present, acquire one sandbox, run
`uname -a` plus a file stage round-trip, and dispose it — printing timings,
and always cleaning up (including on Ctrl-C):

```bash
dotnet run --project tools/CodeyBox.Harness -- openstack-smoke
```

The command reads the same `CodeyBox:Plugins:codeybox.openstack-sandbox`
options and the same `OS_*` credential variables as the provider, so a green
smoke run means the configured cloud actually boots, answers SSH, and stages
files. It exits non-zero when any step fails, after disposing the sandbox.
