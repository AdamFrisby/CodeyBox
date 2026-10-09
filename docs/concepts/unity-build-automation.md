# Unity Build Automation adapter (CBX-NEXT-106)

The `unity-build-automation` adapter compiles, tests, and packages Unity
projects on Unity Build Automation and reports the results through the
neutral external-build framework (`docs/concepts/external-builds.md`).
It is **disabled by default** and stays disabled until the operator
completes the activation steps below. This document describes
prerequisites, licensing responsibility, and activation. It enables
nothing.

Official references:

- <https://docs.unity.com/en-us/build-automation/build-automation-api>
- <https://build-api.cloud.unity3d.com/docs/>
- <https://docs.unity.com/en-us/build-automation/check-build-results>

## What the adapter does

- Dispatches builds only to **operator-approved** Unity
  organization / project / build-target combinations (exact match;
  repository content can never approve a target, a credential, or a URL).
- Builds only the **host-published frozen candidate** (`refs/candidates/…`
  carrying an exact commit SHA). Branch names and latest-on-branch are
  rejected before dispatch, and a provider that reports a branch-tip
  checkout is never adopted as evidence.
- Maps provider states into separate **compile / test / package** evidence.
  Tests pass only on an **explicit test report** with zero failures; a
  player/editor compilation or a successful package is never reported as a
  test pass.
- Reuses the shared framework for credentials handling, scoped sandbox
  tools, duration history and the >10-minute park policy, durable
  completion/phase ownership, budgets, concurrency and license-seat
  reservation, confirmed cancellation, and safe artifact ingestion.

## Supported editors, platforms, configurations

Support is an exact-match allowlist in
`CodeyBox:UnityBuildAutomation` — not a claim about every Unity version:

| Dimension       | Default allowlist                                              |
|-----------------|----------------------------------------------------------------|
| Editor versions | empty — the operator lists each verified version explicitly (e.g. `2022.3.62f1`) |
| Platforms       | `Android`, `iOS`, `WebGL`, `StandaloneWindows64`, `StandaloneOSX`, `StandaloneLinux64` |
| Configurations  | `Release`, `Debug`                                             |
| Cache classes   | `warm` (incremental), `cold` (clean build via `unity.cleanBuild=true`) |

An editor/platform/configuration outside the allowlist fails **before
dispatch** with a typed error. A provider that reports a different
editor, platform, or checkout commit than dispatched is treated as a
failed build, never as evidence. Clean (`cold`) and incremental
(`warm`) builds should be modelled as distinct approved
configurations/targets so duration history and the park estimator keep
them separate.

## Prerequisites

- An existing operator-approved Unity organization, project, and build
  target with a supported editor/platform/toolchain. The adapter performs
  **approved-target discovery/validation only** against this configuration.
- The project builds from hosted Git reachable by Unity Build
  Automation. Snapshot uploads are **not** supported by this adapter;
  LFS pointer pins and submodule pins travel inside the host-published
  candidate commit (shared snapshot-policy semantics).
- Re-verify the REST paths against the current Unity API docs at
  activation time; the adapter pins the fixed base
  `https://build-api.cloud.unity3d.com` and never follows
  repository-controlled URLs.

## Licensing responsibility

Unity licensing, signing identities, seats, and accounts stay entirely in
the existing external build environment. This adapter does **not**
onboard accounts, install or configure licenses/signing, create access
grants, or run paid builds. License-seat capacity is enforced by the
shared framework's reservation (`MaxLicenseSeatsPerProvider`), which the
operator sizes to the Unity plan.

## Source/ref override policy

No Unity source/ref override is supported, by configuration or by submit
input. The checkout commit comes only from the host-published candidate
ref. Any `unity.commitSha`, `unity.branch`, `unity.ref`, or
`unity.checkout` key — in target parameters or submit input — fails
before dispatch. A snapshot carried alongside a candidate ref is an
ambiguous handoff and is rejected.

## Activation steps (explicit, later)

1. Approve the target in `CodeyBox:ExternalBuilds:ApprovedTargets`
   with `ProviderId: unity-build-automation`, the Unity build-target id,
   `AllowGitPublication: true`, `AllowSnapshotUpload: false`, and the
   `unity.orgId` / `unity.projectId` / `unity.editorVersion` /
   `unity.platform` parameters.
2. Fill the adapter allowlists in `CodeyBox:UnityBuildAutomation`
   (organization, project, editor versions) and set `Enabled: true`
   together with the framework's `Enabled: true`.
3. Wire an `IUnityBuildCredentialProvider` from operator-owned secret
   configuration (never repository content, never logged). Until then the
   bundled null provider fails every call closed.
4. Run the adapter test suite
   (`--filter FullyQualifiedName~UnityBuildAutomation`) and re-verify the
   endpoint shapes against the live API docs before approving any target.

Temporary provider builds are never deleted by the adapter except through
confirmed cancellation; Unity-side retention/cleanup stays operator-owned
in the Unity dashboard.
