#Requires -Version 7.0
<#
.SYNOPSIS
  Windows build entry point for CodeyBox (mirrors build.sh).
.DESCRIPTION
  Forwards to `dotnet` with the same telemetry hardening as build.sh.
  With no arguments, builds the whole solution. Supported orchestrator
  topology on Windows is remote-executor only (multipass-remote or sprites
  against a Linux executor host); local VM providers require a Linux host.
  See docs/concepts/host-platforms.md.
#>
[CmdletBinding()]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$DotnetArgs
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

if ($DotnetArgs.Count -gt 0) {
  & dotnet @DotnetArgs
}
else {
  & dotnet build CodeyBox.slnx
}
exit $LASTEXITCODE
