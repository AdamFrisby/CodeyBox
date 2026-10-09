# CodeyBox.AzureSandboxPlugin

Azure Virtual Machines sandbox provider (first-party plugin). Contributes the
`azure` provider kind (`ISandboxProvider`) through the plugin trust model —
off unless an operator allowlists `codeybox.azure-sandbox` AND sets
`CodeyBox:Plugins:codeybox.azure-sandbox:Enabled=true`.

The guest runs on infrastructure CodeyBox does not control, so this kind is
always classified `NotEnforced` by the host: acquisitions requiring enforced
network egress (a named network profile) are refused at placement, and the
per-sandbox network security group is best-effort defence in depth only.

## Current scope

- Per acquisition: an ephemeral ed25519 SSH keypair generated with
  `ssh-keygen` (argv array, no shell); a per-sandbox network security group
  (SSH ingress from the configured orchestrator CIDRs only); a NIC on the
  caller-owned VNet/subnet; an optional provider-owned public IP; one VM
  booted from the configured immutable platform image
  (`publisher:offer:sku:version`, never `latest`) with cloud-init userData,
  tagged `codeybox.managed=true` plus owner/work-item/request/created
  metadata; bounded waits for `Succeeded` provisioning and pinned-key SSH
  readiness.
- Disposal deletes the VM, NIC, public IP, NSG, and OS disk, idempotently,
  with exact ownership revalidation at every delete; the leak reaper lists
  owner-tagged VMs and sweeps unreferenced NICs/NSGs/public IPs/disks. The
  resource group, VNet, and subnet are caller-owned and never touched.
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
  and quota/throttle/auth failures surface as
  `SandboxProvisioningDeferredException` (defer, not fail). No baseline
  bake/snapshot capability is advertised: the configured immutable image is
  the boot source and content pins are rejected loudly rather than silently
  ignored.

## Configuration

All values are hot-reloadable options under
`CodeyBox:Plugins:codeybox.azure-sandbox`; the ARM bearer token comes only
from the environment variable named by `TokenEnvVar`
(default `AZURE_ACCESS_TOKEN`) and never from configuration files:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.azure-sandbox": {
        "Enabled": false,
        "SubscriptionId": "00000000-0000-0000-0000-000000000000",
        "ResourceGroupName": "codeybox-sandboxes",
        "Location": "westeurope",
        "VmSize": "Standard_D2s_v5",
        "VirtualNetworkName": "codeybox-vnet",
        "SubnetName": "codeybox-subnet",
        "ImagePublisher": "Canonical",
        "ImageOffer": "0001-com-ubuntu-server-jammy",
        "ImageSku": "22_04-lts-gen2",
        "ImageVersion": "22.04.20240101120000",
        "OrchestratorSshCidrs": ["203.0.113.0/24"]
      }
    }
  }
}
```

Full operator reference (prerequisites, what to configure, what it costs,
what it cannot do): `docs/extending/azure-sandbox-plugin.md`.
