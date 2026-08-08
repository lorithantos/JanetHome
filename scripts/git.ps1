<#
.SYNOPSIS
    Locates git.exe and forwards all arguments to it.

.DESCRIPTION
    git is not on PATH in every shell this repo is used from -- notably an agent
    session, which does not load the user profile that would add it. Visual Studio
    bundles a copy, so this shim resolves the real git.exe once per invocation and
    forwards everything, rather than each caller rediscovering the path.

    Run with no arguments to get the resolved path. Unlike its RetirementCore
    counterpart this writes the path to the SUCCESS stream, so it can be captured:
    Write-Host goes to the information stream, where '$g = & .\git.ps1' silently
    yields nothing and the next line fails on an empty command name.

    Targets 5.1 as well as 7: a shim that resolves a tool is exactly the thing
    likely to be run from whatever shell happens to be open.

.PARAMETER GitArgs
    Everything to forward. Git's own '-C <path>' works here and is the way to
    operate on another repository without changing directory.

.EXAMPLE
    & "$env:JanetBase\scripts\git.ps1" status --short

.EXAMPLE
    & "$env:JanetBase\scripts\git.ps1" -C D:\Repos\Other log --oneline -5

.EXAMPLE
    $git = & "$env:JanetBase\scripts\git.ps1"      # capturable: resolved path
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$GitArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-GitExecutable {
    $onPath = Get-Command git -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Known-good path on this machine first, then standard standalone installs, so
    # the common case never reaches the recursive scan below.
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe"
        "$env:ProgramFiles\Git\cmd\git.exe"
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe"
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
        "$env:USERPROFILE\scoop\shims\git.exe"
        'C:\ProgramData\chocolatey\bin\git.exe'
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    # Visual Studio's bundled copy, wherever the edition put it.
    $vsRoots = @(
        "$env:ProgramFiles\Microsoft Visual Studio"
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $vsRoots) {
        $found = Get-ChildItem -Path $root -Filter 'git.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

$git = Resolve-GitExecutable

if (-not $git) {
    Write-Error @'
Could not locate git.exe.

Checked: PATH, Program Files, scoop, chocolatey, and Visual Studio's bundled copy.
If you know where git lives, run `where.exe git` in a shell where it works and add
that path to the candidate list in scripts/git.ps1.
'@
    exit 1
}

if ($GitArgs.Count -eq 0) {
    # Success stream, deliberately -- see .DESCRIPTION.
    Write-Output $git
    exit 0
}

& $git @GitArgs
exit $LASTEXITCODE
