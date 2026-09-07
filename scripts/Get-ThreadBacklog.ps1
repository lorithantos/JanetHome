<#
.SYNOPSIS
    Open thread items for one area, read from the RUNNING janet MCP server rather
    than from the installed CLI.

.DESCRIPTION
    Get-ThreadReport.ps1 answers the same question, but it is a shim over the
    global `janet` tool -- a separate copy on PATH that Update-McpServer.ps1
    rotates only when it is passed -ToolProject. The server and the tool can
    therefore be different builds, and on 2026-09-06 they were: the CLI was
    reinstalled at a rotation while the server on 7717 kept serving the build
    before it. This script asks the SERVER, which is what every attached session
    actually sees, and stamps the contract number the server answered with so a
    skew is visible rather than inferred.

    Completed items are excluded twice over. thread_report already omits them
    unless all=true, and the items are filtered on status as well -- cheap, and
    it means an older or newer server that changes its mind about the default
    cannot quietly put finished work back in a backlog.

    'active' NEEDS THE CONTRACT NUMBER TO BE READ CORRECTLY, which is why the
    envelope carries both. From contract 4 (2026-09-06) focus is one cursor per
    area, so a narrowed report's 'active' is that area's own cursor. Before that
    it was one cursor for the whole machine-wide list, so a report narrowed to
    JanetHome could -- and did -- name a gamehub topic. Against a contract-3
    server this script fills activeCaveat rather than presenting the topic as
    though it belonged to the area asked for.

.PARAMETER Area
    Area to narrow to, case-insensitive substring. Defaults to JanetHome.
    '(unfiled)' is the group with no area set.

.PARAMETER Uri
    Server endpoint. Defaults to the janet server on 7717.

.PARAMETER NoLead
    Omit each item's first-line lead. Smaller output when the topics are enough.

.PARAMETER TimeoutSec
    Per-request timeout, passed through.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    .\Get-ThreadBacklog.ps1 -Text

.EXAMPLE
    .\Get-ThreadBacklog.ps1 -Area gamehub -NoLead -Pretty
#>
[CmdletBinding()]
param(
    [string]$Area = 'JanetHome',
    [string]$Uri = 'http://127.0.0.1:7717/',
    [switch]$NoLead,
    [int]$TimeoutSec = 60,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Prop {
    # Strict mode makes a missing property a terminating error, and every field
    # below is one an older or newer server might not send.
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    if ($Object.PSObject.Properties.Name -notcontains $Name) { return $null }
    return $Object.$Name
}

$caller = Join-Path $PSScriptRoot 'Invoke-McpTool.ps1'
$problems = [System.Collections.Generic.List[string]]::new()
$payload = $null

if (-not (Test-Path -LiteralPath $caller)) {
    $problems.Add("Invoke-McpTool.ps1 not found beside this script: $caller")
}
else {
    $toolArgs = @{ area = $Area }
    if ($NoLead) { $toolArgs['lead'] = $false }

    try {
        # Invoke-McpTool wraps the tool's own payload in a call envelope
        # -- { ok, uri, server, tool, elapsedSec, result } -- so the report is
        # under 'result'. Reading the outer object instead finds none of the
        # fields and reports an empty backlog, which is the wrong answer rather
        # than an error: worth the unwrap being explicit.
        $call = & $caller -Tool 'thread_report' -Arguments $toolArgs -Uri $Uri -TimeoutSec $TimeoutSec

        # It emits its envelope as JSON TEXT, the house default for these scripts,
        # so it has to be parsed before any field is reachable. Tolerating an
        # object as well keeps this working if it ever returns one.
        if ($call -is [string] -or $call -is [string[]]) {
            $call = ($call -join "`n") | ConvertFrom-Json
        }

        if ($false -eq (Get-Prop $call 'ok')) {
            $problems.Add("The call to thread_report failed: $(Get-Prop $call 'error')")
        }
        else {
            $payload = Get-Prop $call 'result'
            if ($null -eq $payload) {
                $problems.Add("thread_report answered without a 'result' payload against $Uri.")
            }
        }
    }
    catch {
        $problems.Add("thread_report failed against ${Uri}: $($_.Exception.Message)")
    }
}

$items = @()
$contract = $null
$active = $null
$activeCaveat = $null
$areas = @()
$serverError = $null

if ($problems.Count -eq 0) {
    $contract = Get-Prop $payload 'contract'
    $active = Get-Prop $payload 'active'
    $serverError = Get-Prop $payload 'error'

    $rawAreas = Get-Prop $payload 'areas'
    if ($null -ne $rawAreas) { $areas = @($rawAreas) }

    $rawItems = Get-Prop $payload 'items'
    if ($null -ne $rawItems) {
        # The second exclusion. Report already drops completed items; this makes
        # the promise in the name true whatever the server decided.
        $items = @(@($rawItems) | Where-Object { (Get-Prop $_ 'status') -ne 'done' })
    }

    # A report that could not be read says so in-band rather than throwing, so a
    # populated 'error' with zero items is a fact about the list, not an empty
    # backlog. Surface it as a problem so the exit code carries it too.
    if (-not [string]::IsNullOrWhiteSpace([string]$serverError)) {
        $problems.Add("The server reported a list it could not read: $serverError")
    }

    if ($null -ne $active -and ($null -eq $contract -or $contract -lt 4)) {
        $activeCaveat = "Server is contract $contract, where 'active' names the focus of the WHOLE list rather than of this area, so '$active' may belong to another project. Rotate the server (Update-McpServer.ps1) for a per-area cursor."
    }
}

$note = if ($problems.Count -gt 0) {
    'No answer; see problems.'
}
else {
    $suffix = if ($null -eq $activeCaveat) { '' } else { ' Focus is unreliable at this contract -- see activeCaveat.' }
    "$($items.Count) open item(s) in '$Area', from a contract-$contract server at $Uri.$suffix"
}

$result = [ordered]@{
    ok           = ($problems.Count -eq 0)
    uri          = $Uri
    area         = $Area
    contract     = $contract
    count        = $items.Count
    active       = $active
    activeCaveat = $activeCaveat
    areas        = $areas
    items        = $items
    note         = $note
    problems     = @($problems)
    error        = $serverError
}

if ($Text) {
    "Thread backlog -- $Area ($Uri, contract $contract)"
    foreach ($p in $problems) { "  PROBLEM: $p" }
    if ($null -ne $activeCaveat) { "  ! $activeCaveat" }
    elseif ($null -ne $active) { "  In focus: $active" }

    foreach ($i in $items) {
        ''
        "  {0}" -f (Get-Prop $i 'topic')
        "    status {0}   notes {1}" -f (Get-Prop $i 'status'), (Get-Prop $i 'notesLength')
        $lead = Get-Prop $i 'notesLead'
        if (-not [string]::IsNullOrWhiteSpace([string]$lead)) { "    $lead" }
        $next = Get-Prop $i 'next'
        if (-not [string]::IsNullOrWhiteSpace([string]$next)) { "    NEXT: $next" }
    }

    ''
    "  $note"
    if ($areas.Count -gt 0) {
        '  Elsewhere:'
        foreach ($a in $areas) {
            $name = Get-Prop $a 'area'
            if ($name -ne $Area) { "    {0,-16} {1} open" -f $name, (Get-Prop $a 'open') }
        }
    }
}
else {
    $result | ConvertTo-Json -Depth 6 -Compress:(-not $Pretty)
}

if ($problems.Count -gt 0) { exit 1 }
