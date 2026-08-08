<#
.SYNOPSIS
    Set which thread item is active.

.DESCRIPTION
    Focus is explicit. Under the old stack it was positional -- the top of the
    stack was active by definition -- so changing focus meant pushing or popping,
    and both mutated the list's contents to express a change of attention.

    Selection is by -Topic substring (must match exactly one item) or -Index.
    An ambiguous -Topic is an error, never a first-match guess.

.PARAMETER Topic
    Case-insensitive substring of the topic to activate.

.PARAMETER Index
    Zero-based position, as reported by Show-ThreadItems.ps1.

.PARAMETER None
    Clear the active item without setting another. Nothing active is a valid
    state -- it means no thread is currently being worked.

.OUTPUTS
    JSON: { active, previous, count }

.EXAMPLE
    & "$env:JanetBase\scripts\Set-ActiveThread.ps1" -Topic 'RetirementCore'

.EXAMPLE
    & "$env:JanetBase\scripts\Set-ActiveThread.ps1" -None
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [int]$Index = -1,
    [switch]$None,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ThreadItems.Common.ps1')

$topicValue = $Topic
$indexValue = $Index
$clearOnly = $None.IsPresent

if ($clearOnly -and ($topicValue -ne '' -or $indexValue -ge 0)) {
    throw '-None cannot be combined with -Topic or -Index.'
}
if (-not $clearOnly -and $topicValue -eq '' -and $indexValue -lt 0) {
    throw 'Pass -Topic, -Index, or -None.'
}

$script:previousTopic = $null
$script:newActive = $null

$updated = Invoke-ThreadItemsUpdate -Path $Path -Action {
    param($current)

    $items = @($current)

    foreach ($item in $items) {
        if ($item.status -eq 'active') {
            $script:previousTopic = $item.topic
            $item.status = 'parked'
        }
    }

    if (-not $clearOnly) {
        $target = Find-ThreadItemIndex -Items $items -Topic $topicValue -Index $indexValue

        if ($items[$target].status -eq 'done') {
            throw "'$($items[$target].topic)' is completed. Reopen it with Update-ThreadItem.ps1 -Status parked first."
        }

        $items[$target].status = 'active'
        $script:newActive = $items[$target].topic
    }

    return ,@($items)
}

$live = @($updated | Where-Object { $_.status -ne 'done' })

if ($Text) {
    if ($null -ne $script:newActive) { Write-Host "Active: $($script:newActive)" }
    else { Write-Host 'Nothing active.' }
    if ($null -ne $script:previousTopic) { Write-Host "Was: $($script:previousTopic)" }
    return
}

ConvertTo-Json -InputObject ([PSCustomObject]@{
    active   = $script:newActive
    previous = $script:previousTopic
    count    = $live.Count
}) -Depth 3 -Compress
