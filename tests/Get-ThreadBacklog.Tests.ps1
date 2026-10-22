#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Get-ThreadBacklog.ps1's reading of the thread report envelope.

.DESCRIPTION
    The script reaches the server through Invoke-McpTool.ps1 beside itself, so each test
    copies it into TestDrive next to a stub of that caller which answers with a fixed
    report. Nothing here touches the running janet server or the machine's thread list.

    Written 2026-10-22 for report contract 5: an area selector that matches no item answers
    zero items with 'unmatchedArea' set, and an area whose work is finished answers zero
    items with it null. This script builds its own envelope from the server's result, so
    until then it dropped the flag and the two read the same.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:source = Join-Path $script:repoRoot 'scripts\Get-ThreadBacklog.ps1'

    # Answers 'JanetHome' as a matched area with nothing open, anything else as a miss.
    $script:callerStub = @'
param([string]$Tool, [hashtable]$Arguments, [string]$Uri, [int]$TimeoutSec)
$flag = if ($Arguments['area'] -eq 'JanetHome') { $null } else { [ordered]@{ area = $Arguments['area']; knownAreas = @('gamehub', 'JanetHome') } }
$report = [ordered]@{ contract = 5; count = 0; active = $null; areas = @(); items = @(); notesLength = 0; error = $null; unmatchedArea = $flag }
[ordered]@{ ok = $true; uri = $Uri; tool = $Tool; result = $report } | ConvertTo-Json -Depth 6 -Compress
'@

    function Invoke-Backlog {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$Area, [switch]$Text)
        $dir = Join-Path $TestDrive 'scripts'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        Copy-Item -LiteralPath $script:source -Destination (Join-Path $dir 'Get-ThreadBacklog.ps1') -Force
        [System.IO.File]::WriteAllText((Join-Path $dir 'Invoke-McpTool.ps1'), $script:callerStub, (New-Object System.Text.UTF8Encoding $false))
        return @(& (Join-Path $dir 'Get-ThreadBacklog.ps1') -Area $Area -Text:$Text)
    }
}

Describe 'Get-ThreadBacklog.ps1 and unmatchedArea' {
    It 'carries the flag and says the selector missed when the area matches nothing' {
        $result = (Invoke-Backlog -Area 'repos') -join "`n" | ConvertFrom-Json

        $result.ok | Should -BeTrue
        $result.count | Should -Be 0
        $result.unmatchedArea.area | Should -Be 'repos'
        $result.unmatchedArea.knownAreas | Should -Be @('gamehub', 'JanetHome')
        $result.note | Should -BeLike "*matching 'repos'*selector that missed*gamehub, JanetHome*"
    }

    It 'carries a null flag and the ordinary count note when the area matched' {
        $result = (Invoke-Backlog -Area 'JanetHome') -join "`n" | ConvertFrom-Json

        $result.PSObject.Properties.Name | Should -Contain 'unmatchedArea'
        $result.unmatchedArea | Should -BeNullOrEmpty
        $result.note | Should -BeLike "0 open item(s) in 'JanetHome'*"
    }

    It 'says the selector missed in the text view' {
        $lines = Invoke-Backlog -Area 'repos' -Text

        ($lines -join "`n") | Should -BeLike "*selector that missed*"
    }
}
