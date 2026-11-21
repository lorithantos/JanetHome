<#
.SYNOPSIS
    Reports the work boundary for one project: its slot (owed, chosen or shelved), what is in
    flight, and whether new work may start now.

.DESCRIPTION
    Startup runs this with the session's {projectName} and captures it as 'workBoundary'.
    Re-run it before starting each new unit of work: a session can live for days and the
    brief is read once. The rules are Lori's, decided 2026-11-21, and live in
    scripts\lib\WorkBoundary.ps1 (shared with Set-WorkBoundary.ps1 and the per-prompt hook,
    Invoke-WorkBoundaryHook.ps1); this script only binds arguments and reads the clock.

    Output is compact JSON: project, slot, inFlight (each with any waitingUntil and note),
    now (local, with offset), day.started and day.endsAt (null when no start is recorded
    today), newWorkAllowed, reason (one line when it is false), stopHour, resumeHour,
    spanHours, error. An unknown project is simply shelved. A missing or malformed settings
    file, or an unreadable day state, is reported in 'error', newWorkAllowed is false, and the
    script exits 1 so startup marks the run 'error' -- never silence.

.PARAMETER Project
    The project name: the leaf of the session's project dir. Defaults to the leaf of
    $env:CLAUDE_PROJECT_DIR, then of the current directory.

.PARAMETER SettingsPath
    Defaults to <JanetBase>\.janet\work-boundary.json (machine-local, git-ignored).

.PARAMETER StatePath
    Defaults to <JanetBase>\.janet\work-day.json (today's recorded start).

.PARAMETER Now
    The instant to decide for. Omitted, the system clock is read once, here. Tests always
    pass it (house rule 7).

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-WorkBoundary.ps1" -Project ImageSelectorV2
#>
[CmdletBinding()]
param(
    [AllowEmptyString()][string]$Project = '',
    [string]$SettingsPath = '',
    [string]$StatePath = '',
    [Nullable[DateTimeOffset]]$Now = $null,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\WorkBoundary.ps1')

$paths = Get-WorkBoundaryPath -JanetBase (Split-Path $PSScriptRoot -Parent)
if (-not $SettingsPath) { $SettingsPath = $paths.Settings }
if (-not $StatePath) { $StatePath = $paths.State }
if (-not $PSBoundParameters.ContainsKey('Project')) {
    $dir = $env:CLAUDE_PROJECT_DIR
    if (-not $dir) { $dir = (Get-Location).Path }
    $Project = Get-ProjectNameFromDir $dir
}
# The composition root: the one place the system clock is read.
$instant = if ($null -ne $Now) { [DateTimeOffset]$Now } else { [DateTimeOffset]::Now }

$decision = Get-WorkBoundaryDecision -Project $Project -Now $instant -SettingsPath $SettingsPath -StatePath $StatePath
ConvertTo-WorkBoundaryEnvelope -Decision $decision | ConvertTo-Json -Depth 5 -Compress:(-not $Pretty)
if ($decision.Error) { exit 1 }
exit 0
