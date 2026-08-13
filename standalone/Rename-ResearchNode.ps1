<#
.SYNOPSIS
    Renames a node id in research.json, sweeping every inbound link, without hand-editing JSON.

.DESCRIPTION
    The missing third of the Add/Update family. An id change is not an edit to one
    node: every links array that mentions the old id must move with it, and a rename
    that misses one leaves a dangling link that reads like a deleted node. Doing that
    sweep by hand carries the same hazards as any other hand edit of the graph, so it
    is scripted -- and because ids conventionally carry the kind as a prefix, a rename
    is usually a re-kind too, so -Kind rides along in the same operation.

    Like Add and Update, this splices textually rather than round-tripping the file
    through ConvertTo-Json, so hand-curated grouping, comment keys, and blank lines
    survive. Each affected node is edited inside its own brace-matched span only.

    The graph is the script's jurisdiction; note bodies are not. Markdown files that
    mention the old id are reported in the output as bodyReferences -- fix those with
    Invoke-SurgicalEdit.ps1 or by hand, then re-run the scan to confirm zero.

    Output is JSON by default; -Text for a formatted summary.

.PARAMETER Id
    Current id of the node. Must exist.

.PARAMETER NewId
    The id to rename it to. Must not already exist. Warned about when its prefix does
    not match the node's kind, since ids conventionally carry the kind as a prefix.

.PARAMETER Kind
    Also replace the kind -- the usual companion of a rename. Same set as Add/Update.

.PARAMETER GraphPath
    Graph file to modify. Defaults to research.json in the repo root.

.PARAMETER DryRun
    Report what would change without writing.

.PARAMETER Text
    Formatted output instead of JSON.

.OUTPUTS
    JSON: { renamed, dryRun, id, newId, kind, linksUpdated[], bodyReferences[], warnings[], totalNodes }
    Each bodyReference is { file, count }.

.EXAMPLE
    & "$env:JanetBase\scripts\Rename-ResearchNode.ps1" -Id note.christmas-tree-flattening `
        -NewId skill.christmas-tree-flattening -Kind skill -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Id,

    [Parameter(Mandatory)]
    [string]$NewId,

    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,

    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $GraphPath) { $GraphPath = Join-Path $repoRoot 'research.json' }
if (-not (Test-Path $GraphPath)) { throw "Research graph not found: $GraphPath" }

if ($Id -eq $NewId) { throw "NewId is the same as Id; nothing to rename." }

$raw = Get-Content $GraphPath -Raw -Encoding UTF8
try { $graph = $raw | ConvertFrom-Json }
catch { throw "Research graph is not valid JSON ($GraphPath): $($_.Exception.Message)" }

$nodes = @($graph.nodes)
$node = $nodes | Where-Object { $_.id -eq $Id } | Select-Object -First 1
if (-not $node) { throw "No node with id '$Id'. Use Get-Research.ps1 to find the right id." }
if ($nodes | Where-Object { $_.id -eq $NewId }) { throw "A node with id '$NewId' already exists." }

$warnings = @()

# Ids conventionally carry the kind as a prefix; a mismatch is legal but suspicious.
$effectiveKind = if ($PSBoundParameters.ContainsKey('Kind')) { $Kind } else { [string]$node.kind }
if (-not $NewId.StartsWith("$effectiveKind.")) {
    $warnings += "NewId '$NewId' does not carry the kind prefix '$effectiveKind.'"
}

function Get-Field {
    param($Node, [string]$Name, $Default = $null)
    if ($Node.PSObject.Properties.Name -contains $Name -and $null -ne $Node.$Name) { return $Node.$Name }
    return $Default
}

# ---- Locate a node's text span ---------------------------------------------

# Brace-matching has to be string-aware: summaries and caveats can contain braces and
# escaped quotes, and a naive counter would land in the wrong place and corrupt a
# neighbouring node.
function Find-NodeSpan {
    param([string]$Text, [string]$NodeId)

    $needle = '"id": "' + $NodeId + '"'
    $idx = $Text.IndexOf($needle)
    if ($idx -lt 0) { throw "Could not locate '$NodeId' in the graph text" }
    if ($Text.IndexOf($needle, $idx + 1) -ge 0) { throw "id '$NodeId' appears more than once in the graph text" }

    $start = $Text.LastIndexOf('{', $idx)
    if ($start -lt 0) { throw "Malformed graph: no opening brace before '$NodeId'" }

    $depth = 0
    $inString = $false
    $escaped = $false
    for ($i = $start; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($escaped) { $escaped = $false; continue }
        if ($ch -eq '\') { if ($inString) { $escaped = $true }; continue }
        if ($ch -eq '"') { $inString = -not $inString; continue }
        if ($inString) { continue }
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) { return @{ start = $start; end = $i } }
        }
    }
    throw "Malformed graph: unterminated node object for '$NodeId'"
}

# Replace text inside one node's span only, returning the whole updated document.
# Spans are recomputed after every edit, so earlier splices never stale later offsets.
function Edit-NodeSpan {
    param([string]$Text, [string]$NodeId, [string]$Find, [string]$ReplaceWith)

    $span = Find-NodeSpan -Text $Text -NodeId $NodeId
    $segment = $Text.Substring($span.start, $span.end - $span.start + 1)
    $edited = $segment.Replace($Find, $ReplaceWith)
    return $Text.Substring(0, $span.start) + $edited + $Text.Substring($span.end + 1)
}

# ---- Apply: the node itself, then every inbound link ------------------------

$updated = Edit-NodeSpan -Text $raw -NodeId $Id -Find ('"id": "' + $Id + '"') -ReplaceWith ('"id": "' + $NewId + '"')

if ($PSBoundParameters.ContainsKey('Kind')) {
    $oldKind = [string]$node.kind
    $updated = Edit-NodeSpan -Text $updated -NodeId $NewId `
        -Find ('"kind": "' + $oldKind + '"') -ReplaceWith ('"kind": "' + $Kind + '"')
}

# The quoted-id token '"old.id"' only matches a standalone JSON string element, so a
# prose mention inside a summary (which has text around it) is never touched. The
# reparse below is the backstop either way.
$linkers = @($nodes | Where-Object { @(Get-Field $_ 'links' @()) -contains $Id })
$quotedOld = '"' + $Id + '"'
$quotedNew = '"' + $NewId + '"'
foreach ($linker in $linkers) {
    $linkerId = if ($linker.id -eq $Id) { $NewId } else { $linker.id }
    $updated = Edit-NodeSpan -Text $updated -NodeId $linkerId -Find $quotedOld -ReplaceWith $quotedNew
}

# ---- Verify the whole document before writing -------------------------------

try { $check = $updated | ConvertFrom-Json }
catch { throw "Rename would produce invalid JSON; $GraphPath left unchanged. $($_.Exception.Message)" }

$checkNodes = @($check.nodes)
if ($checkNodes.Count -ne $nodes.Count) {
    throw "Rename changed the node count from $($nodes.Count) to $($checkNodes.Count); $GraphPath left unchanged"
}
if (-not ($checkNodes | Where-Object { $_.id -eq $NewId })) {
    throw "Renamed node '$NewId' not found after splice; $GraphPath left unchanged"
}
if ($checkNodes | Where-Object { $_.id -eq $Id }) {
    throw "Old id '$Id' still present after splice; $GraphPath left unchanged"
}
$stillLinked = @($checkNodes | Where-Object { @(Get-Field $_ 'links' @()) -contains $Id })
if ($stillLinked.Count -gt 0) {
    throw "Old id '$Id' still linked from: $($stillLinked.id -join ', '); $GraphPath left unchanged"
}

# ---- Body references: report, don't touch -----------------------------------

$bodyReferences = @(
    Get-ChildItem $repoRoot -Recurse -Filter *.md -File |
        Where-Object { $_.FullName -notmatch '\\\.git\\' } |
        ForEach-Object {
            $hits = @(Select-String -Path $_.FullName -SimpleMatch $Id)
            if ($hits.Count -gt 0) {
                [PSCustomObject]@{
                    file = $_.FullName.Substring($repoRoot.Length + 1)
                    count = $hits.Count
                }
            }
        }
)
if ($bodyReferences.Count -gt 0) {
    $warnings += "old id still appears in $($bodyReferences.Count) markdown file(s) -- fix bodies separately"
}

$result = [PSCustomObject]@{
    renamed = -not $DryRun; dryRun = [bool]$DryRun
    id = $Id; newId = $NewId
    kind = $effectiveKind
    linksUpdated = @($linkers | ForEach-Object { if ($_.id -eq $Id) { $NewId } else { $_.id } })
    bodyReferences = @($bodyReferences)
    warnings = @($warnings)
    totalNodes = $checkNodes.Count
}

if (-not $DryRun) {
    [System.IO.File]::WriteAllText($GraphPath, $updated, (New-Object System.Text.UTF8Encoding $false))
}

if ($Text) {
    $verb = if ($DryRun) { 'Would rename' } else { 'Renamed' }
    Write-Host "$verb $Id -> $NewId" -ForegroundColor $(if ($DryRun) { 'Cyan' } else { 'Green' })
    if ($result.linksUpdated.Count -gt 0) { Write-Host "  links updated on: $($result.linksUpdated -join ', ')" }
    foreach ($b in $bodyReferences) { Write-Host "  body reference: $($b.file) ($($b.count))" }
    foreach ($w in $warnings) { Write-Warning $w }
    return
}
$result | ConvertTo-Json -Depth 5 -Compress
