<#
.SYNOPSIS
    Queries a .NET XML documentation file -- the same retrieval contract as
    Get-Research.ps1, pointed at a compiled API instead of the research graph.

.DESCRIPTION
    Researching an unfamiliar library from its XML docs by grep is expensive and
    bad at the job.  The file is one long stream of <member> elements whose text
    is hard-wrapped and indented for a doc generator, so a match costs dozens of
    context lines to read, the answer arrives split across them, and the thing
    you actually wanted -- "what is this property's type and what does it do" --
    is spread over five.  A single API question can cost thousands of tokens of
    noise.

    This parses the file instead and returns members: kind, declaring type,
    signature, summary, parameter docs.  Prose is flattened, so <see cref="T:X"/>
    reads as X rather than as markup.

    OUTPUT IS JSON BY DEFAULT, for the same reason Get-Research is: the consumer
    is a model, not a person, and the formatted view is the opt-in (-Text).

    Free-text queries are RANKED, not merely filtered, and capped -- check
    'truncated' before concluding you have seen everything.

    With a package but no query, this returns the cheap orientation view: member
    counts by kind and the largest types.  That is the shape of the API for a
    fraction of the cost of reading it.

.PARAMETER Package
    NuGet package id, resolved from the local package cache. Case-insensitive,
    prefix match. The newest version and the newest target framework win unless
    -Version / -Tfm say otherwise.

.PARAMETER Path
    An explicit .xml documentation file, bypassing package resolution.

.PARAMETER Query
    Free-text, scored across member name, declaring type, summary, and parameter
    docs. Terms score independently.

.PARAMETER Id
    Exact member name, with or without the 'M:'/'T:' prefix.

.PARAMETER Kind
    Type, Method, Property, Field, or Event.

.PARAMETER Type
    Restrict to members whose declaring type matches this substring.

.PARAMETER Full
    Include remarks, exceptions, and type parameters.

.OUTPUTS
    JSON: { source, returned, totalMatches, truncated, members[] }
    Orientation: { source, total, kinds{}, types{} }

.EXAMPLE
    & "$env:JanetBase\scripts\Get-ApiDoc.ps1" -Package LiveChartsCore
    Orientation: what is in this API and which types are biggest.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-ApiDoc.ps1" -Package LiveChartsCore -Query 'tooltip formatter'
    The ranked shortlist, with summaries -- no grep, no context lines.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-ApiDoc.ps1" -Package LiveChartsCore -Type Coordinate -All -Text
    Every member of one type, formatted for reading.
#>
[CmdletBinding()]
param(
    [string]$Package,
    [string]$Path,
    [string]$Version,
    [string]$Tfm,
    [string]$Query,
    [string[]]$Id,
    [ValidateSet('Type', 'Method', 'Property', 'Field', 'Event')]
    [string]$Kind,
    [string]$Type,
    [int]$First = 5,
    [switch]$All,
    [switch]$Full,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Locating the documentation file ---------------------------------------

function Resolve-DocPath {
    param([string]$PackageId, [string]$WantVersion, [string]$WantTfm)

    $cache = Join-Path $env:USERPROFILE '.nuget\packages'
    if (-not (Test-Path $cache)) { throw "No NuGet package cache at $cache. Use -Path." }

    $dir = Get-ChildItem $cache -Directory |
        Where-Object { $_.Name -like "$PackageId*" } |
        Sort-Object { $_.Name.Length } |
        Select-Object -First 1

    if (-not $dir) { throw "No cached package matching '$PackageId'. Use -Path." }

    $versions = @(Get-ChildItem $dir.FullName -Directory)
    if ($WantVersion) {
        $versions = @($versions | Where-Object { $_.Name -eq $WantVersion })
        if ($versions.Count -eq 0) { throw "Version '$WantVersion' not cached for $($dir.Name)." }
    }

    # Newest first, but only where the folder name actually parses as a version;
    # prerelease tags ('2.0.5-beta') do not, and must not sort as though they do.
    $ordered = @($versions | Sort-Object -Descending {
            $parsed = [version]'0.0'
            if ([version]::TryParse($_.Name, [ref]$parsed)) { $parsed } else { [version]'0.0' }
        })

    foreach ($v in $ordered) {
        $libRoot = Join-Path $v.FullName 'lib'
        if (-not (Test-Path $libRoot)) { continue }

        $frameworks = @(Get-ChildItem $libRoot -Directory)
        if ($WantTfm) { $frameworks = @($frameworks | Where-Object { $_.Name -eq $WantTfm }) }

        # A modern TFM carries the same docs as the legacy ones and is the build
        # the caller is almost certainly compiling against.
        $ranked = @($frameworks | Sort-Object -Descending {
                switch -Regex ($_.Name) {
                    '^net\d+\.\d+-' { 400; break }
                    '^net\d+\.\d+'  { 300; break }
                    '^netstandard'  { 200; break }
                    default         { 100 }
                }
            }, { $_.Name })

        foreach ($f in $ranked) {
            $xml = @(Get-ChildItem $f.FullName -Filter '*.xml' -File)
            if ($xml.Count -eq 0) { continue }

            $preferred = $xml | Where-Object { $_.BaseName -eq $dir.Name } | Select-Object -First 1
            if ($preferred) { return $preferred.FullName }
            return $xml[0].FullName
        }
    }

    throw "Package '$($dir.Name)' is cached but ships no XML documentation."
}

if (-not $Path) {
    if (-not $Package) { throw 'Specify -Package or -Path.' }
    $Path = Resolve-DocPath -PackageId $Package -WantVersion $Version -WantTfm $Tfm
}

if (-not (Test-Path $Path)) { throw "Documentation file not found: $Path" }

try {
    $doc = [xml](Get-Content $Path -Raw -Encoding UTF8)
}
catch {
    throw "Not a valid XML document ($Path): $($_.Exception.Message)"
}

# ---- Turning doc markup into prose -----------------------------------------

# The whole reason this script exists. Raw doc XML is markup wrapped for a
# generator: cref attributes carry the information while the tag carries none,
# and the indentation is layout, not meaning. Grep hands you all of it.
function ConvertTo-DocText {
    param([string]$Markup)

    if (-not $Markup) { return '' }

    $t = $Markup
    $t = [regex]::Replace($t, '<see\s+cref="[A-Z]:([^"]+)"\s*/>', '$1')
    $t = [regex]::Replace($t, '<see\s+cref="[A-Z]:([^"]+)"\s*>.*?</see>', '$1')
    $t = [regex]::Replace($t, '<see\s+langword="([^"]+)"\s*/>', '$1')
    $t = [regex]::Replace($t, '<(?:paramref|typeparamref)\s+name="([^"]+)"\s*/>', '$1')
    $t = [regex]::Replace($t, '</?(?:c|para|b|i|list|item|term|description|code)[^>]*>', ' ')
    $t = [regex]::Replace($t, '<[^>]+>', ' ')

    $t = $t -replace '&lt;', '<' -replace '&gt;', '>' -replace '&quot;', '"' -replace '&apos;', "'"
    $t = $t -replace '&amp;', '&'

    return ([regex]::Replace($t, '\s+', ' ')).Trim()
}

function Get-Element {
    param($Member, [string]$Name)
    $node = $Member.SelectSingleNode($Name)
    if ($null -eq $node) { return '' }
    return ConvertTo-DocText $node.InnerXml
}

# ---- Parsing member names ---------------------------------------------------

$kindByPrefix = @{ 'T' = 'Type'; 'M' = 'Method'; 'P' = 'Property'; 'F' = 'Field'; 'E' = 'Event' }

function Split-MemberName {
    param([string]$Raw)

    $prefix = $Raw.Substring(0, 1)
    $rest = $Raw.Substring(2)

    # A conversion operator encodes its return type as '~T' after the argument
    # list; that belongs to the signature, not to the name.
    $returnSuffix = ''
    $tilde = $rest.IndexOf('~')
    if ($tilde -ge 0) {
        $returnSuffix = $rest.Substring($tilde + 1)
        $rest = $rest.Substring(0, $tilde)
    }

    $args = ''
    $paren = $rest.IndexOf('(')
    if ($paren -ge 0) {
        $args = $rest.Substring($paren + 1).TrimEnd(')')
        $rest = $rest.Substring(0, $paren)
    }

    if ($prefix -eq 'T') {
        $declaring = $rest
        $simple = $rest.Split('.')[-1]
    }
    else {
        $lastDot = $rest.LastIndexOf('.')
        if ($lastDot -lt 0) {
            $declaring = ''
            $simple = $rest
        }
        else {
            $declaring = $rest.Substring(0, $lastDot)
            $simple = $rest.Substring($lastDot + 1)
        }
    }

    return [PSCustomObject]@{
        Kind         = $kindByPrefix[$prefix]
        Declaring    = $declaring
        Simple       = $simple
        Args         = $args
        ReturnSuffix = $returnSuffix
    }
}

# Argument lists are fully qualified, which is unreadable and is not what the
# caller is deciding between. Namespaces are dropped; generic arity is kept.
function Format-Signature {
    param($Parsed)

    if (-not $Parsed.Args) {
        if ($Parsed.Kind -eq 'Method') { return "$($Parsed.Simple)()" }
        return $Parsed.Simple
    }

    $depth = 0
    $current = ''
    $parts = @()

    # Split on top-level commas only: a generic argument carries its own.
    foreach ($ch in $Parsed.Args.ToCharArray()) {
        switch ($ch) {
            '{' { $depth++; $current += $ch }
            '}' { $depth--; $current += $ch }
            '(' { $depth++; $current += $ch }
            ')' { $depth--; $current += $ch }
            ',' {
                if ($depth -eq 0) { $parts += $current; $current = '' }
                else { $current += $ch }
            }
            default { $current += $ch }
        }
    }
    if ($current) { $parts += $current }

    $short = foreach ($p in $parts) {
        $p = $p.Trim()
        # Keep only the last namespace segment of each type, inside generics too.
        [regex]::Replace($p, '[\w\.]+\.(\w+)', '$1')
    }

    return "$($Parsed.Simple)($($short -join ', '))"
}

# ---- Building the member table ---------------------------------------------

$members = @()
$byName = @{}

foreach ($m in $doc.doc.members.member) {
    $raw = [string]$m.name
    if (-not $raw -or $raw.Length -lt 3) { continue }
    if (-not $kindByPrefix.ContainsKey($raw.Substring(0, 1))) { continue }

    $parsed = Split-MemberName $raw

    $paramDocs = @()
    foreach ($p in $m.SelectNodes('param')) {
        $paramDocs += [PSCustomObject]@{
            name = [string]$p.name
            doc  = ConvertTo-DocText $p.InnerXml
        }
    }

    $record = [PSCustomObject]@{
        id          = $raw
        kind        = $parsed.Kind
        name        = $parsed.Simple
        declaring   = $parsed.Declaring
        signature   = Format-Signature $parsed
        summary     = Get-Element $m 'summary'
        returns     = Get-Element $m 'returns'
        value       = Get-Element $m 'value'
        parameters  = @($paramDocs)
        inheritdoc  = ''
        remarks     = ''
        exceptions  = @()
        typeparams  = @()
    }

    if ($parsed.ReturnSuffix) {
        $record.returns = ("$($parsed.ReturnSuffix). " + $record.returns).Trim()
    }

    # <inheritdoc/> is why grepping this file misleads: the member you searched
    # for documents nothing, and the prose lives on the interface it implements.
    $inherit = $m.SelectSingleNode('inheritdoc')
    if ($null -ne $inherit) {
        $cref = $inherit.GetAttribute('cref')
        if ($cref) { $record.inheritdoc = $cref }
    }

    if ($Full) {
        $record.remarks = Get-Element $m 'remarks'
        foreach ($e in $m.SelectNodes('exception')) {
            $record.exceptions += [PSCustomObject]@{
                type = ([string]$e.cref) -replace '^[A-Z]:', ''
                doc  = ConvertTo-DocText $e.InnerXml
            }
        }
        foreach ($tp in $m.SelectNodes('typeparam')) {
            $record.typeparams += [PSCustomObject]@{
                name = [string]$tp.name
                doc  = ConvertTo-DocText $tp.InnerXml
            }
        }
    }

    $members += $record
    if (-not $byName.ContainsKey($raw)) { $byName[$raw] = $record }
}

if ($members.Count -eq 0) { throw "No documented members found in $Path" }

# Resolve <inheritdoc/> by FOLLOWING THE CHAIN, not one hop.
#
# One hop looks sufficient and is not: an override typically points at the base
# class, whose own docs are themselves an inheritdoc onto the interface, so
# BarSeries`3 -> Series`3 -> ISeries is the ordinary shape rather than an exotic
# one. Resolving a single hop also makes the result depend on document order,
# because a member resolved before its target was filled in stays empty. Every
# hop is an explicit cref the author wrote, so following them is reading the
# documentation, not guessing at it.
#
# Depth-capped and visited-guarded: circular crefs exist in the wild and must
# not hang the query. An unresolved reference is reported, never silently
# dropped -- "documented elsewhere, and here is where" beats a blank.
foreach ($r in $members) {
    if ($r.summary -or -not $r.inheritdoc) { continue }

    $seen = [System.Collections.Generic.HashSet[string]]::new()
    [void]$seen.Add($r.id)
    $cursor = $r.inheritdoc
    $origin = ''

    for ($hop = 0; $hop -lt 5; $hop++) {
        if (-not $cursor -or -not $seen.Add($cursor)) { break }
        if (-not $byName.ContainsKey($cursor)) { break }

        $source = $byName[$cursor]
        if ($source.summary) {
            $r.summary = $source.summary
            $origin = $cursor
            break
        }
        $cursor = $source.inheritdoc
    }

    $r.inheritdoc = if ($origin) { "inherited from $origin" } else { "see $($r.inheritdoc)" }
}

# ---- Orientation view -------------------------------------------------------

$noFilter = -not ($Query -or $Id -or $Kind -or $Type)

if ($noFilter) {
    $typeCounts = $members |
        Where-Object { $_.declaring } |
        Group-Object declaring |
        Sort-Object Count -Descending |
        Select-Object -First 25

    if (-not $Text) {
        $orientation = [PSCustomObject]@{
            source = $Path
            total  = $members.Count
            kinds  = [PSCustomObject](
                $members | Group-Object kind | Sort-Object Name |
                    ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h[$_.Name] = $_.Count } -End { $h }
            )
            types  = [PSCustomObject](
                $typeCounts |
                    ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h[$_.Name] = $_.Count } -End { $h }
            )
        }
        if ($Pretty) { $orientation | ConvertTo-Json -Depth 4 }
        else { $orientation | ConvertTo-Json -Depth 4 -Compress }
        return
    }

    Write-Host ''
    Write-Host "$Path -- $($members.Count) documented members" -ForegroundColor Cyan
    foreach ($g in ($members | Group-Object kind | Sort-Object Name)) {
        Write-Host ("  {0,-9} {1}" -f $g.Name, $g.Count)
    }
    Write-Host ''
    Write-Host 'LARGEST TYPES' -ForegroundColor Cyan
    foreach ($t in $typeCounts) { Write-Host ("  {0,-5} {1}" -f $t.Count, $t.Name) }
    Write-Host ''
    Write-Host 'Query with -Query <text>, -Type <type>, -Id <member>, or -Kind <kind>.' -ForegroundColor DarkGray
    Write-Host ''
    return
}

# ---- Scoring ----------------------------------------------------------------

function Get-MatchScore {
    param($Member, [string[]]$Terms)

    $score = 0

    foreach ($term in $Terms) {
        # The member's own name is what the caller is trying to recall, so it
        # outranks the type it lives on, which outranks prose about either.
        if ($Member.name -eq $term) { $score += 100; continue }
        if ($Member.name -like "*$term*") { $score += 40 }
        if ($Member.declaring -like "*$term*") { $score += 25 }
        if ($Member.summary -like "*$term*") { $score += 10 }

        foreach ($p in $Member.parameters) {
            if ($p.name -like "*$term*" -or $p.doc -like "*$term*") { $score += 5; break }
        }
    }

    # An undocumented member that matched only on its name is still the right
    # answer sometimes, but it loses every tie against one that explains itself.
    if ($score -gt 0 -and -not $Member.summary) { $score = [Math]::Max(1, $score - 15) }

    return $score
}

$queryTerms = @()
if ($Query) { $queryTerms = @($Query -split '\s+' | Where-Object { $_ }) }

$scored = @()

foreach ($r in $members) {
    $hit = $false
    $score = 0

    if ($Id) {
        foreach ($wanted in $Id) {
            $bare = $r.id -replace '^[A-Z]:', ''
            if ($r.id -eq $wanted -or $bare -eq $wanted) { $hit = $true; $score += 1000; break }
        }
    }

    if ($queryTerms.Count -gt 0) {
        $queryScore = Get-MatchScore $r $queryTerms
        if ($queryScore -gt 0) { $hit = $true; $score += $queryScore }
    }

    # -Kind and -Type select on their own and narrow when combined.
    $selectorOnly = -not ($Id -or $Query)
    if ($Kind) {
        if ($selectorOnly) { $hit = ($r.kind -eq $Kind) }
        elseif ($hit -and $r.kind -ne $Kind) { $hit = $false }
    }
    if ($Type) {
        $typeHit = $r.declaring -like "*$Type*"
        if ($selectorOnly -and -not $Kind) { $hit = $typeHit }
        elseif ($selectorOnly -and $Kind) { $hit = $hit -and $typeHit }
        elseif ($hit -and -not $typeHit) { $hit = $false }
    }

    if ($hit) { $scored += [PSCustomObject]@{ member = $r; score = $score } }
}

$ranked = @($scored | Sort-Object -Property @{ Expression = 'score'; Descending = $true }, @{ Expression = { $_.member.id } })
$totalMatches = $ranked.Count

$capped = $false
if (-not $All -and $queryTerms.Count -gt 0 -and $First -gt 0 -and $ranked.Count -gt $First) {
    $ranked = @($ranked | Select-Object -First $First)
    $capped = $true
}

$results = @($ranked | ForEach-Object { $_.member })

# ---- Output -----------------------------------------------------------------

if (-not $Text) {
    $envelope = [PSCustomObject]@{
        source       = $Path
        returned     = $results.Count
        totalMatches = $totalMatches
        truncated    = $capped
        members      = @($results)
    }
    if ($Pretty) { $envelope | ConvertTo-Json -Depth 6 }
    else { $envelope | ConvertTo-Json -Depth 6 -Compress }
    return
}

if ($results.Count -eq 0) {
    Write-Host 'No matching members. Run with -Package alone for kinds and the largest types.' -ForegroundColor Yellow
    return
}

Write-Host ''
foreach ($r in $results) {
    # A type's 'declaring' is already its full name, so joining would print the
    # last segment twice.
    $header = if ($r.kind -eq 'Type') { $r.declaring } else { "$($r.declaring).$($r.signature)" }
    Write-Host $header -ForegroundColor Cyan
    Write-Host "  [$($r.kind)]" -NoNewline -ForegroundColor DarkGray
    if ($r.summary) { Write-Host " $($r.summary)" }
    else { Write-Host ' (undocumented)' -ForegroundColor DarkGray }

    foreach ($p in $r.parameters) {
        Write-Host "  $($p.name): $($p.doc)" -ForegroundColor DarkGray
    }
    if ($r.returns) { Write-Host "  returns: $($r.returns)" -ForegroundColor DarkGray }
    if ($r.value) { Write-Host "  value:   $($r.value)" -ForegroundColor DarkGray }
    if ($r.inheritdoc) { Write-Host "  $($r.inheritdoc)" -ForegroundColor DarkGray }

    if ($Full) {
        if ($r.remarks) { Write-Host "  remarks: $($r.remarks)" -ForegroundColor DarkGray }
        foreach ($e in $r.exceptions) { Write-Host "  throws $($e.type): $($e.doc)" -ForegroundColor Yellow }
        foreach ($tp in $r.typeparams) { Write-Host "  <$($tp.name)>: $($tp.doc)" -ForegroundColor DarkGray }
    }
    Write-Host ''
}

if ($capped) {
    Write-Host "top $($results.Count) of $totalMatches matches. -First N for more, -All for every match." -ForegroundColor DarkGray
}
else {
    Write-Host "$($results.Count) member$(if ($results.Count -ne 1) {'s'})" -ForegroundColor DarkGray
}
Write-Host ''
