<#
.SYNOPSIS
    Searches any JSON blob -- an array of objects in any file -- the way
    Get-Research searches research.json.

.DESCRIPTION
    Generalizes the research retrieval conventions to arbitrary JSON: saved code
    graphs, deploy manifests, exported tool output. Output is JSON by default
    (the consumer is a model), with the same { returned, totalMatches, truncated,
    items } envelope; -Text is the human opt-in.

    With no -Query and no -Where, prints an orientation view: the shape of the
    blob -- which arrays exist, how many items each holds, what fields the items
    carry. Cheap first call, same philosophy as Get-Research's no-argument view:
    orient before you retrieve.

.PARAMETER Path
    JSON file to search.

.PARAMETER Array
    Dotted path to the array to search (e.g. 'nodes', 'edges'). Optional when
    the root itself is an array, or when the file has exactly one top-level
    array. Anything else requires naming it -- guessing would silently search
    the wrong data.

.PARAMETER Query
    Free-text terms scored over item values (to depth 3). Exact value hits beat
    substring hits; hits in id-ish fields (id, name, key, type, fromId, toId)
    are weighted double. Returns a ranked shortlist.

.PARAMETER Where
    'prop=value' filters; all must match. Case-insensitive; * and ? wildcards
    switch the comparison to -like; dotted paths reach nested objects
    (e.g. 'properties.project=ImageSelectionTools').

.PARAMETER Select
    Project each result down to these properties (dotted paths allowed).
    Missing properties project as null rather than throwing.

.PARAMETER First
    Result cap, default 10. Unlike Get-Research this caps -Where results too:
    a blob array can hold tens of thousands of edges, and an uncapped filter
    would flood the very consumer this script exists to protect. The envelope
    always reports the cap; -All lifts it.

.PARAMETER All
    Return every match.

.PARAMETER Text
    Human-readable output instead of JSON.

.PARAMETER Pretty
    Indent the JSON. Debugging by eye only.

.OUTPUTS
    JSON: { returned, totalMatches, truncated, items[] }
    Orientation (no -Query/-Where): { file, arrays: [ { path, count, fields } ] }
    Check 'truncated' before concluding you have seen everything.

.EXAMPLE
    & "$env:JanetBase\scripts\Search-Json.ps1" -Path graphs\App.solution.graph.json
    Orientation: the arrays in the blob and the fields their items carry.

.EXAMPLE
    & "$env:JanetBase\scripts\Search-Json.ps1" -Path graphs\App.solution.graph.json -Array nodes -Query 'ConversionQueueStatus'
    Ranked nodes mentioning the type, as JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Search-Json.ps1" -Path graphs\App.solution.graph.json -Array edges -Where 'toId=type:App.SomeClass' -All
    Every edge pointing at the class. Explicit filter, capped only by -All's absence.

.EXAMPLE
    & "$env:JanetBase\scripts\Search-Json.ps1" -Path graphs\App.graph.json -Array nodes -Where 'type=class' -Select id,name -All
    Just the ids and names of every class node.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [string]$Array,
    [string]$Query,
    [string[]]$Where,
    [string[]]$Select,
    [int]$First = 10,
    [switch]$All,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "File not found: $Path" }

try {
    # -AsHashtable: faster on large blobs, tolerant of duplicate/odd keys, and a
    # missing hashtable key reads as $null instead of a StrictMode error.
    $root = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
}
catch {
    throw "Not valid JSON ($Path): $($_.Exception.Message)"
}

# ---- Helpers ---------------------------------------------------------------

function Resolve-DottedPath {
    # Value at a dotted path, or $null. Only dictionaries are descended --
    # ConvertFrom-Json -AsHashtable produces dictionaries, lists, and scalars.
    param($Object, [string]$DottedPath)
    $current = $Object
    foreach ($part in ($DottedPath -split '\.')) {
        if ($null -eq $current) { return $null }
        if ($current -is [System.Collections.IDictionary]) { $current = $current[$part] }
        else { return $null }
    }
    return $current
}

function Get-ArrayCandidates {
    # Top-level arrays in the blob. Returns a comma-wrapped array (house rule 1).
    param($Root)
    $found = @()
    if ($Root -is [System.Collections.IList]) {
        $found += [PSCustomObject]@{ ArrayPath = '(root)'; Items = $Root }
        return , $found
    }
    if ($Root -is [System.Collections.IDictionary]) {
        foreach ($key in @($Root.Keys | Sort-Object)) {
            if ($Root[$key] -is [System.Collections.IList]) {
                $found += [PSCustomObject]@{ ArrayPath = $key; Items = $Root[$key] }
            }
        }
    }
    return , $found
}

function Add-StringLeaves {
    # Flattens an item's values into (Name, Value) string pairs, depth-limited.
    # Appends into the caller's list -- returning arrays from recursion trips
    # exactly the unrolling that house rule 1 exists for.
    param($Object, [string]$Name, [int]$Remaining, [System.Collections.Generic.List[object]]$Into)
    if ($null -eq $Object -or $Remaining -lt 0) { return }
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($key in $Object.Keys) {
            Add-StringLeaves -Object $Object[$key] -Name $key -Remaining ($Remaining - 1) -Into $Into
        }
        return
    }
    if ($Object -is [System.Collections.IList]) {
        foreach ($element in $Object) {
            Add-StringLeaves -Object $element -Name $Name -Remaining ($Remaining - 1) -Into $Into
        }
        return
    }
    $Into.Add([PSCustomObject]@{ Name = $Name; Value = [string]$Object })
}

# Fields where a hit means "this IS the thing" rather than "this mentions the thing".
$idishFields = @('id', 'name', 'key', 'type', 'fromId', 'toId')

function Get-ItemScore {
    param($Item, [string[]]$Terms)
    $leaves = [System.Collections.Generic.List[object]]::new()
    Add-StringLeaves -Object $Item -Name '' -Remaining 3 -Into $leaves

    $score = 0
    foreach ($term in $Terms) {
        foreach ($leaf in $leaves) {
            $weight = if ($idishFields -contains $leaf.Name) { 2 } else { 1 }
            if ($leaf.Value -eq $term) { $score += 25 * $weight }
            elseif ($leaf.Value -like "*$term*") { $score += 8 * $weight }
        }
    }
    return $score
}

# ---- Pick the array --------------------------------------------------------

# Get-ArrayCandidates returns comma-wrapped; assignment unrolls exactly one
# layer. @(...) around the call is the house-rule-1 trap: one element holding
# the real array. (Fell into it on the first run of this very script.)
$candidates = Get-ArrayCandidates $root

$items = $null
if ($Array) {
    $resolved = if ($Array -eq '(root)') { $root } else { Resolve-DottedPath $root $Array }
    if ($null -eq $resolved -or $resolved -isnot [System.Collections.IList]) {
        $known = (@($candidates | ForEach-Object { $_.ArrayPath }) -join ', ')
        throw "No array at '$Array' in $Path. Top-level arrays: $known"
    }
    $items = $resolved
}
elseif ($candidates.Count -eq 1) {
    $items = $candidates[0].Items
}

$hasFilter = ($Query -or ($Where -and $Where.Count -gt 0))

# ---- Orientation view: no filter given -------------------------------------

if (-not $hasFilter) {
    $arrayInfos = @()
    $describe = if ($null -ne $items -and $Array) {
        @([PSCustomObject]@{ ArrayPath = $Array; Items = $items })
    }
    else { @($candidates) }

    foreach ($candidate in $describe) {
        # Field census over a sample: enough to show the shape without walking
        # a hundred-thousand-edge graph to learn what an edge looks like.
        $fieldCounts = [ordered]@{}
        $sample = @($candidate.Items | Select-Object -First 50)
        foreach ($item in $sample) {
            if ($item -isnot [System.Collections.IDictionary]) { continue }
            foreach ($key in $item.Keys) {
                if (-not $fieldCounts.Contains($key)) { $fieldCounts[$key] = 0 }
                $fieldCounts[$key]++
            }
        }
        $arrayInfos += [PSCustomObject]@{
            path   = $candidate.ArrayPath
            count  = $candidate.Items.Count
            fields = [PSCustomObject]$fieldCounts
        }
    }

    if (-not $Text) {
        $orientation = [PSCustomObject]@{ file = $Path; arrays = @($arrayInfos) }
        if ($Pretty) { $orientation | ConvertTo-Json -Depth 5 }
        else { $orientation | ConvertTo-Json -Depth 5 -Compress }
        return
    }

    Write-Host ''
    Write-Host $Path -ForegroundColor Cyan
    foreach ($info in $arrayInfos) {
        Write-Host ("  {0}  ({1} items)" -f $info.path, $info.count)
        $names = @($info.fields.PSObject.Properties | ForEach-Object { "$($_.Name)($($_.Value))" })
        if ($names.Count -gt 0) { Write-Host "    fields: $($names -join '  ')" -ForegroundColor DarkGray }
    }
    Write-Host ''
    Write-Host 'Search with -Array <name> plus -Query <text> or -Where prop=value.' -ForegroundColor DarkGray
    Write-Host ''
    return
}

# ---- Filtered search -------------------------------------------------------

if ($null -eq $items) {
    $known = (@($candidates | ForEach-Object { $_.ArrayPath }) -join ', ')
    throw "Multiple top-level arrays in $Path -- name one with -Array. Candidates: $known"
}

$filters = @()
# @($Where) on an unbound parameter yields @($null) -- one null element, not
# zero -- so drop nulls before touching string methods.
foreach ($clause in @($Where | Where-Object { $_ })) {
    $separator = $clause.IndexOf('=')
    if ($separator -lt 1) { throw "Bad -Where clause '$clause'. Expected prop=value." }
    $filters += [PSCustomObject]@{
        Prop     = $clause.Substring(0, $separator)
        Value    = $clause.Substring($separator + 1)
        Wildcard = $clause.Substring($separator + 1).IndexOfAny([char[]]('*', '?')) -ge 0
    }
}

$queryTerms = @()
if ($Query) { $queryTerms = @($Query -split '\s+' | Where-Object { $_ }) }

$matched = [System.Collections.Generic.List[object]]::new()
$index = 0
foreach ($item in $items) {
    $index++
    $keep = $true
    foreach ($filter in $filters) {
        $value = if ($item -is [System.Collections.IDictionary]) { Resolve-DottedPath $item $filter.Prop } else { $null }
        $textValue = if ($null -eq $value) { '' } else { [string]$value }
        $hit = if ($filter.Wildcard) { $textValue -like $filter.Value } else { $textValue -eq $filter.Value }
        if (-not $hit) { $keep = $false; break }
    }
    if (-not $keep) { continue }

    $score = 0
    if ($queryTerms.Count -gt 0) {
        $score = Get-ItemScore -Item $item -Terms $queryTerms
        if ($score -eq 0) { continue }
    }

    $matched.Add([PSCustomObject]@{ Item = $item; Score = $score; Order = $index })
}

# The @() wraps the whole if-statement: statement output unrolls on assignment,
# so a single match would otherwise assign a bare object whose .Count StrictMode
# rejects. (Second house-rule-1 variant this script has stepped in.)
$ranked = @(if ($queryTerms.Count -gt 0) {
        $matched | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, 'Order'
    }
    else {
        $matched   # -Where only: original blob order is meaningful, keep it
    })

$totalMatches = $ranked.Count
$capped = $false
if (-not $All -and $First -gt 0 -and $ranked.Count -gt $First) {
    $ranked = @($ranked | Select-Object -First $First)
    $capped = $true
}

$results = @()
foreach ($entry in $ranked) {
    if ($Select -and $Select.Count -gt 0) {
        $projected = [ordered]@{}
        foreach ($propPath in $Select) {
            $projected[$propPath] = if ($entry.Item -is [System.Collections.IDictionary]) {
                Resolve-DottedPath $entry.Item $propPath
            }
            else { $null }
        }
        $results += [PSCustomObject]$projected
    }
    else {
        $results += $entry.Item
    }
}

if (-not $Text) {
    # Same envelope shape whether empty, capped, or complete -- consumers never
    # special-case, and 'truncated' is the difference between a shortlist and
    # the whole answer.
    $envelope = [PSCustomObject]@{
        returned     = $results.Count
        totalMatches = $totalMatches
        truncated    = $capped
        items        = @($results)
    }
    if ($Pretty) { $envelope | ConvertTo-Json -Depth 8 }
    else { $envelope | ConvertTo-Json -Depth 8 -Compress }
    return
}

Write-Host ''
if ($results.Count -eq 0) {
    Write-Host 'No matches. Run without -Query/-Where for the orientation view.' -ForegroundColor Yellow
    Write-Host ''
    return
}
foreach ($result in $results) {
    if ($result -is [System.Collections.IDictionary]) {
        # Id-ish fields first, then the rest; long values elided. -Select or the
        # JSON view exist for anything this compression hides.
        $keys = @($result.Keys | Sort-Object { if ($idishFields -contains $_) { 0 } else { 1 } }, { $_ })
        foreach ($key in $keys) {
            $value = $result[$key]
            $textValue = if ($null -eq $value) { '' }
            elseif ($value -is [System.Collections.IDictionary] -or $value -is [System.Collections.IList]) {
                ($value | ConvertTo-Json -Depth 2 -Compress)
            }
            else { [string]$value }
            if ($textValue.Length -gt 120) { $textValue = $textValue.Substring(0, 117) + '...' }
            Write-Host ("  {0,-14} {1}" -f $key, $textValue)
        }
    }
    else {
        Write-Host "  $result"
    }
    Write-Host ''
}
if ($capped) {
    Write-Host "top $($results.Count) of $totalMatches matches. -First N for more, -All for every match." -ForegroundColor DarkGray
}
else {
    Write-Host "$($results.Count) item$(if ($results.Count -ne 1) { 's' })" -ForegroundColor DarkGray
}
Write-Host ''
