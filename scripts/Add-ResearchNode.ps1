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
    Ids of related nodes. The reverse link is written on each target automatically,
    so the graph stays navigable from both ends without a follow-up call. Targets
    that do not exist are warned about and skipped.

.PARAMETER Caveats
    What bites you. Missing dependencies, external services contacted, platform
    assumptions, things that are outright broken. A summary describes what a node is
    for, which is the least useful thing to know when it silently does not work --
    caveats are shown on every retrieval, not just the verbose view.

.PARAMETER ScriptParams
    For script nodes: parameter names.

.PARAMETER Section
    For pattern nodes: the DESIGN-NOTES section number.

.PARAMETER Json
    The whole node as a JSON object, instead of the parameters above:
    { id, kind, summary, path, tags[], links[], caveats[], params[], section }.

    PREFER THIS when any text is long or contains quotes. A summary is prose --
    apostrophes, quoted terms, code fragments -- and routing prose through
    PowerShell's quoting rules is how it gets corrupted: this repo's own catalog
    carried "assembly''s" for an hour because a single-quoted here-string doubled
    an apostrophe on the way in, and nothing downstream could tell that from
    intended text. JSON has exactly one escaping rule, the parser enforces it,
    and a malformed blob fails loudly here rather than writing damaged prose.
    Same reason New-TextFile.ps1 offers -Base64.

.PARAMETER JsonPath
    A file containing that same JSON object, for content too large or too
    quote-heavy to pass on a command line at all.

.PARAMETER GraphPath
    Graph file to modify. Defaults to research.json in the repo root.

.PARAMETER DryRun
    Validate and print the node that would be added, without writing.

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ResearchNode.ps1" -Id script.new-thing -Kind script `
        -NodePath 'scripts\New-Thing.ps1' -Summary 'Does the thing.' -Tags files,encoding

.EXAMPLE
    # Prose with quotes in it: pass the blob, not eleven quoted parameters.
    & "$env:JanetBase\scripts\Add-ResearchNode.ps1" -Json @'
    { "id": "note.finding", "kind": "note", "path": "notes\\finding.md",
      "summary": "The parser's \"strict\" mode isn't.", "tags": ["parsing"] }
'@

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ResearchNode.ps1" -Id note.some-finding -Kind note `
        -NodePath 'notes\some-finding.md' -Summary 'What I learned.' -DryRun
#>
[CmdletBinding(DefaultParameterSetName = 'Fields')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Fields')]
    [string]$Id,

    [Parameter(Mandatory, ParameterSetName = 'Fields')]
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,

    [Parameter(Mandatory, ParameterSetName = 'Fields')]
    [string]$Summary,

    [Parameter(Mandatory, ParameterSetName = 'Fields')]
    [string]$NodePath,

    [Parameter(ParameterSetName = 'Fields')]
    [string[]]$Tags = @(),

    [Parameter(ParameterSetName = 'Fields')]
    [string[]]$Links = @(),

    [Parameter(ParameterSetName = 'Fields')]
    [string[]]$Caveats = @(),

    [Parameter(ParameterSetName = 'Fields')]
    [string[]]$ScriptParams = @(),

    [Parameter(ParameterSetName = 'Fields')]
    [string]$Section,

    [Parameter(Mandatory, ParameterSetName = 'Json')]
    [string]$Json,

    [Parameter(Mandatory, ParameterSetName = 'JsonFile')]
    [string]$JsonPath,

    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -ne 'Fields') {
    $rawJson = if ($PSCmdlet.ParameterSetName -eq 'JsonFile') {
        if (-not (Test-Path -LiteralPath $JsonPath)) { throw "JSON file not found: $JsonPath" }
        Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8
    }
    else { $Json }

    try { $blob = $rawJson | ConvertFrom-Json }
    catch { throw "The node JSON does not parse: $($_.Exception.Message)" }

    if ($null -eq $blob -or $blob -isnot [pscustomobject]) {
        throw 'The node JSON must be a single object: { "id": ..., "kind": ..., "summary": ..., "path": ... }'
    }

    # Strict mode turns a missing property into a terminating error, so every
    # read goes through here -- an optional field being absent is normal input,
    # not a failure.
    function Get-BlobField {
        param([pscustomobject]$Blob, [string]$Name)
        if ($Blob.PSObject.Properties.Name -contains $Name) { return $Blob.$Name }
        return $null
    }

    # Read and validate into locals FIRST. A parameter's [ValidateSet] fires on
    # every assignment to that variable, not just on binding, so assigning an
    # empty kind here would surface as PowerShell's validation error instead of
    # the message naming which fields the blob is missing.
    $blobId = [string](Get-BlobField $blob 'id')
    $blobKind = [string](Get-BlobField $blob 'kind')
    $blobSummary = [string](Get-BlobField $blob 'summary')
    $blobPath = [string](Get-BlobField $blob 'path')

    $missing = @()
    foreach ($pair in @{ id = $blobId; kind = $blobKind; summary = $blobSummary; path = $blobPath }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace($pair.Value)) { $missing += $pair.Key }
    }
    if ($missing.Count -gt 0) {
        throw "The node JSON is missing required field(s): $(($missing | Sort-Object) -join ', '). Required: id, kind, summary, path."
    }

    $validKinds = @('script', 'pattern', 'note', 'file', 'skill')
    if ($validKinds -notcontains $blobKind) {
        throw "Unknown kind '$blobKind'. Valid kinds: $($validKinds -join ', ')."
    }

    $Id = $blobId
    $Kind = $blobKind
    $Summary = $blobSummary
    $NodePath = $blobPath

    # @($null) is an array holding one null, which serialises as [""] and writes
    # an empty tag into the file. An absent list must stay empty.
    function Get-BlobList {
        param([pscustomobject]$Blob, [string]$Name)
        $value = Get-BlobField $Blob $Name
        if ($null -eq $value) { return , @() }
        return , @(@($value) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })
    }

    $Tags = Get-BlobList $blob 'tags'
    $Links = Get-BlobList $blob 'links'
    $Caveats = Get-BlobList $blob 'caveats'
    $ScriptParams = Get-BlobList $blob 'params'
    $Section = [string](Get-BlobField $blob 'section')
}

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
        added        = $false
        dryRun       = $true
        id           = $Id
        warnings     = @($warnings)
        nodeText     = $nodeText
        reverseLinks = @($Links | Where-Object { $knownIds -contains $_ })
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

# ---- Reverse links ---------------------------------------------------------

# The graph's convention is bidirectional -- pattern.thread-items and
# skill.crash-escape-analysis each name the other -- so writing only the forward
# direction leaves the new node invisible from its own neighbours. Doing it here
# rather than leaving a reminder in the output is the difference between an
# invariant and a habit.
#
# Delegating to Update-ResearchNode keeps one textual-splice implementation
# instead of two, and inherits its de-duplication (-Append drops repeats), so
# re-adding an existing edge is a no-op rather than a double entry.
$reverseLinked = @()
$updateScript = Join-Path $PSScriptRoot 'Update-ResearchNode.ps1'

foreach ($link in $Links) {
    # Missing targets already produced a warning during validation; the node was
    # still added, so a dangling link stays visible rather than silently dropped.
    if ($knownIds -notcontains $link) { continue }

    try {
        $back = & $updateScript -Id $link -Links $Id -Append -GraphPath $GraphPath |
            ConvertFrom-Json
        if ($back.updated) { $reverseLinked += $link }
    }
    catch {
        # A failed back-link must not undo a successful add: report and continue.
        $warnings += "reverse link on '$link' failed: $($_.Exception.Message)"
    }
}

if ($Text) {
    Write-Host "Added $Id ($newCount nodes total)" -ForegroundColor Green
    if ($reverseLinked.Count -gt 0) {
        Write-Host "Reverse-linked from: $($reverseLinked -join ', ')" -ForegroundColor DarkGray
    }
    foreach ($w in $warnings) { Write-Host "! $w" -ForegroundColor Yellow }
    return
}

[PSCustomObject]@{
    added        = $true
    dryRun       = $false
    id           = $Id
    totalNodes   = $newCount
    warnings     = @($warnings)
    reverseLinks = @($reverseLinked)   # targets that now name this node back
} | ConvertTo-Json -Depth 4 -Compress
