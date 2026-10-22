# Get-DisabledMcpServer: which MCP servers Claude Code will not load for a project dir, and
# where it was told so. Shared by Invoke-JanetStartup.ps1 (which relabels the rules that need
# a disabled server) and Invoke-DelegationGuard.ps1 (which stops demanding razorgraph
# instructions where razorgraph is off), so the two cannot disagree about what is disabled.
# Moved out of startup on 2026-10-22. Dot-source it; it defines functions and changes nothing
# else.
#
# THREE PLACES SAY A SERVER IS OFF, and until 2026-10-22 only the first was read:
#   1. 'disabledMcpjsonServers' in a settings file the harness loads for the project dir --
#      the project's .claude\settings.json and settings.local.json, and ~\.claude\settings.json
#      (which counts for every project dir: a disabled server is disabled wherever it is said).
#   2. 'disabledMcpServers' in ~\.claude.json, under projects[<project dir>]. This is where
#      the /mcp panel's toggle records a server switched off for one project
#      (code.claude.com/docs/en/mcp, "Disable a server without removing it").
#   3. 'disabledMcpjsonServers' in ~\.claude.json under the same project entry, beside
#      'enabledMcpjsonServers': the per-project record of .mcp.json approval choices, which
#      `claude mcp reset-project-choices` clears.
# ~\.claude.json also holds the account and other credentials. Only those two lists under the
# one project entry are read, and nothing from the file is ever emitted except the matched
# server names the caller already asked about and the file's path.
# Its project keys are absolute paths with forward slashes (C:/repos/x), matched here
# case-insensitively against the project dir with its separators normalised.

function Get-McpDisableSource {
    # The (key, list) pairs that disable servers, as source strings for messages: one per
    # list that names anything. Missing, unreadable or malformed files contribute nothing.
    param([string]$ProjectDir)

    $projectClaude = Join-Path $ProjectDir '.claude'
    $settingsPaths = @(
        (Join-Path $projectClaude 'settings.json')
        (Join-Path $projectClaude 'settings.local.json')
        (Join-Path $env:USERPROFILE '.claude\settings.json')
    ) | ForEach-Object { [System.IO.Path]::GetFullPath($_) } | Select-Object -Unique

    foreach ($path in $settingsPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try { $settings = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { continue }
        if ($settings -isnot [System.Management.Automation.PSCustomObject]) { continue }
        $listed = $settings.PSObject.Properties['disabledMcpjsonServers']
        if ($null -eq $listed -or $null -eq $listed.Value) { continue }
        [PSCustomObject]@{ source = "disabledMcpjsonServers in $path"; names = @($listed.Value) }
    }

    $globalPath = Join-Path $env:USERPROFILE '.claude.json'
    if (-not (Test-Path -LiteralPath $globalPath -PathType Leaf)) { return }
    try { $global = Get-Content -LiteralPath $globalPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable }
    catch { return }
    if ($global -isnot [System.Collections.IDictionary] -or -not $global.Contains('projects')) { return }
    $projects = $global['projects']
    if ($projects -isnot [System.Collections.IDictionary]) { return }

    $wanted = ([System.IO.Path]::GetFullPath($ProjectDir) -replace '\\', '/').TrimEnd('/')
    foreach ($key in @($projects.Keys)) {
        $normalised = ([string]$key -replace '\\', '/').TrimEnd('/')
        if (-not [string]::Equals($normalised, $wanted, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $entry = $projects[$key]
        if ($entry -isnot [System.Collections.IDictionary]) { continue }
        foreach ($list in @('disabledMcpServers', 'disabledMcpjsonServers')) {
            if (-not $entry.Contains($list) -or $null -eq $entry[$list]) { continue }
            [PSCustomObject]@{ source = "$list for this project in $globalPath"; names = @($entry[$list]) }
        }
    }
}

function Get-DisabledMcpServer {
    # Which of -Server are switched off for the project dir, each with the sources that say
    # so: an ordered dictionary of server name to source strings, empty when none is off.
    # Names compare case-insensitively, as the harness's own lookups do.
    param([string]$ProjectDir, [string[]]$Server = @())

    $found = [ordered]@{}
    foreach ($origin in @(Get-McpDisableSource -ProjectDir $ProjectDir)) {
        foreach ($name in $origin.names) {
            foreach ($candidate in $Server) {
                if (-not [string]::Equals([string]$name, $candidate, [StringComparison]::OrdinalIgnoreCase)) { continue }
                if (-not $found.Contains($candidate)) { $found[$candidate] = @() }
                if ($found[$candidate] -notcontains $origin.source) { $found[$candidate] += $origin.source }
            }
        }
    }
    return $found
}
