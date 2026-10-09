# AWS EC2 sandbox provider (`codeybox.ec2-sandbox`, kind `ec2`)

First-party hosted-VM sandbox backend. The OpenStack remote-VM provider
is the concrete analogue — same lifecycle shape, SSH data plane, and
tag-scoped leak cleanup, adapted to the EC2 Query API
(`https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_RunInstances.html`).

Status: **disabled by default**. No `SandboxClass` member is added and the
provider is not enabled — enabling is a future operator action (see
`docs/extending/ec2-sandbox-plugin.md`, "Operator prerequisites").

## Layout

- `Ec2SandboxProvider.cs` — `ISandboxProvider` + `IPluginInitializer` +
  `IActiveSandboxProvider`. Owns provisioning (AMI verify → key pair →
  security group → `RunInstances` → Elastic IP → running → SSH → stage),
  tagged-set deletion, client-token/request-tag reconciliation, and the
  ownership-scoped orphan sweep. Declares only `teardown`. No baseline
  surface at all.
- `Ec2ApiClient.cs` — typed EC2 Query client (run/describe/terminate,
  images, key pairs, security groups, addresses, volumes). SigV4-signed,
  streamed response bounds, page-bounded listings, client-side re-checks of
  every server-side filter. No provider logic.
- `Ec2ApiException.cs` — failure taxonomy (`Unavailable`/`Deferred`/
  `Quota`/`Unexpected`/…); ambiguous outcomes reconcile before resubmit.
- `Ec2Sandbox.cs` — live handle (exec/stage/sync/dispose). Sync-back
  runs before cloud cleanup and before the handle is marked disposed
  (two-flag disposal: `_disposeStarted` refuses new execs, `_disposed` is
  set last).
- `Ec2SandboxOptions.cs` / `Ec2CredentialChain.cs` — hot-reloadable
  knobs + host-only credential resolution (`AWS_ACCESS_KEY_ID` /
  `AWS_SECRET_ACCESS_KEY`, optional `AWS_SESSION_TOKEN`, never in config).
- `Ec2CloudInit.cs` — pure cloud-config renderer (pinned ed25519 host
  key, tmpfs mounts). Unit-testable without a cloud.
- `Ec2SshKeys.cs` — `ssh-keygen` key seams, DNS seam, and the
  `OpenSshCliTransport` factory with strict host keys.
- `Ec2SecurityGroupPolicy.cs` / `Ec2SigV4.cs` — pure security-group-rule
  planner (defence in depth; kind stays `NotEnforced`) and the SigV4
  signer (injected credentials/HTTP transport, no ambient identity).
- `Ec2SandboxSmoke.cs` — real end-to-end smoke flow (opt-in, needs a
  funded account; never runs in CI).

## Invariants for future edits

- The credentials stay on the host: never in user-data, logs, prompts, or config.
- Name prefixes gate candidacy; only exact ownership tags authorize
  deletion. Other owners' resources are never touched.
- `RunInstances` launches exactly one instance with a stable per-attempt
  `ClientToken` plus owned tags at creation; ambiguous outcomes reconcile
  by request tag before any resubmit within the bounded `MaxRunAttempts`.
- Terminated instance records are excluded from managed inventory, and only
  live instances keep their request tag alive in the orphan sweep.
- A present-but-unparseable cloud-reported address fails fast — never SSH
  at an unconfirmed target.
- Every bound (bytes, pages, items, retries, waits, rules) is enforced
  before buffering, and every wait is bounded and cancellable.
- `Ec2SandboxOptions` defaults are the single source of truth for knobs;
  `Ec2ClientLimits.Default*` mirrors them for the client.
- On-demand only: no spot, fleet, autoscaling, instance profile, implicit
  default VPC/security group, or unlimited CPU-credit spend.
