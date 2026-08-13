<#
.SYNOPSIS
    Shared helpers for the shims that forward to the janet CLI.

.DESCRIPTION
    Dot-sourced by Get-Research.ps1, Add-/Update-/Rename-ResearchNode.ps1. Not a standalone
    script -- it defines functions and does nothing when run.

    Deliberately does NOT Set-StrictMode: strict mode set in a dot-sourced file leaks into the
    caller's scope. Each entry point sets its own (house rules 5 and 7).
#>

function Get-JanetCommand {
    <#
    .SYNOPSIS
        Resolves the janet CLI, or explains precisely how to get it.
    #>
    [CmdletBinding()]
    param()

    $janet = Get-Command 'janet' -ErrorAction SilentlyContinue
    if ($null -ne $janet) { return $janet.Source }

    throw @'
The janet CLI is not on PATH, and the catalog now lives behind it.

  dotnet tool install -g Janet.Cli --add-source <JanetBase>\.janet-bin\pkg

Or rebuild the server and reinstall the CLI in one step:
  scripts\Update-McpServer.ps1 -Project src\Janet.Mcp\Janet.Mcp.csproj `
      -ToolProject src\Janet.Cli\Janet.Cli.csproj
'@
}

function Get-JanetBaseArgument {
    <#
    .SYNOPSIS
        The --base pair pointing at this repo, so a shim works from any working directory.
    .DESCRIPTION
        Resolved from the script's own location rather than the caller's, because these are
        invoked from hooks, pre-commit gates, and other repos -- none of which can be relied
        on to have the right directory current.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptRoot)

    return , @('--base', (Split-Path $ScriptRoot -Parent))
}
