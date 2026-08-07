<#
.SYNOPSIS
    Queries research.json -- the node graph of scripts, patterns, notes, and files.

.DESCRIPTION
    The retrieval entry point.  Startup deliberately does not load the inventory;
    it loads only a pointer to this script, and the session pulls the two or
    three nodes it actually needs.  Progressive disclosure (DESIGN-NOTES
    section 2) applied to the repo's own index: a full catalog costs tokens on
    every session to answer a question most sessions never ask.

    OUTPUT IS JSON BY DEFAULT.  The consumer here is a model, not a person, so the
    machine-readable form is the default and the formatted view is the opt-in
    (-Text).  Formatting for human eyes first would mean an unambiguous consumer
    paying for line breaks and column alignment it does not read, and it makes the
    node schema hostage to a formatter someone has to remember to update.

    With no arguments this returns the cheap orientation view -- counts by kind and
    the tag index -- not the nodes themselves.  That is the point.

.PARAMETER Id
    Exact node id (e.g. 'script.push-thread-stack'). Accepts several.

.PARAMETER Tag
    Return nodes carrying any of these tags.

.PARAMETER Kind
    Filter to a node kind: script, pattern, note, file, skill.

.PARAMETER Query
    Free-text match over id, summary, and tags. Case-insensitive substring.

.PARAMETER Expand
    Also return nodes reachable by 'links' from the matches.

.PARAMETER Depth
    How many link hops to follow when -Expand is set. Default 1.

.PARAMETER Full
    Include every field. Only meaningful with -Text; JSON always carries everything.

.PARAMETER Text
    Human-readable formatted output instead of JSON. For reading at a terminal.

.PARAMETER Pretty
    Indent the JSON. For debugging by eye; costs ~50% more characters and buys a
    model nothing.

.PARAMETER Path
    Graph file to query. Defaults to research.json in the repo root.

.OUTPUTS
    JSON: { returned, totalMatches, truncated, nodes[] }
    Orientation (no filter): { total, kinds{}, tags{} }
    'truncated' is true when a ranked query was capped -- check it before concluding
    the result set is complete.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-Research.ps1"
    Orientation only: counts by kind, plus the tag index. Cheap.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-Research.ps1" -Query encoding
    The encoding-related nodes, ranked, as JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-Research.ps1" -Id pattern.thread-stack -Expand
    The pattern plus the three scripts that implement it.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-Research.ps1" -Kind script -Text -Full
    The exhaustive view, formatted for reading.
#>
[CmdletBinding()]
param(
    [string[]]$Id,
    [string[]]$Tag,
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,
    [string]$Query,
    [int]$First = 5,
    [switch]$All,
    [switch]$Expand,
    [int]$Depth = 1,
    [switch]$Full,
    [switch]$Text,
    [switch]$Pretty,
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Path) { $Path = Join-Path $repoRoot 'research.json' }

if (-not (Test-Path $Path)) { throw "Research graph not found: $Path" }

try {
    $graph = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Research graph is not valid JSON ($Path): $($_.Exception.Message)"
}

$nodes = @($graph.nodes)
if ($nodes.Count -eq 0) { throw "Research graph has no nodes: $Path" }

function Get-Field {
    # Optional node fields ('params', 'links', 'section') must not throw under
    # StrictMode when a node omits them.
    param($Node, [string]$Name, $Default = $null)
    if ($Node.PSObject.Properties.Name -contains $Name -and $null -ne $Node.$Name) {
        return $Node.$Name
    }
    return $Default
}

$byId = @{}
foreach ($n in $nodes) { $byId[$n.id] = $n }

# ---- Orientation view: no filter given ------------------------------------

$noFilter = -not ($Id -or $Tag -or $Kind -or $Query)

if ($noFilter -and -not $Full) {
    $tagCounts = @{}
    foreach ($n in $nodes) {
        foreach ($t in @(Get-Field $n 'tags' @())) {
            if (-not $tagCounts.ContainsKey($t)) { $tagCounts[$t] = 0 }
            $tagCounts[$t]++
        }
    }

    if (-not $Text) {
        $orientation = [PSCustomObject]@{
            total = $nodes.Count
            kinds = [PSCustomObject](
                $nodes | Group-Object kind | Sort-Object Name |
                    ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h[$_.Name] = $_.Count } -End { $h }
            )
            tags  = [PSCustomObject](
                $tagCounts.GetEnumerator() | Sort-Object Name |
                    ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h[$_.Key] = $_.Value } -End { $h }
            )
        }
        if ($Pretty) { $orientation | ConvertTo-Json -Depth 4 }
        else { $orientation | ConvertTo-Json -Depth 4 -Compress }
        return
    }

    Write-Host ''
    Write-Host "research.json -- $($nodes.Count) nodes" -ForegroundColor Cyan
    foreach ($g in ($nodes | Group-Object kind | Sort-Object Name)) {
        Write-Host ("  {0,-8} {1}" -f $g.Name, $g.Count)
    }
    Write-Host ''
    Write-Host 'TAGS' -ForegroundColor Cyan
    $line = ($tagCounts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)($($_.Value))" }) -join '  '
    Write-Host "  $line"
    Write-Host ''
    Write-Host 'Query with -Query <text>, -Tag <tag>, -Id <id>, or -Kind <kind>. Add -Expand to follow links.' -ForegroundColor DarkGray
    Write-Host ''
    return
}

# ---- Filtered selection ----------------------------------------------------

# Free-text queries are scored, not just filtered. The caller's job is to pick the
# right node without having read research.json, so a broad query must come back as a
# short ranked shortlist rather than everything that happened to contain the word.
# Terms are scored independently, so "thread stack debugging" beats a phrase match.
function Get-MatchScore {
    param($Node, [string[]]$Terms)

    $nodeId = $Node.id
    $summary = [string](Get-Field $Node 'summary' '')
    $nodeTags = @(Get-Field $Node 'tags' @())
    $caveatText = @(Get-Field $Node 'caveats' @()) -join ' '

    $positive = 0
    $penalty = 0

    foreach ($term in $Terms) {
        if ($nodeId -eq $term) { $positive += 100; continue }

        # Tags are curated, so a tag hit is a much stronger signal than prose.
        if ($nodeTags -contains $term) { $positive += 40 }
        elseif (@($nodeTags | Where-Object { $_ -like "*$term*" }).Count -gt 0) { $positive += 20 }

        if ($nodeId -like "*$term*") { $positive += 15 }
        if ($summary -like "*$term*") { $positive += 10 }

        # A caveat hit means the term appears in what is WRONG with the node, not what
        # it is for. Searching "git" should rank the script that does git work above one
        # whose only connection is "requires a git repo". So caveats demote -- and they
        # never select, or a node would surface purely for describing its own breakage.
        if ($caveatText -and $caveatText -like "*$term*") { $penalty += 5 }
    }

    # Floored at 1 so a demoted node still ranks, just last. Losing it entirely would
    # be worse: a broken tool you can see is more useful than one you cannot find.
    $final = if ($positive -gt 0) { [Math]::Max(1, $positive - $penalty) } else { 0 }
    return $final
}

$queryTerms = @()
if ($Query) { $queryTerms = @($Query -split '\s+' | Where-Object { $_ }) }

$scored = @()

foreach ($n in $nodes) {
    $hit = $noFilter   # -Full with no filter means everything
    $score = 0

    # Explicit selectors outrank fuzzy ones -- an -Id hit should never sort below
    # something that merely mentions the word.
    if ($Id -and ($Id -contains $n.id)) { $hit = $true; $score += 1000 }

    if ($Tag) {
        $nodeTags = @(Get-Field $n 'tags' @())
        foreach ($t in $Tag) { if ($nodeTags -contains $t) { $hit = $true; $score += 500; break } }
    }

    if ($queryTerms.Count -gt 0) {
        $queryScore = Get-MatchScore $n $queryTerms
        if ($queryScore -gt 0) { $hit = $true; $score += $queryScore }
    }

    # -Kind alone selects; combined with others it narrows.
    if ($Kind) {
        $kindOnly = -not ($Id -or $Tag -or $Query)
        if ($kindOnly) { $hit = ($n.kind -eq $Kind) }
        elseif ($hit -and $n.kind -ne $Kind) { $hit = $false }
    }

    if ($hit) { $scored += [PSCustomObject]@{ node = $n; score = $score } }
}

$ranked = @($scored | Sort-Object -Property @{ Expression = 'score'; Descending = $true }, @{ Expression = { $_.node.id } })
$totalMatches = $ranked.Count

# Cap only ranked free-text results. Explicit selectors and -All mean the caller
# already knows what they asked for; truncating those would just hide answers.
$capped = $false
if (-not $All -and $queryTerms.Count -gt 0 -and $First -gt 0 -and $ranked.Count -gt $First) {
    $ranked = @($ranked | Select-Object -First $First)
    $capped = $true
}

$matched = [ordered]@{}
foreach ($r in $ranked) { $matched[$r.node.id] = $r.node }

if ($Expand) {
    for ($hop = 0; $hop -lt $Depth; $hop++) {
        foreach ($seed in @($matched.Keys)) {
            foreach ($link in @(Get-Field $byId[$seed] 'links' @())) {
                if ($byId.ContainsKey($link)) {
                    if (-not $matched.Contains($link)) { $matched[$link] = $byId[$link] }
                }
                else {
                    Write-Warning "dangling link: $seed -> $link"
                }
            }
        }
    }
}

$results = @($matched.Values)

if ($results.Count -eq 0) {
    if ($Text) {
        Write-Host 'No matching nodes. Run with no arguments to see kinds and tags.' -ForegroundColor Yellow
        return
    }
    # Same envelope shape as a hit, so a consumer never special-cases empty.
    $empty = [PSCustomObject]@{ returned = 0; totalMatches = 0; truncated = $false; nodes = @() }
    if ($Pretty) { $empty | ConvertTo-Json -Depth 6 } else { $empty | ConvertTo-Json -Depth 6 -Compress }
    return
}

if (-not $Text) {
    # Wrapped, not a bare array: the consumer needs to know a shortlist was a
    # shortlist. Same reason the text path prints "top N of M".
    $envelope = [PSCustomObject]@{
        returned     = $results.Count
        totalMatches = $totalMatches
        truncated    = $capped
        nodes        = @($results)
    }
    if ($Pretty) { $envelope | ConvertTo-Json -Depth 6 }
    else { $envelope | ConvertTo-Json -Depth 6 -Compress }
    return
}

# Ranked results keep rank order; everything else reads better grouped by kind.
$ordered = if ($queryTerms.Count -gt 0) { $results } else { @($results | Sort-Object kind, id) }

Write-Host ''
foreach ($n in $ordered) {
    $section = Get-Field $n 'section' ''
    $suffix = if ($section) { " (section $section)" } else { '' }
    Write-Host "$($n.id)" -ForegroundColor Cyan
    Write-Host "  $(Get-Field $n 'summary' '(no summary)')"
    Write-Host "  $($n.path)$suffix" -ForegroundColor DarkGray
    # Always shown, in every view. The whole point of a caveat is that you see it
    # before you rely on the node, and a warning behind a flag is not a warning.
    foreach ($c in @(Get-Field $n 'caveats' @())) {
        Write-Host "  ! $c" -ForegroundColor Yellow
    }
    # Tags shown on ranked results: picking between three near-neighbours is
    # mostly a tag question, and re-querying to find that out defeats the point.
    if (-not $Full -and $queryTerms.Count -gt 0) {
        $t = @(Get-Field $n 'tags' @())
        if ($t.Count -gt 0) { Write-Host "  tags:   $($t -join ', ')" -ForegroundColor DarkGray }
    }

    # Anything the graph grows that this formatter does not know about still gets
    # printed. The alternative is a view that silently drops new fields until someone
    # remembers to update it -- which is how 'caveats' stayed invisible until the
    # formatter was edited by hand. Use -Json for a consumer that needs structure.
    $knownFields = @('id', 'kind', 'path', 'section', 'summary', 'caveats', 'tags', 'params', 'links')
    foreach ($prop in $n.PSObject.Properties) {
        if ($knownFields -contains $prop.Name) { continue }
        if ($null -eq $prop.Value) { continue }
        $val = if ($prop.Value -is [System.Collections.IEnumerable] -and $prop.Value -isnot [string]) {
            (@($prop.Value) -join ', ')
        }
        else { [string]$prop.Value }
        if ($val) { Write-Host "  $($prop.Name): $val" -ForegroundColor DarkGray }
    }

    if ($Full) {
        $p = @(Get-Field $n 'params' @())
        if ($p.Count -gt 0) { Write-Host "  params: $($p -join ', ')" -ForegroundColor DarkGray }
        $l = @(Get-Field $n 'links' @())
        if ($l.Count -gt 0) { Write-Host "  links:  $($l -join ', ')" -ForegroundColor DarkGray }
        $t = @(Get-Field $n 'tags' @())
        if ($t.Count -gt 0) { Write-Host "  tags:   $($t -join ', ')" -ForegroundColor DarkGray }
    }
    Write-Host ''
}
if ($capped) {
    # Never truncate silently -- a shortlist that looks like the whole answer is
    # worse than a long list.
    Write-Host "top $($results.Count) of $totalMatches matches. -First N for more, -All for every match." -ForegroundColor DarkGray
}
else {
    Write-Host "$($results.Count) node$(if ($results.Count -ne 1) {'s'})" -ForegroundColor DarkGray
}
Write-Host ''
