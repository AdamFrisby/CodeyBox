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
* **Per-project audit policy.** A Python service might require ruff,
  mypy/pyright, pytest, and the architecture LLM review. A Rust binary
  might want rustfmt, clippy, cargo test, and the cheating-detector.
  Both run side-by-side without config interference.

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
          "Languages": ["node"],
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

### `MaxPriority`

Set `MaxPriority` to cap the highest work-item priority accepted for this
project. The global API bound of `[-1000, 1000]` still applies; leaving
`MaxPriority` unset means there is no additional project-level cap. Negative
priorities remain allowed so callers can always lower a work item's priority.

### Inheritance from `Defaults`

Anything a project omits comes from `Defaults` (shallow merge):

* `Agent` — default agent for new work items
* `BaseBranch` — default integration branch
* `Audit.*` — every field in `ProjectAudit` falls through individually

`Languages` is list-typed and not append-merged: if a project sets it, it
replaces the defaults entirely. `AuditTypes` supports the existing list form,
or an object form where keys select audit types and values tune their prompts:

```json
{
  "AuditTypes": {
    "security": {
      "ReviewFocus": "- Project-specific auth and tenant-boundary checks"
    },
    "completeness": {
      "ReviewFocus": "- Product acceptance criteria from the work item"
    }
  },
  "LlmPromptFrameTemplate": "{{reviewFocus}}\n{{resultFile}}"
}
```

See `docs/audit-types.md` for prompt schemas and precedence.

### Project ID rules

* ASCII alphanumeric + `-` and `_`
* 1–64 characters
* Used in REST URLs and the SQLite `project_id` column

## Audit configuration

```json
"Audit": {
  "Profile": "default",
  "MaxIterations": 3,
  "FailingSeverity": "Error",
  "PerIterationTimeoutMinutes": 10,
  "StopOnFirstFailure": false,
  "MaxLlmAuditorParallelism": 3,
  "Languages": ["python", "node"],
  "AuditTypes": ["security", "architecture", "quality", "completeness", "cheating"],
  "MechanicalFixers": ["dotnet-format"],
  "Profiles": {
    "uat": {
      "MaxIterations": 5,
      "Languages": ["csharp"],
      "AuditTypes": ["security", "cheating"],
      "ExcludedAuditors": ["cheating:llm-review"]
    }
  },
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

Mechanical fixers are composed separately from auditors. They run after work
and after each rework, before the next audit iteration. A fixer may mutate the
work tree, but it does not call an LLM and does not consume an audit iteration.
When it changes files, CodeyBox commits the delta as a mechanical commit with
normal prompt-revision trailers before auditors inspect the tree.

| Field | Type | Default | Description |
|---|---|---|---|
| `Profile` | string | `"default"` | Project-default named audit profile. `"default"` uses the top-level `Audit` fields. |
| `Profiles` | object | built-ins include `uat` | Named audit bundles. A profile can set the same fields as `Audit`, plus `ExcludedAuditors` for exact auditor-name removals after preset expansion. |
| `MaxIterations` | int | `3` | How many audit + rework cycles to attempt before giving up with `AuditFailed` |
| `FailingSeverity` | string | `"Error"` | Findings at or above this severity block the merge. `"Warning"` or `"Info"` can be used to widen the gate. |
| `PerIterationTimeoutMinutes` | int | `10` | Wall-clock cap on a single audit iteration's sandbox |
| `StopOnFirstFailure` | bool | `false` | Stop running auditors as soon as one returns a blocking finding — useful when cheap linters precede expensive LLM auditors |
| `MaxLlmAuditorParallelism` | int | `3` | Max LLM auditors one item may try to run concurrently. Default `3` means `security:llm-review`, `completeness:llm-review`, and `cheating:llm-review` all run at the same time, subject to the process-wide `CodeyBox:WorkerPool:MaxConcurrentSandboxes` ceiling. Set to `1` to serialize them if you hit API 429 rate-limit errors. Tool auditors are unaffected and always run sequentially. |
| `MechanicalFixers` | string[] | derived from `Languages` | Deterministic no-model normalizers to run before audit. Omit to inherit or derive defaults; `["dotnet-format"]` is derived when C# language audit is enabled. Set `[]` to disable. |

Declared short-circuit gates are controlled globally with
`CodeyBox:PipelineTuning:AuditShortCircuitEnabled` (default `true`,
hot-reloadable). The built-in `csharp:build-WaE` and `csharp:test-pass`
auditors run before LLM reviewers and skip the LLM panel when they produce a
blocking result. With the default short-circuit toggle enabled, those same
blocking C# gates also skip later tool auditors for that iteration. If
declared short-circuit routing is disabled, or the blocking build/test gate does
not opt into short-circuiting, non-LLM tool auditors continue to run when
`StopOnFirstFailure=false`; the LLM panel remains gated either way.

The built-in `uat` profile is intended for UAT/test-generation work. It keeps
C# format/build/test checks, gitleaks, semgrep, security LLM review, and the
deterministic cheating patterns, while omitting the completeness and cheating
LLM reviewers that tend to over-block on UAT list shape.

### Languages (built-in presets)

| Preset       | Tools                                                              |
|--------------|--------------------------------------------------------------------|
| `csharp` | `dotnet format --verify-no-changes`, `dotnet build --no-incremental /warnaserror`, `dotnet test --no-build` |
| `python` | `ruff format --check .`, `mypy .` or `pyright --workdir .`, `pytest` |
| `node` | `prettier --check .`, `eslint .`, `npm test` |
| `go` | `gofmt -l .` (non-empty output fails), `go vet ./...`, `go test ./...` |
| `rust` | `cargo fmt --check`, `cargo clippy -- -D warnings`, `cargo test` |

Allowed built-in language values are `csharp`, `python`, `node`, `go`, and
`rust`. Unknown strings are logged at startup and skipped. If `Languages` is
omitted and no default is configured, CodeyBox uses an empty language list and
runs no language-specific PR auditors; language-agnostic audit types and
custom auditors still work. Release dependency CVE scans preserve the legacy
default: omitted `Languages` runs the C# scanner, while an explicit empty list
(`[]`) runs no language-specific dependency scanners.

Each language preset recursively checks for that language's marker files before running:
`*.csproj`/`*.sln`/`*.slnx` for C#, `pyproject.toml`/`setup.py`/`setup.cfg`/
`requirements.txt` for Python, `package.json` for Node, `go.mod` for Go, and
`Cargo.toml` for Rust. In side-by-side repositories, the tools run from the
matched project directories. If a language is enabled but its markers are
absent, BuildTestGate language auditors emit an Error finding and block the
LLM panel because the configured build/test evidence was not verified.

All language presets are tool-only (no agent credentials). A buggy linter
cannot exfiltrate the agent's API key — the audit phase runs them in a
credential-free sandbox.

For C# projects, the built-in `dotnet-format` mechanical fixer reuses the
active `csharp:format-check` auditor command, removing only read-only flags
such as `--verify-no-changes`. This keeps the fixer on the same SDK, config,
working-directory discovery, and sandbox baseline as the auditor, so
`csharp:format-check` becomes a defense-in-depth assertion rather than a
source of format-only rework loops.

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

Tool auditors (`security:gitleaks`, `python:test-pass`, `csharp:build-WaE`,
`process:build-script`, etc.) are never affected by these settings — they do
not invoke an LLM.

### Build-script audit gate

`process:build-script` is always available as a credential-free tool auditor.
It looks for a repo-root `build.sh` on the work branch. When the script is
absent, the default behavior is skip-if-absent so existing projects are
unchanged. When present, CodeyBox runs `./build.sh` in the audit-tool sandbox
with the configured timeout; exit `0` passes and ordinary non-zero build exits
become blocking `build failed` findings with stdout/stderr captured.

Set `Audit.BuildScriptRequired=true` for projects that must provide the script:

```json
{
  "Projects": [
    {
      "Id": "my-app",
      "Audit": {
        "BuildScriptRequired": true
      }
    }
  ]
}
```

`build.sh` execution failures are distinct from build failures. If the script
cannot execute, exits `126`/`127`, or times out, the item fails/defer-surfaces as
`could-not-verify` infrastructure rather than a source-code audit finding.

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

### Graphical sandboxes

Set `GraphicalSandbox: true` on a project to run GUI-capable sandboxes for
the phases that need desktop plumbing:

```json
{
  "Id": "desktop-app",
  "RepositoryUrl": "https://example.com/desktop-app.git",
  "GraphicalSandbox": true
}
```

When enabled, work and rework use the graphical sandbox flavor and the
conventional `graphical` network profile. Audit sandboxes use the graphical
flavor when an auditor declares `AuditCapabilities.Graphical`; ordinary tool
auditors keep their configured `auditTool` profile. The Multipass provider bakes
`cb-baseline-graphical`, starts an XFCE desktop with VNC bound to the VM's
profile-bridge address, and exposes screenshot/input APIs through sandbox exec.
Configure
`SandboxNetworkProfiles.graphical = cb-graphical`, add the matching
`graphical cb-graphical ...` line to `/etc/codeybox/networks.conf`, and run
`codeybox-vnc-loopback <multipass-vm-name> 5901` when an operator needs a
localhost VNC view.

### Custom auditors

Four kinds, all configured in JSON:

| Kind           | Required fields           | Notes                                              |
|----------------|---------------------------|----------------------------------------------------|
| `shell`        | `Name`, `Argv`            | Exit 0 = pass; non-zero = Error finding            |
| `diff-pattern` | `Name`, `Patterns[]`      | Regex against added lines in diff                  |
| `llm`          | `Name`, `ReviewFocus`     | LLM review with the project's agent                |
| `plugin`       | `PluginId`                | Delegates to a loaded third-party `IAuditor` plugin |

#### Plugin auditors

Plugin auditors let operators ship custom audit logic as standalone NuGet packages
without modifying CodeyBox core. To enable one, the plugin DLL must be discovered
by the host (see `CodeyBox:Plugins` configuration), and its ID must be in the
allowlist.

```json
"Audit": {
  "Custom": [
    { "Kind": "plugin", "PluginId": "myorg.no-var-keyword" },
    { "Kind": "plugin", "PluginId": "myorg.xml-doc-required" }
  ]
}
```

`PluginId` must match the `id` declared in the plugin's `[CodeyBoxPlugin]`
attribute. If the plugin is not loaded, the composer logs a warning and skips
the entry — other auditors continue normally. No `Name` field is required; the
plugin's own `IAuditor.Name` is used in findings and logs.

See [`docs/auditor-plugins.md`](auditor-plugins.md) for the full authoring guide,
project skeleton, and sample plugin.

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

#### `Kind=noop` + local `RepositoryUrl`: rejected by default

`Upstream.Kind=noop` combined with a `RepositoryUrl` that points at a
local filesystem path (`file://...` or an absolute path like
`/home/me/.codeybox/seeds/foo.git`) is **refused at startup**. Without a
real upstream, every work item forks from the same seed and has nowhere
to merge back to — operators see a parade of independent rewrites
instead of iterative progress, because no Done item ever feeds the next
one's base. In practice this has consumed tens of agent-hours per
misconfigured project before the operator noticed.

If you actually want sandbox-style isolation (each work item starts
from scratch, results aren't intended to compose), opt in explicitly:

```json
"Upstream": {
  "Kind": "noop",
  "AcknowledgeSandboxIsolation": true
}
```

Otherwise, configure a real upstream so merged work flows back to a
shared remote:

```json
"Upstream": {
  "Kind": "github",
  "GitHubOwner": "me",
  "GitHubRepository": "my-app",
  "TokenEnvVar": "MY_APP_GITHUB_TOKEN"
}
```

The validator runs on first load and on every `appsettings.json` reload;
a reload that introduces the dangerous combination is rejected and the
prior snapshot is retained (the rejection is logged at ERROR).

### Plugin upstreams and PluginConfig

When `Kind` is not a built-in (`noop`, `github`, `git-generic`), the orchestrator
looks for a plugin-registered `IUpstreamRemote` whose `Name` matches. Plugins read
their per-project settings from `Upstream.PluginConfig`:

```json
"Upstream": {
  "Kind": "gitea",
  "TokenEnvVar": "MY_GITEA_TOKEN",
  "PluginConfig": {
    "BaseUrl": "https://git.mycompany.example/api/v1",
    "Owner": "myteam",
    "Repository": "myproject"
  }
}
```

The keys inside `PluginConfig` are plugin-defined. Check the plugin's documentation
for which keys it reads. The orchestrator passes this dictionary to the plugin via
`IPluginHost.GetProjectUpstreamConfig(projectId)` — plugins must not rely on any
other per-project state injection mechanism.

**Token security**: tokens must **never** go into `PluginConfig`. Always use
`TokenEnvVar` to name the environment variable holding the token; the plugin reads it
with `Environment.GetEnvironmentVariable(...)`.

See [`docs/upstream-plugins.md`](upstream-plugins.md) for how to author an upstream
remote plugin.

### GitHub upstream: pull request flow

When `Kind=github`, phase 4 now pushes the **work branch** to GitHub
and opens a pull request (workBranch → baseBranch) rather than pushing
the merged base branch directly. This leaves a PR and code-review trail
on GitHub even for fully-automated merges.

Four additional options control the behaviour:

| Option | Type | Default | Description |
|---|---|---|---|
| `MergeMethod` | `"merge"` \| `"squash"` \| `"rebase"` | `"merge"` | Merge strategy used when `AutoMerge=true`. |
| `AutoMerge` | bool | `false` | When `true`, merges the PR via the GitHub API immediately after opening it. When `false`, the PR is left open for human review. Either way the work item transitions to `Done`. |
| `PullRequestTitleTemplate` | string? | — | Template for the PR title. Supports `{title}` (work item title) and `{branch}` (work branch name) placeholders. Defaults to the work item title. |
| `PreMergeVerifyArgv` | string[]? | `[]` | Pre-merge CI gate. When non-empty AND `AutoMerge=true`, the post-local-merge tree is checked out into a worktree and this argv is run against it before the auto-merge API call. A non-zero exit parks the work item at `MergeConflictResolutionFailed` with `pre-merge verify: rebased build failed:` on `LastError`. Empty (the default) skips the gate. The default host registers `LocalGitPreMergeVerifier`; alternative implementations can be plugged in by re-registering `IPreMergeVerifier`. |

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

**Pre-merge CI gate.** GitHub's `mergeable == true` flag only checks for
textual conflicts; it does not catch the case where a clean merge against
a freshly-moved `baseBranch` still breaks the build (a renamed helper,
a drifted constant) or fails previously-green tests. The default
`LocalGitPreMergeVerifier` (registered automatically when the API host
boots) reacts by materialising the merge commit into a temporary worktree
on the host bare repo and running the project's configured
`PreMergeVerifyArgv` against that tree. A non-zero exit parks the work
item with `LastError` prefixed by `pre-merge verify: rebased build failed:`
so operators can tell it apart from race recovery and LLM-merger failure
modes. (Alternative `IPreMergeVerifier` implementations may also return
the `rebase failed:` prefix when they re-fetch + rebase against current
upstream and find a textual conflict that the local merge phase did not
hit.) The gate is opt-in: leaving `PreMergeVerifyArgv` empty skips it
entirely, even though a verifier is registered.

The CI-layer counterpart of this gate lives at
`.github/workflows/pre-merge-revalidate.yml`. After every push to `main`,
it enumerates open PRs and re-runs build + tests against the rebased tree,
posting `codeybox/pre-merge-revalidate` as a commit status on the PR head.
Operators can require that status in branch protection rules so the
auto-merger respects the rebased outcome even when the in-process gate is
unavailable.

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

The admin dashboard shows this as colour-coded usage bars per project.

---

## Monthly cost budget

Set `Budget.MonthlyCostBudgetUsd > 0` to enable spend tracking and automatic alerts over a rolling 30-day window. See [budget-alerts.md](budget-alerts.md) for the full description.

```json
{
  "Budget": {
    "MaxItemsPerHour": 10,
    "MaxItemsPerDay": 50,
    "MaxConcurrentForProject": 2,

    "MonthlyCostBudgetUsd": 500.00,
    "CostWarningThresholdPct": 80,
    "CostHardCapPct": 100,
    "AutoResumeOnRecovery": false
  }
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `MonthlyCostBudgetUsd` | `decimal` | `0` | Max USD spend in rolling 30-day window. 0 = unlimited. |
| `CostWarningThresholdPct` | `int` | `80` | Webhook warning threshold percentage. 0 = disabled. |
| `CostHardCapPct` | `int` | `100` | Auto-pause + webhook threshold percentage. 0 = no auto-pause. |
| `AutoResumeOnRecovery` | `bool` | `false` | Auto-resume when spend drops back below `CostWarningThresholdPct`. |

A project with `MonthlyCostBudgetUsd = 0` (the default) ignores all threshold configuration — no alerts, no auto-pause.
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

Use repository-owned YAML under `codeybox/languages` or
`codeybox/audit-types`, or per-project appsettings overrides, for project-specific preset data. Built-in defaults
live in `CodeyBox.Audit.Presets/Defaults` as embedded resources. Selected preset ids are validated against the composed catalog at startup with did-you-mean diagnostics for typos. See
[`languages.md`](languages.md) and [`audit-types.md`](audit-types.md).

For one-off auditors that don't need a preset (project-specific build
checks, etc.), use a `Custom` entry in the project config — no code change.

---

## Release management

Releases group work items that target a shared release branch instead of
`main`. The feature is **opt-in** via `release.enabled = true`. See
[`releases.md`](releases.md) for a complete description.

```yaml
projects:
  - id: my-app
    release:
      enabled: true
      branchNameTemplate: "release/{name}"   # {name} → release.name value
      autoSyncMainIntervalMinutes: 720        # 0 = disabled
      deepAuditors:
        - owasp-asvs
        - arch-coherence
        - deps-cve-scan
      deepAuditMaxIterations: 5
```

**`branchNameTemplate`** — template for the release branch name. `{name}` is
replaced with the release's `name` field (e.g. `"v1.4.0"` →
`"release/v1.4.0"`). Default: `"release/{name}"`.

**`autoSyncMainIntervalMinutes`** — how often (in minutes) the
`ReleaseMainSyncService` background service merges `main` into each open
release branch. Set to `0` to disable. Default: `720` (12 h).

**`deepAuditors`** — list of auditor names to run during the `in_review`
phase. Built-in values: `owasp-asvs`, `arch-coherence`, `deps-cve-scan`.
`deps-cve-scan` dispatches by `Audit.Languages`: C# uses `dotnet list package
--vulnerable --include-transitive`, Python uses `pip-audit` or `safety`, Node
uses `npm audit --json` pinned to `https://registry.npmjs.org/` with
repository npm proxy settings blocked, Go uses `govulncheck -json ./...`, and
Rust uses `cargo audit`.
For backward compatibility, omitted `Audit.Languages` runs the C# dependency
scanner; an explicit empty list runs none.
Empty list = skip deep audit (transition directly to `released`).

**`deepAuditMaxIterations`** — maximum number of deep audit iterations before
transitioning to `failed`. Default: `5`.
