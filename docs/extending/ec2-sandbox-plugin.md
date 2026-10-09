# AWS EC2 sandbox provider plugin

`CodeyBox.Ec2SandboxPlugin` contributes a cloud-VM sandbox backend for AWS
EC2. Sandboxes are real EC2 instances — one per work item — launched in the
configured region, zone, subnet, and VPC with a per-sandbox SSH key pair, a
per-sandbox security group, ownership tags, and cloud-init user data; exec
and file transfer go over SSH using the shared OpenSSH CLI transport seam.
The OpenStack remote-VM provider is the concrete analogue: same lifecycle
shape, same SSH data plane, same tag-scoped leak cleanup — adapted to the
EC2 Query API (SigV4 signing, `ClientToken` idempotency, AMI/instance-type/
subnet/security-group pins).

**Provider kind:** `ec2` — name it from a
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
  `ec2`). The provider additionally refuses a `ProfileName` that ever
  reaches it.
- Each sandbox gets a dedicated security group that starts deny-all and then
  opens SSH ingress (TCP/22) from the orchestrator CIDRs plus DNS/NTP and
  the resolved `AllowedHosts` egress IPs. That is **best-effort defence in
  depth only** — vendor-managed filters on hardware you do not own are not a
  substitute for the host-enforced guarantee. An empty allowlist means no
  egress beyond DNS/NTP; hostnames that fail DNS narrow egress rather than
  failing the item, and resolutions can go stale.

Provider-specific network-filter limitations, stated plainly:

- The security-group rules are explicit for everything the sandbox needs
  (SSH ingress; DNS/NTP/allowlist egress). The group's exact
  default posture was not independently verified here, so the provider
  assumes nothing about implicit defaults — the plan is complete either
  way, but it is **not** an enforced-egress claim.
- A dedicated guest kernel does not imply host-enforced egress. No network
  policy is enabled and host recognition is unchanged by this provider.
- No price advantage is claimed: capacity planning is the operator's job
  (see Capacity and placement).

What the provider does give:

- A fresh EC2 instance per work item, terminated on disposal — a real guest
  kernel, so the provider reports `SandboxIsolationLevel.DedicatedKernel`
  for workload-trust routing. This is a real isolation claim about the
  *guest boundary*, still independent of the egress classification.
- Genuine RAM-backed tmpfs mounts (including `/run/codeybox/creds`) via
  cloud-init, so file-backed agent credentials are supported — and refused
  anywhere outside tmpfs.
- IMDSv2 hardening on every launch (`HttpTokens=require`, hop limit 1), no
  IAM instance profile attached, on-demand instances only (never spot, no
  fleet, no autoscaling).
- Everything — instance disks, EBS volumes, staged files — lives on AWS
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
      "Enabled": [ "codeybox.ec2-sandbox" ],
      "Allowlist": [ "codeybox.ec2-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "ec2-pool",
            "ProviderKind": "ec2",
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
`CodeyBox:Plugins:codeybox.ec2-sandbox` (full sample with placeholder
values in the next section):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.ec2-sandbox": {
        "Enabled": true,
        "Region": "us-east-1",
        "AmiId": "ami-0123456789abcdef0",
        "InstanceType": "t3.medium",
        "SubnetId": "subnet-0123456789abcdef0",
        "VpcId": "vpc-0123456789abcdef0"
      }
    }
  }
}
```

## Sample: AWS EC2

Every value below is operator-chosen against the AWS console or EC2 API — no
value is account-specific in the provider itself. Placeholders only:
substitute your own ids and CIDRs, and never put the AWS credentials in
configuration (see Credentials).

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.ec2-sandbox": {
        "Enabled": true,
        "Region": "us-east-1",
        "Zone": "us-east-1a",
        "AmiId": "ami-0123456789abcdef0",
        "InstanceType": "t3.medium",
        "SubnetId": "subnet-0123456789abcdef0",
        "VpcId": "vpc-0123456789abcdef0",
        "SecurityGroupIds": [],
        "CreateSecurityGroup": true,
        "AssociatePublicIp": true,
        "AllocateElasticIp": false,
        "VolumeSizeGb": 20,
        "VolumeType": "gp3",
        "RootDeviceName": "/dev/sda1",
        "DeleteOnTermination": true,
        "EnableInstanceMetadata": true,
        "SshUser": "ubuntu",
        "OwnerId": "<owner-tag-for-this-host>",
        "InstanceNamePrefix": "codeybox-",
        "KeyNamePrefix": "codeybox-",
        "SecurityGroupNamePrefix": "codeybox-sg-",
        "OrchestratorSshCidrs": ["<orchestrator-egress-cidr>"],
        "DnsServerIps": ["1.1.1.1", "8.8.8.8"],
        "NtpServerIps": ["1.1.1.1", "8.8.8.8"],
        "MaxSecurityGroupRules": 128,
        "ReadyTimeoutSeconds": 600,
        "SshReadyTimeoutSeconds": 300,
        "ProvisioningRecheckSeconds": 60,
        "MaxRunAttempts": 2,
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

- `AmiId` must be a concrete approved AMI id (hex suffix) whose image state
  is `available` at provisioning time; it is re-verified on every
  acquisition and refused loudly otherwise. A per-acquisition
  `ImageReference` overrides it with the same rules. There is no baseline
  bake/snapshot capability: every acquisition boots this pinned AMI.
- `Region` derives the endpoint `https://ec2.{region}.amazonaws.com`
  (`ServiceUrl` overrides it for loopback test fakes only). `Zone`, when
  set, must belong to `Region` and agree with the subnet's zone.
- `InstanceType` names the exact EC2 instance type; `SubnetId` and `VpcId`
  are explicit pins — the provider never falls back to an implicit default
  VPC, default security group, or instance profile.
- Security groups are either created per sandbox (`CreateSecurityGroup`
  with `VpcId`) or named explicitly (`SecurityGroupIds`); naming none is
  refused. Caller-owned groups listed in `SecurityGroupIds` are attached
  but never deleted; only the per-sandbox group is removed on disposal.
- `AssociatePublicIp` without `AllocateElasticIp` reaches the sandbox on
  its instance public address; `AllocateElasticIp` allocates one Elastic IP
  per sandbox, associates it, and SSHes to it. Disabling the public path
  entirely is refused: the provider would have no SSH path in.
- `VolumeSizeGb`/`VolumeType`/`RootDeviceName` shape the single root EBS
  volume; `DeleteOnTermination` decides whether it dies with the instance
  (surviving owned volumes are deleted explicitly during cleanup).
- `EnableInstanceMetadata` leaves guest IMDS access on (always IMDSv2,
  hop limit 1); cloud API credentials are never placed in user-data or
  guest metadata either way.
- `OrchestratorSshCidrs` must name at least one CIDR — without it nobody
  could SSH in, so provisioning refuses to run.
- All `*Prefix` values must start with `codeybox-` so leak reaping can
  scope candidates — though a prefix alone never authorizes deletion: the
  ownership tags are re-verified at every deletion.
- All values are hot-reloadable options (re-read on every operation); the
  section carries no secret material.

## Credentials

The AWS credentials come **only** from the host environment (injected
clock/transport; no ambient personal-identity fallback):

- `AWS_ACCESS_KEY_ID` — the access key id (no fallback: a missing variable
  fails loudly, naming the variable). Rename the variable via
  `AccessKeyEnvVar`, never the value's location.
- `AWS_SECRET_ACCESS_KEY` — the secret access key (same rules; the value
  stays out of configuration files). Rename via `SecretKeyEnvVar`.
- `AWS_SESSION_TOKEN` — optional session token for temporary credentials
  (rename via `SessionTokenEnvVar`).

Requests are signed with SigV4 per call. The credentials ride the
`Authorization` header only. They are never logged (the credentials record
redacts them in `ToString`), never persisted, never copied into exception
messages — which name the *variable*, never the value — and never copied
into guest user-data, logs, or prompts. Provision them through the usual
host secret path (vault agent, container secrets). Rotation propagates
without a restart because the values are read per call. No signup, key
creation, or grant change is ever performed by this provider: the operator
creates the IAM identity out of band (see Operator prerequisites).

Every endpoint must be `https`. A cleartext `http` URL is refused unless
the dev-only `AllowUnsafeHttp` option is set, and even then only for
loopback hosts (`localhost` / `127.0.0.1` / `::1`): remote `http` URLs are
refused unconditionally, so the credentials can never ride a cleartext
request to a remote host because of one operator edit.

## Operator prerequisites (future work, not performed here)

Before enabling, the operator — outside CodeyBox — must have:

1. An AWS account with an IAM identity (least-privilege EC2 rights for the
   configured region/VPC/subnet) whose access key is exported as
   `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` on the orchestrator host.
2. A chosen region, zone, approved AMI id, instance type, subnet, and VPC
   for the pins.
3. The orchestrator's egress CIDRs for `OrchestratorSshCidrs`.
4. (Optional) An Elastic IP budget when `AllocateElasticIp` is set.
5. Service quotas (instances, EBS, Elastic IPs) sized for the configured
   member `Capacity` — quota exhaustion surfaces as deferred provisioning,
   never as spend beyond the configured bounds.

CodeyBox performs none of these: no account signup, no key creation, no
VPC/subnet/security-group pre-provisioning, no Codey runtime configuration
change.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — an EC2
member never exceeds its cap, and queued acquisitions wait for headroom
rather than overflowing onto the cloud. Live load reaches placement through
the existing admission wrappers (held permits + `SandboxLiveCounter`), so
least-loaded member selection stays accurate. Configure capacity to match
the account's real EC2 limits: the Query API exposes no pre-flight quota
endpoint this provider could consult, so quota exhaustion surfaces as a
deferred provisioning failure with a bounded recheck interval
(`ProvisioningRecheckSeconds`; longer for auth/quota classes so operators
can fix credentials or capacity). The provider never claims an unverified
price advantage — size `Capacity` from the project's actual budget and
limits. CPU-credit spend is bounded by the pinned on-demand instance type;
there is no spot, fleet, or autoscaling surface.

## What the provider implements

- **Lifecycle.** Verify the pinned AMI (must be `available`) → generate an
  ephemeral ed25519 keypair (`ssh-keygen` via argv array, no shell) and
  import it as a per-sandbox EC2 key pair → plan and create a per-sandbox
  security group (deny-all start, then orchestrator-SSH/DNS/NTP/allowlist
  rules) → `RunInstances` exactly one instance with a stable per-attempt
  `ClientToken` and ownership tags at creation
  (`codeybox-owned=true`, `codeybox-owner`, `codeybox-work-item`,
  `codeybox-request`, `codeybox-created`) → optionally allocate and
  associate an Elastic IP → bounded waits for `running` and SSH readiness.
  Disposal (active-exec tracking shutdown, then terminate with bounded
  deletion confirm) removes the instance, Elastic IP association and
  allocation, security group, key pair, and surviving owned EBS volumes,
  idempotently; a 401/403/429/quota/5xx refusal deletes the half-built set
  and defers rather than failing the item.
- **Ambiguous creates reconcile, never duplicate.** The run call carries a
  stable `ClientToken` per attempt, and when it fails ambiguously
  (transport cut, timeout, throttle, 5xx, malformed/truncated/oversized
  response) the provider lists for the exact ownership triple including the
  per-attempt `codeybox-request` tag and adopts the instance it finds
  instead of resubmitting. Zero matches retry within the bounded
  `MaxRunAttempts`; several matches fail closed (refuse to pick).
- **Exec.** Commands run over the shared OpenSSH CLI transport seam
  (`IRemoteHostTransport` / `OpenSshCliTransport` — no copy). Host keys are
  strict: the guest's ed25519 host key is generated on the orchestrator,
  injected via cloud-init, and pinned in a per-sandbox known_hosts file
  (`StrictHostKeyChecking=yes`, global known_hosts ignored; a mismatch
  fails closed). A present-but-unparseable cloud-reported address fails
  fast — never SSH at an unconfirmed target. Cancelling a local SSH
  operation only proves the local transport stopped waiting — the remote
  command may still run — so disposal still terminates the instance as the
  termination fallback.
- **Files.** Host mounts are staged in at create over the same SSH
  transport. Writable host mounts sync back at `SyncStateToHostAsync` and —
  before the instance is terminated and before the handle is marked
  disposed — once more at disposal. tmpfs mounts (including
  `/run/codeybox/creds`) are genuine RAM-backed tmpfs via cloud-init;
  file-backed agent credentials are supported and refused anywhere outside
  tmpfs.
- **Leak cleanup.** `ListAllManagedAsync` inventories owner-tagged
  instances (terminated records excluded) and `DisposeLeakedAsync` deletes
  one leaked sandbox's full tagged set; the same pass sweeps this host's
  key pairs, security groups, unassociated addresses, and unattached owned
  volumes whose request tag matches no live owned instance. Every deletion
  re-verifies the exact `codeybox-owned`/`codeybox-owner` tags — name
  prefixes gate candidacy but never authorize deletion — and other owners'
  resources are never touched. A same-named foreign instance fails closed
  instead of cleaning. A deletion that cannot prove removal keeps its tags
  and is logged for the reaper; cleanup failures stay visible and
  recoverable, never silent.
- **Teardown.** `teardown` is declared. No other capability is declared: no
  baseline bake/snapshot, no suspend/resume, no disk guard, no cache
  seeding, no port publishing, no graphical flavor.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class and a recheck interval — throttles keep the base
  interval; auth/quota classes wait longer so operators can fix credentials
  or capacity.
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Setup defects (unknown/unavailable AMI, bad pins, empty SSH CIDRs, bad
  prefixes, no SSH path) are deterministic errors — provisioning defects in
  the configuration, not service outages.
- A failed create attempts a best-effort delete; when the delete cannot
  prove removal the deferral names the leak risk and the tags retain the
  orphan identity for the reaper.

## What it does not implement

- **Baseline bake/snapshot.** Not implemented and not declared; acquisitions
  always boot the configured pinned AMI.
- **Port publishing, disk guard, cache seeding, detached batch.** Not
  implemented; not declared.
- **Graphical flavor.** Refused: instances boot headless; there is no
  display server.

## Troubleshooting

- `environment variable 'AWS_ACCESS_KEY_ID' is not set` — export the key on
  the orchestrator host (or set `AccessKeyEnvVar` to the variable you use).
  Never put the value in configuration files.
- `must use https://` — the endpoint is cleartext `http`. Fix the URL;
  `AllowUnsafeHttp` only permits loopback test URLs.
- `AMI '…' not found` / `is not available (state '…')` — set `AmiId` to an
  available image id visible to the account (console or
  `aws ec2 describe-images`); a per-acquisition `ImageReference` follows
  the same rules.
- Deferrals naming `quota-exhausted` — raise the account's EC2/EBS/EIP
  limits or lower the member `Capacity`; the provider rechecks after
  `ProvisioningRecheckSeconds`.
- SSH never becomes ready — check `OrchestratorSshCidrs` covers the
  orchestrator's egress address and the subnet routes to it (or set
  `AllocateElasticIp`).
- `exposed no usable public IP address` — the cloud reported a missing or
  unparseable address; the provider fails closed rather than targeting it.

## Verification without a live service (and the live gate)

A live test needs a funded AWS account, creates billable instances, and
requires egress to EC2 — none available in CI, and no cloud API call of any
kind (including dry-run/list) is performed here. In its place,
`Ec2SandboxProviderTests.cs` drives the provider against a fake in-process
cloud (`FakeEc2Cloud`, an `HttpMessageHandler` mirroring the EC2 Query
request/response shapes, SigV4 verification, XML bodies and errors,
NextToken paging, and ownership tags) plus a fake SSH transport, covering
the full create-stage-exec-sync-dispose lifecycle, disabled/config
validation, explicit AMI pin and scope, host-key pinning, invalid tmpfs
credentials, cancellation at every provisioning boundary, remote transport
loss, output limits, partial and ambiguous creates, eventual consistency,
401/403/429/quota/5xx, malformed/truncated/oversized responses, restart
adoption and orphan cleanup, repeated disposal, sync-back before teardown,
retained cleanup failure, and ownership collisions. No test is skipped and
no unavailable coverage is presented as pass. A smoke test that needs a real
account is an explicit opt-in (see below) and never runs in CI.

## Smoke test

With the plugin enabled and the credentials present, launch one instance,
run `uname -a` plus a file stage round-trip, and terminate it — printing
timings, and always cleaning up (including on Ctrl-C). The smoke entry
point reads the same `CodeyBox:Plugins:codeybox.ec2-sandbox` options and
the same `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` credential variables
as the provider, so a green smoke run means the configured account actually
boots, answers SSH, and stages files. It exits non-zero when any step
fails, after disposing the sandbox.
