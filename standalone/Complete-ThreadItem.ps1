<#
.SYNOPSIS
    Mark a thread item done. Retains it -- never drops it.

.DESCRIPTION
    Pop-ThreadStack removed completed items from the file by default, so
    finishing a topic destroyed its notes, and the only record that the work had
    happened was whatever a session had already written down elsewhere.

    Completing here is a status change. The item stays in the list and
    Show-ThreadItems.ps1 -All prints it, so the list doubles as a record of what
    was actually finished.

    It also does NOT auto-activate anything. The stack promoted the next parked
    item automatically, which quietly decided what you worked on next; use
    Set-ActiveThread.ps1 to say so deliberately.

.PARAMETER Topic
    Case-insensitive substring selecting the item. Omit to complete the active one.

.PARAMETER Index
    Zero-based position instead of a topic match.

.OUTPUTS
    JSON: { completed, active, remaining } -- 'active' is null after completing
    the active item, which is expected rather than an error.

.EXAMPLE
    & "$env:JanetBase\scripts\Complete-ThreadItem.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Complete-ThreadItem.ps1" -Topic 'percentile slider'
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [int]$Index = -1,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ThreadItems.Common.ps1')

$topicValue = $Topic
$indexValue = $Index

$script:completedTopic = $null

$updated = Invoke-ThreadItemsUpdate -Path $Path -Action {
    param($current)

    $items = @($current)
    if ($items.Count -eq 0) { throw 'The thread item list is empty; there is nothing to complete.' }

    $target = Find-ThreadItemIndex -Items $items -Topic $topicValue -Index $indexValue

    if ($items[$target].status -eq 'done') {
        throw "'$($items[$target].topic)' is already completed."
    }

    $items[$target].status = 'done'
    $script:completedTopic = $items[$target].topic

    return ,@($items)
}

$live = @($updated | Where-Object { $_.status -ne 'done' })
$activeTopic = $null
foreach ($item in $live) {
    if ($item.status -eq 'active') { $activeTopic = $item.topic; break }
}

if ($Text) {
    Write-Host "Done: $($script:completedTopic)"
    if ($null -ne $activeTopic) { Write-Host "Active: $activeTopic" }
    else { Write-Host 'Nothing active -- pick one with Set-ActiveThread.ps1.' }
    Write-Host "Remaining: $($live.Count)"
    return
}

ConvertTo-Json -InputObject ([PSCustomObject]@{
    completed = $script:completedTopic
    active    = $activeTopic
    remaining = $live.Count
}) -Depth 3 -Compress
