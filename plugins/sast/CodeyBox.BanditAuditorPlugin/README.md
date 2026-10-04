# CodeyBox: Bandit Python SAST Auditor

Auditor plugin wrapping [Bandit](https://bandit.readthedocs.io/) (`bandit`):
it runs Bandit's Python AST security checks over the audited repository
(`bandit -r . -f sarif`), reporting each issue as an audit finding with the
Bandit test id (e.g. `B602` for `subprocess_popen_with_shell_equals_true`)
and `file:line` location.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, invalid configuration, usage
error) is infrastructure — never a pass.

## What it reports

- One finding per Bandit issue. The title carries the test id and the
  first line of the issue text; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root (the scan
  runs with the worktree as its target root, so report URIs stay
  repo-relative).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Bandit severities go through a declared map, never raw:
  `HIGH` (plus SARIF `error`) → `Error` (fails the audit); `MEDIUM` (plus
  SARIF `warning`) → `Warning` (advisory); `LOW` (plus SARIF `note`/
  `none`) → `Info` (informational); anything unrecognised → `Warning`.
  `MinimumSeverity` can only drop findings, it never raises them. The
  auditor is therefore a merge gate for high-severity Python security
  issues, not a blocker on every low hint.
- SARIF results embed code snippets around each location, so findings and
  raw output can contain the reported source context. That is the point of
  a SAST finding; treat audit reports accordingly.
- Each SARIF result also carries `issue_severity` / `issue_confidence`
  properties for operator context; the verdict follows the result `level`
  through the declared map.

## What it cannot see

- **Anything outside Python.** Bandit builds a Python AST per file and
  runs its plugins against AST nodes. Files it cannot parse are skipped;
  repositories with no Python code trivially pass — scope this auditor to
  Python projects.
- **Anything the default test set does not cover.** Bandit is a catalog of
  known-dangerous patterns (exec, pickle, weak crypto, injection sinks),
  not a dataflow engine: it does not track taint across files, and tests
  outside the default set run only when selected with `-t`/`-p` via
  `ExtraArguments` or an operator config file.
- **Suppressed issues, by default.** The scan passes `--ignore-nosec`, so
  `# nosec` comments authored in the audited repository are inert.
  Operators who deliberately trust repo-authored suppression set
  `TrustRepositorySuppression` in scoped config.
- **Repository project files fail closed, they are not honored blindly.**
  Bandit auto-discovers a project `.bandit` INI file by walking the
  recursive scan targets — a file the audit subject controls, whose
  `exclude`/`tests`/`skips` options can drop paths and checks. A present
  `.bandit` anywhere in the audited tree fails the run closed as
  infrastructure. Remove the file, or set `TrustRepositorySuppression` to
  trust it. A repository `bandit.yaml`, `pyproject.toml` `[tool.bandit]`
  section, or legacy `/etc/bandit/bandit.yaml` is inert unless the
  operator names it with `-c` — Bandit loads YAML/TOML configuration only
  from the explicit flag — so those need no presence gate. An
  operator-owned file passed with `-c`/`--configfile`, `--ini`, or
  `-b`/`--baseline` via `ExtraArguments` is canonicalized in the sandbox
  and rejected when it resolves inside the audited worktree.
- **The exit-code override is rejected, not just discouraged.** The
  auditor relies on the default exit convention (exit `1` means issues
  were found, exit `2` means the scan could not run). `--exit-zero` in
  `ExtraArguments` is rejected deterministically: findings would otherwise
  masquerade as a clean verdict instead of being reported. `-f`/`--format`
  and `-o`/`--output` are rejected for the same reason — they would
  replace or divert the SARIF report the parser expects.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  Bandit still walks those files (minus its own built-in excludes for
  version-control, `__pycache__`, `.tox`, and egg metadata), but findings
  under `vendor/`, `third_party/`, `node_modules/` are dropped. Override
  `ExcludePaths` to re-include them.

## Exit codes and failure classification

Bandit's convention (verified against `bandit/cli/main.py` — do not assume
the gitleaks/eslint convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed, no issues above the filters | Verdict (pass when the SARIF document is empty) |
| `1` | Scan completed, issues found | Verdict (`Passed = false` when any finding maps to `Error`) |
| `2` | Could not run: invalid config, no targets, empty test profile, unknown profile, unreadable baseline, baseline with a non-baseline formatter, multiple `.bandit` files, or an argument rejection | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

Only `0` and `1` are findings-producing. A missing `bandit` is always an
infrastructure failure naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **Bandit `1.9.4`** (`ExpectedVersion` in scoped
config). A scanner's checks change between releases, so an unpinned tool
would change findings under you: the auditor probes `bandit --version`
before every run and reports an infrastructure failure on any other
version.

The tool requirement is declared **verify-only** — no `AptPackage`:
Bandit ships as a Python package, not a distro package, and only a pinned
`pip`/`pipx` install carries the version this auditor was verified
against. Provision the pinned release in your sandbox baseline:

```sh
# baseline bake step (needs Python 3.10+ on the image)
BANDIT_VERSION=1.9.4
pipx install "bandit==${BANDIT_VERSION}"    # or: pip install "bandit==${BANDIT_VERSION}"
bandit --version                            # must print bandit 1.9.4
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.bandit"],
      "Enabled": ["codeybox.bandit"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.bandit" }
```

The `bandit` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.bandit`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.9.4` | Pinned Bandit release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, omits `--ignore-nosec` so repo-authored `# nosec` comments suppress findings, and lifts the fail-closed gate on a repo-authored `.bandit` project file. Default keeps the audit subject from silencing the scan. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact Bandit test ids to keep/drop (e.g. `B602`). Output filters; they do not change which tests run. To select tests at scan time (e.g. `-t B602`, `-s B101`, `-p ShellInjection`), use `ExtraArguments`. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `bandit` after the built-in args (never via a shell). `--exit-zero` (findings would masquerade as a clean verdict) and `-f`/`--format`/`-o`/`--output` (would replace or divert the SARIF report the parser expects) are rejected deterministically. `-c`/`--configfile`, `--ini`, and `-b`/`--baseline` values are canonicalized in the sandbox and rejected when they resolve inside the audited worktree. |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. A Python-tree scan is normally seconds; raise it only for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: issues reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore
the auditor. The exclusion is a finding filter layered on Bandit's own
built-in excludes (version-control directories, `__pycache__`, `.tox`,
egg metadata). Re-include them by overriding `ExcludePaths`.
