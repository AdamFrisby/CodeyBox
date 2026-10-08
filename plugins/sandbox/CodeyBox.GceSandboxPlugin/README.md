# CodeyBox.GceSandboxPlugin

Google Compute Engine-backed sandbox provider. Contributes the `gce` provider
kind (`ISandboxProvider`) through the plugin trust model — off unless an
operator allowlists `codeybox.gce-sandbox` AND sets
`CodeyBox:Plugins:codeybox.gce-sandbox:Enabled=true`.

The guest runs on infrastructure CodeyBox does not control, so this kind is
always classified `NotEnforced` by the host: acquisitions requiring enforced
network egress (a named network profile) are refused at placement, and the
provider additionally refuses a `ProfileName` that ever reaches it. The
per-sandbox VPC firewall rule (SSH ingress from the orchestrator CIDRs only)
is best-effort defence in depth, not a host-enforced guarantee. A dedicated
guest kernel does not imply host-enforced egress.

## Current scope

This plugin ships the typed Compute Engine REST client (`GceApiClient`) plus
the `gce` provider (`GceSandboxProvider` / `GceSandbox`):

- Per acquisition: an ephemeral ed25519 SSH keypair generated with
  `ssh-keygen` (argv array, no shell); a per-sandbox VPC firewall rule
  allowing TCP/22 from `OrchestratorSshCidrs` only; one instance booted from
  the configured **pinned** image on the configured network/subnetwork with a
  startup script, labelled `codeybox-managed`/`codeybox-owner` with
  owner/work-item linkage in metadata; an optional reserved static address.
  Bounded waits follow the insert's scoped zone operation until DONE (error
  inspected — quota-shaped errors defer), then poll the instance until
  RUNNING and probe SSH readiness.
- The instance is created with **no service accounts attached**: the guest
  never receives GCP credentials through metadata, and the host access token
  never enters guest metadata, startup scripts, logs, or prompts.
- Exec and file staging reuse the shared OpenSSH CLI transport seam
  (`IRemoteHostTransport` / `OpenSshCliTransport` from
  `CodeyBox.Sandbox.MultipassRemote`) — no copy. Host keys are strict: the
  guest's ed25519 host key is generated on the orchestrator, injected via
  the startup script, and pinned in a per-sandbox known_hosts file
  (`StrictHostKeyChecking=yes`). `no` never appears.
- Real VMs with a dedicated kernel: tmpfs mounts (including
  `/run/codeybox/creds`) are genuine RAM-backed tmpfs via the startup
  script; file-backed agent credentials are supported and refused anywhere
  outside tmpfs. Isolation level:
  `SandboxIsolationLevel.DedicatedKernel`.
- Disposal syncs writable mounts back to the host **before** cloud resources
  are deleted, then deletes the instance (waiting for the delete operation),
  the owned boot disk when `BootDiskAutoDelete` is false, the reserved
  address, and the firewall rule — idempotently and only after revalidating
  exact ownership and scope. Cleanup failures keep the resource identity in
  an unreconciled set for a later sweep instead of claiming deletion. The
  leak reaper lists owner-labelled instances and sweeps orphaned disks,
  addresses, and firewall rules after a restart or partial provisioning.
- Declared capabilities are teardown only (`SandboxCapabilities.Teardown`):
  there is no baseline bake/snapshot support. No managed instance groups,
  no autoscaling, no Windows guests — one pinned-Linux VM per work item.

## Configuration

All values are hot-reloadable options under
`CodeyBox:Plugins:codeybox.gce-sandbox`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.gce-sandbox": {
        "Enabled": false,
        "ComputeBaseUrl": "https://compute.googleapis.com/compute/v1",
        "Project": "<gcp-project-id>",
        "Zone": "europe-west1-b",
        "MachineType": "e2-medium",
        "ImageName": "projects/<project>/global/images/<image>-v1",
        "Network": "projects/<project>/global/networks/<vpc>",
        "Subnetwork": "projects/<project>/regions/<region>/subnetworks/<subnet>",
        "BootDiskSizeGb": 20,
        "BootDiskAutoDelete": true,
        "ReserveStaticAddress": false,
        "AddressType": "EXTERNAL",
        "SshUser": "ubuntu",
        "OwnerId": "",
        "InstanceNamePrefix": "codeybox-",
        "OrchestratorSshCidrs": ["<orchestrator-egress-cidr>"],
        "AccessTokenEnvVar": "GCE_ACCESS_TOKEN",
        "HttpTimeoutSeconds": 60,
        "ReadyTimeoutSeconds": 600,
        "PollIntervalMilliseconds": 2000,
        "MaxPollIntervalMilliseconds": 15000,
        "MaxResponseBytes": 8388608,
        "MaxListItems": 5000,
        "MaxListPages": 100,
        "MaxStartupScriptBytes": 65536,
        "SshReadyTimeoutSeconds": 300,
        "ProvisioningRecheckSeconds": 60,
        "MaxInsertAttempts": 2,
        "SshBinary": "ssh",
        "SshKeygenBinary": "ssh-keygen",
        "SshPort": 22,
        "SshConnectTimeoutSeconds": 10,
        "AllowUnsafeHttp": false
      }
    }
  }
}
```

`ImageName` must name a **pinned concrete image** (full self-link or
`projects/…/global/images/…`); image families are rejected so a moving
family can never silently change the guest. `InstanceNamePrefix` must start
with `codeybox-` so leak reaping never touches unrelated resources.
`OrchestratorSshCidrs` must name at least one CIDR — without it nobody
could SSH in, so provisioning refuses to run.

## Credentials (operator prerequisite)

The Compute Engine access token comes **only** from the host credential
chain: the environment variable named by `AccessTokenEnvVar` (default
`GCE_ACCESS_TOKEN`), read at call time so rotation propagates without a
restart. There is deliberately **no Application Default Credentials
fallback** — personal `gcloud auth` sessions must never silently become the
identity that provisions project infrastructure. The variable missing or
blank refuses provisioning; the token is never written to configuration,
guest metadata, startup scripts, logs, or prompts.

Before enabling, the operator provisions (outside CodeyBox — this plugin
performs no IAM, network, or image setup itself):

- A GCP project with the Compute Engine API enabled.
- A VPC network and subnetwork for the sandbox VMs.
- A pinned Linux image (or a custom image built from one) referenced by its
  exact image name — never a family.
- An explicit workload/machine identity whose access token is exported at
  `GCE_ACCESS_TOKEN`, granted only the Compute Engine rights this provider
  uses in the target project: insert/get/delete/list instances, get/delete/
  list disks, insert/get/delete/list addresses, insert/get/delete/list
  firewall rules, and read zone/region/global operations. Least privilege:
  no broader project roles, and never a privileged default service account
  attached to the guests (the provider attaches none).
- `ssh` and `ssh-keygen` on the orchestrator host PATH.

## Client notes

- 401/403 re-authenticate nothing automatically: 401 surfaces as
  `unauthorized`, quota/429 shapes defer with a bounded recheck, 5xx retries
  the insert with the **same** stable nonzero UUID `requestId` (idempotent
  retry, never a blind duplicate). Unknown-outcome creates reconcile by
  stable instance identity before any resubmit and refuse foreign instances.
- All response bodies are size-bounded while streaming; list operations page
  with item/page caps and fail loudly on truncation instead of reporting a
  partial inventory.
- No hand-rolled crypto: the platform HTTP stack (TLS verification on) and
  the OpenSSH CLI for keys/transport. No new third-party dependencies
  (only in-repo references plus `Microsoft.Extensions.*` abstractions).
