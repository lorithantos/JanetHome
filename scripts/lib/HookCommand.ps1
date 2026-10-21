<#
.SYNOPSIS
    Read the hook commands a Claude settings file declares, and the script each one
    runs.

.DESCRIPTION
    One parser for "what does this hook run", shared so two callers cannot disagree
    about it:

      - Invoke-JanetStartup.ps1 asks whether a hook running a rule's backing script is
        DECLARED (Test-HookDeclared), to decide between ENFORCED and ADVISORY.
      - Test-ConfigPaths.ps1 asks whether the script a hook runs still EXISTS, because
        every hook here is written 'if (Test-Path $s) { & $s }' and a missing script
        is skipped without a word.

    Until 2026-10-21 startup matched a command by substring on the script's leaf and
    Test-ConfigPaths did not read hooks at all, so a user-level hook naming a deleted
    script was invisible to both.

    Dot-source this file, then call Get-HookEntry and Get-HookScript. Neither throws.

.NOTES
    Forms read (anything else naming a .ps1 comes back with a reason, never silently):
      - Join-Path $var 'rel\X.ps1' (also -Path/-ChildPath), and "$var\rel\X.ps1",
        where $var is assigned a quoted literal in the same command, or anything
        involving $env:CLAUDE_PROJECT_DIR (the project form), or IS that variable.
      - A drive-absolute path ending .ps1, quoted or bare.
#>

function Get-HookEntry {
    <#
    .SYNOPSIS
        Every 'command' under 'hooks' in one settings file, with where it sits.

    .DESCRIPTION
        Event, then matcher group, then hook. Parsed, not grepped: a script named in a
        comment key, a permission or an env value is not a hook. Nothing comes back for
        a missing, unreadable or malformed file -- a malformed file declares nothing,
        the same as a missing one. Property reads go through the Properties indexer,
        which an empty JSON object ({}) cannot make throw under StrictMode.

    .OUTPUTS
        [pscustomobject] location ('hooks.PreToolUse[0].hooks[0].command'), command.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    Set-StrictMode -Version Latest
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }

    try {
        $settings = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($settings -isnot [System.Management.Automation.PSCustomObject]) { return }
        $hooks = $settings.PSObject.Properties['hooks']
        if ($null -eq $hooks -or $hooks.Value -isnot [System.Management.Automation.PSCustomObject]) { return }

        foreach ($hookEvent in $hooks.Value.PSObject.Properties) {
            $groups = @($hookEvent.Value)
            for ($g = 0; $g -lt $groups.Count; $g++) {
                if ($groups[$g] -isnot [System.Management.Automation.PSCustomObject]) { continue }
                $inner = $groups[$g].PSObject.Properties['hooks']
                if ($null -eq $inner -or $null -eq $inner.Value) { continue }

                $entries = @($inner.Value)
                for ($h = 0; $h -lt $entries.Count; $h++) {
                    if ($entries[$h] -isnot [System.Management.Automation.PSCustomObject]) { continue }
                    $command = $entries[$h].PSObject.Properties['command']
                    if ($null -eq $command -or $null -eq $command.Value) { continue }
                    [pscustomobject]@{
                        location = "hooks.$($hookEvent.Name)[$g].hooks[$h].command"
                        command  = [string]$command.Value
                    }
                }
            }
        }
    }
    catch { return }
}

function Resolve-HookBase {
    # What a base variable in a hook command stands for: a literal directory, the
    # session's project dir, or unknown. Only the command's own text is consulted --
    # the variable is assigned in the same command string or it is not resolved.
    param([string]$Command, [string]$Name)

    Set-StrictMode -Version Latest
    if ($Name -ieq 'env:CLAUDE_PROJECT_DIR') { return [pscustomobject]@{ kind = 'project'; value = '' } }

    # \b after the name: $d must not match the assignment of $dir.
    $assign = [regex]::Match($Command, '(?i)\$' + [regex]::Escape($Name) + '\b\s*=\s*(?<rhs>[^;]+)')
    if (-not $assign.Success) { return [pscustomobject]@{ kind = 'unknown'; value = '' } }

    $rhs = $assign.Groups['rhs'].Value.Trim()
    $literal = [regex]::Match($rhs, "^'(?<v>[^']*)'$|^`"(?<v>[^`"`$]*)`"$")
    if ($literal.Success) { return [pscustomobject]@{ kind = 'literal'; value = $literal.Groups['v'].Value } }
    if ($rhs -match '(?i)\$env:CLAUDE_PROJECT_DIR') { return [pscustomobject]@{ kind = 'project'; value = '' } }
    return [pscustomobject]@{ kind = 'unknown'; value = '' }
}

function Get-HookScript {
    <#
    .SYNOPSIS
        The .ps1 scripts one hook command runs, resolved where the command says enough.

    .PARAMETER Command
        The hook's command string, as Get-HookEntry returns it.

    .PARAMETER ProjectDir
        The project dir a project-form command ($env:CLAUDE_PROJECT_DIR) resolves
        against. Empty means not knowable -- a user-level hook runs in whichever
        project the session opens -- and such a script comes back with a reason
        rather than a guess.

    .OUTPUTS
        [pscustomobject] script (as written: the relative part of a Join-Path, or the
        absolute path), path (resolved full path, '' when unresolved), reason ('' when
        resolved; otherwise why it could not be). A command that names a .ps1 in no
        form read here yields one entry with an empty script and a reason.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Command, [string]$ProjectDir = '')

    Set-StrictMode -Version Latest
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $found = [System.Collections.Generic.List[object]]::new()

    $relative = @(
        [regex]::Matches($Command, '(?i)Join-Path\s+(?:-Path\s+)?\$(?<base>env:\w+|\w+)\s+(?:-ChildPath\s+)?(?<q>[''"])(?<rel>[^''"]+?\.ps1)\k<q>')
        [regex]::Matches($Command, '(?i)"\$(?<base>env:\w+|\w+)[\\/](?<rel>[^"]+?\.ps1)"')
    )
    foreach ($match in $relative) {
        $rel = $match.Groups['rel'].Value
        $baseName = $match.Groups['base'].Value
        if (-not $seen.Add("$baseName|$rel")) { continue }

        $base = Resolve-HookBase -Command $Command -Name $baseName
        $entry = [pscustomobject]@{ script = $rel; path = ''; reason = '' }
        if ($base.kind -eq 'literal' -and $base.value -match '^[A-Za-z]:[\\/]|^\\\\') {
            $entry.path = [System.IO.Path]::GetFullPath((Join-Path $base.value $rel))
        }
        elseif ($base.kind -eq 'literal') {
            $entry.reason = "base `$$baseName is not an absolute path"
        }
        elseif ($base.kind -eq 'project' -and $ProjectDir) {
            $entry.path = [System.IO.Path]::GetFullPath((Join-Path $ProjectDir $rel))
        }
        elseif ($base.kind -eq 'project') {
            $entry.reason = 'resolves against the session project dir, which is not known for this file'
        }
        else {
            $entry.reason = "base `$$baseName is not assigned a literal or the project dir in the command"
        }
        $found.Add($entry)
    }

    $absolute = @(
        [regex]::Matches($Command, '(?<q>[''"])(?<p>[A-Za-z]:[\\/][^''"\r\n]*?\.ps1)\k<q>')
        [regex]::Matches($Command, '(?<![\w$''"\\/:])(?<p>[A-Za-z]:[\\/][^''"\s;|&<>()]*?\.ps1)(?![\w.])')
    )
    foreach ($match in $absolute) {
        $path = $match.Groups['p'].Value
        if (-not $seen.Add($path)) { continue }
        $found.Add([pscustomobject]@{ script = $path; path = [System.IO.Path]::GetFullPath($path); reason = '' })
    }

    if ($found.Count -eq 0 -and $Command -match '(?i)\.ps1\b') {
        $found.Add([pscustomobject]@{ script = ''; path = ''; reason = 'names a .ps1 in a form the hook parser does not read' })
    }
    return $found.ToArray()
}
