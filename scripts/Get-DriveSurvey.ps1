<#
.SYNOPSIS
    Run the DriveSurvey CLI: find the freshest build, run it, report what it said.

.DESCRIPTION
    A finder, a runner and a reporter -- deliberately nothing else. Every
    argument is passed to survey.exe verbatim, and survey.exe owns the command
    surface, the validation and the help.

    This wrapper used to model the commands as well, and that second copy earned
    its removal: it drifted six commands behind the tool, and it rejected
    "role --volume E: --set working" -- the exact line a person who knows the
    tool types -- because it wanted PowerShell-style parameters instead. A
    wrapper that has to be edited whenever the thing it wraps grows a flag is a
    liability pretending to be a convenience.

    Ask the tool itself what it can do:

        Get-DriveSurvey.ps1 help

.PARAMETER Arguments
    Passed to survey.exe unchanged. With none, the tool prints its own help.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" volumes --text

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" scan --volume E: --role working

.EXAMPLE
    & "$env:JanetBase\scripts\Get-DriveSurvey.ps1" removable --volume E: --text
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- finder -----------------------------------------------------------------
# Resolved fresh each call rather than pinned, so a rebuild of the survey tool is
# picked up without touching this file. NEWEST wins, not Release: preferring
# Release unconditionally meant a stale Release shadowed a Debug build made
# minutes earlier, and the skew surfaced three steps from its cause, as the store
# refusing an older schema.
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
# made the stale-Release skew take three steps to diagnose.
Write-Verbose "survey.exe: $exe (built $((Get-Item $exe).LastWriteTime))"

# ---- runner and reporter ----------------------------------------------------
if (-not $Arguments) { $Arguments = @('help') }

# At a console both streams go straight to the terminal. scan and hash paint a
# carriage-return progress bar on stderr that only animates with nothing between
# it and the console, and the CLI's refusals are already written for a person to
# read -- so there is nothing to improve and something to break.
#
# When output is redirected -- piped, captured, read by a session -- stdout has
# to stay a clean JSON envelope, so stderr is separated and reported as warnings
# rather than interleaved into it. Testing redirection rather than the command
# name is what lets this file hold no list of commands at all.
if (-not [Console]::IsOutputRedirected) {
    & $exe @Arguments
    exit $LASTEXITCODE
}

# 'Stop' would escalate a native command's captured stderr into a TERMINATING
# NativeCommandError under Windows PowerShell 5.1, before the handler below ever
# runs -- delivering the tool's own clean refusal as a wall of PowerShell
# plumbing. PowerShell 7 does not escalate it, which is exactly why this stayed
# invisible: the script is written for 7 and is demonstrably run on 5.1.
$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & $exe @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            Write-Warning $_.Exception.Message
        }
        else {
            $_
        }
    }
}
finally {
    $ErrorActionPreference = $previousErrorAction
}

exit $LASTEXITCODE
