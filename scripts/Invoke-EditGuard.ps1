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

    The candidate and previous graphs are guarded on the same footing as the live one.
    research.candidate.json is the file the C# port writes while it is being built, and
    an unguarded candidate is a live graph one rename away: everything spliced into it
    by hand becomes the catalog at cutover, having passed through none of the checks
    the live file's own edits must pass. research.previous.json is the preserved copy
    the swap leaves behind, and its whole value is being a byte-exact record of what
    the catalog was -- an edit to it is not a mistake to catch later, it is the
    rollback path being quietly destroyed.

    Reads the hook payload on stdin. Emits a deny decision, or nothing at all when the
    call is fine.

.PARAMETER GuardedFile
    File names to protect. Defaults to the live graph plus the candidate and preserved
    copies the cutover uses.

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
    [string[]]$GuardedFile = @('research.json', 'research.candidate.json', 'research.previous.json',
        'research.candidate.base.json'),
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
    if ($GuardedFile -notcontains $leaf) { exit 0 }

    if ($leaf -eq 'research.previous.json' -or $leaf -eq 'research.candidate.base.json') {
        $preservedReason = @"
Edits to $leaf are blocked, and this one has no validating path.

research.previous.json is the byte-exact copy of the catalog as it stood immediately
before the cutover swap, kept so the swap can be reversed by renaming it back.
research.candidate.base.json is the common ancestor frozen when the candidate was
seeded, and the cutover diffs against it to work out which nodes the live graph
gained while the port was being built. Editing either does not improve it -- it
destroys the record the swap reasons from, which is the one thing it is for.

If the live catalog needs a change, change research.json through the validating
scripts. If the swap needs reversing, reverse the renames; do not hand-edit either
side into agreement.
"@
        [PSCustomObject]@{
            hookSpecificOutput = [PSCustomObject]@{
                hookEventName            = 'PreToolUse'
                permissionDecision       = 'deny'
                permissionDecisionReason = $preservedReason
            }
        } | ConvertTo-Json -Depth 4 -Compress
        exit 0
    }

    $candidateNote = if ($leaf -eq 'research.candidate.json') {
        "`nThis is the candidate graph the C# port writes. Pass -GraphPath to the scripts`nbelow, or use the janet CLI, so the same validation applies to it as to the live`nfile -- everything in it becomes the catalog at cutover.`n"
    }
    else { '' }

    $reason = @"
Direct edits to $leaf are blocked. Use the validating scripts instead:
$candidateNote

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
