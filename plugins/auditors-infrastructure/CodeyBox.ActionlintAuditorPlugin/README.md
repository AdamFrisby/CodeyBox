# CodeyBox: Actionlint GitHub Actions Auditor

Auditor plugin wrapping [actionlint](https://github.com/rhysd/actionlint):
it runs `actionlint -format '{{json .}}'` over the audited repository and
reports each error — expression mistakes, invalid action inputs, unknown
runner labels, bad cron/schedule syntax, hardcoded container passwords, and
more — as an audit finding with its rule name (`kind`) and file/line
location preserved.

## What it reports

- One finding per actionlint error. The title carries the rule name, so
  `IncludedRules`/`ExcludedRules` select by rule (e.g. `action`,
  `expression`, `runner-label`, `events`, `shell-name`, `syntax-check`,
  `credentials`). The full rule list is in actionlint's
  [checks documentation](https://github.com/rhysd/actionlint/blob/main/docs/checks.md)
  (rule names match the `kind` values in the report).
- `Location` is the repo-relative workflow path with a `:line` suffix
  wherever actionlint supplies a line number.
- **Severity: declared mapping, advisory by default — not blocking.**
  actionlint has no severity levels, so the auditor assigns the meaning:
  `credentials` (a hardcoded password in a `container:`/`services:`
  section — secret material in the tree) and `syntax-check` (a workflow
  file that could not be parsed at all) map to `Error` and fail the audit;
  every other rule maps to `Warning` (advisory), as does anything
  unrecognised from a foreign build (visible, never silently
  informational). Raw tool tokens never reach findings; the rule name is
  preserved in the finding description as proof the value flowed through
  the mapping.

## What it cannot see

- **Anything outside workflow files.** actionlint inspects only
  `.github/workflows/*.yml`/`*.yaml` (plus `--` piped stdin, which the
  auditor does not use) — application code, Dockerfiles, and deployment
  manifests around the workflows are out of scope.
- **Shell and Python script contents.** The `shellcheck`/`pyflakes`
  integrations are disabled (`-shellcheck= -pyflakes=`) because they shell
  out to unpinned external binaries; `run:` script bodies get only
  actionlint's own expression checks. Findings that would have kind
  `shellcheck`/`pyflakes` therefore never appear.
- **Runtime semantics.** actionlint is static analysis: it cannot tell
  whether secrets exist, endpoints respond, matrices cover the intended
  cases, or a run would go green.
- **Suppressed rules.** The audited repository's `.github/actionlint.yaml`
  (self-hosted runner labels, `ignore` patterns) and
  `actionlint-disable` comments narrow what the tool reports — that is the
  scanner's own configuration surface. Operators who want a fixed
  configuration pin it with `ConfigFile` (outside the audited tree).
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (actionlint still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.
- **Repositories without workflows.** actionlint exits `3`
  ("no project was found") when the tree has no `.github/workflows`
  directory — that is an infrastructure failure, not a pass. Enable this
  auditor only on projects that use GitHub Actions.

## Exit codes and failure classification

Verified by running the pinned binary (1.7.12) across a matrix of clean,
issue-bearing, missing-workflow, bad-flag, and missing-file invocations:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | `[]` | ran clean | pass |
| `1` | JSON error array | ran, issues found | findings |
| `2` | none / plain text | could not run (unknown flags) | infrastructure |
| `3` | none / plain text | could not run (unreadable file, no `.github/workflows` found) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the report, not the exit code: exits `0` and `1` are
findings-producing, and an exit without a parseable JSON error array fails
closed as infrastructure through the parser. A missing `actionlint`
binary, a version mismatch, a timeout, and unparseable output are likewise
infrastructure failures naming the tool — never a passing audit. A scanner
that silently passes because it did not run is the worst outcome
available, and this auditor has no path that produces it.

## Version pinning

The auditor is pinned to **actionlint `1.7.12`** (`ExpectedVersion` in
scoped config): the check surface, popular-action database, and report
shape change between releases, so an unpinned binary would change findings
under you. `actionlint -version` is probed before every run; any other
version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries actionlint. Provision the pinned upstream release
into the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
ACTIONLINT_VERSION=1.7.12
curl -fsSL "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz" -o /tmp/actionlint.tar.gz
tar -xzf /tmp/actionlint.tar.gz -C /tmp actionlint
install -c -m 0755 /tmp/actionlint /usr/local/bin/actionlint
actionlint -version   # must print the pinned version
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.actionlint"],
      "Enabled": ["codeybox.actionlint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.actionlint" }
```

Only then does the declared `actionlint` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.actionlint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.7.12` | Pinned actionlint release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `.github/actionlint.yaml`) | `-config-file` path. Point outside the audited tree for a configuration the repository cannot narrow. |
| `Targets` | — (tool discovery) | Comma-separated repo-relative workflow file paths, passed as positional arguments. Unset → actionlint discovers the nearest `.github/workflows` directory from the work-tree root. Entries must be repo-relative (no absolute paths, no `..`). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule names (`kind` values) to keep/drop (finding-level filter, applied after the scan). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `-ignore` patterns or `-verbose`. **Managed by the auditor:** `-format`/`--format` (the JSON report is the parsing contract), `-init-config`/`--init-config` (auditors never rewrite the tree), and `-shellcheck`/`--shellcheck` / `-pyflakes`/`--pyflakes` (the auditor disables those integrations because they shell out to unpinned binaries) are rejected with a deterministic infrastructure error. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`actionlint` with no positional arguments from the work-tree root: the
tool discovers the nearest `.github/workflows` directory itself, so only
workflow files are ever inspected — application and vendored code is out
of scope by construction. The finding-level `ExcludePaths` default
additionally drops `vendor/`, `third_party/`, and `node_modules/`: a
vendored snapshot carrying its own `.github` directory describes upstream
workflows, not the change under audit, and reporting it would train
operators to ignore the auditor. An operator who wants the scan itself
narrowed to specific files uses `Targets`.
