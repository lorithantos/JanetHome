<#
.SYNOPSIS
    UserPromptSubmit hook: restates the work boundary on every prompt, and says nothing when
    all is well.

.DESCRIPTION
    Startup puts the work boundary in the brief once, and a session's context is compacted
    often enough that a rule read once is a rule lost (Lori, 2026-11-21). So this fires per
    prompt, asks the same code the read command uses (scripts\lib\WorkBoundary.ps1, the
    decision and its rendering -- no second copy of the rules), and prints plain text, which
    the harness adds to the model's context:

      - in flight, window open, start recorded, day not over: NOTHING (it runs on every
        prompt in every project, and every line is billed every time);
      - closed window, or past the day's end: the time, the hard rule, that work in flight
        may finish and nothing new starts;
      - shelved project: that it is shelved, what is in flight, and that nothing changes here;
      - a waiting project: until when, and its note;
      - no start recorded today: the exact command that records one;
      - settings missing or malformed, or any internal error: one line saying so.

    IT NEVER BLOCKS. No deny, no decision, no exit 2: Lori must be able to say "finish up"
    or ask a quick question at 23:30. Every path exits 0, the error path included.

    The project is the leaf of $env:CLAUDE_PROJECT_DIR (the session's project root, which the
    harness exports to hooks and which the user-level wiring keys on), then of the payload's
    'cwd', then of the current directory. CLAUDE_PROJECT_DIR comes first because 'cwd' moves
    when a session changes directory, and the project a session was opened in does not.

    Windows PowerShell 5.1 compatible: "shell": "powershell" may resolve to it.

.PARAMETER InputJson
    The hook payload, for tests. Normally omitted: the harness sends it on stdin.

.PARAMETER Now
    The instant to decide for. Omitted, the system clock is read once, here. Tests always
    pass it (house rule 7).

.PARAMETER SettingsPath
    Defaults to <JanetBase>\.janet\work-boundary.json.

.PARAMETER StatePath
    Defaults to <JanetBase>\.janet\work-day.json.

.NOTES
    Wired as a UserPromptSubmit hook in .claude\settings.json (project dir JanetHome) and in
    ~\.claude\settings.json by absolute path (every other project dir), like the other hooks.
#>
[CmdletBinding()]
param(
    [string]$InputJson,
    [Nullable[DateTimeOffset]]$Now = $null,
    [string]$SettingsPath = '',
    [string]$StatePath = ''
)

Set-StrictMode -Version Latest

try {
    . (Join-Path $PSScriptRoot 'lib\WorkBoundary.ps1')

    $raw = $InputJson
    if (-not $PSBoundParameters.ContainsKey('InputJson')) { $raw = [Console]::In.ReadToEnd() }
    $cwd = $null
    if ($raw) {
        try { $cwd = Get-WorkProp ($raw | ConvertFrom-Json) 'cwd' } catch { $cwd = $null }
    }

    $dir = $env:CLAUDE_PROJECT_DIR
    if (-not $dir -and $cwd -is [string]) { $dir = $cwd }
    if (-not $dir) { $dir = (Get-Location).Path }
    $project = Get-ProjectNameFromDir $dir

    $base = Split-Path $PSScriptRoot -Parent
    $paths = Get-WorkBoundaryPath -JanetBase $base
    if (-not $SettingsPath) { $SettingsPath = $paths.Settings }
    if (-not $StatePath) { $StatePath = $paths.State }

    # The composition root: the one place the system clock is read.
    $instant = [DateTimeOffset]::Now
    if ($null -ne $Now) { $instant = [DateTimeOffset]$Now }

    $decision = Get-WorkBoundaryDecision -Project $project -Now $instant -SettingsPath $SettingsPath -StatePath $StatePath
    $startCommand = "pwsh -NoProfile -File '" + (Join-Path $PSScriptRoot 'Set-WorkBoundary.ps1') + "' -Start"
    $lines = Get-WorkBoundaryReminder -Decision $decision -StartCommand $startCommand
    foreach ($line in $lines) { Write-Output $line }
}
catch {
    # Fail open, and say so in one line: a reminder that crashed must not block the prompt,
    # and must not pass for a boundary with nothing to say either.
    $message = $_.Exception.Message -replace '[\r\n]+', ' '
    Write-Output "Work boundary could not be read: $message"
}
exit 0
