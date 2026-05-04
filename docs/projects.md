# Projects

A CodeyBox orchestrator manages multiple **projects** independently. Each
project has its own upstream git URL, its own credentials (per-project
GitHub PAT etc.), its own auditors, and its own defaults. Work items are
bound to a project via `projectId`; the pipeline resolves the project at
pickup time and uses its config for every phase.

## Why projects

* **Multi-repo from one orchestrator.** One CodeyBox instance can drive
  agents against many independent repositories — a website, an internal
  tool, a CLI — without sharing config or credentials.
* **Per-project tokens.** Each project's GitHub PAT is read from its own
  env var. A token leak from one project doesn't expose any other.
* **Per-project audit policy.** A Python service might require ruff +
  pyright + bandit + the architecture LLM review. A Rust binary might
  want clippy + the cheating-detector. Both run side-by-side without
  config interference.

## Configuration

Project config lives in `appsettings.json` (or any standard config
provider). Everything sits under the `CodeyBox` section:

```json
{
  "CodeyBox": {
    "Defaults": {
      "Agent": "claude",
      "BaseBranch": "main",
      "Audit": {
        "MaxIterations": 3,
        "FailingSeverity": "Error",
        "AuditTypes": ["security", "architecture"]
      }
    },
    "Projects": [
      {
        "Id": "my-app",
        "DisplayName": "My App",
        "RepositoryUrl": "https://github.com/me/my-app.git",
        "BaseBranch": "main",
        "Agent": "claude",
        "DefaultAgentClass": "frontier-coding",
        "Upstream": {
          "Kind": "github",
          "GitHubOwner": "me",
          "GitHubRepository": "my-app",
          "TokenEnvVar": "MY_APP_GITHUB_TOKEN"
        },
        "Audit": {
          "Languages": ["typescript"],
          "AuditTypes": ["security", "architecture", "quality", "completeness", "cheating"]
        }
      },
      {
        "Id": "internal-py",
        "RepositoryUrl": "https://github.com/me/internal.git",
        "Audit": { "Languages": ["python"] }
      }
    ]
  }
}
```

### `GitAuthorName` / `GitAuthorEmail`

Set these two fields together to override the git commit author identity for all
work items in this project. Both must be non-empty for the override to take
effect; setting only one falls through to the host identity.

```json
{
  "Id": "my-app",
  "GitAuthorName": "CI Bot",
  "GitAuthorEmail": "ci-bot@example.com",
  ...
}
```

**Resolution order** (first match wins):
1. Project `GitAuthorName` / `GitAuthorEmail` — if both are set.
2. Host global git config (`git config --global user.name/email`) — read once
   at orchestrator startup; requires `git` on PATH and `~/.gitconfig`.
3. Synthetic fallback: `CodeyBox <codeybox@local>`.

Operators without a project-level override and with a normal `~/.gitconfig`
will automatically see their real identity on commits from the next work item
after a restart. No other config change is needed.

### `DefaultAgentClass`

Set `DefaultAgentClass` to a class ID from `CodeyBox:AgentClasses` to enable
quota-aware routing for all work items in this project without requiring
per-item `agentClassId` in the API payload. See
[docs/agent-classes.md](agent-classes.md) for the full routing model.

A per-item `agentClassId` overrides the project default. Set it to `null`
(omit it in the JSON payload) to fall back to legacy direct `Agent` pick.

### Inheritance from `Defaults`

Anything a project omits comes from `Defaults` (shallow merge):

* `Agent` — default agent for new work items
* `BaseBranch` — default integration branch
* `Audit.*` — every field in `ProjectAudit` falls through individually

`Languages` and `AuditTypes` are list-typed and not append-merged: if a
project sets `AuditTypes`, it replaces the defaults entirely. Choose your
defaults expecting them to be replaced wholesale by per-project overrides.

### Project ID rules

* ASCII alphanumeric + `-` and `_`
* 1–64 characters
* Used in REST URLs and the SQLite `project_id` column

## Audit configuration

```json
"Audit": {
  "MaxIterations": 3,
  "FailingSeverity": "Error",
  "PerIterationTimeoutMinutes": 10,
  "StopOnFirstFailure": false,
  "MaxLlmAuditorParallelism": 3,
  "Languages": ["python", "typescript"],
  "AuditTypes": ["security", "architecture", "quality", "completeness", "cheating"],
  "Custom": [
    { "Kind": "shell", "Name": "tests", "Argv": ["npm", "test"] },
    { "Kind": "diff-pattern", "Name": "no-console-log", "Patterns": [
      { "Description": "console.log added", "Regex": "console\\.log\\(" }
    ] },
    { "Kind": "llm", "Name": "ux-review",
      "ReviewFocus": "- Confusing user-facing strings\n- Inaccessible UI patterns" }
  ]
}
```

The orchestrator's effective auditor list for each project is:

```
Languages.SelectMany(preset) + AuditTypes.SelectMany(preset) + Custom
```

| Field | Type | Default | Description |
|---|---|---|---|
| `MaxIterations` | int | `3` | How many audit + rework cycles to attempt before giving up with `AuditFailed` |
| `FailingSeverity` | string | `"Error"` | Findings at or above this severity block the merge. `"Warning"` or `"Info"` can be used to widen the gate. |
| `PerIterationTimeoutMinutes` | int | `10` | Wall-clock cap on a single audit iteration's sandbox |
| `StopOnFirstFailure` | bool | `false` | Stop running auditors as soon as one returns a blocking finding — useful when cheap linters precede expensive LLM auditors |
| `MaxLlmAuditorParallelism` | int | `3` | Max LLM auditors running concurrently. Default `3` means `security:llm-review`, `completeness:llm-review`, and `cheating:llm-review` all run at the same time. Set to `1` to serialize them if you hit API 429 rate-limit errors. Tool auditors are unaffected and always run sequentially. |

### Languages (built-in presets)

| Preset       | Tools                                                              |
|--------------|--------------------------------------------------------------------|
| `python`     | `ruff check`, `ruff format --check`, `pyright`, `bandit`           |
| `typescript` | `npx eslint`, `npx tsc --noEmit`, `npx prettier --check`           |
| `javascript` | `npx eslint`, `npx prettier --check`                               |
| `go`         | `golangci-lint run`, `go vet`                                      |
| `rust`       | `cargo clippy --all-targets -- -D warnings`, `cargo fmt -- --check`|
| `csharp`     | `dotnet format --verify-no-changes`, `dotnet build /warnaserror`   |
| `ruby`       | `rubocop`, `brakeman`                                              |
| `shell`      | `shellcheck` over tracked `*.sh` files                             |

All language presets are tool-only (no agent credentials). A buggy linter
cannot exfiltrate the agent's API key — the audit phase runs them in a
credential-free sandbox.

### Audit types (built-in presets)

| Preset         | Capability      | What it does                                                                                                                  |
|----------------|-----------------|-------------------------------------------------------------------------------------------------------------------------------|
| `security`     | tool + LLM      | gitleaks (secrets), semgrep auto (SAST), and a comprehensive LLM review aligned to OWASP ASVS 5.0 + Top 10 + CWE Top 25 + LLM-specific issues. Categories include: injection, output encoding/XSS, validation/business logic, API/web service, file handling (path traversal, deserialisation, XXE), authentication, sessions/JWT, authorization (IDOR, mass assignment), OAuth/OIDC, cryptography, secure communication, configuration, data protection (hardcoded secrets, PII), SSRF, resource exhaustion/DoS, logging/error handling, memory safety in unsafe code, race conditions, dependencies, prompt injection / LLM-tool-abuse, and business-logic flaws. |
| `architecture` | LLM             | Loose-coupling, leaking internals, layering violations                                                                        |
| `quality`      | LLM             | Dead code, magic numbers, naming, error handling                                                                              |
| `completeness` | LLM             | TODO markers, missing tests, half-finished implementations                                                                    |
| `cheating`     | tool + LLM      | Suppression markers (`@ts-ignore`, `# noqa`, `#pragma warning disable`, …) and LLM review for shortcuts/stubbed implementations |
| `tests`        | tool + LLM      | Deterministic patterns for no-op assertions (`Assert.True(true)`, `expect(x).toBe(x)`, `assert 1 == 1`, etc.) plus an LLM "are these tests meaningful?" reviewer. Catches implementation-mirroring tests (where `test_add` asserts `add(1,2) == 1+2`), pure-mock tests that only verify mock setup, missing tests for new code paths, missing failure-path coverage, and skipped/removed tests without replacement. |

The `cheating` preset is specifically for catching agent shortcuts: an
LLM that's struggling sometimes disables warnings, stubs functions with
`NotImplementedException`, catches exceptions too broadly, or skips
failing tests. The deterministic diff-pattern auditor catches the most
common suppression markers; the LLM reviewer catches subtler shortcuts
by comparing the diff against the original task.

### Cross-agent review (`AuditAgent` / `PerAuditorAgent`)

By default, LLM auditors run with the same agent as the work phase. To
diversify signal, configure a different model for the audit phase:

```json
"Audit": {
  "AuditAgent": "gemini",
  "AuditTypes": ["security", "architecture", "completeness"]
}
```

For per-auditor control (e.g. security on Claude, completeness on Gemini):

```json
"Audit": {
  "AuditAgent": "gemini",
  "PerAuditorAgent": {
    "security:llm-review": "claude"
  }
}
```

**Resolution order** (per LLM auditor):
1. `PerAuditorAgent[<auditor name>]` if present.
2. Else `AuditAgent` if set.
3. Else the work agent (backwards-compatible default).

**Requirements:**
- The audit agent must be registered (`IAgentRegistry`).
- Its credentials must be available (e.g. `CODEYBOX_GEMINI_API_KEY` set).
  If either is missing, the pipeline logs a warning and falls back to the
  work agent — no crash, no failed work items.

Tool auditors (`security:gitleaks`, `csharp:build-WaE`, etc.) are never
affected by these settings — they do not invoke an LLM.

See [`docs/audit.md`](audit.md) for the full cross-review documentation
including trade-offs, observability events, and quota fallthrough behaviour.

### Stuck-agent detection

```json
"Audit": {
  "StuckThresholdMinutes": 10,
  "AutoRetryOnStuck": false,
  "MaxStuckRetries": 2
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `StuckThresholdMinutes` | int | — (inherits global 10 min) | Minutes of zero CPU + zero TCP activity before the agent is killed. `0` = disabled for this project. Omit to use the orchestrator global default. |
| `AutoRetryOnStuck` | bool | `false` | Re-queue the work item from the same phase after a stuck-kill, rather than transitioning to Failed. |
| `MaxStuckRetries` | int | `2` | Maximum automatic re-queues per work item before the item is marked Failed regardless of `AutoRetryOnStuck`. |

**Resolution order** for `StuckThresholdMinutes`:
1. Project `Audit.StuckThresholdMinutes` — if explicitly set (including `0` to disable).
2. Global `CodeyBox:Pipeline:StuckThresholdMinutes` in appsettings (default `10`).

**Constraints**: the effective threshold must be ≥ 1 minute (or 0 to disable).
A threshold greater than half the phase timeout is not rejected but is
ineffective — the coarse phase timeout will fire first.

**`AutoRetryOnStuck` behaviour**:
- On stuck-detection, the work item is transitioned back to the start of the
  stuck phase (`Queued` for work, `WorkComplete` for rework, `AuditPassed` for
  merge) and `StuckRetries` is incremented.
- Once `StuckRetries ≥ MaxStuckRetries`, further stuck-kills transition the
  item to Failed.
- Manual retries via `POST /workitems/{id}/retry` do **not** consume the
  `StuckRetries` budget.
- The `work_item.agent_stuck` webhook fires on every stuck-kill regardless of
  whether auto-retry is enabled.

**Example — fast detection, no auto-retry (investigate manually)**:
```json
"Audit": { "StuckThresholdMinutes": 5 }
```

**Example — aggressive auto-recovery**:
```json
"Audit": {
  "StuckThresholdMinutes": 8,
  "AutoRetryOnStuck": true,
  "MaxStuckRetries": 3
}
```

**Example — probe disabled (manual VM-console investigation)**:
```json
"Audit": { "StuckThresholdMinutes": 0 }
```

### Per-phase network profiles

Each project can specify which host-enforced network profile applies to
each pipeline phase. Profile names map (via the orchestrator's
`SandboxNetworkProfiles` config) to host bridges set up by
`scripts/setup-host-networks.sh`.

```yaml
networkProfiles:
  work:        claude              # work + (default for) rework
  rework:      claude              # explicit override; falls back to Work if omitted
  auditAgent:  claude              # LLM auditors (architecture, security review, …)
  auditTool:   isolated            # tool-only auditors (linters, scanners — no LLM API needed)
  merge:       internet-only       # merge agent; pick a profile that allows test-suite egress if your tests reach the network
```

The host-side `networks.conf` accepts three profile modes (full
discussion in [`host-firewall.md`](host-firewall.md)):

| Mode             | Egress                                                                | Use case                                                          |
|------------------|-----------------------------------------------------------------------|-------------------------------------------------------------------|
| `-`              | None (DNS + loopback + established only).                             | Tool-only audits, deterministic merge phases, gitops sandboxes.   |
| `internet`       | Block RFC1918 / link-local / cloud-metadata / loopback / multicast; accept the rest. | "Wide reach but no LAN attacks" — agent can hit any external service but can't pivot to your home/office network or cloud-metadata endpoints. |
| `host1,host2,…`  | Strict hostname allowlist; resolved to IPv4 IPs at setup time.        | Production agents bound to known APIs (e.g. `api.anthropic.com`). |

**Why per-project, per-phase**: project A's tests might hit a staging
API at merge time and need network; project B's merge phase wants to be
maximally isolated. Each project decides what its phases need.

Defaults inherit from `Defaults.NetworkProfiles` if a project omits a
field. `Rework` additionally falls back to `Work` (so the common case
of "agent uses the same network for both" doesn't have to be repeated).

A profile referenced in project config but not configured in
`SandboxNetworkProfiles` makes the provider fail loudly at sandbox
creation — never silently degrades to "no enforcement."

### Custom auditors

Three kinds, all configured in JSON:

| Kind           | Required fields           | Notes                                  |
|----------------|---------------------------|----------------------------------------|
| `shell`        | `Name`, `Argv`            | Exit 0 = pass; non-zero = Error finding|
| `diff-pattern` | `Name`, `Patterns[]`      | Regex against added lines in diff      |
| `llm`          | `Name`, `ReviewFocus`     | LLM review with the project's agent    |

## Per-project upstream

Each project specifies its own upstream:

```json
"Upstream": {
  "Kind": "github",                        // "noop" | "git-generic" | "github"
  "GitHubOwner": "me",
  "GitHubRepository": "my-app",
  "TokenEnvVar": "MY_APP_GITHUB_TOKEN"     // env var holding the PAT
}
```

The `TokenEnvVar` indirection is deliberate: tokens never appear in
config files. Each project reads its token fresh from the env every push.
Rotating a token is `unset / set` of the env var before the next work
item; in-flight pushes keep their pre-rotation value.

For `git-generic`, set `GenericUrl` and rely on the host git config
(askpass, SSH agent) for auth. For `noop`, no upstream push happens and
the host bare repo is the source of truth.

### GitHub upstream: pull request flow

When `Kind=github`, phase 4 now pushes the **work branch** to GitHub
and opens a pull request (workBranch → baseBranch) rather than pushing
the merged base branch directly. This leaves a PR and code-review trail
on GitHub even for fully-automated merges.

Three additional options control the behaviour:

| Option | Type | Default | Description |
|---|---|---|---|
| `MergeMethod` | `"merge"` \| `"squash"` \| `"rebase"` | `"merge"` | Merge strategy used when `AutoMerge=true`. |
| `AutoMerge` | bool | `false` | When `true`, merges the PR via the GitHub API immediately after opening it. When `false`, the PR is left open for human review. Either way the work item transitions to `Done`. |
| `PullRequestTitleTemplate` | string? | — | Template for the PR title. Supports `{title}` (work item title) and `{branch}` (work branch name) placeholders. Defaults to the work item title. |

**Example — auto-merge with squash:**

```json
"Upstream": {
  "Kind": "github",
  "GitHubOwner": "me",
  "GitHubRepository": "my-app",
  "TokenEnvVar": "MY_APP_GITHUB_TOKEN",
  "AutoMerge": true,
  "MergeMethod": "squash",
  "PullRequestTitleTemplate": "[bot] {title}"
}
```

**Backward compatibility:** Projects upgrading from the old push-only
behaviour (where phase 4 pushed baseBranch directly) will now get a PR
instead. The local merge produced by phase 3 is still in the host bare
repo; the upstream push is additive. If your branch protection rules
prevent the PAT from merging, leave `AutoMerge=false` and approve the PR
manually.

## Budget caps

Per-project rate limits applied at pickup time. All caps default to 0
(unlimited). Setting any cap > 0 throttles that project without affecting
others.

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "fast-mover",
        "RepositoryUrl": "https://github.com/example/fast-mover",
        "Budget": {
          "MaxItemsPerHour": 10,
          "MaxItemsPerDay": 50,
          "MaxConcurrentForProject": 2
        }
      }
    ]
  }
}
```

### Fields

| Field | Type | Default | Meaning |
|---|---|---|---|
| `MaxItemsPerHour` | `int` | `0` (unlimited) | Max work items that can **start** per rolling 60-minute window |
| `MaxItemsPerDay` | `int` | `0` (unlimited) | Max work items that can **start** per rolling 24-hour window |
| `MaxConcurrentForProject` | `int` | `0` (unlimited) | Max work items in a non-terminal, non-Queued state simultaneously for this project |

"Start" means the moment the orchestrator commits to running an item (after
dependency and quota routing gates pass, `started_at` is written). Items
deferred by a budget cap stay Queued and are re-checked automatically on the
next pickup cycle — no manual intervention needed.

### Deferral semantics

When an item is deferred:

1. Audit event `budget.deferred` is logged with the reason and project ID.
2. Webhook event `budget.deferred` fires: `{ workItemId, projectId, reason, suggestedRetryAt }`.
3. The item is re-enqueued after a short back-off (`MaxItemsPerHour` → 5 min;
   `MaxItemsPerDay` → 60 min; `MaxConcurrentForProject` → 1 min).
4. Other projects' items may be picked up in the meantime.

### Budget usage endpoint

```
GET /projects/{id}/budget/usage
```

Returns current consumption against the configured limits:

```json
{
  "lastHour": 5,
  "last24h": 23,
  "currentlyInFlight": 1,
  "limits": {
    "perHour": 10,
    "perDay": 50,
    "concurrent": 2
  }
}
```

The admin dashboard shows this as colour-coded usage bars per project
(yellow ≥ 80%, red = 100%).

## REST API

```
GET  /projects             — list all configured projects
GET  /projects/{id}        — single project
GET  /projects/{id}/budget/usage  — current budget consumption vs. limits
POST /workitems            — body now requires "projectId" instead of "repositoryUrl"
```

`POST /workitems`:

```json
{
  "projectId": "my-app",
  "title": "Add JSON config support",
  "prompt": "Add a --config flag that reads settings from a JSON file.",
  "agent": null,           // optional — overrides project default
  "baseBranch": null,      // optional — overrides project default
  "workBranch": null,      // optional — defaults to "codeybox/<short id>"
  "pushUpstream": true     // optional — gates phase 4 push
}
```

## Credential provider priority

When multiple [credential plugins](credential-plugins.md) are installed, a project
can declare which plugins it prefers and in what order:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "my-app",
        "CredentialProviderPriority": ["myorg.vault-creds", "myorg.aws-ssm"]
      }
    ]
  }
}
```

### How it works

The `CredentialProviderPriority` list replaces the plugin slot for this project:

- Listed plugins are tried **in order**, between the built-in OAuth-file provider
  and the env-var fallback (BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV).
- Plugins installed but **not listed** are excluded for this project.
- An ID in the list that does not match any installed plugin is **skipped with a
  warning** (not an error) so a misconfigured ID doesn't break the whole chain.
- An **empty** `CredentialProviderPriority` (the default) includes all discovered
  plugins in global discovery order — identical to having no credential plugins at
  all from the operator's perspective.

### Fields

| Field | Type | Default | Description |
|---|---|---|---|
| `CredentialProviderPriority` | `string[]` | `[]` | Ordered list of credential plugin IDs to include in the credential chain for this project. |

### Example — vault first, AWS SSM fallback, no 1Password

```json
{
  "Id": "payments-service",
  "CredentialProviderPriority": ["myorg.vault-creds", "myorg.aws-ssm"]
}
```

The chain for `payments-service` is:
1. Built-in OAuth-file (Claude only)
2. `myorg.vault-creds` plugin
3. `myorg.aws-ssm` plugin
4. Built-in env-var (catch-all)

`myorg.1password`, even if installed and allowlisted globally, is never tried for
this project.

### Example — default behavior (all plugins in discovery order)

```json
{
  "Id": "simple-project",
  "CredentialProviderPriority": []
}
```

An empty list is the default. All discovered plugins are included in global
discovery order. If no plugin returns a credential, the chain falls through to
the built-in env-var provider.

> **Tip:** Operators who want env-var-only behaviour should not install credential
> plugins or should leave the global plugin allowlist empty. There is no
> configuration option that excludes all installed plugins while keeping the
> built-in providers.

See [`docs/credential-plugins.md`](credential-plugins.md) for the full plugin
author guide, chain-order rationale, and sample implementation.

---

## Plugging in a different project source

`IProjectRepository` is the single read surface. The default impl is
config-backed and immutable after startup. To support runtime
CRUD (e.g. to manage projects via a UI), implement `IProjectRepository`
on top of SQLite/Postgres and register it in DI. Nothing else changes;
the orchestrator only reads through the interface.

## Adding a language or audit-type preset

1. New project (or new file in `CodeyBox.Audit.Presets`) registering with
   `PresetCatalog.RegisterLanguage` or `RegisterAuditType`.
2. Return your bundle of `IAuditor`s with truthful capability flags.
3. Document the new preset in this file.

For one-off auditors that don't need a preset (project-specific build
checks, etc.), use a `Custom` entry in the project config — no code change.
