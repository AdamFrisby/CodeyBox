# Upstream, Webhooks, And Releases Manual UAT

These procedures cover the spec-only checks from `docs/uat/00-plan.md` for the
Upstream, Webhooks, And Releases section. Run them against disposable projects,
repositories, webhook receivers, and tokens.

## Live GitHub PR Open And Auto-Merge

1. Create a disposable GitHub repository with branch protection configured the
   same way as the target deployment.
2. Configure a CodeyBox project with `Upstream.Kind=github`, owner, repository,
   token env var, `MergeMethod`, and both `AutoMerge=false` and `AutoMerge=true`
   in separate runs.
3. Queue a small work item with `PushUpstream=true`.
4. Verify CodeyBox pushes the work branch, opens exactly one PR with the
   configured title/body template, and emits `work_item.pull_request_opened`.
5. For the auto-merge run, verify GitHub receives the configured merge method
   and CodeyBox records the remote merge SHA when the PR is mergeable.
6. Repeat with an existing open PR for the same branch and verify the item
   remains recoverable without duplicate PR creation.

## Real Webhook Receiver

1. Start a disposable HTTPS webhook receiver that records headers and request
   bodies.
2. Configure one signed endpoint with an event filter and one unsigned endpoint
   without a filter.
3. Run a work item through at least `work_item.working`, `work_item.done`, and a
   retry or budget event.
4. Verify filtered endpoints receive only matching events, signatures validate
   with the configured secret, and payloads include `externalId` when present.
5. Return transient 5xx responses from the receiver and verify retry attempts
   are capped and logged.

## Live GitHub Release Webhook

1. Configure changelog automation with a GitHub webhook secret and a project
   whose upstream points at a disposable GitHub repository.
2. Install a GitHub release webhook for the repository using the configured
   secret.
3. Publish a GitHub release with a previous tag available.
4. Verify CodeyBox accepts the signed webhook, enumerates merged PRs between the
   previous tag and release tag, generates markdown, and queues a work item for
   the configured changelog path.
5. Send a webhook with a bad signature and verify it returns `401` and queues no
   work item.

## Full Release To Tag And Release Notes

1. Enable release management for a disposable project and configure a release
   branch template, deep auditors, and optional GitHub release creation.
2. Create a release, queue one or more linked work items, and let them complete.
3. Close the release and verify it transitions to review only after linked items
   are terminal.
4. Let deep audit pass, then verify the release branch is merged through the
   configured upstream and the release transitions to `Released`.
5. When GitHub release creation is enabled, verify the configured tag template
   and generated release notes appear on GitHub.
6. Repeat with a failing deep auditor and verify the release transitions to
   `Failed`, then can be reopened for remediation.

## Real Release Branch Auto-Sync

1. Enable `AutoSyncMainInterval` for a disposable release project.
2. Create an open release branch and add a new commit to the project default
   branch.
3. Run the sync service or wait for its next sweep.
4. Verify the default branch is merged into the release branch through the
   configured upstream provider.
5. Introduce a deterministic merge conflict and verify CodeyBox emits
   `release.sync_conflict`, leaves the release open, and does not retry until
   the configured interval elapses or the service restarts.
