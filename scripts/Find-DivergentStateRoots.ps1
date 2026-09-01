<#
.SYNOPSIS
    Reports types whose members split into groups that share no state.

.DESCRIPTION
    Connected-component cohesion (the LCOM4 shape) computed from the code graph
    rather than from source: two members are linked if they touch a common field,
    or if one calls the other. Count the components. One component is a cohesive
    type; two or more means the members partition into clusters that never meet,
    which is the mechanical form of "this is two types wearing one name".

    WHY IT EXISTS, and the case is worth stating because it is what a plain
    reading missed. In GameHub, MovementSystem.GroupOps exposes fifteen verbs to a
    pluggable doctrine. IsClaimed reads the system-wide claim cache; ClaimantOf
    reads the group's own member list. GatherDoctrine.ReconcilePass treats the two
    as a matched pair -- ask whether a cell is claimed, then ask who claimed it --
    so with two concurrent groups the second question returns "nobody" for a cell
    the first said was taken, the squatter swap never fires, and a unit is left
    aimed at a cell it has just proved it cannot reach. Five adversarial reviewers
    and a night of reading found it once. As components it is one row: the members
    reading _group form a cluster that shares nothing with the members reading
    _system.

    So the finding is not "low cohesion is untidy". It is that a divergence in
    BACKING STATE between members used together is invisible at the call site and
    obvious in the graph.

    WHAT IT CANNOT SEE, because the graph cannot: whether two roots are
    semantically alternatives for the same information. _system and _group are
    both "where claim state lives", which is what makes their divergence a bug
    rather than a design. A type that genuinely holds two unrelated
    responsibilities reports identically and is a refactor, not a defect. Read the
    clusters before believing either story.

.PARAMETER Project
    Project name to scan, e.g. Nav.Core. Scanning a whole solution at once is
    allowed but the answer means less -- cohesion is a per-assembly judgement.

.PARAMETER GraphId
    Graph held by the server. Build one first with build_solution.

.PARAMETER Type
    Substring filter on type name. Narrows an otherwise whole-project scan.

.PARAMETER MinComponents
    Report types with at least this many components. Default 2.

.PARAMETER Uri
    MCP endpoint. Defaults to razorgraph on 7718.

.PARAMETER ChunkSize
    Types per research call. Default 10; lower it if a payload is refused.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Find-DivergentStateRoots.ps1" -Project Nav.Core -GraphId gamehub -Text
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$GraphId,
    [string]$Type,
    [int]$MinComponents = 2,
    [string]$Uri = 'http://127.0.0.1:7718/',
    [int]$ChunkSize = 10,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$mcp = Join-Path $PSScriptRoot 'Invoke-McpTool.ps1'
if (-not (Test-Path $mcp)) { throw "Invoke-McpTool.ps1 not found beside this script at $PSScriptRoot." }

function Invoke-Tool {
    param([string]$Name, [hashtable]$ToolArguments)

    $raw = & $mcp -Tool $Name -Arguments $ToolArguments -Uri $Uri
    if ($LASTEXITCODE -ne 0) { throw "$Name failed: $raw" }
    return ($raw | ConvertFrom-Json).result
}

$findArgs = @{ nodeType = 'Class'; project = $Project; graphId = $GraphId; limit = 500 }
if ($Type) { $findArgs['nameContains'] = $Type }
$types = @((Invoke-Tool -Name 'find_nodes' -ToolArguments $findArgs).nodes)

if ($types.Count -eq 0) {
    $empty = [ordered]@{ ok = $false; project = $Project; graphId = $GraphId; typesScanned = 0; findings = @() }
    if ($Text) { Write-Host "No types matched in $Project." -ForegroundColor Yellow } else { [PSCustomObject]$empty | ConvertTo-Json -Depth 4 }
    exit 1
}

$findings = @()
$scanned = 0

for ($offset = 0; $offset -lt $types.Count; $offset += $ChunkSize) {
    $chunk = @($types[$offset..([Math]::Min($offset + $ChunkSize - 1, $types.Count - 1))])
    $sub = Invoke-Tool -Name 'research' -ToolArguments @{
        focusIds  = @($chunk | ForEach-Object { $_.id })
        depth     = 1
        threshold = 0.4
        graphId   = $GraphId
        query     = 'cohesion: which members share backing state'
    }

    # declaringType lives on the node, never on the edge, so members are attributed
    # from the node table and edges are matched into it afterwards.
    $owner = @{}
    # A const is a compile-time literal, not shared state. Counting it as a link
    # merges every static helper that happens to read the same constant, and
    # counting it as a ROOT reports a class as split because its parsing helpers
    # read HeaderLines while its instance members read the real array.
    $constFields = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($node in $sub.nodes) {
        if ($node.type -in @('method', 'property') -and
            $node.PSObject.Properties.Name -contains 'properties' -and
            $node.properties.PSObject.Properties.Name -contains 'declaringType') {
            $owner[$node.id] = $node.properties.declaringType
        }
        elseif ($node.type -eq 'field' -and
            $node.PSObject.Properties.Name -contains 'properties' -and
            $node.properties.PSObject.Properties.Name -contains 'isConst' -and
            $node.properties.isConst) {
            [void]$constFields.Add($node.id)
        }
    }

    foreach ($focus in $chunk) {
        $scanned++
        $typeName = $focus.id -replace '^type:', ''
        $members = @($owner.Keys | Where-Object { $owner[$_] -eq $typeName })
        if ($members.Count -lt 2) { continue }

        # Union-find over members: linked by a shared field, or by one calling the
        # other. Constructors are excluded -- a ctor writes every field, so leaving
        # it in merges every cluster and the report always says "cohesive".
        $members = @($members | Where-Object { $_ -notmatch '\.\.ctor\(' })
        if ($members.Count -lt 2) { continue }

        $parent = @{}
        foreach ($m in $members) { $parent[$m] = $m }

        function Find-Root { param([string]$Id)
            $cursor = $Id
            while ($parent[$cursor] -ne $cursor) { $cursor = $parent[$cursor] }
            return $cursor
        }
        function Join-Member { param([string]$A, [string]$B)
            $ra = Find-Root -Id $A; $rb = Find-Root -Id $B
            if ($ra -ne $rb) { $parent[$ra] = $rb }
        }

        $fieldsOf = @{}
        foreach ($m in $members) { $fieldsOf[$m] = [System.Collections.Generic.HashSet[string]]::new() }

        foreach ($edge in $sub.edges) {
            if ($edge.type -in @('reads', 'writes') -and $edge.to -like 'field:*' -and
                -not $constFields.Contains($edge.to) -and $fieldsOf.ContainsKey($edge.from)) {
                [void]$fieldsOf[$edge.from].Add(($edge.to -replace '^field:', ''))
            }
            elseif ($edge.type -eq 'calls' -and $fieldsOf.ContainsKey($edge.from) -and $fieldsOf.ContainsKey($edge.to)) {
                Join-Member -A $edge.from -B $edge.to
            }
        }

        foreach ($a in $members) {
            foreach ($b in $members) {
                if ($a -ge $b) { continue }
                $shared = [System.Linq.Enumerable]::Any($fieldsOf[$a], [Func[string, bool]] { param($f) $fieldsOf[$b].Contains($f) })
                if ($shared) { Join-Member -A $a -B $b }
            }
        }

        # ONLY MEMBERS THAT TOUCH STATE COUNT TOWARD THE SPLIT. An auto-property or
        # a positional record member reads no field the graph can see -- the
        # backing field is compiler-generated and absent -- so counting them made
        # every record report as N singleton clusters, and the first run flagged 44
        # of 48 types in Nav.Core. A member that touches nothing is not evidence of
        # anything; it is reported alongside the clusters, not as one.
        $stateful = @($members | Where-Object { $fieldsOf[$_].Count -gt 0 })
        $stateless = @($members | Where-Object { $fieldsOf[$_].Count -eq 0 })
        if ($stateful.Count -lt 2) { continue }

        # THE @() IS NOT OPTIONAL, and leaving it off made the first run lie. A
        # Group-Object that produces ONE group returns a bare GroupInfo, and
        # GroupInfo carries its own .Count -- the number of items IN the group. So
        # $groups.Count read 6 members as 6 clusters and reported BinaryHeap, which
        # has exactly one.
        $groups = @($stateful | Group-Object { Find-Root -Id $_ })
        if ($groups.Count -lt $MinComponents) { continue }

        $findings += [ordered]@{
            type          = $typeName
            file          = $focus.filePath
            members       = $members.Count
            statefulCount = $stateful.Count
            statelessCount = $stateless.Count
            components    = $groups.Count
            clusters   = @($groups | Sort-Object Count -Descending | ForEach-Object {
                    [ordered]@{
                        # Strip to the simple name BEFORE the parameter list. Cutting
                        # at the last dot mangles anything whose parameters are
                        # namespace-qualified -- ReachableSpots(System...IReadOnlyList<int>)
                        # came out as "IReadOnlyList<int>)".
                        members = @($_.Group | ForEach-Object {
                                $bare = $_ -replace '^(?:m|prop|field):', ''
                                $head = ($bare -split '\(', 2)[0]
                                $head.Substring($head.LastIndexOf('.') + 1)
                            } | Sort-Object -Unique)
                        reads   = @($_.Group | ForEach-Object { $fieldsOf[$_] } | ForEach-Object { $_ } |
                                Sort-Object -Unique | ForEach-Object { ($_ -replace '^.*\.', '') })
                    }
                })
        }
    }
}

$findings = @($findings | Sort-Object -Property @{ Expression = 'components'; Descending = $true }, @{ Expression = 'members'; Descending = $true })

$envelope = [ordered]@{
    ok           = $true
    project      = $Project
    graphId      = $GraphId
    typesScanned = $scanned
    found        = $findings.Count
    findings     = $findings
}

if (-not $Text) {
    if ($Pretty) { [PSCustomObject]$envelope | ConvertTo-Json -Depth 12 }
    else { [PSCustomObject]$envelope | ConvertTo-Json -Depth 12 -Compress }
    exit 0
}

if ($findings.Count -eq 0) {
    Write-Host "$scanned types scanned in $Project -- every one cohesive at $MinComponents+ components." -ForegroundColor Green
    exit 0
}

Write-Host "$($findings.Count) of $scanned types split into $MinComponents or more clusters:`n"
foreach ($f in $findings) {
    Write-Host "  $($f.type)" -ForegroundColor Yellow
    Write-Host "    $($f.members) members, $($f.components) clusters sharing no state"
    foreach ($c in $f.clusters) {
        $reads = if ($c.reads.Count -gt 0) { $c.reads -join ', ' } else { '(reads no field)' }
        Write-Host "      [$reads]" -ForegroundColor DarkGray
        Write-Host "        $($c.members -join ', ')"
    }
    Write-Host ''
}

exit 0
