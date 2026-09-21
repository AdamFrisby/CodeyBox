# Gitea Upstream Remote (`codeybox.gitea-upstream`)

An upstream-remote plugin implementing `IUpstreamRemote` against Gitea API v1.
Off unless an operator enables it (see *Enablement* below). No core or
pipeline code knows about Gitea — everything goes through the contract.

## What it supports

Core lifecycle (all real, no stubs):

| Operation | How |
|---|---|
| Push a branch | Host-side `git push` to `https://{host}/{owner}/{repo}.git` derived from `BaseUrl`, token via `GIT_USERNAME`/`GIT_PASSWORD` env on the child process only |
| Open a PR | `POST /repos/{owner}/{repo}/pulls` (`title`, `body`, `head`, `base`) |
| PR already exists | 422, or 409 with an "already exists" body → soft partial outcome (`BranchPushed`, no throw) |
| Auto-merge | `POST /pulls/{index}/merge` with `Do: merge \| rebase \| squash` mapped from `MergeMethod`; the merge-commit sha is re-read from the PR afterwards |
| Merge blocked | 405 → `AutoMergeRaced` (orchestrator race recovery); 409 / 423 → partial outcome with `Notes`, PR left open |
| Race recovery | Honors `ExistingPullRequestNumber`: skips creation, proceeds to merge |
| Merge an upstream branch | Host-side clone/fetch/`git merge`/push (Gitea exposes no branch-to-branch merge API); conflicts return `false` with the forge untouched |
| Fetch a base branch | Via the host git module; returns the new sha (`null` when unadvertised) |
| List open PRs | `GET /pulls?state=open`, filtered by head-branch prefix, detail-fetched per PR for `mergeable`; the page cap throws rather than returning a partial list |
| Read a PR | `GET /pulls/{index}` → open/closed/merged + merge-commit sha |
| Releases | `POST /releases` (tag auto-created); 409/422 → `null` |

Extended surfaces (only what Gitea genuinely provides):

| Surface | Mapping |
|---|---|
| Review state | `GET /pulls/{index}/reviews`: `APPROVED`/`REQUEST_CHANGES`/`COMMENT`/`PENDING` (+`REQUEST_REVIEW` as pending); dismissed reviews report `Dismissed` and stale approvals don't count; quorum from the branch protection matching the base branch (`required_approvals`, 0 when no rule) |
| Checks | Combined commit status `GET /commits/{sha}/status`: `success`→passing, `error`/`failure`→failing, `pending`→pending, `warning`→neutral; empty list + `RequiredChecksPassed` is "requires nothing" |
| Comments | Plain discussion via the issues API (`GET`/`POST /issues/{index}/comments`; a Gitea PR shares its index with the backing issue) |
| Webhooks | All four native scopes: `repository` → `/repos/{owner}/{repo}/hooks`, `organization` (or Gitea's own spelling `organisation`) → `/orgs/{org}/hooks` (the configured `Owner` must be the org), `user` → `/user/hooks`, `system` → `/admin/hooks`; type `gitea`, events passed through verbatim |
| Repository metadata | `GET /repos/{owner}/{repo}` (default branch, `internal`/`private`/`public`) plus `GET /branch_protections` (rule name, required approvals, status-check requirement) |
| Capability signal | Unsupported returns `null`, supported-and-empty returns an empty result — never confused |

Deliberately unsupported (returns `null`, never fails a work item):

- File-anchored review threads and thread replies on `PostCommentAsync`
  (they need a Gitea review id; the contract's plain-comment path won't fake them).
- Requested-reviewer lists: Gitea 1.22 has no GET endpoint for them
  (`RequiredReviewers` stays empty).
- Webhook delivery secrets: the contract carries no secret input, so hooks are
  created without one — front the target URL accordingly.
- Unknown webhook scopes: `null` rather than a coerced subscription.

## Configuration

Plugin-scoped defaults (`CodeyBox:Plugins:codeybox.gitea-upstream`):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.gitea-upstream": {
        "BaseUrl": "https://git.example.com/api/v1",
        "Owner": "myteam",
        "Repository": "myproject",
        "TokenEnvVar": "GITEA_TOKEN"
      }
    }
  }
}
```

Per-project overrides (`Upstream.PluginConfig`: `BaseUrl`, `Owner`,
`Repository`) win inside `CompleteAsync`, which is the only contract member
that carries a project id. Every other member uses the scoped defaults, so set
both consistently. Only the token's env-var *name* is configured; the value is
read from the process environment at call time (rotation needs no restart) and
is only ever placed in host-side git env and per-request HTTP headers —
sandboxes never see it.

`Upstream.Kind` for a project using this provider is `gitea`, with
`Upstream.TokenEnvVar` naming the env var (preferred) or the scoped
`TokenEnvVar` as fallback.

### Enablement

New plugins are off by default. The operator must add the assembly to
`CodeyBox:Plugins:AssemblyPaths` (or a package directory), name
`codeybox.gitea-upstream` in both `Allowlist` and `Enabled`, and restart the
host.

## Failure classification

Forge unreachable, 401/403, 429 (retried with capped backoff honoring
`Retry-After`, then thrown), 5xx, or unexpected bodies throw
`GiteaUpstreamException` (an `InvalidOperationException`) so the orchestrator
retries as infrastructure — never a verdict on the diff. Token material is
scrubbed from every message. Soft outcomes (PR exists, merge blocked) return
partial results.

## Requirements

- Gitea **1.19+** recommended (reviews, combined status, branch protections,
  and user hooks all predate it; verified against the 1.22 API surface).
- `BaseUrl` must be `https://` and must not resolve to loopback/link-local or
  RFC 1918/ULA ranges (SSRF guard); DNS names are accepted on their face —
  run the operator's network policy at the edge as well.
- A token with `repo` scope (admin rights additionally required for the
  `system` webhook scope).

## Verification

- Unit/wire tests: `GiteaUpstreamPluginTests` in `tests/CodeyBox.Tests`, driven
  by recorded-shape fixtures in `tests/CodeyBox.Tests/Fixtures/Gitea/` (shapes
  verified against Gitea 1.22 `templates/swagger/v1_json.tmpl`).
- Live integration: `Live_ReadOnlySurfaces` is skipped unless
  `GITEA_TEST_BASEURL`, `GITEA_TEST_TOKEN`, `GITEA_TEST_OWNER`, and
  `GITEA_TEST_REPO` are set (optional `GITEA_TEST_PR`); no live instance
  exists in CI, hence fixtures carry the shape coverage. The live test is
  read-only (metadata + PR listing).

## Known contract findings (not absorbed as special cases)

- Project-agnostic members (`PushAsync`, `ListOpenPullRequestsAsync`,
  `GetPullRequestAsync`, …) carry no project id, so a singleton plugin cannot
  resolve per-project config there — it uses the scoped defaults. Multi-repo
  deployments on one instance are the gap; the contract would need a project
  context on every member to close it.
- `UpstreamWebhookScopes.Organization` ("organization") vs Gitea's native
  "organisation": the provider accepts both spellings for the org scope.
- There is no Forgejo provider in-tree, so the ancestry-divergence comparison
  the task envisaged could not be run; the implementation stuck to Gitea's own
  vocabulary throughout as a hedge.
