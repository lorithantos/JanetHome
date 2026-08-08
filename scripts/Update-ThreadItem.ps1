<#
.SYNOPSIS
    Amend an existing thread item's notes, cursor, refs, or status.

.DESCRIPTION
    The operation the stack never had, and the reason it lost data.

    On 2026-08-08 a session wanting to update its own entry had only push and pop
    to work with, popped to get at it, and destroyed a concurrent session's notes
    irrecoverably. Amending an item in place is an ordinary need; without it,
    callers reach for operations that rewrite the whole list.

    Every field you do not name is preserved. -AppendNotes adds to the existing
    notes rather than replacing them, so accumulating findings does not require
    reading the old value back first and re-sending it.

.PARAMETER Topic
    Case-insensitive substring selecting the item. Omit to target the active one.

.PARAMETER Index
    Zero-based position instead of a topic match.

.PARAMETER Notes
    Replace the notes. With -AppendNotes, add to them instead.

.PARAMETER Next
    Replace the resume cursor. Pass '' to clear it.

.PARAMETER Refs
    Replace the research.json node ids. With -AppendRefs, add to them instead.

.PARAMETER Status
    Move the item between 'active', 'parked', and 'done'. Setting 'active'
    demotes any current active item.

.OUTPUTS
    JSON: { updated, changed[], count }

.EXAMPLE
    & "$env:JanetBase\scripts\Update-ThreadItem.ps1" -Topic 'RazorGraph' -Next 'publish both projects'

.EXAMPLE
    & "$env:JanetBase\scripts\Update-ThreadItem.ps1" -Notes 'ruled out threading' -AppendNotes
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [int]$Index = -1,
    [string]$Notes = $null,
    [string]$Next = $null,
    [string[]]$Refs = $null,
    [ValidateSet('active', 'parked', 'done')][string]$Status = '',
    [switch]$AppendNotes,
    [switch]$AppendRefs,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ThreadItems.Common.ps1')

$topicValue = $Topic
$indexValue = $Index
$statusValue = $Status
$appendNotesSwitch = $AppendNotes.IsPresent
$appendRefsSwitch = $AppendRefs.IsPresent

# Distinguish "not supplied" from "supplied as empty": clearing 'next' is a
# legitimate request, so $null means untouched and '' means clear.
$notesSupplied = $PSBoundParameters.ContainsKey('Notes')
$nextSupplied = $PSBoundParameters.ContainsKey('Next')
$refsSupplied = $PSBoundParameters.ContainsKey('Refs')

if (-not ($notesSupplied -or $nextSupplied -or $refsSupplied -or $statusValue -ne '')) {
    throw 'Nothing to change. Pass -Notes, -Next, -Refs, or -Status.'
}

$script:targetTopic = $null
$script:changed = @()

$updated = Invoke-ThreadItemsUpdate -Path $Path -Action {
    param($current)

    $items = @($current)
    if ($items.Count -eq 0) { throw 'The thread item list is empty.' }

    $target = Find-ThreadItemIndex -Items $items -Topic $topicValue -Index $indexValue
    $item = $items[$target]
    $script:targetTopic = $item.topic

    if ($notesSupplied) {
        if ($appendNotesSwitch -and $item.notes -ne '') {
            $item.notes = $item.notes + "`n`n" + $Notes
        }
        else {
            $item.notes = $Notes
        }
        $script:changed += 'notes'
    }

    if ($nextSupplied) {
        $item.next = $Next
        $script:changed += 'next'
    }

    if ($refsSupplied) {
        if ($appendRefsSwitch) { $item.refs = @($item.refs) + @($Refs) }
        else { $item.refs = @($Refs) }
        $script:changed += 'refs'
    }

    if ($statusValue -ne '') {
        if ($statusValue -eq 'active') {
            foreach ($other in $items) {
                if ($other.status -eq 'active') { $other.status = 'parked' }
            }
        }
        $item.status = $statusValue
        $script:changed += 'status'
    }

    return ,@($items)
}

$live = @($updated | Where-Object { $_.status -ne 'done' })

if ($Text) {
    Write-Host "Updated: $($script:targetTopic)"
    Write-Host "Changed: $(@($script:changed) -join ', ')"
    return
}

ConvertTo-Json -InputObject ([PSCustomObject]@{
    updated = $script:targetTopic
    changed = @($script:changed)
    count   = $live.Count
}) -Depth 3 -Compress
