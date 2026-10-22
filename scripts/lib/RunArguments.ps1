# Resolve-RunArguments and Invoke-ManifestRunEntry: the one reader of a startup
# manifest run entry's 'args', shared by Invoke-JanetStartup.ps1 (which runs every
# entry) and Invoke-GraphGuard.ps1 (which re-runs the razorgraph entry when the
# server is down). Moved here from startup on 2026-10-22, when the razorgraph entry
# stopped being a forwarder script and became a direct call to Ensure-McpServer.ps1
# carrying its arguments: from then on the manifest is the only place razorgraph's
# launch defaults live, so the guard has to read them the same way startup does.
# Dot-source it; it defines functions and changes nothing else.

function Resolve-RunArguments {
    # Validates a run entry's optional 'args' and splits it into the two splats the
    # invocation needs.  Returns an object, so nothing unrolls; read .problems first.
    #
    # 'args' is optional.  Present, it must be an array of strings -- anything else
    # is a problem, never a coercion.  A bare string would splat as one token and
    # happen to work for a lone switch, which is exactly the shape that stops
    # working the day a value is added; a number or object would bind as something
    # the target never asked for.  Read through PSObject.Properties rather than
    # Get-Prop: a one-element array returned from a function unrolls to its
    # element, and the type check here is the point.
    #
    # ONE EXCEPTION, since 2026-10-22: the value after a '-Name' whose parameter is an
    # array type ([string[]]) may itself be an array of strings, and binds as that
    # array.  Ensure-McpServer.ps1's -ServerArgument is the case: razorgraph's
    # launch line is five values, two of which begin with '--', and a flat token
    # list has no way to say where they end.  A nested array anywhere else -- after
    # a scalar or switch parameter, or in a positional slot -- is a problem.
    #
    # ARRAY SPLATTING IS POSITIONAL.  Measured 2026-09-04: & script @('-Area','X')
    # bound the string '-Area' to the first parameter and 'X' to the second, and
    # nothing failed.  Only the automatic $args keeps parameter names through a
    # splat, and a manifest cannot produce that.  So the tokens are read the way
    # the command line reads them -- '-Name value' binds by name, a lone '-Switch'
    # is a switch, anything else is positional -- and each name is checked against
    # the target's own parameter metadata.  A misspelled name is therefore a
    # problem at lint time, not a string quietly bound to the wrong parameter at
    # run time.  A positional value that itself begins with '-' is not supported;
    # nothing in this repo needs one, and saying so beats guessing.
    param($Entry, [string]$Cmd, [string]$FullPath, [bool]$Exists, [string]$ProjectName)
    $result = [PSCustomObject]@{
        tokens     = @()
        named      = [ordered]@{}
        positional = @()
        problems   = @()
    }
    $prop = $Entry.PSObject.Properties['args']
    if ($null -eq $prop -or $null -eq $prop.Value) { return $result }
    $value = $prop.Value
    if ($value -isnot [array]) {
        $result.problems += "run: 'args' for '$Cmd' must be an array of strings, not a $($value.GetType().Name)"
        return $result
    }
    $notStrings = @($value | Where-Object {
        $_ -isnot [string] -and -not ($_ -is [array] -and @($_ | Where-Object { $_ -isnot [string] }).Count -eq 0)
    })
    if ($notStrings.Count -gt 0) {
        $kinds = ($notStrings | ForEach-Object { if ($null -eq $_) { 'null' } else { $_.GetType().Name } }) -join ', '
        $result.problems += "run: 'args' for '$Cmd' must be an array of strings; found $kinds"
        return $result
    }
    # Each element stays a string or becomes a [string[]]; the unary comma keeps a
    # nested array whole through the pipeline instead of flattening it into its parent.
    $result.tokens = @($value | ForEach-Object { if ($_ -is [array]) { ,([string[]]@($_)) } else { [string]$_ } })

    # Token substitution happens before binding, so what the brief reports under
    # 'args' is what actually ran -- the resolved area, not the token.  That is the
    # only place the brief says which repo it narrowed to, so a token surviving
    # into the brief would leave nine items attributed to nothing.
    #
    # {projectName} is the only token, and an unrecognised one is a problem rather
    # than a literal passed through.  Passing it through is the quiet failure:
    # '-Area {repo}' matches nothing filed, and an empty item list is
    # indistinguishable from a finished backlog.
    if ($ProjectName) {
        $result.tokens = @($result.tokens | ForEach-Object {
            if ($_ -is [array]) { ,([string[]]@($_ | ForEach-Object { $_ -replace '\{projectName\}', $ProjectName })) }
            else { $_ -replace '\{projectName\}', $ProjectName }
        })
    }
    foreach ($token in $result.tokens) {
        foreach ($text in @($token)) {
            foreach ($unknown in [regex]::Matches($text, '\{[A-Za-z]\w*\}')) {
                $result.problems += "run: 'args' for '$Cmd' contains unresolved token " +
                                    "'$($unknown.Value)'; the only token is {projectName}" +
                                    $(if (-not $ProjectName) { ' (no project name resolved)' })
            }
        }
    }

    # A missing command is already reported; there is no metadata to check against.
    if (-not $Exists) { return $result }
    $parameters = (Get-Command $FullPath -ErrorAction Stop).Parameters
    for ($i = 0; $i -lt $result.tokens.Count; $i++) {
        $token = $result.tokens[$i]
        if ($token -is [array]) {
            $result.problems += "run: 'args' for '$Cmd' has a nested array at position $i that does not follow an array-typed parameter"
            continue
        }
        if ($token -notmatch '^-([A-Za-z]\w*)$') { $result.positional += $token; continue }
        $name = $Matches[1]
        if (-not $parameters.ContainsKey($name)) {
            $result.problems += "run: '$Cmd' declares no parameter '-$name' (args: $(Format-RunArguments $result.tokens))"
            continue
        }
        $meta = $parameters[$name]
        if ($meta.ParameterType -eq [switch]) { $result.named[$meta.Name] = $true; continue }
        if ($i + 1 -ge $result.tokens.Count) {
            $result.problems += "run: '-$name' for '$Cmd' is not a switch and has no value after it"
            continue
        }
        $i++
        $next = $result.tokens[$i]
        if ($next -is [array] -and -not $meta.ParameterType.IsArray) {
            $result.problems += "run: '-$name' for '$Cmd' takes one value, not an array"
            continue
        }
        $result.named[$meta.Name] = $next
    }
    return $result
}

function Format-RunArguments {
    # The args as one line for a message or the -Text view: a nested array prints as
    # its values joined by commas, the way PowerShell itself would take them.
    param([object[]]$Tokens = @())
    return (@($Tokens | ForEach-Object { if ($_ -is [array]) { "'" + ($_ -join "','") + "'" } else { $_ } }) -join ' ')
}

function Invoke-ManifestRunEntry {
    # Runs the one run entry whose captureAs matches, with its args resolved exactly
    # as startup resolves them, and returns its output. Throws when the manifest has
    # no such entry or its args do not resolve: the caller is a hook that reports a
    # failure in its own words, and a launch with half its arguments is worse.
    # No {projectName} is resolved here -- an entry that needs one is not one a
    # caller outside startup can run, and saying so is the token check's job.
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$CaptureAs, [string]$ManifestPath)
    if (-not $ManifestPath) { $ManifestPath = Join-Path $RepoRoot 'startup-manifest.json' }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $runProp = $manifest.PSObject.Properties['run']
    $entries = if ($null -eq $runProp) { @() } else { @($runProp.Value) }
    $entry = $entries | Where-Object {
        $c = $_.PSObject.Properties['captureAs']
        $null -ne $c -and [string]::Equals([string]$c.Value, $CaptureAs, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if ($null -eq $entry) { throw "no run entry with captureAs '$CaptureAs' in $ManifestPath" }

    $cmd = [string]$entry.cmd
    $fullPath = Join-Path $RepoRoot $cmd
    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    if (-not $exists) { throw "run entry '$CaptureAs' names a missing command '$cmd'" }
    $arguments = Resolve-RunArguments $entry $cmd $fullPath $exists ''
    if (@($arguments.problems).Count -gt 0) { throw ($arguments.problems -join '; ') }

    $named = $arguments.named
    $positional = @($arguments.positional)
    return (& $fullPath @named @positional)
}
