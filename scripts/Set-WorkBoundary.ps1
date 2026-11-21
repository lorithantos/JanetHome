<#
.SYNOPSIS
    Records, corrects or clears today's start, or swaps a work-boundary slot.

.DESCRIPTION
    Three verbs, each a parameter set:

      -Start              Record today's start as now, when Lori begins REAL work. Once per
                          calendar day: a start already recorded today is left alone and
                          reported, so a second session cannot lengthen the day by moving it.
      -Start -At HH:mm    Correct today's start (it began earlier, or later, than recorded).
      -Clear              Forget today's start -- it was a one-off after all. The output names
                          the command that restores it.
      -Slot owed|chosen   Lori's deliberate act, never a session's own idea: -Project puts a
                          project in the slot; -WaitingUntil yyyy-MM-dd and -Note set its
                          wait; -NoWait clears both. The output names the command that undoes it.

    Output is compact JSON: action, changed, message, boundary (the Get-WorkBoundary.ps1
    envelope after the change, for the session's project) and error. Exit 1 on error. The
    rules live in scripts\lib\WorkBoundary.ps1; the stop and resume hours and the span are
    changed by editing the settings file, not here.

.PARAMETER Project
    With -Slot: the project to put in it. Otherwise: the project the returned boundary is
    for, defaulting to the leaf of $env:CLAUDE_PROJECT_DIR, then of the current directory.

.PARAMETER Now
    The instant to act at. Omitted, the system clock is read once, here. Tests always pass it.

.EXAMPLE
    & "$env:JanetBase\scripts\Set-WorkBoundary.ps1" -Start

.EXAMPLE
    & "$env:JanetBase\scripts\Set-WorkBoundary.ps1" -Slot owed -WaitingUntil 2026-11-26 -Note 'waiting on the client'
#>
[CmdletBinding(DefaultParameterSetName = 'Start', SupportsShouldProcess)]
param(
    [Parameter(Mandatory, ParameterSetName = 'Start')][switch]$Start,
    [Parameter(ParameterSetName = 'Start')][string]$At = '',
    [Parameter(Mandatory, ParameterSetName = 'Clear')][switch]$Clear,
    [Parameter(Mandatory, ParameterSetName = 'Slot')][ValidateSet('owed', 'chosen')][string]$Slot,
    [AllowEmptyString()][string]$Project = '',
    [Parameter(ParameterSetName = 'Slot')][string]$WaitingUntil = '',
    [Parameter(ParameterSetName = 'Slot')][switch]$NoWait,
    [Parameter(ParameterSetName = 'Slot')][AllowEmptyString()][string]$Note,
    [string]$SettingsPath = '',
    [string]$StatePath = '',
    [Nullable[DateTimeOffset]]$Now = $null
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\WorkBoundary.ps1')

$paths = Get-WorkBoundaryPath -JanetBase (Split-Path $PSScriptRoot -Parent)
if (-not $SettingsPath) { $SettingsPath = $paths.Settings }
if (-not $StatePath) { $StatePath = $paths.State }
# The composition root: the one place the system clock is read.
$instant = if ($null -ne $Now) { [DateTimeOffset]$Now } else { [DateTimeOffset]::Now }

$action = $PSCmdlet.ParameterSetName.ToLowerInvariant()
switch ($action) {
    'start' { $outcome = Write-WorkDayStart -StatePath $StatePath -Now $instant -At $At }
    'clear' { $outcome = Clear-WorkDayStart -StatePath $StatePath -Now $instant }
    'slot' {
        $slotArgs = @{ SettingsPath = $SettingsPath; Slot = $Slot; Project = $Project; WaitingUntil = $WaitingUntil; NoWait = $NoWait }
        if ($PSBoundParameters.ContainsKey('Note')) { $slotArgs['Note'] = $Note }
        $outcome = Set-WorkSlot @slotArgs
    }
}

# With -Slot, -Project names the slot's occupant, so the boundary is the session's own.
if ($action -ne 'slot' -and $PSBoundParameters.ContainsKey('Project')) {
    $boundaryProject = $Project
}
else {
    $dir = $env:CLAUDE_PROJECT_DIR
    if (-not $dir) { $dir = (Get-Location).Path }
    $boundaryProject = Get-ProjectNameFromDir $dir
}
$decision = Get-WorkBoundaryDecision -Project $boundaryProject -Now $instant -SettingsPath $SettingsPath -StatePath $StatePath
# The line the session repeats to Lori when it records a start.
if ($action -eq 'start' -and -not $outcome.error -and $null -ne $decision.EndsAt) {
    $outcome.message += '; the day ends at ' + (Format-WorkClock $decision.EndsAt $instant)
}

[ordered]@{
    action   = $action
    changed  = $outcome.changed
    message  = $outcome.message
    boundary = (ConvertTo-WorkBoundaryEnvelope -Decision $decision)
    error    = $outcome.error
} | ConvertTo-Json -Depth 6 -Compress
if ($outcome.error) { exit 1 }
exit 0
