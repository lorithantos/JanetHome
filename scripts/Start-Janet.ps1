<#
.SYNOPSIS
    Launches Claude Code with the Janet startup brief already in the first turn.

.DESCRIPTION
    Runs Invoke-JanetStartup.ps1, then starts an interactive `claude` session whose
    opening prompt is the resulting brief plus the instruction to act on it.  Saves
    the two-step dance of starting a session and then asking it to run startup --
    and, more importantly, makes it impossible to forget the second step.

    The launcher process gets $env:JanetBase set by the startup script, and the
    claude child inherits it, so the session can use "$env:JanetBase\scripts\..."
    paths without being told where the repo is.

    Startup failures are NOT swallowed here.  The manifest's onMissing=fail contract
    exists so a broken toolkit stops the session rather than degrading it; launching
    claude with a half-valid brief would be exactly that degradation.  Nothing is
    launched and the exit code is 1.

.PARAMETER Path
    Working directory for the claude session.  Defaults to the current directory --
    the usual case is loading Janet while working in some other repo.

.PARAMETER Prompt
    Task to start on once the brief is absorbed.  Omit to have the session read in,
    report what is parked on the thread stack, and wait.

.PARAMETER IncludeContent
    Embed the full text of the manifest's 'read' files in the prompt instead of
    listing their paths.  Off by default: progressive disclosure applies (DESIGN-NOTES
    section 2), and the session's own Read tool reports what it opened.

.PARAMETER PromptFile
    Where the full prompt is staged.  Defaults to $env:TEMP\Janet\startup-prompt.md,
    overwritten each launch.

    The prompt is ALWAYS handed to claude through this file; what goes on the
    command line is a one-line pointer to it.  The prompt embeds the JSON brief,
    so it always contains double quotes -- and a quoted argument does not survive
    every shell's native-argument passing.  Windows PowerShell 5.1 does not escape
    embedded quotes when building the child's command line, so the string shreds
    into tokens at the first quote and claude's parser reads fragments of the
    brief as options: 'error: unknown option -Query', 2026-08-01, where -Query
    was a substring of the brief's own retrieval hint.  The pointer line contains
    no quotes, which makes it safe under every argument-passing convention this
    launcher might run under.  (Length was never the real constraint; the 32767-
    char command-line cap just made the file path exist already.)

.PARAMETER ManifestPath
    Alternate startup manifest, passed through to Invoke-JanetStartup.ps1.  Defaults
    to the repo's own.  Mainly for trying a manifest change before committing to it.

.PARAMETER ClaudePath
    claude executable.  Resolved from PATH, then the standard install locations.

.PARAMETER ClaudeArgs
    Extra arguments passed through to claude, e.g. -ClaudeArgs '--model','opus'.

.PARAMETER DryRun
    Print the prompt and the exact claude invocation, launch nothing.  Startup still
    runs -- it is the thing being previewed.

.EXAMPLE
    & "D:\Repos\JanetHome\scripts\Start-Janet.ps1"
    Janet-loaded session in the current directory.

.EXAMPLE
    & "$env:JanetBase\scripts\Start-Janet.ps1" -Path D:\Repos\RetirementCore -Prompt 'Pick up the parked thread.'

.EXAMPLE
    & "$env:JanetBase\scripts\Start-Janet.ps1" -DryRun
    Shows what would be sent without spending a session on it.
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Prompt,
    [switch]$IncludeContent,
    [string]$PromptFile = "$env:TEMP\Janet\startup-prompt.md",
    [string]$ManifestPath,
    [string]$ClaudePath,
    [string[]]$ClaudeArgs = @(),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path) { $Path = (Get-Location).Path }
if (-not (Test-Path $Path -PathType Container)) {
    throw "Working directory not found: $Path"
}

$startup = Join-Path $PSScriptRoot 'Invoke-JanetStartup.ps1'
if (-not (Test-Path $startup -PathType Leaf)) {
    throw "Startup script not found next to this one: $startup"
}

function Resolve-Claude {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit -PathType Leaf)) { throw "claude not found at: $Explicit" }
        return (Resolve-Path $Explicit).Path
    }

    $onPath = Get-Command 'claude' -CommandType Application, ExternalScript -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    # Installer locations, in the order they are likely to be current: native
    # installer, npm global, per-user local bin.
    $candidates = @(
        "$env:USERPROFILE\.local\bin\claude.exe"
        "$env:LOCALAPPDATA\Programs\claude\claude.exe"
        "$env:APPDATA\npm\claude.cmd"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate -PathType Leaf)) { return $candidate }
    }

    throw 'claude executable not found on PATH or in the standard install locations. Pass -ClaudePath.'
}

$claude = Resolve-Claude $ClaudePath

# ---- Startup ---------------------------------------------------------------

# -ProjectDir is $Path, not this shell's location: startup runs BEFORE claude does,
# so $env:CLAUDE_PROJECT_DIR is not set yet and the cwd is wherever the launcher was
# invoked from. Both readers want the session's repo -- the {projectName} args token
# that narrows the thread report, and the enforcedBy check that resolves hook paths
# against the dir the harness will load them from.
$startupArgs = @{ IncludeContent = $IncludeContent; ProjectDir = $Path }
if ($ManifestPath) { $startupArgs['ManifestPath'] = $ManifestPath }

$brief = $null
try {
    $brief = (& $startup @startupArgs | Out-String).Trim()
}
catch {
    Write-Host 'Janet startup failed -- not launching a session on a broken brief.' -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

if (-not $brief) {
    Write-Host 'Janet startup produced no brief -- not launching.' -ForegroundColor Red
    exit 1
}

# The brief is JSON by contract; if it is not, something upstream changed and the
# session would be handed prose it cannot rely on.
$parsed = $null
try { $parsed = $brief | ConvertFrom-Json }
catch {
    Write-Host 'Janet startup did not emit JSON -- not launching.' -ForegroundColor Red
    Write-Host $brief
    exit 1
}

$janetBase = $parsed.janetBase
if ($janetBase) { $env:JanetBase = $janetBase }   # inherited by the claude child

$reported = @($parsed.problems)
if ($reported.Count -gt 0) {
    Write-Host "Startup reported $($reported.Count) problem(s) -- not launching." -ForegroundColor Red
    foreach ($problem in $reported) { Write-Host "  - $problem" }
    exit 1
}

# Enforcement notes are the brief's other list: hooks not wired for the current
# project dir. Deliberately not a launch blocker -- this launcher's primary case
# is starting a session in some other repo, which is exactly when the hooks are
# absent. The brief has already relabelled the affected rules ADVISORY, so the
# session is not deceived; the note here is for the human at the terminal.
# (PSObject lookup, not $parsed.enforcement: absent on pre-split briefs via
# -ManifestPath testing, and StrictMode makes an absent property a hard error.)
$enforcementProp = $parsed.PSObject.Properties['enforcement']
$enforcement = if ($enforcementProp) { @($enforcementProp.Value) } else { @() }
foreach ($note in $enforcement) {
    Write-Host "  ! $note" -ForegroundColor Yellow
}

# ---- Prompt ----------------------------------------------------------------

$closing = if ($Prompt) { $Prompt } else {
    'Then summarise what is parked on the thread stack and wait for my instructions.'
}

$instructions = @"
Janet session startup. The JSON brief below is the output of
$startup, which has already run in the launching shell. `$env:JanetBase is
$janetBase and your process inherited it.

Before anything else:

1. Read every file listed under "read" -- all of them, in order. The brief carries
   paths and reasons only, by design; the content is not here.
2. Treat the entries under "rules" as in force for this session. The ones marked
   ENFORCED are backed by a hook or a linter and will fail if you ignore them; the
   ADVISORY ones hold only because you choose to honour them, so honour them.
3. Note the "retrieval" pointer. The tool and note inventory is deliberately not
   loaded -- query it on demand rather than assuming you know what exists.
4. "captured".threadStack is unfinished work from the last session.

$closing

--- BRIEF ---
$brief
"@

# Always staged, never inline -- see the -PromptFile note above.  The command
# line carries only this quote-free pointer.
$promptDir = Split-Path $PromptFile -Parent
if ($promptDir -and -not (Test-Path $promptDir)) {
    New-Item -ItemType Directory -Path $promptDir -Force | Out-Null
}
# UTF8Encoding over -Encoding utf8NoBOM: correct on 5.1 and 7 alike.
[System.IO.File]::WriteAllText($PromptFile, $instructions, (New-Object System.Text.UTF8Encoding $false))
$initialPrompt = "Read $PromptFile in full and follow it before anything else. It is your startup brief, not a document to summarise."

# ---- Launch ----------------------------------------------------------------

$invocationArgs = @($ClaudeArgs) + @($initialPrompt)

if ($DryRun) {
    Write-Host ''
    Write-Host "claude:   $claude" -ForegroundColor Cyan
    Write-Host "cwd:      $Path" -ForegroundColor Cyan
    Write-Host "args:     $($ClaudeArgs -join ' ')" -ForegroundColor Cyan
    Write-Host "staged:   $PromptFile" -ForegroundColor Cyan
    Write-Host "prompt:   $($instructions.Length) characters" -ForegroundColor Cyan
    Write-Host ''
    Write-Host $instructions
    return
}

Write-Host "Janet loaded from $janetBase -- starting claude in $Path" -ForegroundColor Cyan
Write-Host "Brief staged at $PromptFile." -ForegroundColor DarkGray

Push-Location $Path
try { & $claude @invocationArgs }
finally { Pop-Location }
