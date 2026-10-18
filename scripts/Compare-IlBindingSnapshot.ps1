<#
.SYNOPSIS
    Compares two IL binding snapshots and reports every call target (or, at Full
    detail, every operand) that changed, with the method it sits in. Exits 1
    when anything changed.

.DESCRIPTION
    The second half of Get-IlBindingSnapshot.ps1. Snapshot a build before a
    change and after it, then run this: a change that should be
    behaviour-neutral -- usings, namespaces, a new overload or extension method
    elsewhere -- alters no IL unless overload or name resolution picked a
    different target, so any difference reported here is either a silent
    rebind or a real code change, and it names the method so you can tell
    which.

    What is compared, per assembly (paired by assembly name):
      - assembly references (name and version)
      - methods present on one side only
      - per method, the ordered call targets; a sequence diff pairs each
        removed target with the one that replaced it, so a rebind reads as
        "site #3: A.AExt::Hi(Object) => B.BExt::Hi(String)"
      - at Full detail also: the ordered operand list, the normalised body hash,
        the TypeRef/MemberRef set and the custom attribute set

    EXIT CODES. 0: identical. 1: something changed. 2: the comparison could not
    be made (unreadable file, not a snapshot, different -Detail on the two
    sides). A gate should treat anything non-zero as "not proven unchanged".

    WHAT IT CANNOT SEE is stated in the catalog node (script.compare-il-binding-snapshot)
    and is worth reading before trusting a clean result: CallTargets misses type
    rebinds; compiler-generated names and state machines shift with unrelated
    edits; a different SDK or compiler changes IL with no rebind at all.

.PARAMETER Before
    Snapshot taken before the change.

.PARAMETER After
    Snapshot taken after the change.

.PARAMETER First
    List at most this many changed methods (default 50), and at most this many
    entries in each added/removed list. Totals are always complete and
    'truncated' says when a list was cut.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Compare-IlBindingSnapshot.ps1" -Before before.json -After after.json -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Compare-IlBindingSnapshot.ps1" -Before before.json -After after.json
    if ($LASTEXITCODE -ne 0) { throw 'binding changed, or could not be compared' }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Before,

    [Parameter(Mandatory = $true)]
    [string]$After,

    [ValidateRange(1, 100000)]
    [int]$First = 50,

    [switch]$Text,

    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Changes listed per method before the rest are only counted.
$perMethodCap = 20

function Get-Prop {
    param([object]$Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

function Read-Snapshot {
    param([string]$SnapshotPath)
    if (-not (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) { throw "Snapshot not found: $SnapshotPath" }
    $snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
    if ((Get-Prop $snapshot 'format') -ne 'il-binding-snapshot') { throw "Not an IL binding snapshot: $SnapshotPath" }
    if ((Get-Prop $snapshot 'version') -ne 1) { throw "Unsupported snapshot version $(Get-Prop $snapshot 'version'): $SnapshotPath" }
    return $snapshot
}

function ConvertTo-Map {
    # Ordinal, case-sensitive: C# allows members that differ only in case.
    param([object[]]$Items, [string]$KeyName)
    $map = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Items) { $map[$item.$KeyName] = $item }
    return , $map
}

function Compare-Set {
    # ASSIGN THE RESULT: returns a hashtable of the two one-sided lists.
    param([object[]]$Left, [object[]]$Right)
    $leftSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($Left), [System.StringComparer]::Ordinal)
    $rightSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($Right), [System.StringComparer]::Ordinal)
    return @{
        removed = @($Left | Where-Object { -not $rightSet.Contains($_) })
        added   = @($Right | Where-Object { -not $leftSet.Contains($_) })
    }
}

function Compare-Sequence {
    # ASSIGN THE RESULT: returns a comma-wrapped array of change entries. Trims the
    # common prefix and suffix, then runs an LCS over what is left, so a one-site
    # rebind in a long method costs almost nothing.
    param([string[]]$Left, [string[]]$Right)

    $start = 0
    while ($start -lt $Left.Count -and $start -lt $Right.Count -and $Left[$start] -ceq $Right[$start]) { $start++ }
    $leftEnd = $Left.Count - 1
    $rightEnd = $Right.Count - 1
    while ($leftEnd -ge $start -and $rightEnd -ge $start -and $Left[$leftEnd] -ceq $Right[$rightEnd]) {
        $leftEnd--
        $rightEnd--
    }
    $n = $leftEnd - $start + 1
    $m = $rightEnd - $start + 1

    $lcs = [int[, ]]::new($n + 1, $m + 1)
    for ($i = $n - 1; $i -ge 0; $i--) {
        for ($j = $m - 1; $j -ge 0; $j--) {
            $lcs[$i, $j] = if ($Left[$start + $i] -ceq $Right[$start + $j]) { $lcs[($i + 1), ($j + 1)] + 1 }
            else { [Math]::Max($lcs[($i + 1), $j], $lcs[$i, ($j + 1)]) }
        }
    }

    $changes = [System.Collections.Generic.List[object]]::new()
    $removed = [System.Collections.Generic.List[int]]::new()
    $added = [System.Collections.Generic.List[int]]::new()
    $flush = {
        $paired = [Math]::Min($removed.Count, $added.Count)
        for ($k = 0; $k -lt [Math]::Max($removed.Count, $added.Count); $k++) {
            $b = if ($k -lt $removed.Count) { $removed[$k] } else { $null }
            $a = if ($k -lt $added.Count) { $added[$k] } else { $null }
            $kind = if ($k -lt $paired) { 'changed' } elseif ($null -ne $b) { 'removed' } else { 'added' }
            $changes.Add([ordered]@{
                    kind       = $kind
                    beforeSite = $b
                    afterSite  = $a
                    before     = if ($null -ne $b) { $Left[$b] } else { $null }
                    after      = if ($null -ne $a) { $Right[$a] } else { $null }
                })
        }
        $removed.Clear()
        $added.Clear()
    }
    $i = 0
    $j = 0
    while ($i -lt $n -or $j -lt $m) {
        if ($i -lt $n -and $j -lt $m -and $Left[$start + $i] -ceq $Right[$start + $j]) {
            . $flush
            $i++
            $j++
        }
        elseif ($j -lt $m -and ($i -ge $n -or $lcs[$i, ($j + 1)] -ge $lcs[($i + 1), $j])) {
            $added.Add($start + $j)
            $j++
        }
        else {
            $removed.Add($start + $i)
            $i++
        }
    }
    . $flush
    return , $changes.ToArray()
}

function Compare-MethodPair {
    # Returns $null when the two sides of one method agree, otherwise a hashtable:
    # Entry (the report row) and the change counts behind it -- Calls, Operands,
    # Bodies -- which are complete even when Entry's lists are capped.
    param([string]$Assembly, [string]$Id, [object]$LeftMethod, [object]$RightMethod, [bool]$IncludeFull)

    $entry = [ordered]@{ assembly = $Assembly; method = $Id }
    $counts = @{ Entry = $entry; Calls = 0; Operands = 0; Bodies = 0 }

    $views = @(@{ Name = 'calls'; Key = 'Calls' })
    if ($IncludeFull) { $views += @{ Name = 'operands'; Key = 'Operands' } }
    foreach ($view in $views) {
        $leftItems = [string[]]@(Get-Prop $LeftMethod $view.Name)
        $rightItems = [string[]]@(Get-Prop $RightMethod $view.Name)
        if (($leftItems -join "`n") -ceq ($rightItems -join "`n")) { continue }
        $changes = Compare-Sequence -Left $leftItems -Right $rightItems
        $counts[$view.Key] = $changes.Count
        $entry[$view.Name] = Get-Capped -Items $changes -Limit $perMethodCap
        if ($changes.Count -gt $perMethodCap) { $entry["$($view.Name)Omitted"] = $changes.Count - $perMethodCap }
    }
    if ($IncludeFull -and (Get-Prop $LeftMethod 'bodyHash') -ne (Get-Prop $RightMethod 'bodyHash')) {
        $counts.Bodies = 1
        $entry.bodyHash = [ordered]@{ before = Get-Prop $LeftMethod 'bodyHash'; after = Get-Prop $RightMethod 'bodyHash' }
    }
    if ($counts.Calls + $counts.Operands + $counts.Bodies -eq 0) { return $null }
    return $counts
}

function Get-Capped {
    # ASSIGN THE RESULT: returns a comma-wrapped array of at most $Limit items.
    param([object[]]$Items, [int]$Limit)
    return , @($Items | Select-Object -First $Limit)
}

$result = [ordered]@{
    ok        = $false
    changed   = $false
    detail    = $null
    before    = $null
    after     = $null
    summary   = [ordered]@{
        assembliesAdded             = 0
        assembliesRemoved           = 0
        assemblyReferencesChanged   = 0
        methodsAdded                = 0
        methodsRemoved              = 0
        methodsChanged              = 0
        changedCallSites            = 0
        changedOperands             = 0
        changedBodies               = 0
        referencesChanged           = 0
        attributesChanged           = 0
    }
    truncated = $false
    changes   = [ordered]@{
        assemblies         = [ordered]@{ added = @(); removed = @() }
        assemblyReferences = @()
        methods            = [ordered]@{ added = @(); removed = @(); changed = @() }
        references         = @()
        attributes         = @()
    }
    notes     = @()
    error     = $null
}

try {
    $left = Read-Snapshot -SnapshotPath $Before
    $right = Read-Snapshot -SnapshotPath $After
    if ($left.detail -ne $right.detail) {
        throw "Snapshots were taken at different detail ($($left.detail) and $($right.detail)); retake one so both match."
    }
    $full = $left.detail -eq 'Full'
    $result.detail = $left.detail

    $describe = {
        param($snapshot, $file)
        [ordered]@{
            file       = [System.IO.Path]::GetFullPath($file)
            created    = Get-Prop $snapshot 'created'
            sdk        = Get-Prop $snapshot.source 'sdk'
            assemblies = @($snapshot.assemblies).Count
            methods    = [int](@($snapshot.assemblies) | ForEach-Object { @($_.methods).Count } | Measure-Object -Sum).Sum
            callSites  = [int](@($snapshot.assemblies) | Measure-Object -Property callSites -Sum).Sum
        }
    }
    $result.before = & $describe $left $Before
    $result.after = & $describe $right $After

    $notes = [System.Collections.Generic.List[string]]::new()
    if ($result.before.sdk -ne $result.after.sdk) {
        $notes.Add("SDK differs ($($result.before.sdk) -> $($result.after.sdk)): a compiler change alters IL with no rebind, so expect noise.")
    }

    $leftAssemblies = ConvertTo-Map -Items @($left.assemblies) -KeyName 'name'
    $rightAssemblies = ConvertTo-Map -Items @($right.assemblies) -KeyName 'name'
    $assembliesRemoved = @($leftAssemblies.Keys | Where-Object { -not $rightAssemblies.ContainsKey($_) } | Sort-Object)
    $assembliesAdded = @($rightAssemblies.Keys | Where-Object { -not $leftAssemblies.ContainsKey($_) } | Sort-Object)

    $referenceChanges = [System.Collections.Generic.List[object]]::new()
    $methodsAdded = [System.Collections.Generic.List[string]]::new()
    $methodsRemoved = [System.Collections.Generic.List[string]]::new()
    $methodsChanged = [System.Collections.Generic.List[object]]::new()
    $metadataRefChanges = [System.Collections.Generic.List[object]]::new()
    $attributeChanges = [System.Collections.Generic.List[object]]::new()
    $identicalFiles = 0

    foreach ($name in @($leftAssemblies.Keys | Where-Object { $rightAssemblies.ContainsKey($_) } | Sort-Object)) {
        $l = $leftAssemblies[$name]
        $r = $rightAssemblies[$name]
        if ($l.sha256 -eq $r.sha256) { $identicalFiles++ }

        $refDiff = Compare-Set -Left @($l.assemblyReferences) -Right @($r.assemblyReferences)
        foreach ($entry in $refDiff.removed) { $referenceChanges.Add([ordered]@{ assembly = $name; kind = 'removed'; reference = $entry }) }
        foreach ($entry in $refDiff.added) { $referenceChanges.Add([ordered]@{ assembly = $name; kind = 'added'; reference = $entry }) }

        $leftMethods = ConvertTo-Map -Items @($l.methods) -KeyName 'id'
        $rightMethods = ConvertTo-Map -Items @($r.methods) -KeyName 'id'
        foreach ($id in @($leftMethods.Keys | Where-Object { -not $rightMethods.ContainsKey($_) } | Sort-Object)) { $methodsRemoved.Add("$name | $id") }
        foreach ($id in @($rightMethods.Keys | Where-Object { -not $leftMethods.ContainsKey($_) } | Sort-Object)) { $methodsAdded.Add("$name | $id") }

        foreach ($id in @($leftMethods.Keys | Where-Object { $rightMethods.ContainsKey($_) } | Sort-Object)) {
            $pair = Compare-MethodPair -Assembly $name -Id $id -LeftMethod $leftMethods[$id] -RightMethod $rightMethods[$id] -IncludeFull $full
            if ($null -eq $pair) { continue }
            $result.summary.changedCallSites += $pair.Calls
            $result.summary.changedOperands += $pair.Operands
            $result.summary.changedBodies += $pair.Bodies
            $methodsChanged.Add($pair.Entry)
        }

        if ($full) {
            $metaDiff = Compare-Set -Left @(Get-Prop $l 'references') -Right @(Get-Prop $r 'references')
            foreach ($row in $metaDiff.removed) { $metadataRefChanges.Add([ordered]@{ assembly = $name; kind = 'removed'; row = $row }) }
            foreach ($row in $metaDiff.added) { $metadataRefChanges.Add([ordered]@{ assembly = $name; kind = 'added'; row = $row }) }
            $attributeDiff = Compare-Set -Left @(Get-Prop $l 'attributes') -Right @(Get-Prop $r 'attributes')
            foreach ($row in $attributeDiff.removed) { $attributeChanges.Add([ordered]@{ assembly = $name; kind = 'removed'; attribute = $row }) }
            foreach ($row in $attributeDiff.added) { $attributeChanges.Add([ordered]@{ assembly = $name; kind = 'added'; attribute = $row }) }
        }
    }

    if ($referenceChanges.Count -gt 0) {
        $notes.Add('Assembly reference versions changed: a package or framework move rebinds by definition, and may change IL beyond what the source change explains.')
    }
    $paired = @($leftAssemblies.Keys | Where-Object { $rightAssemblies.ContainsKey($_) }).Count
    if ($paired -gt 0 -and $identicalFiles -eq $paired) { $notes.Add("All $paired paired assemblies are byte-identical.") }

    $s = $result.summary
    $s.assembliesAdded = $assembliesAdded.Count
    $s.assembliesRemoved = $assembliesRemoved.Count
    $s.assemblyReferencesChanged = $referenceChanges.Count
    $s.methodsAdded = $methodsAdded.Count
    $s.methodsRemoved = $methodsRemoved.Count
    $s.methodsChanged = $methodsChanged.Count
    $s.referencesChanged = $metadataRefChanges.Count
    $s.attributesChanged = $attributeChanges.Count

    $c = $result.changes
    $c.assemblies.added = Get-Capped -Items $assembliesAdded -Limit $First
    $c.assemblies.removed = Get-Capped -Items $assembliesRemoved -Limit $First
    $c.assemblyReferences = Get-Capped -Items $referenceChanges.ToArray() -Limit $First
    $c.methods.added = Get-Capped -Items $methodsAdded.ToArray() -Limit $First
    $c.methods.removed = Get-Capped -Items $methodsRemoved.ToArray() -Limit $First
    $c.methods.changed = Get-Capped -Items $methodsChanged.ToArray() -Limit $First
    $c.references = Get-Capped -Items $metadataRefChanges.ToArray() -Limit $First
    $c.attributes = Get-Capped -Items $attributeChanges.ToArray() -Limit $First
    $result.truncated = @($assembliesAdded.Count, $assembliesRemoved.Count, $referenceChanges.Count, $methodsAdded.Count,
        $methodsRemoved.Count, $methodsChanged.Count, $metadataRefChanges.Count, $attributeChanges.Count |
            Where-Object { $_ -gt $First }).Count -gt 0

    $result.changed = ($s.Values | Measure-Object -Sum).Sum -gt 0
    $result.notes = $notes.ToArray()
    $result.ok = $true
}
catch {
    $result.error = $_.Exception.Message
}

$exitCode = if (-not $result.ok) { 2 } elseif ($result.changed) { 1 } else { 0 }

if (-not $Text) {
    if ($Pretty) { [pscustomobject]$result | ConvertTo-Json -Depth 8 }
    else { [pscustomobject]$result | ConvertTo-Json -Depth 8 -Compress }
    exit $exitCode
}

# -Text goes to the output stream, not the host, so a caller can capture it.
if (-not $result.ok) {
    Write-Output "compare failed: $($result.error)"
    exit $exitCode
}

$b = $result.before
$a = $result.after
Write-Output "IL binding compare ($($result.detail))"
Write-Output "  before: $($b.assemblies) assemblies, $($b.methods) methods, $($b.callSites) call sites"
Write-Output "  after:  $($a.assemblies) assemblies, $($a.methods) methods, $($a.callSites) call sites"
foreach ($note in $result.notes) { Write-Output "  note: $note" }

if (-not $result.changed) {
    Write-Output 'UNCHANGED: nothing compared differs.'
    exit 0
}

$s = $result.summary
Write-Output ("CHANGED: {0} methods ({1} call sites, {2} operands, {3} bodies); methods +{4} -{5}; assemblies +{6} -{7}; assembly refs {8}; metadata refs {9}; attributes {10}" -f
    $s.methodsChanged, $s.changedCallSites, $s.changedOperands, $s.changedBodies, $s.methodsAdded, $s.methodsRemoved,
    $s.assembliesAdded, $s.assembliesRemoved, $s.assemblyReferencesChanged, $s.referencesChanged, $s.attributesChanged)

$formatChange = {
    param($view, $change)
    switch ($change.kind) {
        'changed' { "    $view #$($change.beforeSite): $($change.before)  =>  #$($change.afterSite): $($change.after)" }
        'removed' { "    $view #$($change.beforeSite) removed: $($change.before)" }
        default { "    $view #$($change.afterSite) added: $($change.after)" }
    }
}
foreach ($method in $result.changes.methods.changed) {
    Write-Output "  $($method.assembly) | $($method.method)"
    if ($method.Contains('calls')) { foreach ($change in $method.calls) { & $formatChange 'call' $change } }
    if ($method.Contains('callsOmitted')) { Write-Output "    ... $($method.callsOmitted) more call changes" }
    if ($method.Contains('operands')) { foreach ($change in $method.operands) { & $formatChange 'operand' $change } }
    if ($method.Contains('operandsOmitted')) { Write-Output "    ... $($method.operandsOmitted) more operand changes" }
    if ($method.Contains('bodyHash')) { Write-Output '    body hash differs' }
}
foreach ($id in $result.changes.methods.removed) { Write-Output "  - method $id" }
foreach ($id in $result.changes.methods.added) { Write-Output "  + method $id" }
foreach ($name in $result.changes.assemblies.removed) { Write-Output "  - assembly $name" }
foreach ($name in $result.changes.assemblies.added) { Write-Output "  + assembly $name" }
foreach ($row in $result.changes.assemblyReferences) { Write-Output "  $($row.kind) assembly reference in $($row.assembly): $($row.reference)" }
foreach ($row in $result.changes.references) { Write-Output "  $($row.kind) in $($row.assembly): $($row.row)" }
foreach ($row in $result.changes.attributes) { Write-Output "  $($row.kind) attribute in $($row.assembly): $($row.attribute)" }
if ($result.truncated) { Write-Output "  (lists cut at -First $First; totals above are complete)" }
exit 1
