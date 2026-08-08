<#
.SYNOPSIS
    Add a topic to the thread item list.

.DESCRIPTION
    Appends. It does NOT change what is active unless you ask with -Active.

    That is the whole point of the list replacing the stack. Push-ThreadStack
    made every new topic active and parked whatever was, so recording a piece of
    work for later and descending into it now were the same operation -- there
    was no way to note something without losing your place.

.PARAMETER Topic
    The topic to add. Must be unique enough to select later.

.PARAMETER Notes
    Free-form detail too small or too fresh to be worth a research.json node.

.PARAMETER Next
    The resume cursor: the one thing to do first on returning to this topic.

.PARAMETER Refs
    research.json node ids carrying this topic's durable context.

.PARAMETER Active
    Also make this the active item, demoting the current one to parked.

.OUTPUTS
    JSON: { added, count, active }

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ThreadItem.ps1" -Topic 'cache eviction' -Next 'query the telemetry table'

.EXAMPLE
    & "$env:JanetBase\scripts\Add-ThreadItem.ps1" -Topic 'chase the AV' -Active
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Topic,
    [string]$Notes = '',
    [string]$Next = '',
    [string[]]$Refs = @(),
    [switch]$Active,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ThreadItems.Common.ps1')

$topicValue = $Topic
$activeSwitch = $Active.IsPresent

$updated = Invoke-ThreadItemsUpdate -Path $Path -Action {
    param($current)

    $items = @($current)

    foreach ($existing in $items) {
        if ($existing.topic -eq $topicValue) {
            throw "An item with topic '$topicValue' already exists. Use Update-ThreadItem.ps1 to amend it."
        }
    }

    if ($activeSwitch) {
        foreach ($existing in $items) {
            if ($existing.status -eq 'active') { $existing.status = 'parked' }
        }
    }

    $status = 'parked'
    if ($activeSwitch) { $status = 'active' }

    $items += [PSCustomObject]@{
        topic  = $topicValue
        status = $status
        refs   = @($Refs)
        next   = $Next
        notes  = $Notes
    }

    return ,@($items)
}

$live = @($updated | Where-Object { $_.status -ne 'done' })
$activeTopic = $null
foreach ($item in $live) {
    if ($item.status -eq 'active') { $activeTopic = $item.topic; break }
}

if ($Text) {
    Write-Host "Added: $topicValue"
    if ($null -ne $activeTopic) { Write-Host "Active: $activeTopic" } else { Write-Host 'Nothing active.' }
    Write-Host "Items: $($live.Count)"
    return
}

ConvertTo-Json -InputObject ([PSCustomObject]@{
    added  = $topicValue
    count  = $live.Count
    active = $activeTopic
}) -Depth 3 -Compress
