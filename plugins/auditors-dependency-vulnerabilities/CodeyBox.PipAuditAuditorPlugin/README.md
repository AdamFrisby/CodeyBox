# CodeyBox: pip-audit Python Dependency CVE Auditor

Auditor plugin wrapping [pip-audit](https://github.com/pypa/pip-audit):
it resolves the audited repository's Python dependencies (requirements
files or a `pyproject.toml` / `pylock.toml` project directory) and queries
the configured vulnerability service (PyPI by default) for known CVEs,
reporting every advisory as an audit finding.

## What it reports

- One finding per advisory in pip-audit's `--format json` report on
  **stdout** — current releases emit
  `{"dependencies":[…],"fixes":[…]}`, older releases a bare dependency
  array; both shapes parse.
- Each dependency carries `name`, `version`, and `vulns[]` with `id`
  (e.g. `PYSEC-2019-179`), `fix_versions[]`, `aliases[]`
  (e.g. `CVE-2019-1010083`), and `description`, all folded into the finding
  message (aliases capped at 8, description at 2000 chars).
- The advisory id becomes the rule id **verbatim** — `PYSEC-…`, `CVE-…`,
  `GHSA-…` — so `IncludedRules`/`ExcludedRules` share pip-audit's own
  `--ignore-vuln` vocabulary. Advisories with no id get
  `pip-audit/advisory`.
- **Severity: mapped, blocking gate.** pip-audit emits no per-vulnerability
  severity, so a confirmed CVE takes the declared default, Error, and fails
  the audit — matching pip-audit's own exit-1-on-any-vuln gate. Reports that
  do carry a severity token map it the way every other auditor maps it:
  `critical`/`high`/`error` → Error, `medium`/`moderate`/`warning` →
  Warning, `low`/`negligible`/`info` → Info, unknown → Error. Raw tool
  tokens never reach findings.
- **Location: absent, by upstream design.** The report carries package names
  and installed versions but no file or line, so findings carry no
  `Location`; the package context is in the message text instead.

## What it cannot see

- **File positions for advisories.** See above — an upstream limitation of
  the JSON report.
- **Anything outside the resolved dependency set.** The subject is the
  dependencies pip-audit resolves from the configured requirements files or
  project directory. Code outside those pins, unpinned transitive internals
  pip cannot resolve, and non-Python code are out of scope.
- **Skipped dependencies.** Entries the tool could not audit
  (`skip_reason`, no `vulns`) yield no findings — there is no verdict to
  report. With pip-audit's `--strict` (via `ExtraArguments`) a skip is a
  loud run failure instead.
- **Fix outcomes.** The report's `fixes` section (from `--fix`, which this
  auditor never passes) is ignored.

## Exit codes and failure classification

pip-audit does **not** follow the common "0 = clean, 1 = findings,
2 = could not run" convention — verified against the 2.10.x source
(`pip_audit/_cli.py`): `sys.exit(1)` fires both when vulnerabilities are
found **and** on every `_fatal` path (unresolvable input, missing project
file, unreachable vulnerability service, strict-mode skip). The
discriminator is the JSON manifest on stdout, printed only when the audit
completes.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON manifest | ran clean | pass |
| `1` | JSON manifest | ran, vulnerabilities found | findings |
| `1` | no JSON | could not run (`_fatal`: bad input, resolution or service failure) | infrastructure |
| `2` | usage text | argparse flag misuse | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

Do not pass `--output`/`-o` via `ExtraArguments`: the report would land in
a file, stdout would be empty, and the run would fail closed as
infrastructure. A missing `pip-audit` binary, a version mismatch, a
timeout, and unparseable output are likewise infrastructure failures naming
the tool — never a passing audit.

## Version pinning and provisioning

The auditor is pinned to **pip-audit `2.10.1`** (`ExpectedVersion` in scoped
config): the report shape changed between releases (bare array → object),
so an unpinned binary would change findings under you. `pip-audit --version`
is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a version pin):

- `pip-audit` — provision via `python3 -m pip install "pip-audit==2.10.1"`
  (Python 3.10 or newer) through `CodeyBox:MultipassExtraRuncmd` /
  `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`.

It reaches baseline provisioning only while the plugin is enabled.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.pip-audit"],
      "Enabled": ["codeybox.pip-audit"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.pip-audit" }
```

Only then does the declared `pip-audit` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.pip-audit`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.10.1` | Pinned pip-audit release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Requirements` | `requirements.txt` | Comma-separated requirements files (repo-relative), each passed as `-r`. An explicit value always wins over `ProjectPath`, mirroring pip-audit's own precedence. |
| `ProjectPath` | — (`-r requirements.txt`) | Local project directory (`pyproject.toml` / `pylock.*.toml`) audited positionally instead of requirements files. Used only when `Requirements` is unset. |
| `VulnerabilityService` | — (tool default `pypi`) | `-s` — `osv`, `pypi`, or `esms`. Anything else is a deterministic configuration failure. |
| `Local` | `false` | `-l` — audit only locally-installed packages satisfying the requirements. |
| `IgnoreVulns` | — | Comma-separated advisory ids (bare `PYSEC-`/`CVE-`/`GHSA-` ids, aliases match), each passed as `--ignore-vuln` — tool-side suppression using the same vocabulary as `ExcludedRules`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Raise to `error` for an advisory-only posture over known-low severities (ungraded CVEs still block — see gate behaviour). |
| `IncludedRules` / `ExcludedRules` | — | Exact advisory ids to keep/drop. |
| `ExcludePaths` | — | Repo-relative paths dropped from findings. **Currently inert**: the report carries no file paths, so there is nothing to match. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Do not pass `--format`/`-f` or `--output`/`-o` — a non-JSON or redirected report fails closed as infrastructure. |
| `TimeoutSeconds` | `300` | Per-run bound — covers pip resolution and the vulnerability-service query; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a stdout overrun that clips the JSON manifest fails closed as infrastructure). |

## Network egress

The auditor declares `AuditCapabilities.Network` unconditionally — there is
no offline mode: every run queries the vulnerability service (PyPI by
default) and pip-audit resolves requirements through pip. The service and
index hosts must be in the deployment's `AuditToolAllowedHosts` list, or
the run fails loudly as infrastructure. The requirements content is
repository-controlled resolution input by tool design (a requirement can
name an arbitrary index or URL), so the sandbox egress allowlist is the
containment for resolution traffic — review dependency-file diffs with the
same care as code.

## Default scope

`pip-audit --format json --progress-spinner off -r requirements.txt` at the
worktree root: the resolved dependency set of the root requirements file.
pip-audit's subject is a resolved dependency set, not a file tree —
vendored trees (`vendor/`, `third_party/`) are never resolved and need no
exclusion. No `ExcludePaths` defaults apply (findings carry no paths).
Repositories without a root `requirements.txt` fail loudly (missing input
is a `_fatal` run failure, never a pass) — point `Requirements` at the
real files, or `ProjectPath` at the project directory, instead.
