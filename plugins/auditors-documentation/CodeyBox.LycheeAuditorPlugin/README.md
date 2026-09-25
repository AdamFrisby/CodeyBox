# CodeyBox: Lychee Broken-Link Auditor

Auditor plugin wrapping [lychee](https://lychee.cli.rs): it checks links in
the audited repository's documentation files with `lychee --format json
--offline .` and reports each failed link check as an audit finding with the
source file and `file:line` location lychee supplies.

## What it reports

- One finding per failed link check. lychee's JSON report carries no rule
  ids, so the auditor synthesizes two stable ones:
  - `lychee/broken-link` — an `error_map` entry: a link that was checked and
    failed (missing file, dead URL, missing `#fragment`). Maps to
    `AuditSeverity.Error`.
  - `lychee/timeout` — a `timeout_map` entry: a request that timed out. Maps
    to `AuditSeverity.Warning` — a timeout is weak evidence of a broken link
    and stays advisory.
- The finding title carries the synthesized rule id and `url — status`; the
  description carries the tool, rule, tool-reported level, location, and the
  full message. `Location` is `path:span.line` when lychee reports a span.
- **Gate behaviour: hybrid / severity-driven.** `lychee/broken-link` findings
  fail the audit; `lychee/timeout` findings are advisory. With the default
  offline scope timeouts cannot occur, so the default gate is effectively
  blocking on every finding.
- `success_map`, `redirect_map`, `excluded_map`, and `suggestion_map` are
  not failures and never become findings.

## What it cannot see

- **Remote links by default.** The auditor runs under
  `AuditCapabilities.None` — no credentials, no network — and passes
  `--offline`, so lychee restricts checking to the `file` scheme: relative
  file links and in-repo `#fragment` targets are verified; `http(s)`,
  `mailto`, and other remote URLs are excluded, not reported. Set
  `CheckRemoteLinks` (below) to opt in — the auditor then declares
  `AuditCapabilities.Network` and the sandbox is provisioned with egress
  subject to the project's audit-tool network profile. Remote findings are
  inherently non-deterministic (a flaky site produces flaky findings).
- **Line numbers lychee does not emit.** `span.line` is reported per link in
  v0.24.x JSON; entries without a span produce findings with no line.
- **Files outside the walked extensions.** The `.` input is filtered to
  lychee's documentation extensions (`md`/`mkd`/`mdx`/`mdown`/`mdwn`/`mkdn`/
  `mkdown`/`markdown`, `html`/`htm`, `css`, `txt`, `xml`). Links inside
  `*.py`, `*.cs`, `*.yml`, etc. are not extracted.
- **Gitignored and excluded inputs.** lychee skips files covered by
  `.gitignore`/`.ignore`, and paths listed in `ExcludePaths` are excluded at
  crawl time via `--exclude-path` — links inside them are never checked.
- **Site-root-absolute links resolve only with `RootDirectory`.** A link like
  `/docs/guide.md` is only checkable if the deploy root is known; without
  `RootDirectory` lychee reports it — which is the honest result.

## Exit codes and failure classification

lychee's convention (verified against v0.24.x — **not** the common
"1 = findings" pattern):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | All non-excluded links checked OK | Verdict (pass) |
| `2` | Link check failures (`error_map`/`timeout_map` non-empty) | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` | Missing inputs, runtime failure, or general config error | Infrastructure (`AuditUnavailableException`) |
| `3` | Errors in the lychee config file | Infrastructure |
| `0`/`2` with no JSON on stdout | Execution failure, or a `--format` override that changed the report shape | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `lychee` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **lychee `0.24.2`** (`ExpectedVersion` in scoped
config). A checker's request behaviour, defaults, and report shape change
between releases, so an unpinned tool would change findings under you: the
auditor probes `lychee --version` before every run and reports an
infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: lychee is
not packaged by Ubuntu/Debian and the requirement must carry a version pin.
Provision the pinned release in your sandbox baseline, e.g.:

```sh
# baseline bake step
cargo install lychee --locked --version 0.24.2
# or unpack the lychee-v0.24.2 release tarball from the project's releases
lychee --version   # must print "lychee 0.24.2"
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.lychee"],
      "Enabled": ["codeybox.lychee"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.lychee" }
```

The `lychee` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.lychee`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.24.2` | Pinned lychee release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `CheckRemoteLinks` | `false` | `true` drops `--offline` and declares `AuditCapabilities.Network` so the audit sandbox gets egress. Remote findings are non-deterministic — timeouts and site outages become part of the verdict. |
| `RootDirectory` | `null` | Absolute in-sandbox path passed to `--root-dir`; resolves site-root-absolute links (`/docs/x.md`) in local files. Ignored when `ExtraArguments` supplies `--root-dir`. |
| `Inputs` | `.` | Comma-separated lychee inputs (files, globs, directories) replacing the whole-tree `.` default — e.g. `docs,README.md` to scope to one tree. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned lychee config. Takes precedence over the `/dev/null` pin and the repo's own config files. Ignored when `ExtraArguments` already supplies `--config`/`-c`. |
| `TrustRepositorySuppression` | `false` | When `false` (default) a repo-root `.lycheeignore` fails closed and `--config /dev/null` disables all default config-file lookup. When `true`, lychee honors `.lycheeignore` and repo-authored `lychee.toml`/`[lychee]` sections. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop; the only ids are `lychee/broken-link` and `lychee/timeout`. |
| `ExcludePaths` | `.git/`, `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Each entry is also translated to an anchored `--exclude-path` regex so excluded trees are never crawled. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--exclude <url-regex>`, `--accept <codes>`, `--scheme https`, or an operator `--config`/`--exclude-path`. A repeated flag wins over the built-in default — take care: `--format` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and lychee honors two repo-authored surfaces:
`.lycheeignore` in the working directory (loaded unconditionally — no flag
disables it — each line a URL-exclusion regex) and the default config files
(`lychee.toml`, or `[lychee]`-equivalent sections in `Cargo.toml`,
`pyproject.toml`, `package.json`), which can widen `exclude`/`accept` and
silence findings. By default the auditor fails closed when a repo-root
`.lycheeignore` exists, and passes `--config /dev/null` — an empty config
that turns off every default lookup — so the checked ruleset cannot be
steered by the audited tree. `TrustRepositorySuppression: true` restores the
tool's default behavior for repos whose own lychee config is the intended
contract; a deliberate `ConfigPath` overrides the pin without trusting the
repo.

If your repository legitimately ships `lychee.toml` or `.lycheeignore` for its
own CI, either point `ConfigPath` at that file (it is then the explicit,
operator-chosen config) or set `TrustRepositorySuppression: true`.

## Default scope

`lychee .` — the whole worktree, walked recursively and filtered to
documentation extensions, with hidden files included (docs live under
`.github/`) and `.git/` excluded. Findings under vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`) prefixes are excluded both at crawl time
(`--exclude-path`) and at finding level (`ExcludePaths`): broken links in
third-party docs or build output belong to upstream packages, not the change
under audit — reporting them trains operators to ignore the auditor.
`Inputs` narrows the crawl itself when whole-tree coverage is too broad.
