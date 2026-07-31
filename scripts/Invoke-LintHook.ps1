<#
.SYNOPSIS
    PostToolUse hook: lints a .ps1 immediately after it is written or edited.

.DESCRIPTION
    Turns "run Test-PowerShellRules.ps1 before committing" from something an agent has
    to remember into something that happens. Violations come back as context on the
    same turn, while the change is still in hand, rather than at some later commit that
    may never occur -- this repo is not even a git repository, so a pre-commit rule had
    nothing to hang on.

    Only reports violations. A clean file produces no output and no noise.

.NOTES
    Wired via .claude\settings.json as a PostToolUse hook on Write|Edit.
    Exits 0 always: a lint failure is information for the model, not a reason to fail
    the tool call that already succeeded.
#>
[CmdletBinding()]
param(
    # Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.
    [string]$InputJson
)

Set-StrictMode -Version Latest

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

try {
    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json

    # Write returns the path in tool_response; Edit carries it on tool_input.
    $filePath = Get-Prop (Get-Prop $payload 'tool_response') 'filePath'
    if (-not $filePath) { $filePath = Get-Prop (Get-Prop $payload 'tool_input') 'file_path' }
    if (-not $filePath) { exit 0 }
    if ($filePath -notlike '*.ps1') { exit 0 }
    if (-not (Test-Path $filePath)) { exit 0 }

    $linter = Join-Path $PSScriptRoot 'Test-PowerShellRules.ps1'
    if (-not (Test-Path $linter)) { exit 0 }

    $result = & $linter -Path $filePath | ConvertFrom-Json
    $violations = Get-Prop $result 'violations'
    if (-not $violations -or $violations -eq 0) { exit 0 }

    $lines = @($result.findings | ForEach-Object { "  line $($_.line) [$($_.rule)] $($_.message)" })
    $context = "House-rule violations in $(Split-Path $filePath -Leaf):" +
        [Environment]::NewLine + ($lines -join [Environment]::NewLine) +
        [Environment]::NewLine + "See notes\powershell-house-rules.md. Fix before moving on."

    [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName     = 'PostToolUse'
            additionalContext = $context
        }
    } | ConvertTo-Json -Depth 4 -Compress
}
catch {
    exit 0
}

exit 0
