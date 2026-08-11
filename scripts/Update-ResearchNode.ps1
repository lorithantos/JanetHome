<#
.SYNOPSIS
    Updates an existing node in research.json, with validation, without hand-editing JSON.

.DESCRIPTION
    The counterpart to Add-ResearchNode.ps1. Editing a node by hand carries exactly the
    same hazards as adding one by hand, and happens more often -- summaries go stale,
    parameter lists drift, caveats get discovered later.

    Like Add, this splices textually rather than round-tripping the file through
    ConvertTo-Json, so the hand-curated grouping, comment keys, and blank lines survive
    and a one-field change stays a one-node diff.

    Fields not named on the command line are preserved exactly, INCLUDING fields this
    script has never heard of. Growing the node schema must never mean an update silently
    drops the new field.

    Output is JSON by default; -Text for a formatted summary of what changed.

.PARAMETER Id
    Id of the node to update. Must exist.

.PARAMETER Summary
    Replace the one-line summary.

.PARAMETER NodePath
    Replace the repo-relative path.

.PARAMETER Kind
    Replace the kind. Note this does not rename the id, which conventionally carries the
    kind as its prefix -- you will usually want to reconsider the id too.

.PARAMETER Section
    Replace the DESIGN-NOTES section number.

.PARAMETER Tags
    Replace the tags, or add to them with -Append.

.PARAMETER Links
    Replace the links, or add to them with -Append.

.PARAMETER Caveats
    Replace the caveats, or add to them with -Append.

.PARAMETER ScriptParams
    Replace the params list, or add to it with -Append.

.PARAMETER Append
    Array parameters add to what is there instead of replacing it. Duplicates are
    dropped. Without this, arrays are replaced wholesale.

.PARAMETER Remove
    Field names to delete from the node entirely, e.g. -Remove caveats. Cannot remove
    id, kind, path, or summary.

.PARAMETER Json
    The changes as a JSON object -- { id, summary, tags[], caveats[], ... } --
    instead of the parameters above. Only the fields present are changed, so it
    is a patch, not a replacement of the node.

    PREFER THIS for prose. A summary is written text, and PowerShell's quoting
    rules mangle written text: this catalog carried "assembly''s" because a
    single-quoted here-string doubled an apostrophe on the way in, and no
    downstream check could tell corruption from intent. JSON has one escaping
    rule and the parser enforces it.

    -Id may be given either as a parameter or as the blob's "id"; the parameter
    wins if both appear.

.PARAMETER JsonPath
    A file holding that same JSON object, for text too large or quote-heavy to
    put on a command line.

.PARAMETER GraphPath
    Graph file to modify. Defaults to research.json in the repo root.

.PARAMETER DryRun
    Report what would change without writing.

.PARAMETER Text
    Formatted output instead of JSON.

.OUTPUTS
    JSON: { updated, dryRun, id, changes[], warnings[], totalNodes }
    Each change is { field, from, to }.

.EXAMPLE
    & "$env:JanetBase\scripts\Update-ResearchNode.ps1" -Id script.get-research `
        -ScriptParams Id,Tag,Kind,Query,First,All,Expand,Depth,Full,Text,Pretty,Path

.EXAMPLE
    & "$env:JanetBase\scripts\Update-ResearchNode.ps1" -Id script.new-text-file `
        -Caveats 'Base64 mode is the only reliable path for content with quotes.' -Append

.EXAMPLE
    & "$env:JanetBase\scripts\Update-ResearchNode.ps1" -Id script.foo -Remove caveats -DryRun
#>
[CmdletBinding()]
param(
    [string]$Id,

    [string]$Summary,
    [string]$NodePath,

    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,

    [string]$Section,
    [string[]]$Tags,
    [string[]]$Links,
    [string[]]$Caveats,
    [string[]]$ScriptParams,

    [string]$Json,
    [string]$JsonPath,

    [switch]$Append,
    [string[]]$Remove = @(),
    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Json -and $JsonPath) { throw 'Give -Json or -JsonPath, not both.' }

if ($Json -or $JsonPath) {
    $rawJson = if ($JsonPath) {
        if (-not (Test-Path -LiteralPath $JsonPath)) { throw "JSON file not found: $JsonPath" }
        Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8
    }
    else { $Json }

    try { $patch = $rawJson | ConvertFrom-Json }
    catch { throw "The patch JSON does not parse: $($_.Exception.Message)" }

    if ($null -eq $patch -or $patch -isnot [pscustomobject]) {
        throw 'The patch JSON must be a single object, e.g. { "id": "script.foo", "summary": "..." }'
    }

    $present = $patch.PSObject.Properties.Name

    # Only fields the blob actually carries are touched. Everything else keeps
    # whatever the parameters said, or stays unset -- a patch, not a template
    # that blanks the fields it forgot to mention.
    #
    # Each one is registered in PSBoundParameters as well as assigned, because
    # "was this field asked for" is decided downstream by ContainsKey, not by
    # the variable having a value -- that is what distinguishes clearing a field
    # from leaving it alone. Assigning only the variable applies nothing at all,
    # silently: the first run of this patch path reported 0 changes and wrote
    # nothing while claiming success.
    function Set-Patched {
        param([string]$Name, $Value)
        Set-Variable -Name $Name -Value $Value -Scope 1
        $PSCmdlet.MyInvocation.BoundParameters[$Name] = $Value
    }

    if ($present -contains 'id' -and -not $Id) { $Id = [string]$patch.id }
    if ($present -contains 'summary') { Set-Patched 'Summary' ([string]$patch.summary) }
    if ($present -contains 'path') { Set-Patched 'NodePath' ([string]$patch.path) }
    if ($present -contains 'section') { Set-Patched 'Section' ([string]$patch.section) }

    if ($present -contains 'kind') {
        $patchKind = [string]$patch.kind
        $validKinds = @('script', 'pattern', 'note', 'file', 'skill')
        if ($validKinds -notcontains $patchKind) {
            throw "Unknown kind '$patchKind'. Valid kinds: $($validKinds -join ', ')."
        }
        Set-Patched 'Kind' $patchKind
    }

    # @($null) holds one null and would write an empty string into the array.
    foreach ($pair in @(@{ Field = 'tags'; Var = 'Tags' }, @{ Field = 'links'; Var = 'Links' },
            @{ Field = 'caveats'; Var = 'Caveats' }, @{ Field = 'params'; Var = 'ScriptParams' })) {
        if ($present -notcontains $pair.Field) { continue }
        $values = @(@($patch.($pair.Field)) |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
                ForEach-Object { [string]$_ })
        Set-Patched $pair.Var $values
    }
}

if (-not $Id) { throw 'An id is required: pass -Id, or include "id" in the JSON.' }

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $GraphPath) { $GraphPath = Join-Path $repoRoot 'research.json' }
if (-not (Test-Path $GraphPath)) { throw "Research graph not found: $GraphPath" }

$raw = Get-Content $GraphPath -Raw -Encoding UTF8
try { $graph = $raw | ConvertFrom-Json }
catch { throw "Research graph is not valid JSON ($GraphPath): $($_.Exception.Message)" }

$nodes = @($graph.nodes)
$node = $nodes | Where-Object { $_.id -eq $Id } | Select-Object -First 1
if (-not $node) { throw "No node with id '$Id'. Use Get-Research.ps1 to find the right id." }

function Get-Field {
    param($Node, [string]$Name, $Default = $null)
    if ($Node.PSObject.Properties.Name -contains $Name -and $null -ne $Node.$Name) { return $Node.$Name }
    return $Default
}

# ---- Locate the node's text span -------------------------------------------

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

$span = Find-NodeSpan -Text $raw -NodeId $Id

# ---- Compute the new field set ---------------------------------------------

$changes = @()
$warnings = @()

function Resolve-Array {
    param([string]$Name, $Incoming, $Existing)
    $current = @($Existing)
    if ($Append) {
        $merged = @($current) + @($Incoming) | Select-Object -Unique
        return , @($merged)
    }
    return , @($Incoming)
}

# Start from every existing property so unknown fields survive untouched.
$fields = [ordered]@{}
foreach ($prop in $node.PSObject.Properties) { $fields[$prop.Name] = $prop.Value }

function Set-Field {
    param([string]$Name, $Value)
    $old = if ($fields.Contains($Name)) { $fields[$Name] } else { $null }
    $oldText = if ($old -is [array]) { ($old -join ', ') } else { [string]$old }
    $newText = if ($Value -is [array]) { ($Value -join ', ') } else { [string]$Value }
    if ($oldText -ne $newText) {
        $script:changes += [PSCustomObject]@{ field = $Name; from = $oldText; to = $newText }
    }
    $fields[$Name] = $Value
}

if ($PSBoundParameters.ContainsKey('Summary')) { Set-Field 'summary' $Summary }
if ($PSBoundParameters.ContainsKey('Kind')) { Set-Field 'kind' $Kind }
if ($PSBoundParameters.ContainsKey('Section')) { Set-Field 'section' $Section }

if ($PSBoundParameters.ContainsKey('NodePath')) {
    Set-Field 'path' $NodePath
    if (-not (Test-Path (Join-Path $repoRoot $NodePath))) { $warnings += "path does not exist: $NodePath" }
}

if ($PSBoundParameters.ContainsKey('Tags')) { Set-Field 'tags' (Resolve-Array 'tags' $Tags (Get-Field $node 'tags' @())) }
if ($PSBoundParameters.ContainsKey('Caveats')) { Set-Field 'caveats' (Resolve-Array 'caveats' $Caveats (Get-Field $node 'caveats' @())) }
if ($PSBoundParameters.ContainsKey('ScriptParams')) { Set-Field 'params' (Resolve-Array 'params' $ScriptParams (Get-Field $node 'params' @())) }

if ($PSBoundParameters.ContainsKey('Links')) {
    $resolved = Resolve-Array 'links' $Links (Get-Field $node 'links' @())
    Set-Field 'links' $resolved
    $knownIds = @($nodes.id)
    foreach ($l in @($resolved)) {
        if ($knownIds -notcontains $l) { $warnings += "link target does not exist: $l" }
    }
}

$protected = @('id', 'kind', 'path', 'summary')
foreach ($r in $Remove) {
    if ($protected -contains $r) { throw "Refusing to remove required field '$r'" }
    if ($fields.Contains($r)) {
        $changes += [PSCustomObject]@{ field = $r; from = ([string]$fields[$r]); to = '(removed)' }
        $fields.Remove($r)
    }
    else { $warnings += "field not present, nothing to remove: $r" }
}

if ($changes.Count -eq 0) {
    $result = [PSCustomObject]@{
        updated = $false; dryRun = [bool]$DryRun; id = $Id
        changes = @(); warnings = @($warnings) + 'no changes requested'
        totalNodes = $nodes.Count
    }
    if ($Text) { Write-Host "No changes for $Id." -ForegroundColor Yellow; return }
    $result | ConvertTo-Json -Depth 5 -Compress
    return
}

# ---- Render the node text --------------------------------------------------

function ConvertTo-JsonScalar { param([string]$Value) return ($Value | ConvertTo-Json) }
function ConvertTo-JsonArray {
    param($Values)
    $v = @($Values)
    if ($v.Count -eq 0) { return '[]' }
    return '[' + (($v | ForEach-Object { ConvertTo-JsonScalar ([string]$_) }) -join ', ') + ']'
}

# Canonical order first, then anything else the schema has grown, so an unknown field
# is preserved rather than quietly dropped.
$order = @('id', 'kind', 'path', 'section', 'summary', 'params', 'caveats', 'tags', 'links')
$emitOrder = @($order | Where-Object { $fields.Contains($_) })
$emitOrder += @($fields.Keys | Where-Object { $order -notcontains $_ })

$lines = @('    {')
for ($i = 0; $i -lt $emitOrder.Count; $i++) {
    $name = $emitOrder[$i]
    $value = $fields[$name]
    $comma = if ($i -lt $emitOrder.Count - 1) { ',' } else { '' }

    $isArray = $value -is [System.Collections.IEnumerable] -and $value -isnot [string]

    if ($name -eq 'caveats' -and $isArray -and @($value).Count -gt 0) {
        # One caveat per line: they are prose, and the file stays readable where it
        # matters most.
        $lines += '      "caveats": ['
        $cv = @($value)
        for ($c = 0; $c -lt $cv.Count; $c++) {
            $cComma = if ($c -lt $cv.Count - 1) { ',' } else { '' }
            $lines += '        ' + (ConvertTo-JsonScalar ([string]$cv[$c])) + $cComma
        }
        $lines += '      ]' + $comma
    }
    elseif ($isArray) {
        $lines += '      ' + (ConvertTo-JsonScalar $name) + ': ' + (ConvertTo-JsonArray $value) + $comma
    }
    else {
        $lines += '      ' + (ConvertTo-JsonScalar $name) + ': ' + (ConvertTo-JsonScalar ([string]$value)) + $comma
    }
}
$lines += '    }'
$nodeText = $lines -join [Environment]::NewLine

if ($DryRun) {
    $result = [PSCustomObject]@{
        updated = $false; dryRun = $true; id = $Id
        changes = @($changes); warnings = @($warnings)
        totalNodes = $nodes.Count; nodeText = $nodeText
    }
    if ($Text) {
        Write-Host ''
        Write-Host "Would update $Id :" -ForegroundColor Cyan
        foreach ($c in $changes) { Write-Host "  $($c.field): '$($c.from)' -> '$($c.to)'" }
        foreach ($w in $warnings) { Write-Warning $w }
        Write-Host ''
        Write-Host $nodeText
        Write-Host ''
        return
    }
    $result | ConvertTo-Json -Depth 5 -Compress
    return
}

# ---- Splice and verify -----------------------------------------------------

$updated = $raw.Substring(0, $span.start) + $nodeText.TrimStart() + $raw.Substring($span.end + 1)

try { $check = $updated | ConvertFrom-Json }
catch { throw "Update would produce invalid JSON; $GraphPath left unchanged. $($_.Exception.Message)" }

$checkNodes = @($check.nodes)
if ($checkNodes.Count -ne $nodes.Count) {
    throw "Update changed the node count from $($nodes.Count) to $($checkNodes.Count); $GraphPath left unchanged"
}
$checkNode = $checkNodes | Where-Object { $_.id -eq $Id } | Select-Object -First 1
if (-not $checkNode) { throw "Updated node '$Id' not found after splice; $GraphPath left unchanged" }

[System.IO.File]::WriteAllText($GraphPath, $updated, (New-Object System.Text.UTF8Encoding $false))

$result = [PSCustomObject]@{
    updated = $true; dryRun = $false; id = $Id
    changes = @($changes); warnings = @($warnings)
    totalNodes = $checkNodes.Count
}

if ($Text) {
    Write-Host "Updated $Id" -ForegroundColor Green
    foreach ($c in $changes) { Write-Host "  $($c.field): '$($c.from)' -> '$($c.to)'" }
    foreach ($w in $warnings) { Write-Warning $w }
    return
}
$result | ConvertTo-Json -Depth 5 -Compress
