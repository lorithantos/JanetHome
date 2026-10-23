<#
.SYNOPSIS
    Chooses the Pester files the pre-commit gate runs: the *.Tests.ps1 files git tracks.

.DESCRIPTION
    Dot-source this file, then call Select-TrackedTestFile with the output of
    `git ls-files -- tests` run against the index the commit is written from.

    Until 2026-10-23 the gate ran every *.Tests.ps1 on disk under tests\, so another
    session's in-flight, UNTRACKED test file could fail this session's commit -- the
    commit was judged on a file it does not contain (thread item 58). Tracked is the
    rule Lori chose that day. A file staged new in this very commit is in the index,
    so ls-files lists it and it runs; one that is only on disk is skipped.

    Kept apart from Test-PreCommit.ps1 so the selection is testable: the gate is a
    top-level script that exits, and cannot be dot-sourced.
#>

function Select-TrackedTestFile {
    <#
    .SYNOPSIS
        Full paths of the tracked tests\**\*.Tests.ps1 files that exist on disk.

    .PARAMETER TrackedPath
        Lines of `git ls-files` output: repo-relative, forward slashes. Anything
        outside tests\ or not named *.Tests.ps1 is ignored, so the whole ls-files
        listing may be passed.

    .PARAMETER RepoRoot
        The working tree the relative paths resolve against.

    .OUTPUTS
        [string[]], comma-wrapped (house rule 1): assign the result, and the
        caller gets an array of 0 or N full paths. A tracked file deleted from the
        working tree but not staged is dropped -- there is nothing on disk to run.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$TrackedPath,

        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $selected = @($TrackedPath |
        Where-Object { $_ -like 'tests/*' -and $_ -like '*.Tests.ps1' } |
        ForEach-Object { Join-Path $RepoRoot ($_ -replace '/', '\') } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    return , [string[]]$selected
}
