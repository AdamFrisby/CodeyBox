# CodeyBox: PSScriptAnalyzer PowerShell Analysis Auditor

Auditor plugin wrapping [PSScriptAnalyzer](https://github.com/PowerShell/PSScriptAnalyzer)'s
`Invoke-ScriptAnalyzer` cmdlet: it scans the audited repository's PowerShell
files (`.ps1`, `.psm1`, `.psd1`) and reports every rule violation as an audit
finding.

## What it reports

- One finding per diagnostic record in the scan's JSON report.
- `Title` carries the rule id (e.g. `PSAvoidUsingWriteHost`) plus the
  diagnostic message; the description carries the tool, rule, tool severity,
  and location.
- `Location` is `path:line` — repo-relative for the default `.` target;
  absolute paths are relativized against the probed scan root, and
  out-of-root absolute paths keep an explicit `file://` marker.
- **Severity mapping (declared, never raw pass-through):**

  | PSScriptAnalyzer | CodeyBox | Gate |
  |---|---|---|
  | `Error` | `Error` | blocks the audit |
  | `ParseError` | `Error` | blocks the audit — the file cannot be parsed at all |
  | `Warning` | `Warning` | advisory |
  | `Information` | `Info` | informational |
  | unrecognised | `Warning` | advisory |

  `MinimumSeverity` can only drop findings, never raise them.
  **Intended gate behaviour:** blocking on `Error`/`ParseError` severities
  only — style and best-practice notes stay advisory. The plugin is not a
  mandatory gate by default; it only contributes findings (and a failing
  verdict on error-severity diagnostics) for the projects that enable it.

## What it cannot see

- **Anything outside `.ps1`/`.psm1`/`.psd1`.** Invoke-ScriptAnalyzer only
  parses PowerShell source; other files are ignored by construction.
- **Semantics beyond its rule set.** It is a static analyzer, not a security
  scanner for PowerShell — a rule corpus miss is not a vulnerability.
- **Suppressed violations.** In-source `[SuppressMessageAttribute]`
  suppressions are honored by the tool and produce no finding; that is the
  tool's normal lint contract (suppression is visible in the source diff
  itself). `-IncludeSuppressed`/`-SuppressedOnly` are reserved — see
  `ExtraArguments`.
- **Findings under an `ExcludePaths` prefix** are dropped from the report —
  the filter is post-scan, so excluded trees still cost scan time.

## The shim contract

`Invoke-ScriptAnalyzer` is a **cmdlet inside the PSScriptAnalyzer module, not
a standalone binary** — it has no process exit convention of its own. The
declared tool is therefore an operator-provisioned executable named
`Invoke-ScriptAnalyzer` on the sandbox PATH, running under `pwsh`
(PowerShell 7). Its contract is owned by this auditor; provision it verbatim:

```powershell
#!/usr/bin/env pwsh
# Invoke-ScriptAnalyzer shim for the CodeyBox PSScriptAnalyzer auditor.
# Forwards every argument to the Invoke-ScriptAnalyzer cmdlet and serializes
# the diagnostics to a compact JSON array on stdout.
#   exit 0 = analysis ran, no diagnostics
#   exit 2 = analysis ran, diagnostics emitted (report on stdout)
#   exit 1 = the cmdlet could not run (module missing, bad arguments, or
#            ANY mid-scan error — see below)
# A bare "--version" prints the installed PSScriptAnalyzer module version.

# Fail closed on non-terminating errors too: under the default
# $ErrorActionPreference=Continue an unreadable file or a per-file engine
# failure writes an error record to stderr but still yields a PARTIAL
# report under a findings-producing exit — the verdict would silently cover
# less than the tree. Stop makes every error terminating, so the process
# exits 1 and the run classifies as infrastructure, not a partial verdict.
$ErrorActionPreference = 'Stop'
trap { Write-Error -ErrorRecord $_; exit 1 }

if ($args.Count -eq 1 -and $args[0] -eq '--version') {
    $module = Get-Module -ListAvailable PSScriptAnalyzer |
        Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $module) { exit 1 }
    Write-Output $module.Version.ToString()
    exit 0
}

$report = @(Invoke-ScriptAnalyzer @args | ForEach-Object {
    [PSCustomObject]@{
        RuleName = [string]$_.RuleName
        Severity = [string]$_.Severity
        Message  = [string]$_.Message
        File     = [string]$_.Extent.File
        Line     = [int]$_.Extent.StartLineNumber
    }
})
ConvertTo-Json -InputObject $report -Compress -Depth 4
if ($report.Count -gt 0) { exit 2 }
```

Provisioning example (`CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd`, or stage the shim via `ExecutableProvisions`):

```sh
# PowerShell 7 host (adjust to the image — Microsoft's apt feed or tarball)
# ... install pwsh ...
pwsh -NoProfile -Command "Install-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -Force -Scope AllUsers"
# write the shim above to /usr/local/bin/Invoke-ScriptAnalyzer
chmod +x /usr/local/bin/Invoke-ScriptAnalyzer
Invoke-ScriptAnalyzer --version   # must print the pinned module version
```

## Exit codes and failure classification

`Invoke-ScriptAnalyzer` itself returns **objects, not exit codes** — this is
the non-obvious part: a raw `pwsh -Command "Invoke-ScriptAnalyzer …"` exits
`0` whether it found violations or not, and its `-EnableExit` switch exits
with the *count of error records*, which collides with exec-failure codes
(the base hardwires 126/127 to "cannot execute / not found" before the
findings-exit check) and wraps modulo 256. The shim above defines the
auditor's convention instead:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | analysis completed, no diagnostics (`[]` on stdout) | clean pass |
| `2` | analysis completed, diagnostics emitted (JSON array on stdout) | findings |
| `1` | the cmdlet could not run — missing module, bad parameters, terminating error, or any mid-scan error record (`$ErrorActionPreference = 'Stop'` makes them all terminating) | infrastructure |
| `126`/`127` | shim missing or not executable (a missing `pwsh` surfaces here — it is the shim's interpreter) | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

A findings-producing exit whose stdout yields no parseable report also fails
closed as infrastructure — "found problems" and "could not run" stay
distinguishable. A missing `Invoke-ScriptAnalyzer` shim, a missing `pwsh`
host (the shim's `--version` probe then fails as exec 127), a version
mismatch, and a timeout are likewise infrastructure failures naming the
tool — never a passing audit.

## Version pinning

Pinned to **PSScriptAnalyzer `1.25.0`** (`ExpectedVersion` in scoped
config): the rule corpus changes between releases, so an unpinned module
changes findings under you. The shim's `--version` reports the installed
module version and is probed before every run; a missing shim, an
unrecognised version string, or a version other than the pinned one is an
infrastructure failure.

The tool requirement is **verify-only** — no `AptPackage`: no distro package
carries a version-pinned PSScriptAnalyzer module. Provision pwsh, the pinned
`Install-Module`, and the shim into the sandbox baseline as shown above.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.psscriptanalyzer"],
      "Enabled": ["codeybox.psscriptanalyzer"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.psscriptanalyzer" }
```

Only then do the declared `Invoke-ScriptAnalyzer` and `pwsh` tool
requirements reach baseline provisioning (presence-verified at bake time;
nothing is apt-installed because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.psscriptanalyzer`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.25.0` | Pinned PSScriptAnalyzer module release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TargetPath` | `.` | Single `-Path` value — a repo-relative path keeps finding locations repo-relative. |
| `SettingsPath` | — | `-Settings` value: a built-in preset name shipped by the pinned module (`CmdletDesign`, `CodeFormatting`, `CodeFormattingAllman`, `CodeFormattingOTBS`, `CodeFormattingStroustrup`, `DSC`, `PSGallery`, `ScriptingStyle`, `ScriptSecurity` — exact names only; PSScriptAnalyzer 1.25.0 has no comma-list form), or a `.psd1` path that must resolve **outside** the audited worktree and contain no wildcard characters (in-tree paths and globs are rejected deterministically — see below). Unset → a generated empty settings file pinning the default rule set. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (e.g. `PSAvoidUsingWriteHost`). Post-scan filtering. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter; setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (argv entries, never a shell) — e.g. `-IncludeRule`,`PSAvoid*`, `-ExcludeRule`, `-Severity`. Reserved flags are rejected deterministically: `-Path`/`-PSPath`, `-ScriptDefinition`, `-Recurse`, `-Settings`/`-Profile`, `-Fix`, `-SuppressedOnly`, `-IncludeSuppressed`, `-ReportSummary`, `-SaveDscDependency`, `-EnableExit`, `-WhatIf`/`-wi`, `-Confirm`/`-cf`, `-CustomRulePath`/`-CustomizedRulePath`, `-RecurseCustomRulePath`, `-IncludeDefaultRules`, `-ErrorAction`/`-ea` — matched case-insensitively including PowerShell's unambiguous-prefix and alias binding (so `-Set` is `-Settings`), with U+2013/U+2014/U+2015 dashes normalized to `-` (so `–Settings` is `-Settings`). |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps. A scan whose JSON report exceeds the stream cap fails closed rather than parsing a truncated report. |

## The settings-file surface

`Invoke-ScriptAnalyzer` **does** auto-load a settings file from the
repository: with no `-Settings` argument the cmdlet enters
`SettingsMode.Auto` and reads `PSScriptAnalyzerSettings.psd1` from the
resolved `-Path` directory (or its directory, when `-Path` names a file).
A `.psd1` settings file is executable gate configuration — it can carry
`ExcludeRules`, `Severity`, and `CustomRulePath` (arbitrary PowerShell
module code), and its values take precedence over conflicting command-line
parameters — so a committed `PSScriptAnalyzerSettings.psd1` would let the
diff author empty the report or run in-tree code inside the analyzer while
the audit reports a clean pass.

The auditor therefore **always** passes `-Settings`:

- `SettingsPath` unset → a generated empty settings file (`@{}`, the
  default rule set) written into the per-run scratch directory before the
  scan. Auto-discovery can never engage.
- `SettingsPath` = a built-in preset name → passed verbatim. Preset names
  are validated against the pinned module's shipped list (`CmdletDesign`,
  `CodeFormatting`, `CodeFormattingAllman`, `CodeFormattingOTBS`,
  `CodeFormattingStroustrup`, `DSC`, `PSGallery`, `ScriptingStyle`,
  `ScriptSecurity`) because any other name-shaped value is treated by the
  cmdlet as a file path, not a preset.
- `SettingsPath` = any other value → treated as a file path. Wildcard
  characters (`*`, `?`, `[`, `]`) are rejected — the cmdlet resolves the
  value through a globbing provider-path resolver, so a glob could expand
  to an in-tree file the containment check never sees. The path and the
  scan cwd are canonicalized in the sandbox with `realpath -m`, and the
  run is rejected as a deterministic configuration failure when the
  canonical path lands inside the tree — relative paths (the cmdlet
  resolves them against its cwd — the worktree), `..` segments, and
  symlinked components all collapse to the path the cmdlet would actually
  open.

Custom rule modules are reachable only through an operator's
outside-worktree settings file: `-CustomRulePath` and friends are reserved
in `ExtraArguments` for the same reason — they resolve cwd-relative inside
the worktree and execute repository-controlled code.

## Default scope

`Invoke-ScriptAnalyzer -Path . -Recurse` over the whole work tree: the tool
itself restricts coverage to `.ps1`/`.psm1`/`.psd1` files. On top of that,
findings under `vendor/`, `third_party/`, and `node_modules/` are dropped by
default — violations inside vendored PowerShell modules describe upstream
packages, not the change under audit, and reporting them would train
operators to ignore the auditor. Narrow the scan itself with `TargetPath`.

The auditor declares `AuditCapabilities.None`: analysis needs no network and
no agent credentials. (That is also why `-SaveDscDependency` is reserved —
it fetches modules from the PowerShell Gallery mid-scan.)
