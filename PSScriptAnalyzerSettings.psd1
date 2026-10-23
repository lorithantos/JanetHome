# PSScriptAnalyzer settings for this repo. The pre-commit gate (scripts\Test-PreCommit.ps1,
# step 1) passes this file to Invoke-ScriptAnalyzer for every staged .ps1, and fails the
# commit on any Warning or Error that is left. Decided by Lori 2026-10-23 (thread item 40).
#
# Two rules are excluded repo-wide, and only these two, because each fires on a deliberate
# house convention rather than on a defect:
#
#   PSAvoidUsingWriteHost  -- every -Text mode, gate step and hook here writes to the host on
#                             purpose; stdout is reserved for the JSON envelope. It was 318 of
#                             the 360 findings over scripts\ on 2026-10-22.
#   PSUseSingularNouns     -- names such as Show-ThreadItems, Test-StandaloneScripts and
#                             Find-DivergentStateRoots are public entry points that hooks,
#                             the catalog and other scripts call by name.
#
# Every other finding is fixed, or suppressed where it stands with a SuppressMessageAttribute
# whose Justification says why. Add a rule here only with the same kind of reason.
@{
    ExcludeRules = @(
        'PSAvoidUsingWriteHost'
        'PSUseSingularNouns'
    )
}
