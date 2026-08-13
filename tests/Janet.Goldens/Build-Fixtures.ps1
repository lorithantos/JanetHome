<#
.SYNOPSIS
    Builds the two test fixtures from the live research graph.

.DESCRIPTION
    Fixtures/research.json is a byte copy: the ranking tests are tuned against
    the whole corpus and a subset would not reproduce the ordering.

    Fixtures/layout.json keeps ten nodes, lifted as EXACT TEXT rather than
    reserialized. The writer tests assert byte equality, so the fixture has to
    carry the real file's layout idioms -- comment keys, the blank line between
    node groups, the field order -- or the goldens would freeze a layout the
    live file never had.

    Node spans are found by indentation, not by counting braces: every node's
    braces sit alone at four spaces and every line of prose is indented at six
    or more, so there is nothing for a brace inside a string to confuse.
#>
[CmdletBinding()]
param(
    [string]$Source = 'D:\Repos\JanetHome\research.json',
    [string]$FixtureDirectory = 'D:\Repos\JanetHome\tests\Janet.Tests\Fixtures'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$keep = @(
    'pattern.progressive-disclosure'
    'pattern.thread-items'
    'file.research'
    'script.get-research'
    'script.search-json'
    'script.add-research-node'
    'script.update-research-node'
    'script.get-script-catalog'
    'script.invoke-research-guard'
    'note.janet-mcp-port'
)

$raw = Get-Content $Source -Raw
$newline = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
$lines = $raw -split "`r?`n"

# --- locate every node span -------------------------------------------------
$nodes = @()
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -eq '    {') { $start = $i; continue }
    if ($start -ge 0 -and $lines[$i] -match '^    \},?$') {
        $id = $null
        for ($j = $start; $j -lt $i; $j++) {
            if ($lines[$j] -match '^      "id": "([^"]+)"') { $id = $Matches[1]; break }
        }
        $nodes += [pscustomobject]@{ Id = $id; Start = $start; End = $i }
        $start = -1
    }
}

Write-Host "nodes found: $($nodes.Count)"
$missing = $keep | Where-Object { $_ -notin $nodes.Id }
if ($missing) { throw "not in the graph: $($missing -join ', ')" }

# --- header, footer ---------------------------------------------------------
$firstNode = ($nodes | Select-Object -First 1).Start
$lastNode = ($nodes | Select-Object -Last 1).End

$header = $lines[0..($firstNode - 1)]
$footer = $lines[($lastNode + 1)..($lines.Count - 1)]

# --- rebuild ----------------------------------------------------------------
$kept = $nodes | Where-Object { $_.Id -in $keep }
$body = [System.Collections.Generic.List[string]]::new()

for ($k = 0; $k -lt $kept.Count; $k++) {
    $node = $kept[$k]

    # Preserve the blank-line grouping: if this node was preceded by one in the
    # source, it keeps it here.
    if ($k -gt 0 -and $node.Start -gt 0 -and $lines[$node.Start - 1] -eq '') {
        $body.Add('')
    }

    for ($i = $node.Start; $i -lt $node.End; $i++) { $body.Add($lines[$i]) }

    # Last node closes without a comma, whatever it did in the source.
    $body.Add($(if ($k -eq $kept.Count - 1) { '    }' } else { '    },' }))
}

$text = (@($header) + @($body) + @($footer)) -join $newline

$null = New-Item -ItemType Directory -Force -Path $FixtureDirectory
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

[System.IO.File]::WriteAllText((Join-Path $FixtureDirectory 'layout.json'), $text, $utf8NoBom)
Copy-Item $Source (Join-Path $FixtureDirectory 'research.json') -Force

# --- verify -----------------------------------------------------------------
$parsed = Get-Content (Join-Path $FixtureDirectory 'layout.json') -Raw | ConvertFrom-Json
Write-Host "layout.json: $($parsed.nodes.Count) nodes, $((Get-Item (Join-Path $FixtureDirectory 'layout.json')).Length) bytes"
Write-Host "  ids: $(($parsed.nodes.id) -join ', ')"

$inbound = @($parsed.nodes | Where-Object { $_.PSObject.Properties.Name -contains 'links' -and $_.links -contains 'script.get-research' })
Write-Host "  inbound links to script.get-research: $($inbound.Count)"
Write-Host "research.json: $((Get-Item (Join-Path $FixtureDirectory 'research.json')).Length) bytes"
