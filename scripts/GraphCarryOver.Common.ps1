<#
.SYNOPSIS
    Decides which RazorGraph graphs survive a server rotation, and in what order they
    are loaded back.

.DESCRIPTION
    Dot-sourced by Update-McpServer.ps1. Not a standalone script -- it defines functions
    and does nothing on its own.

    Deliberately does NOT set Set-StrictMode: strict mode set in a dot-sourced file leaks
    into the caller's scope. The caller sets its own, and everything here is written to
    survive it (optional properties are probed, never read blind).

    The registry of a razorgraph server is in-process memory, so a rotation used to drop
    every loaded graph without a word. The carry-over saves them before the stop and loads
    them after the start -- but not all of them: a session that builds a repo three times
    (once per agent worktree, say) leaves three graphs of one codebase, and carrying the
    stale two forward is how a stale answer outlives the process that produced it. So the
    rule is "repo, newest wins", keyed on (main repo root, graph kind).

    Select-GraphCarryOver is the decision, and it is pure given its two resolvers: tests
    hand it rows and stub resolvers, and no server is involved.

    GRAPH KIND, read off the source because list_graphs carries no language field:
      dotnet  source is a file (.sln, .slnx, .csproj, ...). build_graph and build_solution
              only accept files.
      lua     source is a directory. build_lua is the only builder that takes one. A
              directory named like a file (createflickrset.lrdevplugin) is still a
              directory, which is why this probes the disk before looking at the extension.
      saved   source is a .json: the graph was loaded from a file, and the file does not
              say which language built it. A saved graph is never deduplicated against
              anything -- its key is its own file -- because guessing its kind wrong would
              discard a Lua graph in favour of a C# one.
#>

# The builders' own file types. A missing source with one of these extensions was a
# file; a missing source with anything else was a directory (build_lua's input).
$script:GraphCarryOverProjectExtensions = @('.sln', '.slnx', '.csproj', '.vbproj', '.fsproj')

function Get-GraphCarryOverProperty {
    # $null when the row lacks the property. list_graphs omits a null builtAt entirely
    # (a Lua graph has neither stamp), and a missing property is a terminating error
    # under the caller's StrictMode.
    param([object]$Row, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Row) { return $null }
    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertTo-GraphCarryOverStamp {
    # ConvertFrom-Json turns an ISO string into a DateTime on its own, and a test may pass
    # a string or a DateTimeOffset, so all three shapes are accepted.
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]$Value }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) { return $parsed }
    return $null
}

function ConvertTo-GraphCarryOverPath {
    # Full path, no trailing separator, so two spellings of one directory compare equal.
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    $full = try { [System.IO.Path]::GetFullPath($Path) } catch { $Path }
    $trimmed = $full.TrimEnd('\', '/')
    # 'C:\' trimmed to 'C:' would mean "current directory on C:", so a root keeps its slash.
    if ($trimmed -match '^[A-Za-z]:$') { return $trimmed + '\' }
    return $trimmed
}

function Get-GraphKind {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Source)

    if (Test-Path -LiteralPath $Source -PathType Container) { return 'lua' }

    $extension = [System.IO.Path]::GetExtension($Source).ToLowerInvariant()
    if ($extension -eq '.json') { return 'saved' }
    if (Test-Path -LiteralPath $Source -PathType Leaf) { return 'dotnet' }
    if ($script:GraphCarryOverProjectExtensions -contains $extension) { return 'dotnet' }

    # Gone from disk and not named like a project file: it was a directory.
    return 'lua'
}

function Get-GraphRepoRoot {
    <#
    .SYNOPSIS
        The MAIN repo root a graph source belongs to, so an agent worktree and the
        checkout it was made from are one repo. A directory that no longer exists is
        asked about through its nearest existing ancestor. Falls back to the source's
        directory when no such ancestor is in a git repo.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Source)

    $directory = if (Test-Path -LiteralPath $Source -PathType Container) { $Source }
    else { Split-Path -Path $Source -Parent }
    if ([string]::IsNullOrWhiteSpace($directory)) { return (ConvertTo-GraphCarryOverPath $Source) }

    $fallback = ConvertTo-GraphCarryOverPath $directory

    # A gone directory is asked about through its nearest EXISTING ancestor. Agent worktrees
    # are cleaned up after the agent finishes, and a graph built in one would otherwise be
    # carried as a repo of its own. The ancestor goes through the same common-dir lookup, so
    # an ancestor that is itself a worktree still resolves to its main repo. The cost: a
    # deleted standalone repo nested inside another repo folds into the outer one.
    $probe = $directory
    while (-not [string]::IsNullOrWhiteSpace($probe) -and -not (Test-Path -LiteralPath $probe -PathType Container)) {
        $probe = Split-Path -Path $probe -Parent
    }
    if ([string]::IsNullOrWhiteSpace($probe)) { return $fallback }

    # --git-common-dir rather than --show-toplevel: a worktree's toplevel is the worktree
    # itself, while its common dir is the main repo's .git. Caught as well as exit-checked,
    # because with $PSNativeCommandUseErrorActionPreference on, a non-zero exit under Stop
    # is a terminating error rather than a $LASTEXITCODE.
    $common = $null
    try {
        $common = & git -C $probe rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($LASTEXITCODE -ne 0) { $common = $null }
    }
    catch { $common = $null }

    $common = @($common | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1
    if ($null -eq $common) { return $fallback }

    $common = ConvertTo-GraphCarryOverPath ([string]$common)

    # A normal repo or worktree: <root>\.git, so the parent is the root. Anything else (a
    # bare repo, a submodule's .git\modules\<name>) is still unique per repo, so it is used
    # as the key itself rather than collapsing several repos onto one shared parent.
    if ((Split-Path -Path $common -Leaf) -eq '.git') { return (ConvertTo-GraphCarryOverPath (Split-Path -Path $common -Parent)) }
    return $common
}

function Select-GraphCarryOver {
    <#
    .SYNOPSIS
        Decides which graphs to carry over a rotation and the order to load them in.

    .PARAMETER Graph
        Rows as list_graphs returns them: graphId, source, builtAt, loadedAt, and
        optionally isCurrent. Missing stamps are allowed.

    .PARAMETER Current
        The graph that was current before the rotation.

    .PARAMETER Remembered
        Saved-file path -> @{ source; kind }, from the previous rotation's manifest. A
        graph carried over once has the rotation file as its source (load_graph records
        the file it read), and without this map every carried graph would look like a
        'saved' graph of the rotation folder.

    .PARAMETER ResolveRepo
        Source -> repo key. Defaults to Get-GraphRepoRoot.

    .PARAMETER ResolveKind
        Source -> graph kind. Defaults to Get-GraphKind.

    .OUTPUTS
        Keep (load order: oldest first, the previous current LAST when it survived),
        Drop (each naming the graph that won), Current, CurrentKept, LastLoaded.
    #>
    [CmdletBinding()]
    param(
        [object[]]$Graph = @(),
        [string]$Current,
        [hashtable]$Remembered = @{},
        [scriptblock]$ResolveRepo = { param($s) Get-GraphRepoRoot -Source $s },
        [scriptblock]$ResolveKind = { param($s) Get-GraphKind -Source $s }
    )

    $rows = foreach ($g in @($Graph)) {
        if ($null -eq $g) { continue }
        $id = [string](Get-GraphCarryOverProperty $g 'graphId')
        $listedSource = [string](Get-GraphCarryOverProperty $g 'source')

        $memo = $null
        if ($listedSource) {
            $lookup = ConvertTo-GraphCarryOverPath $listedSource
            if ($Remembered.ContainsKey($lookup)) { $memo = $Remembered[$lookup] }
        }

        if ($null -ne $memo) {
            $source = [string]$memo.source
            $kind = [string]$memo.kind
        }
        else {
            $source = $listedSource
            $kind = if ($source) { [string](& $ResolveKind $source) } else { 'saved' }
        }

        # A saved graph is keyed on its own file, so it never displaces or is displaced.
        $repo = if (-not $source) { "(no source) $id" }
        elseif ($kind -eq 'saved') { ConvertTo-GraphCarryOverPath $source }
        else { [string](& $ResolveRepo $source) }

        $builtAt = ConvertTo-GraphCarryOverStamp (Get-GraphCarryOverProperty $g 'builtAt')
        $loadedAt = ConvertTo-GraphCarryOverStamp (Get-GraphCarryOverProperty $g 'loadedAt')
        $stamp = if ($null -ne $builtAt) { $builtAt } else { $loadedAt }
        $stampField = if ($null -ne $builtAt) { 'builtAt' } elseif ($null -ne $loadedAt) { 'loadedAt' } else { 'none' }

        $isCurrentFlag = Get-GraphCarryOverProperty $g 'isCurrent'
        $isCurrent = ($Current -and [string]::Equals($id, $Current, [StringComparison]::OrdinalIgnoreCase)) -or
            ($isCurrentFlag -eq $true)

        [PSCustomObject]@{
            GraphId          = $id
            Source           = $source
            ListedSource     = $listedSource
            Kind             = $kind
            RepoRoot         = $repo
            Stamp            = if ($null -ne $stamp) { $stamp } else { [DateTimeOffset]::MinValue }
            StampField       = $stampField
            ExtractorBuiltAt = Get-GraphCarryOverProperty $g 'extractorBuiltAt'
            IsCurrent        = [bool]$isCurrent
        }
    }
    $rows = @($rows)

    $keep = [System.Collections.Generic.List[object]]::new()
    $drop = [System.Collections.Generic.List[object]]::new()

    $groups = $rows | Group-Object -Property { ('{0}|{1}' -f $_.RepoRoot, $_.Kind).ToLowerInvariant() }
    foreach ($group in @($groups)) {
        # Newest wins. A tie goes to the current graph, then to the id, so the answer does
        # not depend on the order the server happened to list them in.
        $ranked = @($group.Group | Sort-Object -Property `
            @{ Expression = { $_.Stamp }; Descending = $true },
            @{ Expression = { $_.IsCurrent }; Descending = $true },
            @{ Expression = { $_.GraphId }; Descending = $false })

        $winner = $ranked[0]
        $keep.Add($winner)
        foreach ($loser in @($ranked | Select-Object -Skip 1)) {
            $drop.Add([PSCustomObject]@{
                    GraphId   = $loser.GraphId
                    Source    = $loser.Source
                    Kind      = $loser.Kind
                    RepoRoot  = $loser.RepoRoot
                    Winner    = $winner.GraphId
                    IsCurrent = $loser.IsCurrent
                    Reason    = ("older build in the same repo ({0}, {1}) -- '{2}' won ({3} {4} vs {5} {6})" -f
                        $loser.RepoRoot, $loser.Kind, $winner.GraphId,
                        $winner.StampField, (Format-GraphCarryOverStamp $winner),
                        $loser.StampField, (Format-GraphCarryOverStamp $loser))
                })
        }
    }

    # Load order: oldest first, and the previous current last so it stays current -- a
    # load makes the loaded graph current.
    $ordered = @($keep | Sort-Object -Property `
        @{ Expression = { $_.IsCurrent }; Descending = $false },
        @{ Expression = { $_.Stamp }; Descending = $false },
        @{ Expression = { $_.GraphId }; Descending = $false })

    $currentKept = @($ordered | Where-Object { $_.IsCurrent }).Count -gt 0
    $currentId = if ($Current) { $Current }
    else { @($rows | Where-Object { $_.IsCurrent } | ForEach-Object { $_.GraphId }) | Select-Object -First 1 }

    [PSCustomObject]@{
        Keep        = $ordered
        Drop        = @($drop)
        Current     = $currentId
        CurrentKept = [bool]$currentKept
        LastLoaded  = if ($ordered.Count -gt 0) { $ordered[-1].GraphId } else { $null }
    }
}

function Format-GraphCarryOverStamp {
    param([object]$Row)
    if ($Row.StampField -eq 'none') { return '(none)' }
    return $Row.Stamp.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss.fff') + 'Z'
}

function Get-GraphCarryOverFileName {
    # <graphId>.json, with anything a file name cannot hold replaced.
    param([Parameter(Mandatory)][string]$GraphId)
    $name = $GraphId
    foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $name = $name.Replace([string]$c, '_') }
    return "$name.json"
}

function Read-GraphCarryOverManifest {
    # Saved-file path -> @{ source; kind }, from the previous rotation. Empty when there
    # was none or it cannot be read: the cost is only that a twice-carried graph is
    # treated as 'saved' and kept rather than deduplicated.
    param([Parameter(Mandatory)][string]$Directory)
    $map = @{}
    $path = Join-Path $Directory 'manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $map }
    try {
        $entries = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
        foreach ($e in $entries) {
            $file = [string](Get-GraphCarryOverProperty $e 'file')
            $source = [string](Get-GraphCarryOverProperty $e 'source')
            if (-not $file -or -not $source) { continue }
            $map[(ConvertTo-GraphCarryOverPath $file)] = @{
                source = $source
                kind   = [string](Get-GraphCarryOverProperty $e 'kind')
            }
        }
    }
    catch { return @{} }
    return $map
}

function Format-GraphCarryOverReport {
    <#
    .SYNOPSIS
        The lines a rotation prints about its graphs. Pure, so the wording is testable
        and the sample in the catalog is what the log really says.

    .PARAMETER State
        Port, Plan (Select-GraphCarryOver), Saved, Loaded, Failures, CurrentAfter,
        Stage ('listed', 'saved', 'loaded', 'not-loaded'), Note, Directory, HeldCount.
    #>
    param([Parameter(Mandatory)][object]$State)

    $lines = [System.Collections.Generic.List[string]]::new()
    $plan = $State.Plan
    $failures = @($State.Failures)

    if ($null -eq $plan) {
        $lines.Add("graph carry-over on port $($State.Port): NOTHING carried -- $($State.Note)")
        foreach ($f in $failures) { $lines.Add("  FAILED       $f") }
        return , $lines.ToArray()
    }

    if ($State.HeldCount -eq 0) {
        $lines.Add("graph carry-over on port $($State.Port): the old server held no graphs; nothing to carry.")
        return , $lines.ToArray()
    }

    $loaded = @($State.Loaded)
    # Ids padded to one column, so the sources line up and a long list scans.
    $width = (@($loaded) + @($plan.Drop) | ForEach-Object { ([string]$_.GraphId).Length } | Measure-Object -Maximum).Maximum
    if ($null -eq $width) { $width = 0 }
    $was = if ($plan.Current) { "'$($plan.Current)'" } else { 'none' }

    if ($State.Stage -eq 'not-loaded') {
        $lines.Add(("graph carry-over on port {0}: saved {1} of {2} graphs but loaded NONE -- {3}. load_graph them from {4}, each under the id in its file name." -f
                $State.Port, @($State.Saved).Count, $State.HeldCount, $State.Note, $State.Directory))
    }
    else {
        $now = if ($State.CurrentAfter) { "'$($State.CurrentAfter)'" } else { 'unknown' }
        $lines.Add(("graph carry-over on port {0}: carried {1} of {2} graphs; current is now {3} (was {4})" -f
                $State.Port, $loaded.Count, $State.HeldCount, $now, $was))
    }

    foreach ($row in $loaded) {
        $lines.Add(('  carried      {0}  {1}' -f ([string]$row.GraphId).PadRight($width), $row.Source))
    }
    foreach ($row in @($plan.Drop)) {
        $lines.Add(('  not carried  {0}  {1} -- {2}' -f ([string]$row.GraphId).PadRight($width), $row.Source, $row.Reason))
    }
    foreach ($f in $failures) { $lines.Add("  FAILED       $f") }

    if ($plan.Current -and -not $plan.CurrentKept -and $State.Stage -ne 'not-loaded') {
        $lines.Add(("  current {0} lost the dedupe, so current is whatever loaded last ({1})." -f $was, $(if ($plan.LastLoaded) { "'$($plan.LastLoaded)'" } else { 'none' })))
    }

    if ($loaded.Count -gt 0) {
        $lines.Add('  carried graphs keep their old extractorBuiltAt: the PREVIOUS extractor built them, so rebuild for answers from the new one.')
    }

    return , $lines.ToArray()
}
