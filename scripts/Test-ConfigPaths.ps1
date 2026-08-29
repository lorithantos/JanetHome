<#
.SYNOPSIS
    Check that absolute paths in CLIENT config still resolve, and suggest replacements
    for the ones that do not.

.DESCRIPTION
    Invoke-JanetStartup.ps1 verifies that every manifest entry resolves. Its resolution
    set is the manifest's OWN entries -- client config is outside it, so .mcp.json and
    .claude\settings.json can name paths that stopped existing and nothing says so.

    That gap is not theoretical. When these repos moved from D:\Repos to C:\repos
    (observed 2026-08-29), .mcp.json still pointed at
    D:/Repos/RazorGraphTool/.mcp-bin/RazorGraph.Mcp.exe and the only symptom was an MCP
    server reporting CONNECTION_CLOSED, which names nothing; two settings.json permission
    rules pointed at D:\Repos\JanetHome\scripts\Get-Research.ps1 and silently granted
    nothing at all. Same quiet degradation Ensure-McpServer.ps1 was written for, one layer
    further out. It bites a NEW user hardest: a fresh clone to a different drive trips
    every one of these at once, and none of them announce it.

    Findings must NOT be routed into the brief's 'problems' field. Start-Janet.ps1 gates
    launch on problems, and this check fires legitimately when run from a foreign project
    dir -- which is exactly the 2026-08-01 regression where enforcement notes went there
    and broke launching from anywhere but this repo. Capture it as its own field and let
    the session read 'ok'.

    Reports, and proposes. It does not rewrite: a repaired permission allow-list is a
    privilege change, and that is the user's call to make deliberately rather than a
    startup script's to make quietly. 'suggested' is filled in when the missing file's
    leaf name resolves to exactly one candidate under this repo.

    Never throws (house rule 6). Every failure lands in the JSON result.

.PARAMETER ProjectDir
    Extra directory whose .mcp.json and .claude\settings.json to check, on top of this
    repo's. Defaults to CLAUDE_PROJECT_DIR, else the current directory -- the same
    resolution Invoke-JanetStartup.ps1 uses.

.PARAMETER SkipUserSettings
    Leave ~\.claude\settings.json out. It is shared by every repo, so a finding there is
    not this project's business -- but it is where a stale path does the most damage.

.PARAMETER Pretty
    Indent the JSON. The default is compressed: the consumer is a model, not a terminal.

.EXAMPLE
    .\Test-ConfigPaths.ps1

.EXAMPLE
    # What would a fresh clone of this repo find, checked against another project?
    .\Test-ConfigPaths.ps1 -ProjectDir C:\repos\RetirementCore -Pretty
#>
[CmdletBinding()]
param(
    [string]$ProjectDir,
    [switch]$SkipUserSettings,
    [switch]$Pretty
)

Set-StrictMode -Version Latest

# House rule 6: resolve from this script's own location, never a hardcoded layout.
$repoRoot = Split-Path $PSScriptRoot -Parent

function Get-Prop {
    # Optional-property read that cannot throw under StrictMode (house rule 2). The
    # Properties INDEXER is deliberate: '.Properties.Name -contains' throws when the
    # PSCustomObject has no properties at all, which an empty JSON object produces.
    param($Object, [string]$Name, $Default = $null)

    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    if ($null -eq $property.Value) { return $Default }
    return $property.Value
}

function Read-RuleTarget {
    # One permission rule -> { path, reason }, or $null when the rule names no absolute
    # path. A non-empty 'reason' means "cannot check this", never "this is broken":
    # fail open, the same way onExtractNoMatch defaults to warn elsewhere in the stack.
    param([string]$Rule)

    # "Tool(body)". A tool-only rule such as "Read" has no body and so no path.
    $shape = [regex]::Match($Rule, '^[A-Za-z][A-Za-z0-9_]*\((.*)\)$')
    if (-not $shape.Success) { return $null }

    $body = $shape.Groups[1].Value.Trim()
    if ($body.StartsWith('& ')) { $body = $body.Substring(2).Trim() }
    $body = $body.Trim('"', "'")

    # Only drive-absolute paths are in scope. This check exists for the drive move; a
    # relative rule like ".\scripts\build.ps1*" resolves against whichever project dir
    # the session was launched in, which is not knowable from here, and guessing would
    # produce false accusations against rules that work fine.
    if ($body -notmatch '^[A-Za-z]:[\\/]') { return $null }

    if ($body -match '\$\{|\$env:') {
        return [pscustomobject]@{ path = $body; reason = 'contains a variable this script does not expand' }
    }

    $star = $body.IndexOf('*')
    if ($star -lt 0) { return [pscustomobject]@{ path = $body; reason = '' } }

    $head = $body.Substring(0, $star)
    $tail = $body.Substring($star)

    # A trailing glob is "this command plus any arguments", so the head IS the path. A
    # glob with a separator after it is a mid-path wildcard and names a set, not a file.
    if ($tail -match '[\\/]') {
        return [pscustomobject]@{ path = $body; reason = 'wildcard appears mid-path' }
    }
    return [pscustomobject]@{ path = $head; reason = '' }
}

function Find-Replacement {
    # The missing file's leaf, looked for under this repo, resolved by DEEPEST shared
    # path suffix. Matching on the leaf alone is not enough: this repo carries both
    # scripts\Get-Research.ps1 and standalone\Get-Research.ps1, and "two candidates, no
    # suggestion" is a worse answer than the obvious one when the broken rule itself
    # named \scripts\. Scoring by trailing segments picks the copy that lived where the
    # rule said it did. Still returns '' on a genuine tie -- a confident wrong
    # suggestion is what gets pasted in without checking.
    param([string]$MissingPath)

    $leaf = Split-Path $MissingPath -Leaf
    if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf.Contains('*')) { return '' }

    # SilentlyContinue rather than Stop: one unreadable subtree must not cost the whole
    # search and silently downgrade every suggestion to ''.
    $found = @(Get-ChildItem -LiteralPath $repoRoot -Filter $leaf -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|\.git|\.janet-bin|\.mcp-bin)\\' })

    if ($found.Count -eq 0) { return '' }
    if ($found.Count -eq 1) { return $found[0].FullName }

    # @() around the split: a one-segment path yields a bare string, and [array]::Reverse
    # throws on anything that is not an array.
    $wanted = @($MissingPath -split '[\\/]' | Where-Object { $_ })
    [array]::Reverse($wanted)

    $best = ''
    $bestScore = -1
    $tied = $false

    foreach ($candidate in $found) {
        $parts = @($candidate.FullName -split '[\\/]' | Where-Object { $_ })
        [array]::Reverse($parts)

        $score = 0
        while ($score -lt $wanted.Count -and $score -lt $parts.Count -and $wanted[$score] -eq $parts[$score]) {
            $score++
        }

        if ($score -gt $bestScore) {
            $bestScore = $score
            $best = $candidate.FullName
            $tied = $false
        }
        elseif ($score -eq $bestScore) {
            $tied = $true
        }
    }

    if ($tied) { return '' }
    return $best
}

$result = [ordered]@{
    ok           = $false
    filesScanned = @()
    checked      = 0
    broken       = @()
    unchecked    = @()
    note         = ''
    error        = $null
}

# Lists rather than += : append is O(n^2) on arrays, and a typed list serialises as []
# when empty instead of vanishing the way a bare @() does on output (house rule 1).
$scanned = [System.Collections.Generic.List[string]]::new()
$broken = [System.Collections.Generic.List[object]]::new()
$unchecked = [System.Collections.Generic.List[object]]::new()
$checked = 0

try {
    if (-not $ProjectDir) {
        $ProjectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $repoRoot '.mcp.json'))
    $candidates.Add((Join-Path $repoRoot '.claude\settings.json'))
    $candidates.Add((Join-Path $ProjectDir '.mcp.json'))
    $candidates.Add((Join-Path $ProjectDir '.claude\settings.json'))
    if (-not $SkipUserSettings) {
        $candidates.Add((Join-Path $env:USERPROFILE '.claude\settings.json'))
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($file in $candidates) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }

        $full = [System.IO.Path]::GetFullPath($file)
        if (-not $seen.Add($full)) { continue }
        $scanned.Add($full)

        try {
            $json = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            # Unparseable config is its own quiet failure: a broken settings.json
            # disables every setting in it without a word.
            $unchecked.Add([pscustomobject]@{
                    file   = $full
                    rule   = ''
                    reason = "not valid JSON: $($_.Exception.Message)"
                })
            continue
        }

        $configDir = Split-Path $full -Parent

        # --- .mcp.json: stdio servers name an executable ------------------------------
        $servers = Get-Prop $json 'mcpServers'
        if ($null -ne $servers) {
            foreach ($entry in $servers.PSObject.Properties) {
                $server = $entry.Value

                # An http/sse server carries a URL and no path at all -- which is the
                # actual fix for this whole class of problem, not merely exempt from it.
                if (Get-Prop $server 'url') { continue }

                $command = Get-Prop $server 'command'
                if (-not $command) { continue }

                # A relative command resolves against the config file's own directory,
                # which IS knowable here, unlike a relative permission rule.
                $target = if ($command -match '^[A-Za-z]:[\\/]|^[\\/]') {
                    $command
                }
                else {
                    Join-Path $configDir $command
                }

                $checked++
                if (Test-Path -LiteralPath $target -PathType Leaf) { continue }

                $broken.Add([pscustomobject]@{
                        file      = $full
                        rule      = "mcpServers.$($entry.Name).command"
                        path      = $command
                        suggested = Find-Replacement $target
                    })
            }
        }

        # --- settings.json: permission rules embedding a path -------------------------
        $permissions = Get-Prop $json 'permissions'
        if ($null -ne $permissions) {
            foreach ($listName in @('allow', 'deny', 'ask')) {
                $rules = Get-Prop $permissions $listName
                if ($null -eq $rules) { continue }

                foreach ($rule in @($rules)) {
                    $target = Read-RuleTarget ([string]$rule)
                    if ($null -eq $target) { continue }

                    if ($target.reason) {
                        $unchecked.Add([pscustomobject]@{
                                file   = $full
                                rule   = [string]$rule
                                reason = $target.reason
                            })
                        continue
                    }

                    $checked++
                    if (Test-Path -LiteralPath $target.path) { continue }

                    $broken.Add([pscustomobject]@{
                            file      = $full
                            rule      = [string]$rule
                            path      = $target.path
                            suggested = Find-Replacement $target.path
                        })
                }
            }
        }
    }

    $result.filesScanned = $scanned.ToArray()
    $result.checked = $checked
    $result.broken = $broken.ToArray()
    $result.unchecked = $unchecked.ToArray()
    $result.ok = ($broken.Count -eq 0)

    $result.note = if ($scanned.Count -eq 0) {
        'No client config found to check.'
    }
    elseif ($broken.Count -eq 0) {
        "$checked absolute path(s) across $($scanned.Count) file(s) all resolve."
    }
    else {
        "$($broken.Count) of $checked path(s) no longer resolve. These fail silently: " +
        'a stale MCP command reports only a connection error, and a stale permission ' +
        'rule grants nothing. Nothing is rewritten here -- editing a permission list ' +
        'is a privilege change and stays a deliberate act.'
    }
}
catch {
    # Startup path: capture and report, never propagate (house rule 6, DESIGN-NOTES 8).
    $result.ok = $false
    $result.error = $_.Exception.Message
}

$result | ConvertTo-Json -Depth 5 -Compress:(-not $Pretty)
