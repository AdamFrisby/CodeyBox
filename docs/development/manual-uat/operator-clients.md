# Operator Clients Manual UAT

These procedures cover the manual/spec-only flows from `docs/uat/00-plan.md` for
the Operator Clients section.

## Installed CLI

Prerequisites:
- Running CodeyBox API endpoint URL.
- API key with queue access.
- Installed `codeybox` binary on `PATH`.

Procedure:
1. Run `codeybox version` with no API URL, API key, or config file available.
   - Expected: exits `0`, prints one version line, and does not contact the API.
2. Run `codeybox --help`.
   - Expected: help lists `queue`, `configure`, and `version`.
3. Run `codeybox configure` and enter the API URL and API key.
   - Expected: config is written to the displayed config path.
4. Run `codeybox queue add --project <project> --title "Manual CLI UAT" --prompt "manual cli uat" --json`.
   - Expected: exits `0`, prints a JSON work item, and the work item is visible in the API/dashboard.
5. Run `codeybox queue ls --project <project> --json`, then `codeybox queue show <id>`.
   - Expected: both commands exit `0`; JSON/human output includes the work item ID and state.
6. Run `codeybox queue watch <id>` until the item reaches a terminal state.
   - Expected: state transitions are printed without duplicate consecutive state lines.
7. Against an endpoint with an invalid API key, run `codeybox queue ls`.
   - Expected: exits non-zero and prints a concise error without a stack trace.

## Project Configuration Wizard

Prerequisites:
- Terminal with interactive stdin/stdout.
- `dotnet` SDK available.

Procedure:
1. Run `dotnet run --project src/CodeyBox.Cli/CodeyBox.Wizard.csproj`.
2. Enter a valid project ID, display name, repository URL, base branch, default
   agent, upstream kind, audit languages/types, and per-phase network profiles.
   - Expected: prompts appear in the order documented in the UAT plan.
3. Choose `github` upstream.
   - Expected: wizard prompts for owner, repository, and token environment
     variable, then emits a GitHub upstream entry.
4. Repeat with `git-generic` upstream.
   - Expected: wizard prompts for URL and optional token environment variable,
     then emits a `git-generic` upstream entry.
5. Set `CODEYBOX_NETWORK_PROFILES=restricted,default-internet` and rerun.
   - Expected: phase profile choices use those environment-provided values.
6. Choose to save to a new temp file, then rerun and decline overwrite.
   - Expected: first run writes JSON; second run reports cancellation and leaves
     the existing file unchanged.

Known automation gap: scripted non-interactive stdin currently fails before
prompt input is consumed; this is tracked as a follow-up suggestion.

## Admin Dashboard

Prerequisites:
- Running CodeyBox API endpoint URL.
- Admin dashboard configured with `CodeyBoxAdmin:ApiBaseUrl`.
- If auth is enabled, `CODEYBOX_ADMIN_USERNAME` and
  `CODEYBOX_ADMIN_PASSWORD` are configured.

Procedure:
1. Navigate to the dashboard root.
   - Expected: unauthenticated users are redirected to `/login` when auth is
     required; valid credentials open the queue page.
2. On Queue, verify list, empty state, new work item, edit queued item, cancel,
   retry, reorder, global pause/resume, project pause/resume, and budget badges.
3. Open a work item detail page.
   - Expected: diff, timeline, stdout, audit reports, costs, timings, replays,
     and questions tabs load or show clear empty/error states.
4. Open Releases.
   - Expected: release list/detail pages show lifecycle actions and audit
     iterations.
5. Open Suggestions.
   - Expected: filters, detail view, dismiss, bulk dismiss, and promote controls
     call the API and update visible state.
6. Open Fleet and Plugins.
   - Expected: system health, loaded agents, and loaded auditor plugins render;
     API failures show an error banner without breaking navigation.
7. Disconnect the API or stdout SignalR endpoint while a detail page is open.
   - Expected: the page reports stale/error state and remains navigable.
