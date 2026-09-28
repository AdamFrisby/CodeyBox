# CodeyBox: Hadolint Dockerfile Auditor

Auditor plugin wrapping [hadolint](https://github.com/hadolint/hadolint):
it lints every Dockerfile in the audited repository and reports each
violation as an audit finding with its rule id (`DLxxxx`/`SCxxxx`) and
file/line location.

## What it reports

- One finding per hadolint violation, carrying the tool's rule id
  (`DL3006`, `DL3008`, `SC2086`, …), the rule message, and the file path
  plus 1-based line from the SARIF report. Dockerfile parse errors surface
  under rule id `DL1000`.
- **Severity: blocking on error only.** hadolint assigns each rule a
  severity (`error`, `warning`, `info`, `style`); its SARIF emitter folds
  `info` and `style` into SARIF `note`. The declared mapping sends SARIF
  `error` → CodeyBox `error`, `warning` → `warning`, `note` → `info`, so a
  parse error or an error-escalated rule fails the audit while style and
  best-practice notes stay advisory. Raw tool levels never reach findings.

## What it cannot see

- **Files it was not asked to lint.** Discovery matches
  `Dockerfile*`, `*.dockerfile`, `Containerfile*`, `*.containerfile`
  (case-insensitive). A Dockerfile under any other name (e.g.
  `docker/web.prod`) is out of scope unless the operator names it in
  `Targets`.
- **Pruned directories.** Discovery skips directories named `.git`,
  `vendor`, `third_party`, `node_modules`, `dist`, `build`, `out` or
  `coverage` at any depth, and findings under the default `ExcludePaths`
  prefixes are dropped post-scan. Re-include via `Targets` plus an
  `ExcludePaths` override.
- **Suppressed violations.** Inline `# hadolint ignore=RULE` pragmas (and
  `# hadolint global ignore=…`) always apply unless disabled through tool
  configuration — a repo can silence rules line-by-line. The audit reports
  what the tool reports; a warnings-clean run that disagrees with the diff
  is a signal to inspect suppression comments.
- **Semantic correctness.** hadolint checks Dockerfile best practices and
  shell hygiene (via embedded ShellCheck) — not whether the image builds,
  the pinned versions exist, or the runtime is minimal.
- **More than 200 discovered Dockerfiles.** Discovery beyond the bound
  fails closed (infrastructure) instead of scanning a silent subset; scope
  the scan with `Targets`.
- **Dash-leading filenames as flags.** Every scan target is passed as one
  argv entry with a `./` prefix, so a repository-controlled file such as
  `--config=x.dockerfile` is parsed as a path, never as a hadolint flag;
  a `Targets` entry with a leading dash fails closed instead of scanning
  under foreign flags.

## Exit codes and failure classification

Verified empirically against hadolint 2.15.1 (not assumed — hadolint does
**not** follow the common "1 = findings, 2 = could not run" convention):

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF report | ran clean (or findings below the failure threshold — the report still carries them) | pass / findings from the report |
| `1` | SARIF report | ran, violations at/above the threshold | findings |
| `1` | `Please provide a Dockerfile`, no SARIF | zero file arguments — discovery found no Dockerfiles and no `Targets` | clean pass (nothing checkable) |
| `1` | none / not SARIF | could not run (bad flags → usage error on stderr; unreadable file → Haskell exception with backtrace on stderr) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `hadolint` binary, a version mismatch, a timeout, a failed
Dockerfile-discovery probe, and unparseable output are likewise
infrastructure failures naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **hadolint `2.15.1`** (`ExpectedVersion` in
scoped config): the rule set changes between releases, so an unpinned
binary would change findings under you. `hadolint --version` is probed
before every run; a missing binary, an unrecognised version string, or any
other version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries a version pin. Provision the pinned upstream
release into the sandbox baseline via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
HADOLINT_VERSION=2.15.1
curl -fsSL "https://github.com/hadolint/hadolint/releases/download/v${HADOLINT_VERSION}/hadolint-Linux-x86_64" -o /usr/local/bin/hadolint
chmod +x /usr/local/bin/hadolint
hadolint --version   # must print the pinned version
```

## Repository configuration

hadolint auto-discovers `$PWD/.hadolint.yaml` (`.yml`) when no `--config`
is passed. That file can ignore rules or re-severity them, so it is
executable gate configuration owned by the audit subject:

- Default (`TrustRepositoryConfig=true`): the repo file applies — linting
  against the project's own lint contract is the meaningful check — and
  the load is logged at info level.
- `ConfigPath`: an operator-owned config file passed as `--config`,
  replacing repository discovery entirely. It should live outside the
  audited tree.
- `TrustRepositoryConfig=false` with no `ConfigPath`: the scan runs under
  hadolint's defaults via `--config /dev/null`, neutralising repository
  discovery.

Inline `# hadolint ignore=…` pragmas apply in all modes unless disabled
through tool configuration.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.hadolint"],
      "Enabled": ["codeybox.hadolint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.hadolint" }
```

Only then does the declared `hadolint` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.hadolint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.15.1` | Pinned hadolint release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Targets` | — (discovery) | Comma-separated repository-relative Dockerfile paths scanned positionally. Unset → fixed `find` discovery (see above). Set → discovery skipped; entries are validated (no `..`, no leading dash — a leading-dash positional would be option-parsed as a flag) and passed with a `./` prefix that keeps them positional. |
| `ConfigPath` | — | Operator-owned hadolint config file, passed as `--config` (replaces repository discovery). Should live outside the audited tree. |
| `TrustRepositoryConfig` | `true` | Honour the repo's `.hadolint.yaml`/`.hadolint.yml`. `false` with no `ConfigPath` runs under hadolint defaults via `--config /dev/null`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. `warning` keeps errors and warnings; `error` keeps only blocking findings. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (`DL3006`, `SC2086`, `DL1000`, …). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args and targets (never via a shell), e.g. `--ignore,DL3006` or `--failure-threshold,error`. A `--config` here wins over `ConfigPath`/`TrustRepositoryConfig`. Do not pass `-f`/`--format` or `-o`/`--output` — they replace the SARIF report the parser reads and fail the run closed as infrastructure. |
| `TimeoutSeconds` | `300` | Per-run bound (discovery is capped at 30s within it); exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

Whole-tree Dockerfile discovery (see above): every
`Dockerfile*`/`*.dockerfile`/`Containerfile*` outside pruned noise
directories is linted, and findings under `vendor/`, `third_party/`,
`node_modules/`, `dist/`, `build/`, `out/`, `coverage/` are dropped by
default — violations shipped inside a dependency or generated output
describe upstream packages or build artefacts, not the change under audit,
and reporting them would train operators to ignore the auditor. An
operator who wants a narrower (or wider, oddly-named) scan sets `Targets`
or overrides `ExcludePaths`.
