# CodeyBox.OpenStackSandboxPlugin

OpenStack-backed sandbox provider (first target: Infomaniak Public Cloud, a
standard OpenStack deployment). Contributes the `openstack` provider kind
(`ISandboxProvider`) through the plugin trust model — off unless an operator
allowlists `codeybox.openstack-sandbox` AND sets
`CodeyBox:Plugins:codeybox.openstack-sandbox:Enabled=true`.

The guest runs on infrastructure CodeyBox does not control, so this kind is
always classified `NotEnforced` by the host (same rule as
`docs/extending/daytona-sandbox-plugin.md`): acquisitions requiring enforced
network egress (a named network profile) are refused at placement, and any
service-side network controls the provider applies later are best-effort
defence in depth only.

## Current scope

This plugin ships the typed REST client (`OpenStackApiClient`) plus the
`openstack` provider (`OpenStackSandboxProvider` / `OpenStackSandbox`):

- Per acquisition: an ephemeral ed25519 SSH keypair generated with
  `ssh-keygen` (argv array, no shell) and registered as a Nova keypair; a
  per-sandbox Neutron security group; a server booted from the configured
  (or spec-pinned) image on the configured network with cloud-init
  user_data, tagged `codeybox` plus owner/work-item/created metadata; an
  optional floating IP; bounded waits for ACTIVE and SSH readiness.
- Disposal deletes the server, floating IP, keypair, and security group,
  idempotently; the leak reaper lists owner-tagged servers and sweeps
  orphaned keypairs, security groups, and floating IPs.
- Exec and file staging reuse the shared OpenSSH CLI transport seam
  (`IRemoteHostTransport` / `OpenSshCliTransport` from
  `CodeyBox.Sandbox.MultipassRemote`) — no copy. Host keys are strict:
  the guest's ed25519 host key is generated on the orchestrator, injected
  via cloud-init `ssh_keys`, and pinned in a per-sandbox known_hosts file
  (`StrictHostKeyChecking=yes`, global known_hosts ignored). `no` never
  appears.
- Real VMs with a dedicated kernel: tmpfs mounts (including
  `/run/codeybox/creds`) are genuine RAM-backed tmpfs via cloud-init
  `mounts:`; file-backed agent credentials are supported and refused
  anywhere outside tmpfs. Isolation level:
  `SandboxIsolationLevel.DedicatedKernel`.
- Capacity: the existing admission gate (`SandboxMember.Capacity`) applies,
  and provisioning additionally defers when Nova quota headroom (limits
  API) is exhausted instead of failing items. 409/429/quota failures and
  build ERROR states (deleted, not left running) surface as
  `SandboxProvisioningDeferredException`.

## Configuration

All values are hot-reloadable options under
`CodeyBox:Plugins:codeybox.openstack-sandbox` (sample values target
Infomaniak Public Cloud — nothing else in the plugin is cloud-specific):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.openstack-sandbox": {
        "Enabled": false,
        "AuthUrl": "https://api.pub1.infomaniak.cloud:5000/v3",
        "Region": "dc4-a",
        "Interface": "public",
        "ImageName": "Ubuntu 24.04",
        "FlavorName": "standard-2-8",
        "NetworkId": "<neutron-network-id>",
        "FloatingNetworkId": "",
        "SshUser": "ubuntu",
        "OwnerId": "",
        "ServerNamePrefix": "codeybox-",
        "KeypairNamePrefix": "codeybox-",
        "SecurityGroupNamePrefix": "codeybox-sg-",
        "OrchestratorSshCidrs": ["<orchestrator-egress-cidr>"],
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

`ImageName` accepts an image id or an exact image name (exactly one match).
`OwnerId` defaults to the machine name. `FloatingNetworkId` empty disables
floating IPs (SSH goes to the fixed address). `OrchestratorSshCidrs` must
name at least one CIDR — without it nobody could SSH in, so provisioning
refuses to run. `ServerNamePrefix` / `SecurityGroupNamePrefix` must start
with `codeybox-` so leak reaping never touches unrelated resources.

## Egress

Best-effort defence in depth only: each sandbox security group starts
deny-all, then opens SSH ingress (TCP/22) from `OrchestratorSshCidrs`,
DNS (UDP/TCP 53) to `DnsServerIps`, NTP (UDP 123) to `NtpServerIps`, and
full TCP+UDP egress to the IPs `AllowedHosts` (plus `HostGitEndpoint`)
resolves to at create time. Hostnames that fail DNS narrow egress rather
than failing the item, and resolutions can go stale — hence best-effort.
An empty allowlist means no egress beyond DNS/NTP. A named network profile
is refused outright (this kind can never be promoted to enforced).

## Credentials

The application-credential id and secret come **only** from the host
credential chain (environment), using the standard OpenStack names:

- `OS_AUTH_URL` (fallback when `AuthUrl` is empty)
- `OS_APPLICATION_CREDENTIAL_ID`
- `OS_APPLICATION_CREDENTIAL_SECRET` — never logged, never persisted
- `OS_REGION_NAME` (fallback when `Region` is empty)
- `OS_INTERFACE` (optional; `public` when unset)

Every endpoint — the auth URL and each catalog URL — must be `https`. A
cleartext `http` URL is refused unless the dev-only `AllowUnsafeHttp` option
is set, and even then only for loopback hosts: remote `http` URLs are refused
unconditionally, so the secret can never ride a cleartext request to a remote
host because of one operator edit.

## Client notes

- 401 re-authenticates once and retries; a second 401 surfaces as
  `Unauthorized`. 409/413/429 map to typed transient/quota errors with
  `Retry-After` honoured (header first, `overLimit` body second).
- All response bodies are size-bounded while streaming; list operations page
  with item/page caps and fail loudly on truncation instead of reporting a
  partial inventory.
- No hand-rolled crypto: the platform HTTP stack (TLS verification on) and
  the OpenSSH CLI for keys/transport. No new third-party dependencies
  (only in-repo references: `CodeyBox.HostProcess` for process execution,
  `CodeyBox.Sandbox` for conventions, `CodeyBox.Sandbox.MultipassRemote`
  for the shared SSH transport seam).
