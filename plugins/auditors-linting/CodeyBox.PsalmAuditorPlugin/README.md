# CodeyBox: Psalm PHP Static Analysis Auditor

Auditor plugin wrapping [Psalm](https://psalm.dev): it runs
`psalm --output-format json --no-cache` on the audited repository and reports
each diagnostic as an audit finding with the Psalm issue type (e.g.
`UndefinedFunction`, `MixedReturnStatement`) and `file:line` location.

## What it reports

- One finding per Psalm issue: title carries the issue type (when Psalm
  supplies one) plus the first message line; the description carries the
  tool, issue type, location, and the full message. `Location` is
  `path:line` — absolute `file_name` spellings are relativized against the
  exec working directory; the usual relative spellings pass through.
- **Gate behaviour: severity-driven — not blocking on every finding.**
  Psalm reports `error` and `info` severities. Error-level issues map to
  `Error` and fail the audit; informational issues map to `Info` and are
  advisory. Which checks run — and at which severity — comes from the level
  in force: the repo's `psalm.xml` by default (`errorLevel` plus
  `issueHandlers`), or `ConfigPath` set by the operator. `MinimumSeverity`
  can only drop findings, so the intended way to harden the gate is the
  level/ruleset itself.

## What it cannot see

- **Analysis that needs the project's dependencies.** Psalm resolves the
  project's `vendor/autoload.php` when present. Without installed
  dependencies, symbols from third-party packages resolve as unknown — the
  analysis is degraded, not clean. Provision `composer install` into the
  sandbox baseline for full coverage.
- **Runtime-dynamic constructs.** Psalm is static analysis: `__call` /
  `__get` magic, dynamic dispatch, `eval`, and value-dependent types are
  invisible to it.
- **Repository-controlled suppression.** Psalm honors `@psalm-suppress`
  annotations, `issueHandlers` / `excludeFiles` in its config file, and
  `psalm-baseline.xml` — all authored inside the audited repository — and
  offers no flag to make them inert. The subject can therefore weaken or
  silence checks; this is a documented limitation, not a gate. Operators who
  need an operator-owned ruleset pin one outside the repository via
  `ConfigPath` (or `--config` in `ExtraArguments`) — inline suppressions
  and baselines remain honored even then.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  Psalm still analyses those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`
  are dropped. Override `ExcludePaths` to re-include them.

## Exit codes and failure classification

Psalm's documented contract:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed with no issues (JSON report on stdout) | Verdict (pass) |
| `2` + valid JSON report | Analysis completed and found issues | Verdict (`Passed = false` — errors map to `Error`, infos to `Info`) |
| `1` | Could not run: missing/unreadable config, bad flags, internal errors (printed as text, no report) | Infrastructure — the parser fails closed |
| `0`/`2` with malformed or non-array JSON | Foreign output — crash, truncation, an operator `--output-format` override | Infrastructure |
| `2` + valid report with zero findings | Inconsistent report | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A repository with no `psalm.xml` is **not** a pass: Psalm exits 1 with
"Could not locate a config file" and no report, which is an infrastructure
failure. Enable this auditor only on projects where Psalm analysis is a
meaningful gate.

A missing `psalm` — or a missing `php` interpreter (the binary is a phar /
Composer binstub that execs through php) — is always an infrastructure
failure naming the tool, never a passing audit.

## Version pinning

The auditor is pinned to **Psalm `6.20.0`** (`ExpectedVersion` in scoped
config). A static analyser's rules and report shape change between releases,
so an unpinned tool would change findings under you: the auditor probes
`psalm --version` before every run and reports an infrastructure failure
on any other version.

The `psalm` tool requirement is **verify-only** — no `AptPackage`: no
distro package carries a version pin. Provision the pinned release (plus a
PHP CLI runtime — declared as a second requirement, `php-cli` via apt) into
your sandbox baseline:

```sh
# baseline bake step
composer global require vimeo/psalm:6.20.0   # or: install the signed psalm.phar on PATH
psalm --version   # must print 6.20.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.psalm"],
      "Enabled": ["codeybox.psalm"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.psalm" }
```

The `psalm`/`php` tool requirements are only contributed to baseline
provisioning while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.psalm`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `6.20.0` | Pinned Psalm release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned Psalm config outside the repository, or a repo file overriding the `psalm.xml` lookup. Ignored when `ExtraArguments` already supplies `--config`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Set `warning` to keep only warnings and errors; the default keeps informational findings as advisory. |
| `IncludedRules` / `ExcludedRules` | — | Exact Psalm issue types to keep/drop (e.g. `UndefinedFunction`, `MixedReturnStatement`). Setting `IncludedRules` also drops findings that carry no issue type. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--threads`, `--ignore-baseline`, or explicit targets. A repeated flag wins over the built-in default — take care: `--output-format` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. Size the budget to the tree — a cold-cache Psalm run on a large codebase is not instant. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

Bare `psalm` — no positional path arguments — so the repository's own
`psalm.xml` `projectFiles` section decides what gets analysed: the project's
own declaration of checkable scope. `--no-cache` keeps Psalm from writing
its cache directory into the audited tree.
On top of that, findings under vendored (`vendor/`, `third_party/`,
`node_modules/`) and generated (`dist/`, `build/`, `out/`, `coverage/`)
prefixes are dropped by default: violations there belong to upstream
packages or build output, not the change under audit — reporting them
produces noise that trains operators to ignore the auditor. Re-include a
prefix by overriding `ExcludePaths`.

A repository that runs Psalm at a lenient `errorLevel` (Psalm's default for
`--init` is whatever the initializer inferred) gets a lenient gate; set the
config's `errorLevel` (or pin an operator-owned config via `ConfigPath`) to
tighten it.

## Config file the repository must carry

Psalm requires a `psalm.xml` (or `psalm.xml.dist`) in the audited
repository describing the analysed files. A barebones example:

```xml
<?xml version="1.0"?>
<psalm errorLevel="1">
  <projectFiles>
    <directory name="src" />
  </projectFiles>
</psalm>
```

Without a config file Psalm cannot run (exit 1 — infrastructure, never a
pass). Operators auditing repositories that do not carry one should either
add it to the repository or pin an operator-owned config via `ConfigPath`.
