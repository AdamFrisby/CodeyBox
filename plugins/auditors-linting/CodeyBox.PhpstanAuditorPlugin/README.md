# CodeyBox: PHPStan PHP Static Analysis Auditor

Auditor plugin wrapping [PHPStan](https://phpstan.org): it runs
`phpstan analyse --error-format json --no-progress .` on the audited
repository and reports each diagnostic as an audit finding with the PHPStan
error identifier (e.g. `argument.type`, `variable.undefined`) and `file:line`
location.

## What it reports

- One finding per PHPStan file-specific error: title carries the identifier
  (when PHPStan supplies one) plus the first message line; the description
  carries the tool, identifier, location, the full message, and any `tip`
  text PHPStan attached. `Location` is `path:line` — PHPStan keys its JSON
  `files` map by absolute path, which the auditor relativizes against the
  exec working directory.
- One finding per non-file-specific error in the report's top-level
  `errors[]` (e.g. `ignoreErrors` patterns that matched nothing), without a
  location.
- **Gate behaviour: blocking by default.** PHPStan has no severity levels —
  every reported issue is an analysis error — so every finding maps to
  `Error` and fails the audit. Which checks run comes from the level in
  force: the repo's `phpstan.neon` by default, or `Level`/`ConfigPath` set by
  the operator. `MinimumSeverity` can only drop findings, so the intended way
  to harden the gate is the level/ruleset itself.

## What it cannot see

- **Analysis that needs the project's dependencies.** PHPStan autoloads the
  project's `vendor/autoload.php` when present. Without installed
  dependencies, symbols from third-party packages resolve as unknown — the
  analysis is degraded, not clean. Provision `composer install` into the
  sandbox baseline for full coverage.
- **Runtime-dynamic constructs.** PHPStan is static analysis: `__call` /
  `__get` magic, dynamic dispatch, `eval`, and value-dependent types are
  invisible to it.
- **Repository-controlled suppression.** PHPStan honors `@phpstan-ignore`
  and `@phpstan-ignore-line`/`-next-line` comments, `ignoreErrors`,
  `excludePaths`, and `phpstan-baseline.neon` — all authored inside the
  audited repository — and offers no flag to make them inert. The subject
  can therefore weaken or silence checks; this is a documented limitation,
  not a gate. Operators who need an operator-owned ruleset pin one outside
  the repository via `ConfigPath` (or `-c` in `ExtraArguments`) — inline
  ignores remain honored even then.
- **Warnings.** PHPStan's internal "warnings" channel (e.g. a partially
  unreadable result cache) is not part of the JSON report; warnings never
  become findings.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  PHPStan still analyses those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`
  are dropped. Override `ExcludePaths` to re-include them.

## Exit codes and failure classification

PHPStan's convention (verified against v2.2.x) is **not** the usual linter
table — exit `1` is shared by "found problems" and "could not run":

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed clean (JSON report on stdout) | Verdict (pass) |
| `1` + valid JSON report | Analysis completed with errors | Verdict (`Passed = false` — every error maps to `Error`) |
| `1` + non-JSON stdout | Could not run or incomplete: missing/unreadable config, invalid level, **zero PHP files found**, internal errors (printed as text, no report) | Infrastructure — the parser fails closed |
| `0`/`1` with malformed or shapeless JSON | Foreign output — crash, truncation, an operator `--error-format` override | Infrastructure |
| `1` + valid report with zero findings | Inconsistent report | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention (e.g. PHP fatals at `255`) | Infrastructure (fails loud, never a pass) |

A repository with no PHP sources is **not** a pass: `phpstan analyse .` exits
1 with "No files found to analyse." and no report, which is an infrastructure
failure. Enable this auditor only on projects where PHP analysis is a
meaningful gate.

A missing `phpstan` — or a missing `php` interpreter (the binary is a phar /
Composer binstub that execs through php) — is always an infrastructure
failure naming the tool, never a passing audit.

## Version pinning

The auditor is pinned to **PHPStan `2.2.14`** (`ExpectedVersion` in scoped
config). A static analyser's rules and report shape change between releases,
so an unpinned tool would change findings under you: the auditor probes
`phpstan --version` before every run and reports an infrastructure failure
on any other version.

The `phpstan` tool requirement is **verify-only** — no `AptPackage`: no
distro package carries a version pin. Provision the pinned release (plus a
PHP CLI runtime — declared as a second requirement, `php-cli` via apt) into
your sandbox baseline:

```sh
# baseline bake step
composer global require phpstan/phpstan:2.2.14   # or: install the signed phpstan.phar on PATH
phpstan --version   # must print PHPStan - PHP Static Analysis Tool 2.2.14
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.phpstan"],
      "Enabled": ["codeybox.phpstan"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.phpstan" }
```

The `phpstan`/`php` tool requirements are only contributed to baseline
provisioning while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.phpstan`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.2.14` | Pinned PHPStan release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `-c` — an operator-pinned PHPStan config outside the repository, or a repo file overriding the `phpstan.neon` lookup. Ignored when `ExtraArguments` already supplies `-c`/`--configuration`. |
| `Level` | `null` | Analysis level passed to `-l` (`0`–`9` or `max`). Overrides the level in the repo's config; unset means the config's `level` (or PHPStan default `0`). Ignored when `ExtraArguments` supplies `-l`/`--level`. An invalid value is a deterministic infrastructure failure. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact PHPStan error identifiers to keep/drop (e.g. `argument.type`, `variable.undefined`). Setting `IncludedRules` also drops findings that carry no identifier, including non-file-specific errors. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--memory-limit`, `-a`/`--autoload-file`, or explicit targets. A repeated flag wins over the built-in default — take care: `--error-format` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. Size the budget to the tree — a cold-cache PHPStan run on a large codebase is not instant. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`phpstan analyse .` — the whole audited tree. A CLI path argument replaces
the `paths` parameter in the repository's config, so the scan always covers
the tree; the config's `excludePaths` and `level` still apply.
On top of that, findings under vendored (`vendor/`, `third_party/`,
`node_modules/`) and generated (`dist/`, `build/`, `out/`, `coverage/`)
prefixes are dropped by default: violations there belong to upstream
packages or build output, not the change under audit — reporting them
produces noise that trains operators to ignore the auditor. Re-include a
prefix by overriding `ExcludePaths`.

A repository that runs PHPStan without a config file is analysed at level
`0` (PHPStan's default) — a meaningful but lenient gate; set `Level` or add
`phpstan.neon` to tighten it.
