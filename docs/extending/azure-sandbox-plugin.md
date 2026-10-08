# Azure Virtual Machines sandbox provider plugin

`CodeyBox.AzureSandboxPlugin` contributes a cloud-VM sandbox backend on Azure
Virtual Machines. Sandboxes are real ARM VMs — one per work item — on the
operator's subscription with a per-sandbox SSH keypair, a per-sandbox network
security group, and cloud-init user data; exec and file transfer go over SSH
using the shared OpenSSH CLI transport seam.

**Provider kind:** `azure` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind. No `SandboxClass` member is added by default:
an operator opts in explicitly.

## Isolation — read this before enabling

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee CodeyBox gives for local VMs — nftables
allowlist drops enforced in a kernel CodeyBox owns — **cannot apply here**.

The host therefore classifies this kind `NotEnforced`, unconditionally. The
plugin cannot promote itself; no option, label, or return value changes the
classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress and
  is refused for this kind (unplaceable, `enforced-egress`, naming `azure`).
  The provider additionally refuses a `ProfileName` that ever reaches it.
- Each sandbox gets a dedicated network security group that opens SSH ingress
  from the configured orchestrator CIDRs only. That is **best-effort defence
  in depth only** — service-side controls on hardware you do not own are not a
  substitute for the host-enforced guarantee. Guest outbound traffic is not
  filtered by this provider: do not send workloads that need enforced egress
  here; that needs a separate enforced-egress review.

What the provider does give:

- A fresh ARM VM per work item, torn down on disposal — a real guest kernel,
  so the provider reports `SandboxIsolationLevel.DedicatedKernel` for
  workload-trust routing. This is a real isolation claim about the *guest
  boundary*, still independent of the egress classification.
- Genuine RAM-backed tmpfs mounts (including `/run/codeybox/creds`) via
  cloud-init, so file-backed agent credentials are supported — and refused
  anywhere outside tmpfs.
- Everything — VM disks, staged files — lives on Azure storage, which
  CodeyBox does not control. Do not send workloads whose content or secrets
  you are unwilling to place on third-party infrastructure.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.azure-sandbox" ],
      "Allowlist": [ "codeybox.azure-sandbox" ],
      "codeybox.azure-sandbox": {
        "Enabled": true,
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
        "OrchestratorSshCidrs": [ "203.0.113.0/24" ]
      }
    }
  }
}
```

## Operator prerequisites (performed by the operator, not by CodeyBox)

1. **Subscription, resource group, region.** Create (or choose) a
   subscription, a resource group, and a region for sandbox resources, and
   put their exact names in `SubscriptionId`, `ResourceGroupName`, and
   `Location`. CodeyBox never creates or deletes the resource group.
2. **Virtual network and subnet.** Create a VNet with a subnet that has an
   outbound path the orchestrator can reach (private address SSH by default;
   see 5). CodeyBox attaches NICs to this subnet but never creates, modifies,
   or deletes the VNet or subnet.
3. **Pinned immutable image.** Pick an exact platform image version —
   `publisher:offer:sku:version` with a concrete version, never `latest` —
   and put the four parts in `ImagePublisher`/`ImageOffer`/`ImageSku`/
   `ImageVersion`. The version is part of the boot contract: changing it
   changes what every new sandbox runs.
4. **ARM credentials.** Export a bearer token in the environment variable
   named by `TokenEnvVar` (default `AZURE_ACCESS_TOKEN`), e.g. via
   `az account get-access-token --resource https://management.azure.com/`.
   The token needs permission to create/delete VMs, NICs, NSGs, public IPs,
   and disks in the resource group. It is read at call time, so rotation is
   pickup without restart; it is never logged or written to guest metadata.
5. **SSH reachability.** List the orchestrator egress CIDRs in
   `OrchestratorSshCidrs` — the provider opens TCP/22 from exactly these.
   With `AllocatePublicIp: false` (default) the orchestrator must reach the
   private subnet (VPN/peering); with `true` each sandbox gets a Standard
   static public IP (provider-owned, deleted on disposal).

## What it costs and what it cannot do

- One VM (plus NIC, NSG, OS disk, and optionally a public IP) per sandbox,
  billed to the operator's subscription for its lifetime; the leak reaper
  deletes owner-tagged leftovers, but a host that dies mid-provision may
  leave resources until the next reaper pass.
- Linux guests only, headless only. No native Windows guests, no VMSS, no
  broad template deployments, no baseline bake/snapshot capability.
- 401/403/429/quota/5xx surface as provisioning deferrals (the item retries
  after a bounded recheck), not item failures.
