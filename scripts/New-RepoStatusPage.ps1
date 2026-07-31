<#
.SYNOPSIS
    Generates an HTML status page for local Git repositories.

.DESCRIPTION
    Scans a root directory for Git repositories and produces a dark-themed
    HTML briefing page.  Each repo is shown as a collapsible section with
    branch state, working-tree status, stash counts, orphaned-branch
    detection, and ahead/behind tracking vs the default branch.

    Branches are filtered to a single author by default (the current user).
    Use -NoFilter to show all branches.

.PARAMETER RootPath
    Root directory to scan for Git repos. Default: C:\repos

.PARAMETER Author
    Filter branches to only show those whose last commit author matches
    this value (case-insensitive substring match against the combined
    author email and name). Default: $env:USERNAME

.PARAMETER OutFile
    Path for the generated HTML file.
    Default: $env:TEMP\Janet\<today>\repo-status.html

.PARAMETER Open
    Open the HTML file in the default browser after generation.

.PARAMETER NoFilter
    Show all branches regardless of author.

.EXAMPLE
    & "$HOME\.github\scripts\New-RepoStatusPage.ps1" -Open
    # Scans C:\repos, filters to current user, opens in browser.

.EXAMPLE
    & "$HOME\.github\scripts\New-RepoStatusPage.ps1" -Author "jdoe" -OutFile "C:\temp\status.html" -Open
    # Custom author and output path.

.EXAMPLE
    & "$HOME\.github\scripts\New-RepoStatusPage.ps1" -NoFilter -Open
    # Show every branch from every author.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RootPath = 'C:\repos',

    [Parameter()]
    [string]$Author = $env:USERNAME,

    [Parameter()]
    [string]$OutFile,

    [Parameter()]
    [switch]$Open,

    [Parameter()]
    [switch]$NoFilter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib\Invoke-External.ps1"


$ntf = Join-Path $HOME '.github\scripts\New-TextFile.ps1'
$today = Get-Date -Format 'yyyy-MM-dd'

if (-not $OutFile) {
    $OutFile = Join-Path $env:TEMP ('Janet\' + $today + '\repo-status.html')
}

if (-not (Test-Path $RootPath)) {
    Write-Error "Root path not found: $RootPath"
    return
}

# ── Data gathering ──────────────────────────────────────────────────
$repoDirs = Get-ChildItem -Path $RootPath -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName '.git') }

$repos = [System.Collections.ArrayList]::new()
$authorPattern = if (-not $NoFilter) { [regex]::Escape($Author.ToLower()) } else { $null }

foreach ($d in $repoDirs) {
    Write-Verbose ('Scanning: ' + $d.Name)
    Push-Location $d.FullName
    try {
        Invoke-External -Quiet -NoThrow git config --local core.pager '' | Out-Null

        $curResult = Invoke-External -NoThrow git rev-parse --abbrev-ref HEAD
        $cur = if ($curResult.ExitCode -eq 0 -and $curResult.StdOut) { (($curResult.StdOut | Select-Object -First 1).ToString().Trim()) } else { '(detached)' }

        # Working-tree status
        $st = @((Invoke-External -NoThrow git status --porcelain).StdOut | Where-Object { $_ })
        $mo = 0; $sg = 0; $ut = 0
        foreach ($l in $st) {
            if ($l.Length -lt 2) { continue }
            if ($l.Substring(0, 2) -eq '??') { $ut++; continue }
            if ($l[0] -ne ' ' -and $l[0] -ne '?') { $sg++ }
            if ($l[1] -ne ' ' -and $l[1] -ne '?') { $mo++ }
        }
        $dirty = ($mo + $sg + $ut) -gt 0

        # Last commit on HEAD
        $lMsgResult = Invoke-External -NoThrow git log -1 --format='%s'
        $lMsg = if ($lMsgResult.ExitCode -eq 0 -and $lMsgResult.StdOut) { (($lMsgResult.StdOut | Select-Object -First 1).ToString().Trim()) } else { '(no commits)' }
        $lDateResult = Invoke-External -NoThrow git log -1 --format='%ai'
        $lDate = if ($lDateResult.ExitCode -eq 0 -and $lDateResult.StdOut) { (($lDateResult.StdOut | Select-Object -First 1).ToString().Trim()) } else { '' }

        # Stashes
        $stsh = @((Invoke-External -NoThrow git stash list).StdOut | Where-Object { $_ })
        $stCnt = $stsh.Count

        # Default branch
        $allB = @((Invoke-External -NoThrow git branch --format='%(refname:short)').StdOut | Where-Object { $_ })
        $def = $null
        if ('main' -in $allB) { $def = 'main' }
        elseif ('master' -in $allB) { $def = 'master' }

        # Remote-status lookup
        $remoteStatus = @{}
        $refLines = @((Invoke-External -NoThrow git for-each-ref --format='%(refname:short) %(upstream) %(upstream:track)' refs/heads/).StdOut | Where-Object { $_ })
        foreach ($rl in $refLines) {
            $parts = $rl -split '\s+', 3
            $brName = $parts[0]
            $upstream = if ($parts.Count -ge 2) { $parts[1] } else { '' }
            $track   = if ($parts.Count -ge 3) { $parts[2] } else { '' }
            if (-not $upstream) {
                $remoteStatus[$brName] = 'local'
            } elseif ($track -match '\[gone\]') {
                $remoteStatus[$brName] = 'orphaned'
            } else {
                $remoteStatus[$brName] = 'tracking'
            }
        }

        # Per-branch details
        $brs = [System.Collections.ArrayList]::new()
        foreach ($b in $allB) {
            $bmResult = Invoke-External -NoThrow git log -1 --format='%s' $b
            $bm = if ($bmResult.ExitCode -eq 0 -and $bmResult.StdOut) { (($bmResult.StdOut | Select-Object -First 1).ToString().Trim()) } else { '' }
            $bdResult = Invoke-External -NoThrow git log -1 --format='%aI' $b
            $bd = if ($bdResult.ExitCode -eq 0 -and $bdResult.StdOut) { (($bdResult.StdOut | Select-Object -First 1).ToString().Trim()) } else { '' }
            $baResult = Invoke-External -NoThrow git log -1 --format='%ae %an' $b
            $ba = if ($baResult.ExitCode -eq 0 -and $baResult.StdOut) { (($baResult.StdOut | Select-Object -First 1).ToString().Trim().ToLower()) } else { '' }
            $ah = 0; $bh = 0
            if ($def -and $b -ne $def) {
                $abResult = Invoke-External -NoThrow git rev-list --left-right --count ($def + '...' + $b)
                $ab = if ($abResult.ExitCode -eq 0 -and $abResult.StdOut) { ($abResult.StdOut | Select-Object -First 1).ToString() } else { '' }
                if ($ab -match '(\d+)\s+(\d+)') {
                    $bh = [int]$Matches[1]; $ah = [int]$Matches[2]
                }
            }
            $rs = if ($remoteStatus.ContainsKey($b)) { $remoteStatus[$b] } else { 'local' }
            [void]$brs.Add([PSCustomObject]@{
                n=$b; c=($b -eq $cur); d=$bd; m=$bm
                a=$ah; b=$bh; rs=$rs; au=$ba
            })
        }

        $brs = @($brs | Sort-Object @{E={$_.c};D=$true}, @{E={$_.d};D=$true})

        if ($authorPattern) {
            $brs = @($brs | Where-Object { $_.au -match $authorPattern })
        }

        [void]$repos.Add([PSCustomObject]@{
            name=$d.Name; branch=$cur; dirty=$dirty
            mod=$mo; stg=$sg; unt=$ut; msg=$lMsg; date=$lDate
            stash=$stCnt; def=$(if ($def) { $def } else { '(none)' })
            branches=$brs; hasBranches=($brs.Count -gt 0)
        })
    } finally { Pop-Location }
}

$repos = @($repos | Sort-Object @{E={$_.dirty};D=$true}, @{E={$_.name}})
Write-Verbose ('Gathered data for ' + $repos.Count + ' repos')

# ── HTML generation ─────────────────────────────────────────────────
function HtmlEnc([string]$s)        { if ($s) { [System.Net.WebUtility]::HtmlEncode($s) } else { '' } }
function Truncate([string]$s,[int]$n) { if ($s -and $s.Length -gt $n) { $s.Substring(0, $n) + '...' } else { $s } }

$dirtyCount    = @($repos | Where-Object { $_.dirty }).Count
$cleanCount    = @($repos | Where-Object { -not $_.dirty }).Count
$stashCount    = @($repos | Where-Object { $_.stash -gt 0 }).Count
$orphanedTotal = 0
$orphanedRepos = 0
foreach ($r in $repos) {
    $oc = @($r.branches | Where-Object { $_.rs -eq 'orphaned' }).Count
    $orphanedTotal += $oc
    if ($oc -gt 0) { $orphanedRepos++ }
}

$filterLabel = if ($NoFilter) { 'all authors' } else { $Author }

$h = [System.Text.StringBuilder]::new(65536)

# Head / CSS
[void]$h.AppendLine('<!DOCTYPE html>')
[void]$h.AppendLine('<html lang="en"><head><meta charset="utf-8">')
[void]$h.AppendLine('<title>Repo Status -- ' + $today + '</title>')
[void]$h.AppendLine('<style>')
[void]$h.AppendLine('*{margin:0;padding:0;box-sizing:border-box}')
[void]$h.AppendLine('body{background:#1a1a2e;color:#e0e0e0;font-family:Segoe UI,system-ui,sans-serif;padding:24px 32px;line-height:1.5}')
[void]$h.AppendLine('h1{color:#e8e8e8;font-size:1.6rem;font-weight:600;margin-bottom:4px}')
[void]$h.AppendLine('.subtitle{color:#999;font-size:0.85rem;margin-bottom:24px}')
[void]$h.AppendLine('.summary{display:flex;gap:16px;margin-bottom:24px;flex-wrap:wrap}')
[void]$h.AppendLine('.summary .pill{padding:6px 16px;border-radius:20px;font-size:0.82rem;font-weight:500}')
[void]$h.AppendLine('.pill-clean{background:#2a3a2a;color:#b3e6b3;border:1px solid #3a5a3a}')
[void]$h.AppendLine('.pill-dirty{background:#3a3020;color:#ffe0a3;border:1px solid #5a4a2a}')
[void]$h.AppendLine('.pill-stash{background:#3a2020;color:#ffb3b3;border:1px solid #5a2a2a}')
[void]$h.AppendLine('.pill-orphan{background:#3a1a2a;color:#ffb3b3;border:1px solid #5a2a3a}')
[void]$h.AppendLine('details{margin-bottom:8px;border:1px solid #2a2a4a;border-radius:8px;overflow:hidden}')
[void]$h.AppendLine('details[open]{margin-bottom:12px}')
[void]$h.AppendLine('summary.repo-sum{cursor:pointer;padding:12px 16px;background:#16213e;display:flex;align-items:center;gap:12px;user-select:none;list-style:none}')
[void]$h.AppendLine('summary.repo-sum::-webkit-details-marker{display:none}')
[void]$h.AppendLine('summary.repo-sum::before{content:"\25B8";display:inline-block;transition:transform .15s;font-size:.75rem;color:#666;width:14px}')
[void]$h.AppendLine('details[open]>summary.repo-sum::before{transform:rotate(90deg)}')
[void]$h.AppendLine('summary.repo-sum:hover{background:#1a2744}')
[void]$h.AppendLine('.repo-name{font-weight:600;font-size:0.95rem}')
[void]$h.AppendLine('.badge{padding:2px 10px;border-radius:12px;font-size:0.72rem;font-weight:500}')
[void]$h.AppendLine('.badge-clean{background:#2a3a2a;color:#b3e6b3}')
[void]$h.AppendLine('.badge-dirty{background:#3a3020;color:#ffe0a3}')
[void]$h.AppendLine('.badge-stash{background:#3a2020;color:#ffb3b3}')
[void]$h.AppendLine('.badge-orphan{background:#3a1a2a;color:#ffb3b3}')
[void]$h.AppendLine('.branch-tag{color:#88aadd;font-size:0.82rem;font-family:Cascadia Code,Consolas,monospace}')
[void]$h.AppendLine('.detail-body{padding:12px 16px;background:#0f0f23}')
[void]$h.AppendLine('.meta{font-size:0.8rem;color:#999;margin-bottom:10px}')
[void]$h.AppendLine('table{width:100%;border-collapse:collapse;font-size:0.82rem}')
[void]$h.AppendLine('th{text-align:left;padding:6px 10px;background:#16213e;color:#aaa;font-weight:500;border-bottom:1px solid #2a2a4a}')
[void]$h.AppendLine('td{padding:6px 10px;border-bottom:1px solid #1a1a3a}')
[void]$h.AppendLine('tr:hover td{background:#16213e}')
[void]$h.AppendLine('tr.orphan-row td{background:#2a1a1a}')
[void]$h.AppendLine('tr.orphan-row:hover td{background:#3a2020}')
[void]$h.AppendLine('.cur{font-weight:600;color:#88ddaa}')
[void]$h.AppendLine('.ahead{color:#88ccff;font-size:0.78rem}')
[void]$h.AppendLine('.behind{color:#ffaa88;font-size:0.78rem}')
[void]$h.AppendLine('.commit-msg{color:#ccc;max-width:420px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}')
[void]$h.AppendLine('.commit-date{color:#888;white-space:nowrap}')
[void]$h.AppendLine('.file-counts{font-size:0.78rem;color:#bbb}')
[void]$h.AppendLine('.rs-tracking{color:#888;font-size:0.78rem}')
[void]$h.AppendLine('.rs-local{color:#666;font-size:0.78rem}')
[void]$h.AppendLine('.rs-orphaned{color:#ffb3b3;font-weight:600;font-size:0.78rem}')
[void]$h.AppendLine('</style></head><body>')

# Header + summary
[void]$h.AppendLine('<h1>Repo Status -- ' + $today + '</h1>')
[void]$h.AppendLine('<div class="subtitle">Generated ' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ' -- ' + $repos.Count + ' repositories -- branches by ' + (HtmlEnc $filterLabel) + '</div>')
[void]$h.AppendLine('<div class="summary">')
[void]$h.AppendLine('<span class="pill pill-clean">' + $cleanCount + ' clean</span>')
[void]$h.AppendLine('<span class="pill pill-dirty">' + $dirtyCount + ' dirty</span>')
[void]$h.AppendLine('<span class="pill pill-stash">' + $stashCount + ' with stashes</span>')
if ($orphanedTotal -gt 0) {
    [void]$h.AppendLine('<span class="pill pill-orphan">' + $orphanedTotal + ' orphaned branch' + $(if ($orphanedTotal -ne 1) { 'es' } else { '' }) + ' across ' + $orphanedRepos + ' repo' + $(if ($orphanedRepos -ne 1) { 's' } else { '' }) + '</span>')
}
[void]$h.AppendLine('</div>')

# Repo sections
foreach ($r in $repos) {
    $color = if ($r.stash -gt 0) { '#ffb3b3' } elseif ($r.dirty) { '#ffe0a3' } else { '#b3e6b3' }
    $statusBadge = if ($r.dirty) { '<span class="badge badge-dirty">dirty</span>' } else { '<span class="badge badge-clean">clean</span>' }
    $stashBadge = ''
    if ($r.stash -gt 0) {
        $pl = if ($r.stash -gt 1) { 'es' } else { '' }
        $stashBadge = ' <span class="badge badge-stash">' + $r.stash + ' stash' + $pl + '</span>'
    }
    $repoOrphanCnt = @($r.branches | Where-Object { $_.rs -eq 'orphaned' }).Count
    $orphanBadge = ''
    if ($repoOrphanCnt -gt 0) {
        $orphanBadge = ' <span class="badge badge-orphan">' + $repoOrphanCnt + ' orphaned</span>'
    }
    $fileCounts = ''
    if ($r.dirty) {
        $parts = @()
        if ($r.stg -gt 0) { $parts += ('' + $r.stg + ' staged') }
        if ($r.mod -gt 0) { $parts += ('' + $r.mod + ' modified') }
        if ($r.unt -gt 0) { $parts += ('' + $r.unt + ' untracked') }
        $fileCounts = ' <span class="file-counts">(' + ($parts -join ', ') + ')</span>'
    }

    [void]$h.AppendLine('<details>')
    [void]$h.AppendLine('<summary class="repo-sum" style="border-left:3px solid ' + $color + '">')
    [void]$h.AppendLine('<span class="repo-name" style="color:' + $color + '">' + (HtmlEnc $r.name) + '</span>')
    [void]$h.AppendLine('<span class="branch-tag">' + (HtmlEnc $r.branch) + '</span>')
    [void]$h.AppendLine($statusBadge + $stashBadge + $orphanBadge + $fileCounts)
    [void]$h.AppendLine('</summary>')
    [void]$h.AppendLine('<div class="detail-body">')
    [void]$h.AppendLine('<div class="meta">Last commit: ' + (HtmlEnc (Truncate $r.msg 80)) + ' <span style="color:#666">(' + (HtmlEnc $r.date) + ')</span> | Default branch: <b>' + (HtmlEnc $r.def) + '</b></div>')

    if (-not $r.hasBranches) {
        $noLabel = if ($NoFilter) { 'no local branches' } else { 'no local branches by ' + (HtmlEnc $Author) }
        [void]$h.AppendLine('<div style="color:#666;font-style:italic;padding:8px 0;font-size:0.85rem">' + $noLabel + '</div>')
    } else {
        [void]$h.AppendLine('<table><tr><th>Branch</th><th>Last Commit</th><th>Message</th><th>vs ' + (HtmlEnc $r.def) + '</th><th>Remote Status</th></tr>')
        foreach ($br in $r.branches) {
            $rowClass = if ($br.rs -eq 'orphaned') { ' class="orphan-row"' } else { '' }
            $nc = if ($br.c) { ' class="cur"' } else { '' }
            $nt = if ($br.c) { '* ' + (HtmlEnc $br.n) } else { HtmlEnc $br.n }
            $abText = ''
            if ($br.n -ne $r.def) {
                $p2 = @()
                if ($br.a -gt 0) { $p2 += '<span class="ahead">+' + $br.a + ' ahead</span>' }
                if ($br.b -gt 0) { $p2 += '<span class="behind">-' + $br.b + ' behind</span>' }
                if ($p2.Count -gt 0) { $abText = $p2 -join ' ' }
                elseif ($r.def -ne '(none)') { $abText = '<span style="color:#555">even</span>' }
            } else { $abText = '<span style="color:#555">--</span>' }
            $ds = if ($br.d.Length -ge 10) { $br.d.Substring(0, 10) } else { $br.d }
            $rsCell = switch ($br.rs) {
                'tracking' { '<span class="rs-tracking">tracking</span>' }
                'orphaned' { '<span class="rs-orphaned">ORPHANED</span>' }
                default    { '<span class="rs-local">local only</span>' }
            }
            [void]$h.AppendLine('<tr' + $rowClass + '><td' + $nc + '>' + $nt + '</td><td class="commit-date">' + (HtmlEnc $ds) + '</td><td class="commit-msg">' + (HtmlEnc (Truncate $br.m 72)) + '</td><td>' + $abText + '</td><td>' + $rsCell + '</td></tr>')
        }
        [void]$h.AppendLine('</table>')
    }
    [void]$h.AppendLine('</div></details>')
}

[void]$h.AppendLine('</body></html>')

# ── Write output ────────────────────────────────────────────────────
$html = $h.ToString()
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($html))
& $ntf -Path $OutFile -Base64 $b64 -Force

Write-Verbose ('Orphaned branches: ' + $orphanedTotal + ' across ' + $orphanedRepos + ' repos')
Write-Host $OutFile

if ($Open) {
    Start-Process $OutFile
}
