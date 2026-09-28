# CodeyBox: ShellCheck Shell Script Analyser

Auditor plugin wrapping [ShellCheck](https://www.shellcheck.net/) (shell
script static analysis) on the shared `ExternalToolAuditorBase`: it analyses
the audited repository with `shellcheck -f json1 -- <scripts...>` and reports
each diagnostic as an audit finding with the ShellCheck code (e.g. `SC2086`,
`SC2045`, `SC1046`) and `file:line` location. Shell analysis only.

## What it reports

- One finding per ShellCheck diagnostic. The title carries the rule code and
  the first line of the message (e.g. "SC2086: Double quote to prevent
  globbing and word splitting"); the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine` in repository-relative form.
- **Syntax errors** (`SC10xx`–`SC11xx`, e.g. a missing `fi`) are findings
  too — level `error`, so they fail the audit rather than vanishing as tool
  noise.
- **Gate behaviour: blocking on error-severity findings only.** ShellCheck
  assigns each diagnostic a level (`error`, `warning`, `info`, `style`);
  `error` maps to `Error` and fails the audit, `warning` maps to `Warning`,
  and `info`/`style` map to `Info` (advisory). `MinimumSeverity` is honored
  but only drops findings, never raises them. To gate harder, pass
  `--severity=warning` in `ExtraArguments` (suppresses info/style at the
  source) or select rules explicitly (see below).

## What it cannot see

- **Non-shell files and extensionless scripts.** Discovery covers `*.sh`,
  `*.bash`, `*.ksh`, `*.bsh` (case-insensitive). Extensionless executables
  with a shebang line, `*.zsh` (unsupported by ShellCheck), Docker
  `RUN` heredocs, and shell embedded in other files produce no findings
  unless listed explicitly in `Targets`. A repository with no checkable
  files is a clean pass (the tool's own zero-arguments diagnostic maps to
  zero findings), not an error.
- **Sourced files outside the target set.** The scan does not pass
  `-x`/`--external-sources`, so `source`d files outside the discovered or
  configured targets are not followed (ShellCheck still reports `SC1090`/
  `SC1091` notes it cannot follow them — `info` severity, advisory).
- **Optional checks (by default).** Checks ShellCheck ships disabled (see
  `shellcheck --list-optional`, e.g. `add-default-case`,
  `check-set-e-suppressed`) stay off. Enable them with `-o <check>` (or
  `-o all`) in `ExtraArguments`.
- **Suppressed diagnostics.** Inline `# shellcheck disable=SCxxxx`
  pragmas authored in the audited tree are always honoured — ShellCheck
  0.9.0 provides no flag to ignore them. The subject can silence individual
  lines; that silence is visible in the audited diff. (The repository
  *config file*, by contrast, is neutralised by default-off
  `TrustRepositoryConfig=false` → `--norc`; see below.)
- **The repository ruleset's blind spots.** Only the checks the effective
  configuration enables are run. A clean audit says nothing about checks
  the repo disabled in `.shellcheckrc` or pragma-suppressed line by line.
  Operators who want a fixed bar pass `--norc` (via
  `TrustRepositoryConfig=false`) plus `-e`/`-i` selections in
  `ExtraArguments`.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output. The parser additionally caps a single report at 10 000 comments,
  mirroring the shared SARIF parser bound.

## Exit codes and failure classification

ShellCheck's convention (verified empirically against 0.9.0 — **not**
assumed from the common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scanned clean (empty json1 `comments`) | Verdict (pass) |
| `1` | Scanned, diagnostics found (json1 report on stdout) | Verdict (`Passed = false` iff an error-severity finding is present) |
| `2` | File errors (unreadable inputs — stdout still carries an empty report, so the exit code, not the report, is the discriminator) | Infrastructure (`AuditUnavailableException`) |
| `3` with `No files specified.` on stderr | Invoked with zero file arguments: discovery found no shell scripts and no `Targets` configured | Verdict (clean pass, zero findings) |
| `3` otherwise | Usage errors: bad flags (including an operator `--format` replacing the json1 report the parser expects), unreadable config | Infrastructure — the JSON parser fails closed on the empty stdout |
| `4` | Unsupported `-f` format selection (empty stdout) | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `shellcheck` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **ShellCheck `0.9.0`** (`ExpectedVersion` in scoped
config) — the release its exit codes, severities, and report shape were
verified against. The auditor probes `shellcheck --version` before every run
and reports an infrastructure failure on any other version.

The tool requirement is declared **with `AptPackage = "shellcheck"`**, so
baseline provisioning installs it automatically **only when this plugin is
enabled** (the host constructs the `apt-get install` itself; the plugin
supplies only the package name):

```
apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y shellcheck
```

On baselines whose distro `shellcheck` differs from `0.9.0`, set
`ExpectedVersion` in scoped config to the provisioned release.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs and verifies `shellcheck` only in
that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.shellcheck"],
      "Enabled": ["codeybox.shellcheck"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.shellcheck" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.shellcheck`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.9.0` | Pinned shellcheck release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Targets` | — (discovery) | Comma-separated repository-relative shell script paths (positional args). Unset → `find` discovery (see below). Set → discovery is skipped. Leading-dash entries fail closed (they would be option-parsed as flags). |
| `TrustRepositoryConfig` | `true` | When `true` (default) the repo's `.shellcheckrc` applies and its load is announced in the audit log. When `false`, the scan passes `--norc` and runs under shellcheck defaults. Ignored when `ExtraArguments` already supplies `--norc`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Only drops, never raises. |
| `IncludedRules` / `ExcludedRules` | — | Exact ShellCheck codes to keep/drop (e.g. `SC2086`, `SC2045`). Filters reported findings; use `-i`/`-e` in `ExtraArguments` to filter at the source instead. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; shellcheck echoes targets as passed (repository-relative), so these prefixes match. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `-S <severity>`, `-i`/`-e <codes>`, `-o <optional-check>`, `-s <shell>`, or `--norc`. A repeated `-f`/`--format` replaces the json1 report the parser expects and breaks the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound (discovery probes share it under a 30 s cap). Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is half-open by design.** The
configuration *file* (`.shellcheckrc`) is honoured by default because the
project's own lint contract is the meaningful check — changes to it are
visible in the audited diff — and `TrustRepositoryConfig=false` neutralises
it via `--norc` (shellcheck 0.9.0 offers no `--config`/`--rcfile` flag for
an operator-owned file). Inline `# shellcheck disable=` pragmas, however,
cannot be neutralised by any flag and are honoured unconditionally; expect
findings to be absent exactly where the repo asked for silence, and review
those pragmas as part of the diff. The auditor runs under
`AuditCapabilities.None` (no agent credentials, no network).

## Default scope

`find` discovery of `*.sh`/`*.bash`/`*.ksh`/`*.bsh` (case-insensitive)
pruning `.git`, `vendor`, `third_party`, `node_modules`, `dist`, `build`,
`out`, `coverage` at any depth, plus the repository's own declaration of
scope via `Targets` — that is the project's own set of checkable scripts.
On top of that, the finding-level `ExcludePaths` backstop lists vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`) prefixes: violations there belong to
upstream packages or build output, not the change under audit — reporting
them produces noise that trains operators to ignore the auditor. Because
shellcheck echoes target paths as passed (repository-relative, `./`-prefix
stripped for locations), the backstop matches, unlike tools that report
absolute URIs. Re-include a path by overriding `ExcludePaths`; cover
extensionless scripts by listing them in `Targets` (at most 200 discovered
targets — beyond that the run fails closed directing the operator to
`Targets`, rather than scanning a silent subset).
