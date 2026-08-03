<#
.SYNOPSIS
    Adds a node to research.json, with validation, without hand-editing JSON.

.DESCRIPTION
    Growing the graph is the point of the graph, so adding to it should cost one
    command. This validates before it writes: the id must be unique and well formed,
    the referenced file should exist, and links that resolve to nothing are reported.

    Insertion is textual -- the new node is spliced in before the closing bracket of
    the nodes array rather than round-tripping the whole file through ConvertTo-Json.
    research.json is hand-curated: it has comment keys, grouped sections, and blank
    lines between kinds. Reserializing would silently flatten all of that and turn
    every add into a whole-file diff. The result is re-parsed before it is written, so
    a splice that would corrupt the file aborts instead.

.PARAMETER Id
    Node id, conventionally <kind>.<kebab-slug> (e.g. 'script.get-research').

.PARAMETER Kind
    script, pattern, note, or file.

.PARAMETER Summary
    One line. This is what retrieval matches and what a reader picks from, so write
    it as a claim, not a title.

.PARAMETER NodePath
    Repo-relative path the node points at, e.g. 'scripts\Foo.ps1'.

.PARAMETER Tags
    Tags for retrieval. Reuse existing ones where they fit -- run Get-Research.ps1
    with no arguments to see the tag index.

.PARAMETER Links
    Ids of related nodes. Links are bidirectional in spirit but stored one way;
    consider adding the reverse link to the other node too.

.PARAMETER Caveats
    What bites you. Missing dependencies, external services contacted, platform
    assumptions, things that are outright broken. A summary describes what a node is
    for, which is the least useful thing to know when it silently does not work --
    caveats are shown on every retrieval, not just the verbose view.

.PARAMETER ScriptParams
    For script nodes: parameter names.

.PARAMETER Section
    For pattern nodes: the DESIGN-NOTES section number.

.PARAMETER GraphPath
    Graph file to modify. Defaults to research.json in the repo root.

.PARAMETER DryRun
    Validate and print the node that would be added, without writing.

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ResearchNode.ps1" -Id script.new-thing -Kind script `
        -NodePath 'scripts\New-Thing.ps1' -Summary 'Does the thing.' -Tags files,encoding

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ResearchNode.ps1" -Id note.some-finding -Kind note `
        -NodePath 'notes\some-finding.md' -Summary 'What I learned.' -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Id,

    [Parameter(Mandatory)]
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,

    [Parameter(Mandatory)]
    [string]$Summary,

    [Parameter(Mandatory)]
    [string]$NodePath,

    [string[]]$Tags = @(),
    [string[]]$Links = @(),
    [string[]]$Caveats = @(),
    [string[]]$ScriptParams = @(),
    [string]$Section,
    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $GraphPath) { $GraphPath = Join-Path $repoRoot 'research.json' }

if (-not (Test-Path $GraphPath)) { throw "Research graph not found: $GraphPath" }

$raw = Get-Content $GraphPath -Raw -Encoding UTF8
try {
    $graph = $raw | ConvertFrom-Json
}
catch {
    throw "Research graph is not valid JSON ($GraphPath): $($_.Exception.Message)"
}

$nodes = @($graph.nodes)

# ---- Validate --------------------------------------------------------------

$problems = @()
$warnings = @()

if ($nodes.id -contains $Id) { $problems += "id '$Id' already exists" }
if ($Id -notmatch '^[a-z]+\.[a-z0-9-]+$') {
    $warnings += "id '$Id' does not match the <kind>.<kebab-slug> convention"
}
elseif ($Id -notlike "$Kind.*") {
    $warnings += "id '$Id' does not start with its kind ('$Kind.')"
}

$fullNodePath = Join-Path $repoRoot $NodePath
if (-not (Test-Path $fullNodePath)) {
    $warnings += "path does not exist: $NodePath"
}

$knownIds = @($nodes.id)
foreach ($link in $Links) {
    if ($knownIds -notcontains $link) { $warnings += "link target does not exist: $link" }
}

if ($problems.Count -gt 0) {
    throw "Cannot add node:$([Environment]::NewLine)$(($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine)"
}

# Warnings go in the returned payload, not only to the warning stream: the caller is
# usually a model reading stdout, and a warning it cannot see is a warning that did
# not happen. Still emitted to the stream as well, for -Text use at a terminal.
if ($Text) { foreach ($w in $warnings) { Write-Warning $w } }

# ---- Build the node text ---------------------------------------------------

# Round-tripping each scalar through ConvertTo-Json gets quoting and escaping right
# without hand-rolling an encoder.
function ConvertTo-JsonScalar { param([string]$Value) return ($Value | ConvertTo-Json) }

function ConvertTo-JsonArray {
    param([string[]]$Values)
    if ($Values.Count -eq 0) { return '[]' }
    return '[' + (($Values | ForEach-Object { ConvertTo-JsonScalar $_ }) -join ', ') + ']'
}

$lines = @()
$lines += '    {'
$lines += '      "id": ' + (ConvertTo-JsonScalar $Id) + ','
$lines += '      "kind": ' + (ConvertTo-JsonScalar $Kind) + ','
$lines += '      "path": ' + (ConvertTo-JsonScalar $NodePath) + ','
if ($Section) { $lines += '      "section": ' + (ConvertTo-JsonScalar $Section) + ',' }
$lines += '      "summary": ' + (ConvertTo-JsonScalar $Summary) + ','
if ($ScriptParams.Count -gt 0) { $lines += '      "params": ' + (ConvertTo-JsonArray $ScriptParams) + ',' }
$lines += '      "tags": ' + (ConvertTo-JsonArray $Tags) + ','
if ($Caveats.Count -gt 0) {
    # One caveat per line: they are prose, and joining them onto one line makes the
    # file unreadable exactly where readability matters most.
    $lines += '      "caveats": ['
    for ($i = 0; $i -lt $Caveats.Count; $i++) {
        $comma = if ($i -lt $Caveats.Count - 1) { ',' } else { '' }
        $lines += '        ' + (ConvertTo-JsonScalar $Caveats[$i]) + $comma
    }
    $lines += '      ],'
}
$lines += '      "links": ' + (ConvertTo-JsonArray $Links)
$lines += '    }'

$nodeText = $lines -join [Environment]::NewLine

if ($DryRun) {
    if ($Text) {
        Write-Host ''
        Write-Host "Would add to $GraphPath :" -ForegroundColor Cyan
        Write-Host $nodeText
        Write-Host ''
        return
    }
    [PSCustomObject]@{
        added    = $false
        dryRun   = $true
        id       = $Id
        warnings = @($warnings)
        nodeText = $nodeText
    } | ConvertTo-Json -Depth 4 -Compress
    return
}

# ---- Splice ----------------------------------------------------------------

# Locate the ']' that closes the nodes array: the last ']' in the file, since the
# array is the final structure before the closing brace.
$closeIndex = $raw.LastIndexOf(']')
if ($closeIndex -lt 0) { throw "Could not find the end of the nodes array in $GraphPath" }

$before = $raw.Substring(0, $closeIndex)
$after = $raw.Substring($closeIndex)

# $before ends with the previous node's '}' plus whitespace. Splice in a comma and
# the new node, keeping the existing trailing whitespace shape.
$trimmed = $before.TrimEnd()
if (-not $trimmed.EndsWith('}')) {
    throw "Unexpected structure before the end of the nodes array; refusing to edit $GraphPath"
}

$nl = [Environment]::NewLine
$updated = $trimmed + ',' + $nl + $nl + $nodeText + $nl + '  ' + $after.TrimStart()

# Never write a file we just broke.
try {
    $check = $updated | ConvertFrom-Json
}
catch {
    throw "Splice would produce invalid JSON; $GraphPath left unchanged. $($_.Exception.Message)"
}

$newCount = @($check.nodes).Count
if ($newCount -ne ($nodes.Count + 1)) {
    throw "Splice produced $newCount nodes, expected $($nodes.Count + 1); $GraphPath left unchanged"
}

[System.IO.File]::WriteAllText($GraphPath, $updated, (New-Object System.Text.UTF8Encoding $false))

if ($Text) {
    Write-Host "Added $Id ($newCount nodes total)" -ForegroundColor Green
    if ($Links.Count -gt 0) {
        Write-Host "Consider adding the reverse link on: $($Links -join ', ')" -ForegroundColor DarkGray
    }
    return
}

[PSCustomObject]@{
    added        = $true
    dryRun       = $false
    id           = $Id
    totalNodes   = $newCount
    warnings     = @($warnings)
    reverseLinks = @($Links)   # links are stored one way; these nodes may want the reverse
} | ConvertTo-Json -Depth 4 -Compress
