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

## REST API

```
GET  /projects             — list all configured projects
GET  /projects/{id}        — single project
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
