<#
.SYNOPSIS
    Query the drive-survey inventory: what is on which drive, what was never
    examined, and what exists on one volume but not another.

.DESCRIPTION
    Thin contract surface over the DriveSurvey CLI (D:\Repos\DriveSurvey), which
    owns the actual walking, identity resolution, and SQLite store. This wrapper
    exists so sessions reach the inventory the same way they reach everything
    else in this repo, with the same output conventions.

    The tool is READ-ONLY against surveyed volumes and never deletes anything.
    Volume identity keys on the NTFS boot-sector serial, which travels with the
    data across docks and letter changes; a docked drive that cannot prove which
    physical drive it is (the Sabrent bridge blanks hardware serials) is refused
    rather than guessed at -- answer the refusal with -Nickname.

    Output is the CLI's JSON envelope on the success stream by default; -Text
    passes through the human-readable rendering instead.

.PARAMETER Command
    volumes      every volume ever seen, online or not, plus attached disks that
                 mount no volume (a clone Windows silently declined to mount)
    scan         index one volume (requires -Volume)
    tree         heaviest subtrees of a volume (requires -Volume)
    compare      what exists on one side and not the other (requires -Volume
                 and -Against)
    blind-spots  what the last scan did not look inside (requires -Volume)
    check        reconciliation: indexed + blind-spot bytes vs the volume's own
                 used figure (requires -Volume, volume must be mounted)
    unprotected  folders on working drives with no durable copy
    removable    folders where EVERY file is proven redundant (requires -Volume)
    duplicates   byte-identical groups, with a ruling on each
    role         classify a volume durable|working (requires -Volume and -Role)

    NOT wrapped, deliberately rather than by omission: 'copy' writes to a drive
    and this wrapper is read-only by contract, and 'declare' is bookkeeping with
    three different argument shapes. Call survey.exe directly for those.

.PARAMETER Volume
    Drive letter ("F:"), NTFS serial ("6A5D-761C"), or nickname. For scan, only
    a mounted drive letter works; the query commands accept any identifier and
    answer for offline drives too.

.PARAMETER Against
    The right-hand volume for compare.

.PARAMETER Nickname
    Operator identity assertion for scan, answering the clone gate: "this drive
    in the dock is photo-backup-b". Only needed when the hardware cannot say.

.PARAMETER Role
    durable or working. For 'role' it is the classification being set and is
    required; for 'scan' it answers the refusal a volume raises the first time
    it is seen on a bus that could be either a backup drive or a card. Copies on
    a 'working' volume are never counted as redundancy.

.PARAMETER Manifest
    For removable and duplicates: write the proposal to strict JSON with the
    reasoning attached to every entry. An existing path is refused.

.PARAMETER Depth
    Subtree depth for tree (1 or 2; default 1).

.PARAMETER Text
    Human-readable output instead of JSON.

.PARAMETER Pretty
    Indented JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" volumes

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" compare -Volume V: -Against F: -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" scan -Volume F: -Nickname photo-backup-b
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('volumes', 'scan', 'tree', 'compare', 'blind-spots', 'check',
                 'hash', 'hash-plan', 'status', 'adopt', 'role', 'removable',
                 'duplicates', 'unprotected')]
    [string]$Command,

    [string]$Volume,
    [string]$Against,
    [string]$Nickname,
    [ValidateSet('durable', 'working')]
    [string]$Role,
    [string]$Manifest,
    [ValidateSet('quick', 'full')]
    [string]$Tier = 'quick',
    [int]$Depth = 1,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The exe is resolved fresh each call rather than pinned, so a rebuild of the
# survey tool is picked up without touching this wrapper. NEWEST wins, not
# Release: preferring Release unconditionally meant a stale Release shadowed a
# Debug build made minutes earlier, which defeated the "a mid-development
# session still works" intent it was written for. The skew surfaced only as the
# store refusing an older schema -- a loud failure, but three steps from its cause.
$surveyRoot = 'D:\Repos\DriveSurvey\src\DriveSurvey.Cli\bin'
$exe = @(
    Join-Path $surveyRoot 'Release\net10.0-windows\survey.exe'
    Join-Path $surveyRoot 'Debug\net10.0-windows\survey.exe'
) | Where-Object { Test-Path $_ } |
    Get-Item |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $exe) {
    throw "survey.exe not found under $surveyRoot -- build D:\Repos\DriveSurvey\DriveSurvey.slnx first"
}

# Which binary answered is otherwise invisible, and that invisibility is what
# made the stale-Release skew take three steps to diagnose. -Verbose says so.
Write-Verbose "survey.exe: $exe (built $((Get-Item $exe).LastWriteTime))"

$surveyArgs = [System.Collections.Generic.List[string]]::new()
$surveyArgs.Add($Command)

switch ($Command) {
    'compare' {
        if (-not $Volume -or -not $Against) {
            throw 'compare needs -Volume and -Against (letter, serial, or nickname)'
        }
        $surveyArgs.Add($Volume)
        $surveyArgs.Add($Against)
    }
    'role' {
        # Without -Role this is the LISTING form -- every volume and how it is
        # classified -- and it takes no volume at all. Only setting one has to
        # say which.
        if ($Role) {
            if (-not $Volume) { throw 'role -Role needs -Volume: which volume is being classified' }
            $surveyArgs.Add('--volume')
            $surveyArgs.Add($Volume)
            $surveyArgs.Add('--set')
            $surveyArgs.Add($Role)
        }
    }
    { $_ -in 'volumes', 'status', 'hash-plan', 'duplicates', 'unprotected' } { }
    default {
        if (-not $Volume) { throw "$Command needs -Volume" }
        $surveyArgs.Add('--volume')
        $surveyArgs.Add($Volume)
    }
}

if ($Nickname) { $surveyArgs.Add('--nickname'); $surveyArgs.Add($Nickname) }
if ($Command -eq 'tree') { $surveyArgs.Add('--depth'); $surveyArgs.Add([string]$Depth) }
if ($Command -eq 'hash') { $surveyArgs.Add('--tier'); $surveyArgs.Add($Tier) }

# 'role' SETS a classification (handled above, with its volume); 'scan' ASSERTS
# one for a volume being recorded for the first time. Same value, different flag,
# so one parameter serves both commands.
if ($Command -eq 'scan' -and $Role) { $surveyArgs.Add('--role'); $surveyArgs.Add($Role) }
if ($Manifest) { $surveyArgs.Add('--manifest'); $surveyArgs.Add($Manifest) }
if ($Text) { $surveyArgs.Add('--text') }
if ($Pretty) { $surveyArgs.Add('--pretty') }

# hash and scan paint a live progress bar on stderr, which only works if the
# console is left alone: routing those streams through the pipeline below turns
# every frame into an ErrorRecord and destroys the display. Long-running
# commands therefore run straight through to the terminal.
if ($Command -in 'hash', 'scan') {
    & $exe @surveyArgs
    exit $LASTEXITCODE
}

# Progress rides stderr by design (stdout stays pure JSON); PowerShell would
# dress those lines up as ErrorRecords, so route the streams explicitly.
& $exe @surveyArgs 2>&1 | ForEach-Object {
    if ($_ -is [System.Management.Automation.ErrorRecord]) {
        Write-Warning $_.Exception.Message
    }
    else {
        $_
    }
}

exit $LASTEXITCODE
