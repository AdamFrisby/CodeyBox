# CodeyBox: GitLab Upstream (`codeybox.gitlab-upstream`)

First-class upstream remote for [GitLab](https://gitlab.com) (SaaS and
self-hosted). One project, one plugin. **Off unless an operator enables it.**

## Enablement

1. Add this assembly to `CodeyBox:Plugins:AssemblyPaths`.
2. Add `codeybox.gitlab-upstream` to `CodeyBox:Plugins:Allowlist`.
3. Set `Upstream.Kind = "gitlab"` on the project.

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.gitlab-upstream"],
      "AssemblyPaths": ["/opt/codeybox/plugins/CodeyBox.GitLabUpstreamPlugin.dll"]
    },
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://gitlab.example.com/myteam/myproject.git",
        "Upstream": {
          "Kind": "gitlab",
          "TokenEnvVar": "GITLAB_TOKEN",
          "AutoMerge": true,
          "MergeMethod": "squash",
          "PluginConfig": {
            "BaseUrl": "https://gitlab.example.com/api/v4",
            "Project": "myteam/myproject"
          }
        }
      }
    ]
  }
}
```

## Configuration

Per-project `Upstream.PluginConfig` keys (highest precedence):

| Key | Required | Meaning |
|-----|----------|---------|
| `BaseUrl` | yes | GitLab **API** base, e.g. `https://gitlab.com/api/v4`. The instance root is accepted too (`/api/v4` is appended). `http` is allowed — LAN instances are the norm for self-hosted forges. Must not embed credentials. |
| `Project` | yes | Project path (`group/subgroup/project`) or numeric project id. |
| `PerPage` | no | Items per API page, 1–100 (default 50). |
| `MaxListPages` | no | Page cap per listing, 1–50 (default 10). Bounds every list so a large repository yields a bounded answer, never an unbounded buffer. |

Calls that carry no project context — push, base-branch fetch, MR
listing/reads, release-sync merges, and all extended read surfaces — fall
back to the plugin-scoped section
`CodeyBox:Plugins:codeybox.gitlab-upstream` with the same keys (plus
`TokenEnvVar`, naming the env var that holds the token). Single-instance
operators can therefore configure the project once; multi-repository
operators should use per-project `PluginConfig` for the write path and point
the scoped fallback at the repository they want swept.

All values are re-read on each operation, so they are hot-reloadable.

### Credentials

The token **never** appears in configuration files. The operator puts a
GitLab personal/project access token (`api` scope; `read_api` suffices for
read-only surfaces, plus `write_repository` for push/MR write/merge) in an
environment variable, names it in `Upstream.TokenEnvVar`, and the
orchestrator forwards only the *name*. This remote reads the value with
`Environment.GetEnvironmentVariable(name)` at call time, sends it as a
`PRIVATE-TOKEN` header and via a short-lived `GIT_ASKPASS` script for git
pushes. The value is redacted from every error message, never logged, and
never mounted into a sandbox — the plugin references no sandbox assembly at
all (covered by test).

## What is supported

Core lifecycle (`IUpstreamRemote`):

- Push a branch (inside `CompleteAsync`, and standalone via `PushAsync`
  against the scoped project), open an MR, optionally auto-merge.
- Reuse of `ExistingPullRequestNumber` for the orchestrator's race-recovery
  re-run (create is skipped, the still-open MR is merged).
- Release-sync branch merge via a host-side temp clone
  (`TryMergeUpstreamBranchAsync`).
- Base-branch fetch (`FetchBaseBranchAsync`); MR listing with source-branch
  prefix filter and forge-computed mergeability; MR state reads
  (opened/closed/merged + merge sha, preferring the squash sha for
  squash-merges).

Extended surfaces (all genuinely GitLab REST API v4 — GitLab's vocabulary,
not GitHub's):

- **Reviews** — GitLab approvals are not GitHub reviews: approvals can be
  rule-based with required counts and eligible approvers. Rule names go in
  `RequiredReviewers`, the quorum in `RequiredApprovalCount`, and
  `RequirementsMet` follows the forge's own `approved` verdict. Individual
  approvals appear as `Approved` reviews; stale/dismissed has no GitLab
  equivalent and is not synthesized.
- **Checks** — the latest pipeline for the head sha (jobs are the individual
  checks) plus commit statuses. `RequiredChecksPassed` follows the latest
  pipeline's `success`; without any pipeline it means "no failing check".
- **Comments** — MR discussions and notes, list and post: plain top-level
  notes, replies inside an existing discussion (`ReplyToId` carries the
  GitLab discussion id), and file-anchored review threads (the MR's diff
  refs supply the base/head SHAs GitLab requires for a positioned
  discussion). System notes are filtered from listings.
- **Webhooks** — project hooks (`repository` scope), group hooks
  (`organization` scope, resolved through the project's group namespace),
  and instance system hooks (`system` scope). Event names are GitLab's
  native trigger names and passed through to the matching trigger flags;
  names with no GitLab equivalent decline creation (`null`) rather than a
  subscription that silently drops them.
- **Repository metadata** — default branch, visibility
  (`public`/`internal`/`private`), protected branches with the
  project-level approvals quorum and the
  `only_allow_merge_if_pipeline_succeeds` status-check requirement.

Failure classification: unreachable / unauthorised / forbidden /
rate-limited / 5xx throw `GitLabUpstreamException` (infrastructure — the
orchestrator retries, never a verdict on the diff). MR-already-exists
(409 on create with an "already exists" body) and merge-blocked (409/422
on merge) return partial results with `Notes`; 405 on merge sets
`AutoMergeRaced` for the orchestrator's race recovery.

## What is NOT supported (by design)

- **User-scoped webhooks** — GitLab has no user-level hooks. Requests
  return `null`.
- **Releases/tags** — left on the contract default (`null`); GitLab
  releases exist but are outside this provider's scope.
- **Unknown webhook scopes and unmappable event names** — `null` rather
  than a coerced subscription.

Unsupported is always non-fatal: callers log and continue. `null` means
"this forge cannot tell you"; a non-null empty list means "supported and
empty" — never confuse the two when gating a merge.

## Approximations (GitLab concepts without a 1:1 contract field)

- Review `RequirementsMet` is the forge's own `approved` flag, which
  already accounts for every applicable rule — the provider never
  re-implements quorum logic.
- `RequiredChecksPassed` follows the latest pipeline; pipeline-less SHAs
  with no failing commit status count as "requires nothing".
- `HasMergeConflict` covers `conflict` and `need_rebase` detailed statuses;
  `checking` (still computing) is treated as unknown and skipped for the
  next sweep tick.
- MR merge-style mapping: `squash` passes `squash: true` on the merge call;
  `merge`/`rebase` pass `squash: false` (GitLab's fast-forward/rebase
  behaviour itself is a project setting, not a per-merge flag).
- Protected-branch rules report the project-level approvals quorum on
  every rule; GitLab quotas live per rule, the project setting is the
  honest shared approximation and is documented as such.

## Instance version requirements

Developed against GitLab REST API v4 (16.x/17.x shapes: `detailed_merge_status`
on MRs, `approval_state`, pipeline `status` values, discussions with
`position`, project/group/system hooks). The provider degrades honestly on
older instances: a missing endpoint (404) yields `null`/empty metadata,
never an error. Group hooks require the 13.x group-hooks API; instances
without it fall back to `null` for the organization scope.

## Verification without a live instance

No live GitLab instance exists in CI, so integration coverage uses
recorded-shape fixtures (`tests/CodeyBox.Tests/Fixtures/GitLab/`,
transcribed from the published GitLab REST API v4 object shapes on
2026-09-29) asserting that real response shapes parse into the provider's
contract mappings, plus queue-driven tests for every lifecycle and failure
path. To run against a real instance, set `GITLAB_TEST_BASEURL`,
`GITLAB_TEST_PROJECT` (path or id), and `GITLAB_TEST_TOKEN` (optional
`GITLAB_TEST_MR`) and run the `Live_ReadOnlySurfaces` test, which is
skipped otherwise and is read-only (metadata + MR listing).
