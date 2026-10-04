# CodeyBox: ScanCode Licence and Copyright Auditor

Auditor plugin wrapping [ScanCode Toolkit](https://github.com/aboutcode-org/scancode-toolkit):
it scans the audited repository's worktree (`scancode --license --copyright .`)
for licence detections and copyright notices, reporting each as an audit
finding from scancode's native JSON report.

## What it reports

- One finding per licence detection in the report's `files[].licenses[]`.
  The licence `key` (e.g. `gpl-3.0`, `mit`) becomes the finding's rule id, so
  `IncludedRules`/`ExcludedRules` select by licence — and the same licence
  in two files is two findings. The message carries the licence category,
  match score, and matched-rule identifier; the file path and the evidence
  `start_line` are preserved in the finding location.
- One finding per copyright statement in `files[].copyrights[]`, with the
  stable rule id `copyright-notice` and the notice text plus its start line.
  Holder derivations (`holders[]`) are not reported separately — the notice
  finding already carries the statement they were derived from.
- One advisory finding per per-file scan error (`scan_errors[]`, rule id
  `scan-error`): the scan completed, but that file could not be fully
  analysed.
- **Severity: mapped, severity-driven gate.** ScanCode reports no severities
  of its own, so the auditor maps the licence `category` vocabulary:
  `Copyleft` → Error (fails the audit), `Copyleft Limited`,
  `Proprietary Free`, `Commercial`, `Free Restricted`, `Source-available`,
  `Unstated License` → Warning (advisory), `Permissive`, `Public Domain`,
  `Patent License` → Info (informational). Copyright notices → Info.
  Anything unrecognised → Warning. `MinimumSeverity` only drops findings,
  it never raises them. The auditor is therefore a merge gate for
  strong-copyleft licence detections only.

## What it cannot see

- **Licences scancode does not know.** Detections come from scancode's
  built-in rule database at the pinned release. A bespoke proprietary
  licence text with no matching rule produces no finding — absence of
  findings is not proof of absence of licence obligations.
- **Weak evidence.** Short mentions ("GPL" in a comment) match with low
  scores and are still reported; triage by score in the finding message.
- **Declared-but-unscanned dependencies.** The auditor scans files in the
  worktree, not resolved dependency graphs — pair it with a
  dependency-vulnerability auditor for the SBOM side.
- **Vendored trees by default.** `vendor/`, `third_party/`,
  `node_modules/`, and `.git/` are excluded (see below).

## Exit codes and failure classification

ScanCode does **not** follow the common "0 = clean, 1 = findings"
convention — verified in the 32.5.0 `cli.py` source
(`rc = 0 if success else 1`). A completed scan exits `0` whether or not it
detected anything: the JSON report is the verdict, never the exit code.

| Exit | Meaning | Classification |
|---|---|---|
| `0` | scan completed (clean or with detections — the report decides) | pass / findings |
| `1` | could not run: usage error, unreadable input, interrupted/crashed scan | infrastructure |
| `126`/`127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

A missing binary, a version other than `ExpectedVersion`, a missing or
oversized report, or an unparseable report all fail closed as
infrastructure naming `scancode` — never a pass, never a finding.

## Policy configuration and repository-controlled files

ScanCode loads no scan configuration from the audited worktree (verified in
the 32.5.0 CLI: every behaviour switch arrives on argv; there is no
config-file option and no working-directory auto-discovery), so a file
committed in the audited repository cannot rescope or silence the scan —
there is no repository-suppression gate to configure.

The application new-version phone-home is disabled by the auditor via
`--no-check-version` — latency and egress, not signal. The scan itself is
fully local (the licence rule database ships inside the installed tool), so
the auditor declares no network capability.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.scancode"],
      "Enabled": ["codeybox.scancode"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.scancode" }
```

Only then does the declared `scancode` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared — provision
`scancode-toolkit==32.5.0` via pip or the upstream release archive).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.scancode`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `32.5.0` | Pinned scancode release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (licence keys such as `gpl-3.0`, or `copyright-notice` / `scan-error`) to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `.git/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--ignore`, `--timeout`, `-n`, `--license-score`. Reserved flags (`--json`, `--strip-root`, `--from-json`, …) are rejected deterministically. |
| `TimeoutSeconds` | `600` | Per-run bound — covers the full-tree rule match; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `8 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`scancode --license --copyright .` at the worktree root — the whole tree.
`vendor/`, `third_party/`, and `node_modules/` are excluded by default:
findings there describe upstream code, not the audit subject — noise that
trains operators to ignore the auditor. `.git/` is excluded too: matches
inside version-control internals are packfile-fragment noise, not licence
grants. Re-include a path by overriding `ExcludePaths` in scoped config.
