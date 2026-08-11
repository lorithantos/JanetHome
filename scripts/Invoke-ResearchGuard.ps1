<#
.SYNOPSIS
    PreToolUse hook: refuses to let a new script be written until the catalog has
    been asked whether one already exists.

.DESCRIPTION
    Research first. Not "graph first" -- graphing is what you do once you know
    what you are building; asking the catalog is what tells you whether to build
    it at all, and it comes before everything.

    The failure this exists for, 2026-08-11: needing a library's type names, the
    session hand-rolled reflection, fought a sibling-assembly load failure, and
    wrote Get-AssemblyApi.ps1 -- while Get-ApiDoc.ps1 already sat in the catalog
    answering that exact question from the XML docs the package ships. Nothing
    was violated, because no rule existed: the manifest explained HOW to query
    the catalog and never said TO. An advisory rule would not have helped either,
    for the reason the edit guard already documents -- an agent does not skip a
    step from fatigue, it skips from rationalisation, and "I only need one type
    name" is a rationalisation.

    So the check is mechanical, and clearing it is one command. Writing a NEW
    .ps1 into a scripts directory is denied unless the catalog was queried
    recently. The denial is not a nag: it runs the query itself, on terms taken
    from the file name, and hands back what it found. Either one of those nodes
    is the answer -- in which case nothing needed writing -- or none is, and a
    single Get-Research.ps1 call clears the guard and records that the question
    was asked.

    Deliberately narrow. It fires on CREATION of a script, not on edits to
    existing ones, and not on notes or source files: the moment a duplicate tool
    becomes permanent is the moment it is authored, and a guard that fires on
    everything gets clicked past.

.PARAMETER WindowMinutes
    How recently the catalog must have been consulted. Default 60.

.PARAMETER InputJson
    Hook payload, for testing. Normally arrives on stdin.

.OUTPUTS
    A PreToolUse deny decision, or nothing when the call is fine.

.EXAMPLE
    '{"tool_name":"Write","tool_input":{"file_path":"D:\\Repos\\JanetHome\\scripts\\Get-Thing.ps1"}}' |
        & "$env:JanetBase\scripts\Invoke-ResearchGuard.ps1"

.NOTES
    Wired via .claude\settings.json as a PreToolUse hook on Write.
    Exits 0 always -- a hook that crashes must not block unrelated work.

    The trace is a single file in the temp directory, so concurrent sessions
    share it: one session's query clears another's guard. Accepted rather than
    solved, because the alternative is per-session state this hook has no
    reliable way to key, and the check is a prompt to think, not a security
    boundary.
#>
[CmdletBinding()]
param(
    [int]$WindowMinutes = 60,
    [string]$InputJson
)

Set-StrictMode -Version Latest

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# Get-AssemblyApi.ps1 -> "Assembly Api". The verb is dropped because every
# script has one and it scores nothing; what is left is what the script is
# ABOUT, which is what the catalog indexes.
function Get-SearchTerms {
    param([string]$FileName)

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    $words = [regex]::Matches($stem, '[A-Z][a-z0-9]*|[a-z0-9]+') | ForEach-Object { $_.Value }

    $verbs = @('get', 'set', 'new', 'add', 'update', 'remove', 'invoke', 'show', 'test', 'convert',
        'convertto', 'convertfrom', 'complete', 'start', 'stop', 'read', 'write', 'find', 'export', 'import')

    $kept = @($words | Where-Object { $verbs -notcontains $_.ToLowerInvariant() })
    if ($kept.Count -eq 0) { $kept = $words }

    return ($kept -join ' ')
}

try {
    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json

    # Creation only. Editing a script that exists is not the duplication risk,
    # and gating it would make the guard something to be worked around.
    $toolName = Get-Prop $payload 'tool_name'
    if ($toolName -and $toolName -ne 'Write') { exit 0 }

    $filePath = Get-Prop (Get-Prop $payload 'tool_input') 'file_path'
    if (-not $filePath) { exit 0 }
    if ([System.IO.Path]::GetExtension($filePath) -ne '.ps1') { exit 0 }
    if (Test-Path -LiteralPath $filePath) { exit 0 }

    $parent = Split-Path -Leaf (Split-Path -Parent $filePath)
    if ($parent -ne 'scripts') { exit 0 }

    # Already asked? Then the decision was informed, whatever it was.
    $tracePath = Join-Path ([System.IO.Path]::GetTempPath()) 'janet-research-trace.json'
    if (Test-Path $tracePath) {
        $trace = Get-Content $tracePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $lastUtc = Get-Prop $trace 'lastUtc'
        if ($lastUtc) {
            $age = (Get-Date).ToUniversalTime() - ([datetime]::Parse($lastUtc)).ToUniversalTime()
            if ($age.TotalMinutes -le $WindowMinutes) { exit 0 }
        }
    }

    $terms = Get-SearchTerms (Split-Path -Leaf $filePath)

    # Run the query the session should have run, so the denial arrives with the
    # answer attached rather than with homework.
    $found = ''
    try {
        $research = Join-Path $PSScriptRoot 'Get-Research.ps1'
        if (Test-Path $research) {
            $result = & $research -Query $terms -First 5 -NoTrace | ConvertFrom-Json
            if ($result.nodes -and @($result.nodes).Count -gt 0) {
                $lines = foreach ($n in $result.nodes) {
                    $summary = [string]$n.summary
                    if ($summary.Length -gt 220) { $summary = $summary.Substring(0, 220) + '...' }
                    "  [$($n.id)] $summary"
                }
                $found = "Closest existing nodes for '$terms':`n" + ($lines -join "`n")
            }
            else {
                $found = "The catalog has nothing matching '$terms'."
            }
        }
    }
    catch {
        $found = "(The guard could not query the catalog: $($_.Exception.Message))"
    }

    $reason = @"
Research first: the catalog has not been consulted this session, and this Write creates a NEW script.

$found

Either one of those already does the job -- in which case use it and write nothing -- or none does.
To proceed, ask the catalog yourself, which records that the question was asked and clears this guard:

  scripts\Get-Research.ps1 -Query '$terms'

Then re-issue the Write. If an existing node ALMOST fits, prefer extending it over adding a sibling;
if the new script is genuinely distinct, say in its research node how it differs from the near miss.

Graph-first is for once you know what you are building. This comes before that.
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
    # A broken guard must fail open, not wedge every script in the repo.
    exit 0
}

exit 0
