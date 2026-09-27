# CodeyBox: Psalm PHP Static Analysis

Auditor plugin wrapping [psalm](https://psalm.dev/) (`vimeo/psalm`): it
analyses the audited repository with `psalm --output-format=json` and
reports each issue as an audit finding with the psalm issue type (e.g.
`InvalidReturnType`, `PossiblyNullReference`) and `file:line` location.

## What it reports

- One finding per psalm issue. The title carries the issue type and the
  first line of the message (e.g. `InvalidReturnType: The declared return
  type 'int' for f is incorrect`); the description carries the tool, issue
  type, tool-reported level, location, and the full message. `Location` is
  `path:line` — the report's `file_path` is an absolute path relativized
  against the audited worktree root (`file_name` is the fallback), and
  `line_from` is already 1-based. Issues outside the worktree keep their
  absolute path with an explicit `file://` marker (e.g.
  `file:///opt/stubs/x.php:7`) so an out-of-tree path can never be mistaken
  for a repository file — the shared finding pipeline trims a bare leading
  `/`, which would otherwise hide that the path was outside the audited
  tree. Config-level issues that carry no file (e.g.
  `UnusedBaselineEntry`) produce findings with no location.
- **Gate behaviour: blocking on error-severity issues — the same verdict
  psalm itself returns.** Psalm's severities go through a declared map,
  never raw: `error` → `Error` (fails the audit); `info` → `Info`
  (advisory). Which issue types are errors comes from the analysis
  contract in force — the audited repository's `psalm.xml` /
  `psalm.xml.dist` (`errorLevel`, per-issue `issueHandlers`), or an
  operator-pinned config via `ConfigPath`. `MinimumSeverity` can only drop
  findings, so the intended way to harden the gate is raising issue
  levels in the config.

## What it cannot see

- **Non-PHP code.** Only files under the config's `projectFiles` /
  `file` entries are analyzed; other languages produce no findings.
- **Code outside the config's scope.** With no positional arguments,
  psalm analyzes what the `projectFiles` of the config in force declare.
- **A repo without `psalm.xml` is a failure, not a pass.** Psalm cannot
  run without a configuration file; a repository with no `psalm.xml` or
  `psalm.xml.dist` (and no `ConfigPath` override) fails the run as
  infrastructure with psalm's own "config not found" text in the detail.
  Enable this plugin only for projects that carry a psalm config.
- **`composer.json` without `vendor/` is a hard stop, not a degraded
  scan.** A non-phar psalm requires a Composer autoloader and exits 1
  ("Failed to find a valid Composer autoloader") when the repo declares
  `composer.json` but `composer install` never ran — infrastructure, not a
  pass or a finding. Provision the dependencies in the sandbox baseline
  (`composer install`) for the faithful contract the project intends. A
  repo with no `composer.json` at all scans fine; missing third-party
  symbols then surface as `UndefinedClass`/`UndefinedDocblockClass`
  findings.
- **The repo's code runs inside the audit sandbox.** Psalm requires the
  project's composer autoloader (`vendor/autoload.php` — including any
  `autoload.files` entries, which execute arbitrary PHP) and a
  repo-authored `psalm.xml` can name `<pluginClass>` entries whose code
  psalm loads during the scan. This applies even to `psalm --version`,
  which resolves the autoloader before printing. Containment: the auditor
  declares `AuditCapabilities.None` — no agent credentials, no network —
  inside the provider's scrubbed environment. `TrustRepositoryConfig=false`
  plus an operator-owned `ConfigPath` removes the `<pluginClass>` /
  `issueHandlers` half of this surface; the autoloader half is inherent to
  psalm.
- **Inline suppressions and the baseline stay honored.** `@psalm-suppress`
  docblocks, `@psalm-ignore-*` annotations, and the config's
  `errorBaseline` file (`psalm-baseline.xml`) are authored inside the
  audited repository — a diff can hide its own new issues by extending the
  baseline or suppressing inline. Psalm has no flag that makes docblock
  suppressions inert; `--ignore-baseline` (via `ExtraArguments`)
  neutralizes the baseline file specifically. A warnings-clean local run
  that disagrees with the audit is a signal to inspect the diff's config,
  baseline, and suppression comments.
- **More than `MaxFindings` issues** are dropped and the truncation is
  reported in the raw output.

## Exit codes and failure classification

Psalm **inverts** the common "0 clean / 1 findings / 2 error" convention
(verified against the vimeo/psalm source — since 4.5, upstream #5087 —
not assumed):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed, no `error`-severity issues (`info` issues may be in the report) | Verdict (pass, or advisory findings) |
| `2` with a JSON issue array on stdout | Analysis completed, ≥1 `error`-severity issue | Verdict (`Passed = false` when any finding maps to `Error`) |
| `0`/`2` with no parseable JSON array on stdout | Not a report — e.g. a crash after the exit path was reached | Infrastructure — the parser fails closed |
| `1` | Could not run: bad arguments, missing/unparseable `psalm.xml`, uncaught exception (psalm's error handler exits 1) | Infrastructure |
| `255` | PHP engine fatal (OOM, crash) | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `psalm` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **psalm `6.17.2`** (`ExpectedVersion` in scoped
config). A static analyzer's issue implementations and its report shape
change between releases, so an unpinned tool would change findings under
you: the auditor probes `psalm --version` before every run and reports an
infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: psalm
ships via Composer, Phive, or as `psalm.phar` release download, and needs
PHP >= 8.1 CLI already on PATH. No distro package carries a version pin.
Provision the pinned release in your sandbox baseline **only when this
plugin is enabled**:

```sh
# baseline bake step — PHP CLI must already be installed
composer global require vimeo/psalm:6.17.2
# ensure the composer global bin-dir is on PATH, or:
curl -fsSL -o /usr/local/bin/psalm https://github.com/vimeo/psalm/releases/download/6.17.2/psalm.phar
chmod +x /usr/local/bin/psalm
psalm --version   # must print "Psalm 6.17.2@<sha>"
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `psalm` only in that state:

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

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.psalm`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `6.17.2` | Pinned psalm release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Passed to `--config` — an operator-owned `psalm.xml`. Ignored when `ExtraArguments` already supplies `--config`/`-c`. Caveat: psalm derives its base directory from the config location when `resolveFromConfigFile` applies, so an out-of-repo config must retarget the repository through its own `projectFiles` entries. |
| `TrustRepositoryConfig` | `true` | `false` refuses to load the audited repo's `psalm.xml`/`psalm.xml.dist`. Psalm cannot run config-free, so this **requires** `ConfigPath` (or `--config` in `ExtraArguments`) pointing at a configuration the audit subject does not control — otherwise the run fails closed as a deterministic infrastructure error. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact psalm issue types to keep/drop (e.g. `InvalidReturnType`, `PossiblyNullReference`). |
| `ExcludePaths` | `vendor/`, `node_modules/`, `var/cache/`, `storage/framework/`, `bootstrap/cache/`, `generated/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--ignore-baseline`, `--find-unused-code`, `--taint-analysis`, `--threads=`, `--memory-limit=`, or positional file/directory arguments. A repeated `--output-format` replaces the JSON report and fails closed as infrastructure; a repeated `--show-info` overrides the built-in `true`. |
| `TimeoutSeconds` | `600` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `4 MiB` / `1000` | Output/result caps; overruns are reported as truncation — a JSON report cut mid-document fails closed as infrastructure. |

## Default scope

`psalm --output-format=json --show-info=true --no-cache --no-progress`
over the audited tree, with the repository's own psalm configuration
deciding which files are analyzed. On top of that, findings under vendored
(`vendor/`, `node_modules/`), framework-generated (`var/cache/`,
`storage/framework/`, `bootstrap/cache/`, `generated/`), and
build/coverage (`dist/`, `build/`, `out/`, `coverage/`) prefixes are
dropped by default: issues there belong to installed packages or generated
code, not the change under audit — reporting them produces noise that
trains operators to ignore the auditor. Re-include a prefix by overriding
`ExcludePaths`.
