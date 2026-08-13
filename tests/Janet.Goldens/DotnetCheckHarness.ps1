<#
.SYNOPSIS
    Calls Invoke-DotnetCheck.ps1's own functions against the test fixtures and prints what
    they answered, so the port can be compared with the original rather than with a reading
    of it.

.DESCRIPTION
    The script it takes apart is not dot-sourceable: it defines its functions and then runs
    a build. So this lifts out the top-level function definitions and the $script: variables
    they depend on -- in source order, by AST rather than by regex -- writes them to one
    file, dot-sources that, and calls them.

    Nothing here re-implements anything. If a lifted function is missing a dependency the
    call fails loudly rather than falling back, because a harness that quietly substitutes
    its own behaviour produces goldens that agree with nothing.

.PARAMETER Script
    The original Invoke-DotnetCheck.ps1, already extracted from git at the chosen ref.

.PARAMETER Fixtures
    The tests\Janet.Tests\Fixtures directory.

.PARAMETER Case
    Which answer to print.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Fixtures,
    [Parameter(Mandatory)]
    [ValidateSet('read-build-output', 'group-warnings', 'warning-keys', 'compare-baseline', 'baseline-path', 'read-trx')]
    [string]$Case
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Export-ScriptPart {
    <#
    .SYNOPSIS
        Writes the script's top-level functions and $script: assignments to a dot-sourceable
        file, preserving source order.
    #>
    [CmdletBinding()]
    param([string]$Path)

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)

    if ($errors.Count -gt 0) {
        throw "Cannot parse $Path -- $($errors[0].Message)"
    }

    $kept = foreach ($statement in $ast.EndBlock.Statements) {
        if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
            $statement.Extent.Text
            continue
        }

        # The regexes and the contract number live in $script: variables assigned at top
        # level, and Read-BuildOutput is useless without them.
        if ($statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $statement.Left.Extent.Text -like '$script:*') {
            $statement.Extent.Text
        }
    }

    $lifted = Join-Path ([IO.Path]::GetTempPath()) ("janet-lifted-" + [Guid]::NewGuid().ToString('N') + '.ps1')
    Set-Content -LiteralPath $lifted -Value ($kept -join "`n`n") -Encoding utf8NoBOM

    return $lifted
}

$liftedPath = Export-ScriptPart -Path $Script

try {
    . $liftedPath

    foreach ($required in @('Read-BuildOutput', 'Group-WarningInstance', 'Get-WarningKey',
            'Compare-WarningBaseline', 'Get-BaselinePath', 'Read-TrxDirectory')) {
        if (-not (Get-Command $required -CommandType Function -ErrorAction SilentlyContinue)) {
            throw "Lifting produced no '$required'. The original's shape changed; fix the harness rather than the golden."
        }
    }

    $buildLines = @(Get-Content (Join-Path $Fixtures 'msbuild-output.txt'))
    $diagnostics = Read-BuildOutput -Lines $buildLines
    $warnings = @($diagnostics | Where-Object severity -eq 'warning')

    switch ($Case) {
        'read-build-output' {
            @{ diagnostics = @($diagnostics) } | ConvertTo-Json -Depth 8 -Compress
        }

        'group-warnings' {
            # Assigned before wrapping. Group-WarningInstance comma-wraps its return, and
            # calling it inline inside @() preserves that wrap -- the golden came out as an
            # array holding the array. Assignment unrolls it; the shape is the original's,
            # the nesting was the harness's.
            $groups = Group-WarningInstance -Warnings $warnings
            @{ groups = @($groups) } | ConvertTo-Json -Depth 8 -Compress
        }

        'warning-keys' {
            @{ keys = @($warnings | ForEach-Object { Get-WarningKey $_ }) } | ConvertTo-Json -Depth 8 -Compress
        }

        'compare-baseline' {
            $baseline = Get-Content (Join-Path $Fixtures 'warning-baseline.json') -Raw | ConvertFrom-Json
            Compare-WarningBaseline -Current $warnings -Baseline $baseline |
                ConvertTo-Json -Depth 8 -Compress
        }

        'baseline-path' {
            # LOCALAPPDATA differs per machine, so the golden records the file name the hash
            # produces rather than an absolute path nobody else has.
            $path = Get-BaselinePath -ResolvedTarget 'D:\Repos\Sample\App.slnx' -BuildConfiguration 'Debug'
            @{ fileName = (Split-Path $path -Leaf) } | ConvertTo-Json -Compress
        }

        'read-trx' {
            Read-TrxDirectory -Directory (Join-Path $Fixtures 'trx') | ConvertTo-Json -Depth 8 -Compress
        }
    }
}
finally {
    Remove-Item -LiteralPath $liftedPath -Force -ErrorAction SilentlyContinue
}
