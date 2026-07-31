<#
.SYNOPSIS
    PreToolUse hook: blocks direct edits to files that must only be changed through
    a validating script.

.DESCRIPTION
    research.json is append-and-update-only through Add-ResearchNode.ps1 and
    Update-ResearchNode.ps1, which validate ids, check links, preserve unknown fields,
    and refuse to write a file that would not parse. A direct Edit bypasses every one
    of those checks.

    This exists because the rule was violated the day it was written -- not from
    ignorance of it, but from deciding a particular edit was small enough not to count.
    An agent does not skip a step from fatigue; it skips from rationalisation. So the
    step is no longer optional.

    Reads the hook payload on stdin. Emits a deny decision, or nothing at all when the
    call is fine.

.PARAMETER GuardedFile
    File name to protect. Defaults to research.json.

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on
    stdin. PowerShell's object pipeline cannot bind to a script reading raw stdin,
    so without this the hook could only be tested by launching a whole process.

.NOTES
    Wired via .claude\settings.json as a PreToolUse hook on Write|Edit.
    Exits 0 always -- a hook that crashes must not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$GuardedFile = 'research.json',
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
    $toolInput = Get-Prop $payload 'tool_input'
    $filePath = Get-Prop $toolInput 'file_path'
    if (-not $filePath) { exit 0 }

    $leaf = Split-Path $filePath -Leaf
    if ($leaf -ne $GuardedFile) { exit 0 }

    $reason = @"
Direct edits to $GuardedFile are blocked. Use the validating scripts instead:

  Add a node:     scripts\Add-ResearchNode.ps1 -Id <kind>.<slug> -Kind <kind> -NodePath <path> -Summary '<one line>' [-Tags a,b] [-Links id1,id2] [-Caveats '...']
  Change a node:  scripts\Update-ResearchNode.ps1 -Id <id> [-Summary|-Tags|-Links|-Caveats|-ScriptParams ...] [-Append] [-Remove field]

They validate ids, warn on dangling links and missing paths, preserve fields they do
not know about, and refuse to write a file that would not parse. A direct edit does
none of that. Add -DryRun to either to preview.

If the file is genuinely corrupt and needs manual repair, say so and ask the user.
"@

    [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 4 -Compress
}
catch {
    # A broken guard must fail open, not wedge every edit in the repo.
    exit 0
}

exit 0
