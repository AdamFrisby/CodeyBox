# Azure DevOps Upstream Remote Plugin

`IUpstreamRemote` provider for **Azure DevOps Repos** (pull requests, build
validations, work-item-linked branches and service-hook subscriptions).
One project, one plugin: `CodeyBox.AzureDevOpsUpstreamPlugin`
(`IUpstreamRemote.Name = "azure-devops"`, plugin id
`codeybox.azure-devops-upstream`).

The plugin is **off unless an operator enables it**: add the assembly to
`CodeyBox:Plugins:AssemblyPaths` and the id to
`CodeyBox:Plugins:Allowlist`, then set `Upstream.Kind = "azure-devops"` on
the project. See `docs/extending/upstream-plugins.md` for the registration
mechanics shared by all upstream plugins.

## Configuration

Per-project settings live in `Upstream.PluginConfig` (read at runtime via
`IUpstreamPluginHost.GetProjectUpstreamConfig` — the only per-project state
the plugin sees):

```json
"Upstream": {
  "Kind": "azure-devops",
  "TokenEnvVar": "CODEYBOX_ADO_PAT",
  "MergeMethod": "merge",
  "AutoMerge": false,
  "PluginConfig": {
    "Organization": "contoso",
    "Project": "Fabrikam",
    "Repository": "WebApp"
  }
}
```

| Key | Required | Default | Meaning |
|---|---|---|---|
| `Organization` | yes | — | Organisation (cloud) or collection (server) |
| `Project` | yes | — | Team project name or GUID |
| `Repository` | yes | — | Repository name or GUID |
| `InstanceUrl` | no | `https://dev.azure.com` | Instance base; point at the collection for Azure DevOps Server, e.g. `https://tfs.example.invalid:8080/tfs` |
| `ApiVersion` | no | `7.1` | REST `api-version` (see version requirements) |
| `HttpTimeoutSeconds` | no | `30` | Per-request timeout (clamped 5–300) |
| `PageSize` | no | `100` | `$top` per list call (clamped 1–100) |
| `MaxPages` | no | `25` | Max pages followed per list call (clamped 1–100) |
| `MaxOpenPullRequests` | no | `500` | Hard cap buffered by one list call, enforced before buffering (clamped 1–5000) |
| `MaxRetries` | no | `3` | Bounded retries for safe (GET) calls on 429/5xx, honouring `Retry-After` (clamped 0–5) |
| `RetryBaseDelayMilliseconds` | no | `500` | Retry base delay (clamped 100–30000) |
| `DeleteSourceBranchOnMerge` | no | `false` | Delete the source branch when auto-completing |

Every key is also accepted in the operator ScopedConfig section
(`CodeyBox:Plugins:codeybox.azure-devops-upstream`), which additionally
provides the defaults for the project-less paths (`PushAsync`,
`FetchBaseBranchAsync`, `TryMergeUpstreamBranchAsync` and all read
surfaces): per-project `PluginConfig` wins where a project id is available
(`CompleteAsync`), ScopedConfig fills the rest. All values are re-read on
each operation, so they are hot-reloadable. `TokenEnvVar` for the
project-less paths likewise comes from ScopedConfig.

### Credentials

The PAT **never** appears in configuration. Set `Upstream.TokenEnvVar` to
the env-var name holding the PAT; the plugin reads it with
`Environment.GetEnvironmentVariable` and attaches it as HTTP `Basic`
(empty username + PAT) on host-side REST calls and via a 0700 `GIT_ASKPASS`
script for host-side git pushes. The token is scrubbed from every error
message, never logged, never embedded in a URL, and never passed to a
sandbox — the plugin has no sandbox dependency at all (asserted by test).
Required PAT scope: **Code (Read & Write)** for push/PR/merge,
plus **Build (Read)** for build validations and **Service Hooks
(Read, Query & Manage)** for subscription management.

## Instance-version requirements

- Azure DevOps Services (cloud): fully supported at `api-version` 7.1.
- Azure DevOps Server 2022+: supported at `api-version` 7.1.
- Older on-premises servers: set `ApiVersion` to `6.0`. Newer fields
  (`mergeStatus` detail, some policy settings) may be absent; PRs whose
  merge status is unknown are skipped by the list call and reconsidered on
  the next tick rather than reported from stale data.

## What is supported

| Contract surface | Azure DevOps mapping |
|---|---|
| Push branch | `git push` to `https://{instance}/{org}/{project}/_git/{repo}` via the host git module |
| Open PR / auto-merge | `POST …/pullrequests`, `PATCH …/pullrequests/{id}` to `completed` (`merge`→`noFastForward`, `squash`→`squash`, `rebase`→`rebase`) |
| Merge upstream branch | Host-side git merge+push (no direct branch-merge API exists); `false` on conflict |
| Fetch base branch | Host-side fetch of the base ref |
| List open PRs | Active PRs with source-branch prefix filter; `mergeStatus: conflicts` → conflict flag; forge continuation tokens followed to exhaustion |
| Read PR state | `active`→Open, `completed`→Merged (+ merge commit), `abandoned`→Closed |
| Review state | Reviewer votes (`≥5` approved, `-10` rejected, else pending); required reviewers outstanding; quorum from the "minimum reviewers" policy; `RequirementsMet` = no negative votes and every required reviewer approved |
| Checks/statuses | Commit statuses + build validations by source sha; `RequiredChecksPassed` only when every check passed / neutral / skipped; empty list with `true` means the forge requires nothing for this sha |
| Comments | PR threads flattened (file-anchored threads carry path + 1-based line); post top-level, file-anchored, or reply-in-thread via `ReplyToId` |
| Webhook subscriptions | Service-hook subscriptions, native scope `"project"` only; event names are forge-native passthrough (e.g. `git.pullrequest.created`); one subscription per event, the first returned |
| Repository metadata | Default branch, project visibility, branch protections from reviewer + build policies |
| PR description | The static `UpstreamCompletionRequest.Description` body (no LLM generator is wired in this plugin) |

## What is not supported (and why)

- **Forge releases** (`CreateTagAndReleaseAsync`): Azure DevOps has no
  PR-linked release object — returns `null` (unsupported), never a forced
  translation.
- **Work items**: Azure DevOps carries work items as a first-class concept
  with no contract equivalent. The plugin does not read, link or complete
  work items; the gap is reported here rather than widened into the
  contract.
- **Webhook scopes other than `"project"`**: `repository`, `organization`,
  `user`, `system` return `null` (unsupported) instead of a coerced
  mapping. A `null` list/create/delete result means "this forge cannot
  tell"; an empty list means "supported and empty" — callers must not
  conflate the two.
- **Releases/tags via git**: pushing tags is outside the upstream contract
  and is not performed.

## Failure semantics

Forge unreachable, unauthorised (401), forbidden (403), rate-limited (429)
or rejecting (5xx) is **infrastructure**: the plugin throws
`InvalidOperationException` so the orchestrator retries — never a verdict
on the work item's diff. Soft outcomes return partial results:
PR-already-exists (409 on create) and unmergeable-on-complete (409, mapped
to `AutoMergeRaced` so the orchestrator re-fetches base and retries).
Missing PRs read as `null`. Pagination always follows the forge
continuation token to exhaustion within the configured bounds, so a large
repository never produces a partial answer that reads as complete.

## Integration evidence

There is no live Azure DevOps instance in this environment, so the test
suite (`tests/CodeyBox.Tests/AzureDevOpsUpstreamPluginTests.cs`) exercises
the full lifecycle plus every extended surface against recorded
api-version-7.1 response shapes embedded as fixtures (PR create/get/
complete, reviewers, commit statuses, builds, threads, service-hook
subscriptions, repository/project/policy payloads). Shapes were taken from
the published REST API contracts for
`dev.azure.com/{organization}/{project}/_apis/git` (pull requests,
repositories, threads, commits/statuses), `/_apis/build/builds`,
`/_apis/policy/configurations`, `/_apis/projects` and
`{organization}/_apis/hooks/subscriptions`.
