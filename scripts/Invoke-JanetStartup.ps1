<#
.SYNOPSIS
    Executes startup-manifest.json: sets $env:JanetBase, verifies every entry
    resolves, runs the startup commands, and emits a session brief.

.DESCRIPTION
    The manifest-driven startup from DESIGN-NOTES section 1.  A prose context file
    drifts silently -- it stays syntactically valid while going factually wrong.
    This is checkable instead: every 'read' path and every 'run' command either
    resolves or startup fails, per the manifest's onMissing setting.

    A 'run' entry is { cmd, captureAs, why } plus an optional 'args': an array of
    strings passed to the command as typed -- '-Name value' binds by name, a lone
    '-Switch' is a switch, anything else is positional.  Path resolution is on
    'cmd' alone.  'args' that is not an array of strings, or that names a
    parameter the command does not declare, is a problem and therefore a hard
    stop under onMissing 'fail'.  Since 2026-09-04, when the thread report entry
    gained ["-Area", "<this repo>"] so the brief carries one project's items and a
    per-area map of the rest instead of every open item on the machine.

    An args token may contain {projectName}: the leaf name of -ProjectDir, which is
    the repo the session will actually work in.  It exists because "<this repo>"
    above was first written as the literal 'JanetHome', while this launcher's
    stated usual case is loading Janet while working somewhere else -- so the brief
    narrowed to the wrong repo, listing nine JanetHome items in full and showing
    the session's own two as a bare count.  The area stays a manifest VALUE, which
    is the property worth keeping; what changed is that the value names the session
    rather than the repo Janet lives in.  Any other {token} is a problem: an
    unrecognised one would otherwise reach the command as a literal brace string
    and narrow to an area nothing is filed under, which returns an empty list and
    reads as a clean backlog.

    Validation happens before anything executes, so a broken manifest reports all
    its problems at once rather than failing halfway through with side effects on
    disk.

.PARAMETER ManifestPath
    Manifest to execute.  Defaults to startup-manifest.json in the repo root
    (resolved from this script's own location, so there is no bootstrap
    dependency on $env:JanetBase already being set).

.PARAMETER ProjectDir
    The repo the session works in, which is not necessarily the repo Janet lives
    in.  Defaults to $env:CLAUDE_PROJECT_DIR, then the current directory.  Two
    things read it: the {projectName} args token, and the enforcedBy check, which
    resolves hook paths against the project dir because that is the only place the
    harness will look.  Start-Janet.ps1 passes its -Path, so both answer for the
    session about to start rather than for whatever directory the launching shell
    happened to be sitting in.

.PARAMETER Text
    Formatted output for reading at a terminal.  The default is JSON: the brief's
    consumer is the session model, and structure beats column alignment for that
    reader.  Captured command output lands in the 'captured' property either way.

.PARAMETER Pretty
    Indent the JSON. For debugging by eye.

.PARAMETER IncludeContent
    Include the full text of each 'read' entry in the output.  Off by default:
    the brief lists paths and reasons, and the reader decides what to open.
    Progressive disclosure (section 2) applies to startup too.

.PARAMETER Full
    Emit every manifest field rather than the trimmed brief.  The default brief
    carries the contract and drops reference material the session can retrieve on
    demand -- see notes\startup-brief-budget.md, which measured the untrimmed
    brief at 4525 characters, 84% of it prose that was not the contract.

    Trimmed means: '$'-prefixed keys are dropped at every level (they are notes
    for whoever edits the manifest, not payload); 'retrieval' reduces to the
    pointer plus a one-line hint; rules emit their 'text' without the 'why'; and
    a run entry's output is omitted when the identical string is already in
    'captured'.  Nothing is lost that a query cannot recover.

.PARAMETER OutFile
    Where to persist the JSON brief.  Defaults to .janet\last-brief.json in the
    repo root; pass '' to skip writing.

    Written on every run, -Text included.  The console view and the persisted
    brief are different consumers, not alternatives -- reading the formatted
    output at a terminal is not a reason to leave the last brief on disk stale,
    and it silently was until 2026-08-01.

    The brief exists to be *ingested*, not pasted.  Handing it to a session on the
    command line truncates it, cannot be re-run by the reader, and carries the
    brief's text without the project context whose guarantees it describes -- a
    pasted brief once claimed two hooks were enforcing rules in a session where
    neither hook was loaded.  A file has none of those properties.

.PARAMETER SkipRun
    Validate and report without executing the 'run' entries.  Use to lint the
    manifest.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-JanetStartup.ps1"

.EXAMPLE
    & "D:\Repos\JanetHome\scripts\Invoke-JanetStartup.ps1" | ConvertFrom-Json
    JSON is the default; there is no -Json switch.  -Text opts into the
    formatted view instead.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-JanetStartup.ps1" -SkipRun
    Lints the manifest: every path resolves, every command exists, every 'args'
    is an array of strings naming parameters the command declares. No execution.
#>
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$ProjectDir,
    [switch]$Text,
    [switch]$Pretty,
    [switch]$IncludeContent,
    [switch]$Full,
    [string]$OutFile,
    [switch]$SkipRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'startup-manifest.json' }

if (-not (Test-Path $ManifestPath)) {
    throw "Startup manifest not found: $ManifestPath"
}

try {
    $manifest = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Startup manifest is not valid JSON ($ManifestPath): $($_.Exception.Message)"
}

# Set the framework variable before validation -- several scripts resolve paths
# through it, including ones the manifest is about to run.
$env:JanetBase = $repoRoot

# The project dir is the repo the SESSION works in; $repoRoot is where Janet
# lives, and the usual case is that they differ.  Resolved here rather than at
# the enforcement check further down, because validation now reads it too: the
# {projectName} args token is substituted before any run entry is bound.
#
# Assigning into the parameter is deliberate, not the $full/$text collision this
# file warns about below.  There is exactly one $ProjectDir and an unset one has
# no meaning to preserve, so defaulting in place is the whole intent -- but note
# that any local named $ProjectDir anywhere in this script is now this parameter.
if (-not $ProjectDir) {
    $ProjectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
}
$projectName = Split-Path $ProjectDir -Leaf

function Get-Prop {
    # ConvertFrom-Json returns PSCustomObject; reading an absent property is a
    # hard error under StrictMode.  Optional manifest fields ('why',
    # 'captureAs') must not blow up before validation gets to report the real
    # problem, so every read goes through here.
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name -and $null -ne $Object.$Name) {
        return $Object.$Name
    }
    return $Default
}

function Get-ManifestSection {
    # ASSIGN THE RESULT -- do not call this inline inside @(...).
    # The unary comma is load-bearing: 'return @()' unrolls the empty array to
    # nothing, so the caller gets $null and a later .Count throws under
    # StrictMode.  The wrap survives assignment (which unrolls one layer) but
    # NOT an inline @(...) call, which would hand you a 1-element array holding
    # the real one.
    param($Object, [string]$Name)
    $value = Get-Prop $Object $Name
    if ($null -eq $value) { return ,@() }
    return ,@($value)
}

function Select-PayloadFields {
    # ASSIGN THE RESULT -- comma-wrapped, same reason as Get-ManifestSection.
    # Drops '$'-prefixed keys ('$comment', '$comment.json', ...).  Those are notes
    # for whoever edits the manifest; nothing stripped them before, so 303
    # characters of editorial aside were billed to every session.  Returns an
    # ordered hashtable so callers can drop or add fields before emitting.
    # $Only defaults to an empty array, not $null: an omitted [string[]] is $null,
    # and $null.Count is a terminating error under StrictMode (house rule 2).
    param($Object, [string[]]$Only = @())
    $out = [ordered]@{}
    if ($null -eq $Object) { return ,$out }
    foreach ($prop in $Object.PSObject.Properties) {
        if ($prop.Name.StartsWith('$')) { continue }
        if ($Only.Count -gt 0 -and $Only -notcontains $prop.Name) { continue }
        $out[$prop.Name] = $prop.Value
    }
    return ,$out
}

function Test-RuleEnforced {
    # A rule's 'enforcedBy' names the hook script backing it.  Two wirings can arm
    # it, mirroring where the harness actually loads hooks from:
    #
    #   1. Project-level: .claude\settings.json in the project dir resolves the
    #      script as Join-Path <project dir> <enforcedBy>.  Only live when the
    #      session's project dir is this repo.
    #   2. User-level: ~\.claude\settings.json (wired 2026-08-01) runs the script by
    #      absolute path from this repo whenever the project dir is NOT this repo.
    #      Tested the same way the wiring works: the settings file names the script
    #      and the absolute script exists.  The name match is textual, so a hook
    #      commented out by renaming would still read as wired -- accepted; the
    #      check is against the config the harness actually loads, and settings
    #      files have no comment syntax to hide behind.
    #
    # Without this check a rule reads ENFORCED while nothing enforces it, which the
    # manifest's own $comment.rules calls worse than an honest suggestion.
    param($Rule, [string]$ProjectDir)
    if ($Rule -is [string]) { return $true }
    $backing = Get-Prop $Rule 'enforcedBy'
    if (-not $backing) { return $true }
    if (Test-Path (Join-Path $ProjectDir $backing) -PathType Leaf) { return $true }
    $userSettings = Join-Path $env:USERPROFILE '.claude\settings.json'
    if (-not (Test-Path $userSettings -PathType Leaf)) { return $false }
    if (-not (Test-Path (Join-Path $repoRoot $backing) -PathType Leaf)) { return $false }
    $leaf = Split-Path $backing -Leaf
    return ((Get-Content $userSettings -Raw -Encoding UTF8) -like "*$leaf*")
}

function Get-RuleText {
    # A rule is either a plain string or { text, why }.  The split exists so the
    # justification stays on disk and reviewable without being billed every
    # session -- an advisory rule stripped to a bare imperative is easier to argue
    # past, which is the failure the manifest's own $comment.rules warns about.
    param($Rule, [switch]$WithWhy)
    if ($Rule -is [string]) { return $Rule }
    # $ruleText, not $text: see the $fullPath note below. A function-local would
    # shadow rather than collide, but keeping the convention costs nothing and the
    # collision is invisible when it does bite.
    $ruleText = Get-Prop $Rule 'text'
    if (-not $ruleText) { return $null }
    $why = Get-Prop $Rule 'why'
    if ($WithWhy -and $why) { return "$ruleText -- $why" }
    return $ruleText
}

$readEntries = Get-ManifestSection $manifest 'read'
$runEntries  = Get-ManifestSection $manifest 'run'
$rules       = Get-ManifestSection $manifest 'rules'
$retrieval   = Get-Prop $manifest 'retrieval'

$onMissing = Get-Prop $manifest 'onMissing' 'fail'

# ---- Validation pass: resolve everything before doing anything -------------

$problems = @()
$reads = @()
foreach ($entry in $readEntries) {
    $path = Get-Prop $entry 'path'
    if (-not $path) { $problems += "read: entry with no 'path'"; continue }
    # $fullPath, not $full: PowerShell variable names are case-insensitive, so a
    # local $full and the -Full switch parameter are the same variable, and
    # assigning a path to a typed [switch] throws at the assignment.
    $fullPath = Join-Path $repoRoot $path
    $exists = Test-Path $fullPath -PathType Leaf
    if (-not $exists) { $problems += "read: missing file '$path'" }
    $reads += [PSCustomObject]@{
        path   = $path
        full   = $fullPath
        why    = (Get-Prop $entry 'why' '')
        exists = $exists
    }
}

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
    $notStrings = @($value | Where-Object { $_ -isnot [string] })
    if ($notStrings.Count -gt 0) {
        $kinds = ($notStrings | ForEach-Object { if ($null -eq $_) { 'null' } else { $_.GetType().Name } }) -join ', '
        $result.problems += "run: 'args' for '$Cmd' must be an array of strings; found $kinds"
        return $result
    }
    $result.tokens = @([string[]]$value)

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
        $result.tokens = @($result.tokens | ForEach-Object { $_ -replace '\{projectName\}', $ProjectName })
    }
    foreach ($token in $result.tokens) {
        foreach ($unknown in [regex]::Matches($token, '\{[A-Za-z]\w*\}')) {
            $result.problems += "run: 'args' for '$Cmd' contains unresolved token " +
                                "'$($unknown.Value)'; the only token is {projectName}" +
                                $(if (-not $ProjectName) { ' (no project name resolved)' })
        }
    }

    # A missing command is already reported; there is no metadata to check against.
    if (-not $Exists) { return $result }
    $parameters = (Get-Command $FullPath -ErrorAction Stop).Parameters
    for ($i = 0; $i -lt $result.tokens.Count; $i++) {
        $token = $result.tokens[$i]
        if ($token -notmatch '^-([A-Za-z]\w*)$') { $result.positional += $token; continue }
        $name = $Matches[1]
        if (-not $parameters.ContainsKey($name)) {
            $result.problems += "run: '$Cmd' declares no parameter '-$name' (args: $($result.tokens -join ' '))"
            continue
        }
        $meta = $parameters[$name]
        if ($meta.ParameterType -eq [switch]) { $result.named[$meta.Name] = $true; continue }
        if ($i + 1 -ge $result.tokens.Count) {
            $result.problems += "run: '-$name' for '$Cmd' is not a switch and has no value after it"
            continue
        }
        $i++
        $result.named[$meta.Name] = $result.tokens[$i]
    }
    return $result
}

$runs = @()
foreach ($entry in $runEntries) {
    $cmd = Get-Prop $entry 'cmd'
    if (-not $cmd) { $problems += "run: entry with no 'cmd'"; continue }
    # Path resolution is on 'cmd' alone; 'args' never touches it.
    $fullPath = Join-Path $repoRoot $cmd
    $exists = Test-Path $fullPath -PathType Leaf
    if (-not $exists) { $problems += "run: missing command '$cmd'" }
    $arguments = Resolve-RunArguments $entry $cmd $fullPath $exists $projectName
    foreach ($problem in $arguments.problems) { $problems += $problem }
    $runs += [PSCustomObject]@{
        cmd        = $cmd
        full       = $fullPath
        args       = @($arguments.tokens)
        named      = $arguments.named
        positional = @($arguments.positional)
        why        = (Get-Prop $entry 'why' '')
        captureAs  = (Get-Prop $entry 'captureAs' '')
        exists     = $exists
    }
}

# The retrieval pointer replaces an eagerly-loaded inventory, so it has to be
# held to the same contract: a dead pointer is worse than no pointer, because
# the session believes it has a way to look things up.
if ($null -ne $retrieval) {
    foreach ($field in @('graph', 'via')) {
        $value = Get-Prop $retrieval $field
        if (-not $value) { $problems += "retrieval: missing '$field'"; continue }
        if (-not (Test-Path (Join-Path $repoRoot $value) -PathType Leaf)) {
            $problems += "retrieval: '$field' does not resolve -- '$value'"
        }
    }
}

# A rule that emits nothing is a rule the session never sees, which is exactly the
# quiet degradation the manifest contract exists to prevent.
for ($i = 0; $i -lt $rules.Count; $i++) {
    if ($null -eq (Get-RuleText $rules[$i])) {
        $problems += "rules: entry $i has neither string form nor a 'text' field"
    }
}

# Hooks load from the project dir, which is not necessarily this repo. Resolved
# at the top of the script now, because the args token reads it during validation.
$unwired = @()
foreach ($rule in $rules) {
    if (-not (Test-RuleEnforced $rule $ProjectDir)) {
        $unwired += (Get-Prop $rule 'enforcedBy')
    }
}
# Reported, never fatal -- deliberately kept out of $problems, which onMissing
# governs. An unwired hook is not a manifest entry failing to resolve: the
# manifest is intact and every path in it still resolves. What is false is one
# rule's ENFORCED label, and the honest repair is to relabel that rule (which the
# brief does, below) and say so here -- not to refuse to start.
#
# Working on this repo from another repo's project dir is legitimate, and a
# session that starts with an accurate brief beats one that will not start at all.
# This check exists because on 2026-08-01 both hook-backed rules read ENFORCED in
# a session where neither hook was loaded; the fix for that is truthful labelling,
# and briefly was over-corrected into a hard stop that made startup depend on the
# caller's working directory.
$enforcementNotes = @()
if ($unwired.Count -gt 0) {
    $enforcementNotes += "enforcement: $($unwired.Count) rule(s) labelled ENFORCED are not wired " +
                         "for project dir '$ProjectDir' (missing: $($unwired -join ', ')). " +
                         "They are emitted as ADVISORY. Launch with '$repoRoot' as the project " +
                         "dir to arm them."
}

if ($problems.Count -gt 0) {
    $detail = ($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    $summary = "Startup manifest has $($problems.Count) unresolved entr$(if ($problems.Count -eq 1) {'y'} else {'ies'}):"
    if ($onMissing -eq 'fail') {
        throw "$summary$([Environment]::NewLine)$detail"
    }
    Write-Warning "$summary$([Environment]::NewLine)$detail"
}

# Startup continues, so the caller gets this on the warning stream as well as in
# the brief -- an unarmed guard is worth noticing even when it is not worth
# stopping for.
foreach ($note in $enforcementNotes) { Write-Warning $note }

# ---- Execution pass --------------------------------------------------------

$captured = [ordered]@{}
$runResults = @()

foreach ($run in $runs) {
    if ($SkipRun -or -not $run.exists) {
        $runResults += [PSCustomObject]@{
            cmd = $run.cmd; args = $run.args; captureAs = $run.captureAs
            status = $(if ($SkipRun) { 'skipped' } else { 'missing' })
            output = ''
        }
        continue
    }

    # Startup must not be able to hang the session on one bad script, so failures
    # are captured and reported rather than thrown (section 8).
    #
    # Two splats: the hashtable binds by name, the array fills what is left
    # positionally.  See Resolve-RunArguments for why the tokens cannot simply be
    # splatted as the array they arrived as.
    # Both the error stream and the information stream are captured, and the exit
    # code decides the status.  Only 6>&1 was redirected until 2026-09-04, which
    # made a whole class of failure invisible: a run entry that is a shim over a
    # CLI does not throw when the CLI fails, it writes to stderr and exits 1. The
    # brief then carried status 'ok' with an EMPTY capture and no problem, and
    # Start-Janet launched -- found by the tests for the {projectName} token, where
    # narrowing to a repo with nothing filed made Get-ThreadReport exit 1 and the
    # session got a blank thread report labelled ok. An empty string is the worst
    # possible report of a failure, because it is what a clean result looks like.
    #
    # $LASTEXITCODE is reset first: it is a leftover from whatever ran last, and
    # under StrictMode reading it before anything native has run is an error.
    $named = $run.named
    $positional = @($run.positional)
    $global:LASTEXITCODE = 0
    try {
        $output = (& $run.full @named @positional 2>&1 6>&1 | Out-String).TrimEnd()
        $status = if ($LASTEXITCODE -ne 0) { 'error' } else { 'ok' }
    }
    catch {
        $output = $_.Exception.Message
        $status = 'error'
    }

    if ($run.captureAs) { $captured[$run.captureAs] = $output }
    $runResults += [PSCustomObject]@{
        cmd = $run.cmd; args = $run.args; captureAs = $run.captureAs
        status = $status; output = $output
    }
}

# ---- Brief -----------------------------------------------------------------

# Built unconditionally.  -Text selects how this run is *displayed*; it does not
# mean no brief was produced, and persisting it is not the JSON path's private
# business -- see the OutFile note above.

$readOut = $reads | ForEach-Object {
    $o = [ordered]@{ path = $_.path; why = $_.why; exists = $_.exists }
    if ($IncludeContent -and $_.exists) { $o.content = (Get-Content $_.full -Raw -Encoding UTF8) }
    [PSCustomObject]$o
}

# Emitting output here as well as in 'captured' sends the identical string
# twice.  Cheap today because the thread stack is small; it scales with every
# future startup command's output, and the duplicate carries nothing.
$runOut = $runResults | ForEach-Object {
    $o = [ordered]@{ cmd = $_.cmd; captureAs = $_.captureAs; status = $_.status }
    # Only when the manifest passed any: a reader of 'captured.threadReport' needs
    # to know it was narrowed, and an empty array on every other entry says nothing.
    if (@($_.args).Count -gt 0) { $o.args = @($_.args) }
    if ($Full -or -not $_.captureAs) { $o.output = $_.output }
    [PSCustomObject]$o
}

# The pointer replaced an eagerly-loaded inventory; untrimmed it became an
# eagerly-loaded manual for the retrieval tool, at 44% of the whole brief.
# 'add' and 'update' restate parameter lists that -? and the graph node hold,
# and 'envelope' describes a shape every query response demonstrates.
$retrievalOut = $null
if ($null -ne $retrieval) {
    $fields = if ($Full) {
        Select-PayloadFields $retrieval
    } else {
        Select-PayloadFields $retrieval -Only @('graph', 'via', 'hint')
    }
    $retrievalOut = [PSCustomObject]$fields
}

# The label must not overstate itself.  This is the ordinary path, not a
# leftover: an unwired hook downgrades its rule and startup carries on.
$rulesOut = @()
foreach ($rule in $rules) {
    $line = Get-RuleText $rule -WithWhy:$Full
    if (-not $line) { continue }
    if (-not (Test-RuleEnforced $rule $ProjectDir)) {
        $line = $line -replace '^ENFORCED:', 'ADVISORY (claims ENFORCED; hook not wired here):'
    }
    $rulesOut += $line
}

# 'problems' and 'enforcement' are separate fields because they carry different
# severities and consumers act on that difference: Start-Janet refuses to launch
# on any 'problems' entry, which is right for a manifest that failed to resolve
# and wrong for a hook that is not wired -- launching from another repo is that
# launcher's documented primary case. Folding both into 'problems' re-fatalized
# the exact condition the 2026-08-01 fix made non-fatal, one layer up. A brief
# field is a contract with every consumer, not just the session model.
$brief = [PSCustomObject]@{
    janetBase   = $repoRoot
    manifest    = $ManifestPath
    read        = @($readOut)
    run         = @($runOut)
    captured    = [PSCustomObject]$captured
    retrieval   = $retrievalOut
    rules       = @($rulesOut)
    problems    = @($problems)
    enforcement = @($enforcementNotes)
}
$json = if ($Pretty) { $brief | ConvertTo-Json -Depth 6 }
        else { $brief | ConvertTo-Json -Depth 6 -Compress }

# Unbound means "use the default"; an explicitly empty string means "do not
# write", which is why this tests the bound parameters rather than truthiness.
if (-not $PSBoundParameters.ContainsKey('OutFile')) {
    $OutFile = Join-Path $repoRoot '.janet\last-brief.json'
}
if ($OutFile) {
    $outDir = Split-Path $OutFile -Parent
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    # UTF8Encoding($false), not -Encoding utf8NoBOM: correct on 5.1 and 7 alike
    # (house rules section 8).
    [System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding $false))
}

if (-not $Text) {
    $json
    return
}

Write-Host ''
Write-Host "Janet startup -- $repoRoot" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor DarkGray
Write-Host "`$env:JanetBase set to $repoRoot"
Write-Host ''

Write-Host 'READ THESE' -ForegroundColor Cyan
foreach ($r in $reads) {
    $mark = if ($r.exists) { ' ' } else { '!' }
    Write-Host "$mark $($r.path)"
    if ($r.why) { Write-Host "    $($r.why)" -ForegroundColor DarkGray }
    if ($IncludeContent -and $r.exists) {
        Write-Host ''
        Write-Host (Get-Content $r.full -Raw -Encoding UTF8)
        Write-Host ''
    }
}
Write-Host ''

foreach ($res in $runResults) {
    $label = if ($res.captureAs) { $res.captureAs } else { $res.cmd }
    Write-Host $label.ToUpperInvariant() -ForegroundColor Cyan
    if (@($res.args).Count -gt 0) { Write-Host "  $($res.cmd) $($res.args -join ' ')" -ForegroundColor DarkGray }
    switch ($res.status) {
        'ok'      { if ($res.output) { Write-Host $res.output } else { Write-Host '(no output)' -ForegroundColor DarkGray } }
        'skipped' { Write-Host '(skipped)' -ForegroundColor DarkGray }
        'missing' { Write-Host "(missing: $($res.cmd))" -ForegroundColor Yellow }
        'error'   { Write-Host "(failed: $($res.output))" -ForegroundColor Yellow }
    }
    Write-Host ''
}

if ($null -ne $retrieval) {
    Write-Host 'RETRIEVAL' -ForegroundColor Cyan
    Write-Host "  The tool/note inventory is not loaded. Query $(Get-Prop $retrieval 'graph') via:"
    Write-Host "    $(Get-Prop $retrieval 'via')"
    $usage = Get-ManifestSection $retrieval 'usage'
    foreach ($u in $usage) { Write-Host "      $u" -ForegroundColor DarkGray }
    $add = Get-Prop $retrieval 'add'
    if ($add) {
        Write-Host "  Add:      $add" -ForegroundColor DarkGray
    }
    $update = Get-Prop $retrieval 'update'
    if ($update) {
        Write-Host "  Update:   $update" -ForegroundColor DarkGray
    }
    $envelope = Get-Prop $retrieval 'envelope'
    if ($envelope) {
        Write-Host "  Shape:    $envelope" -ForegroundColor DarkGray
    }
    $caveats = Get-Prop $retrieval 'caveats'
    if ($caveats) {
        Write-Host "  Caveats:  $caveats" -ForegroundColor DarkGray
    }
    $fallback = Get-Prop $retrieval 'fallback'
    if ($fallback) { Write-Host "  Fallback: $fallback" -ForegroundColor DarkGray }
    Write-Host ''
}

if ($enforcementNotes.Count -gt 0) {
    Write-Host 'ENFORCEMENT' -ForegroundColor Yellow
    foreach ($note in $enforcementNotes) { Write-Host "  $note" -ForegroundColor Yellow }
    Write-Host ''
}

if ($rules.Count -gt 0) {
    # Always with the 'why': the terminal reader scrolls, and this is the view
    # someone uses when deciding whether a rule still earns its place.
    Write-Host 'OPERATING RULES' -ForegroundColor Cyan
    foreach ($rule in $rules) {
        $ruleText = Get-RuleText $rule
        if (-not (Test-RuleEnforced $rule $ProjectDir)) {
            $ruleText = $ruleText -replace '^ENFORCED:', 'ADVISORY (claims ENFORCED; hook not wired here):'
        }
        Write-Host "  - $ruleText"
        $why = if ($rule -is [string]) { $null } else { Get-Prop $rule 'why' }
        if ($why) { Write-Host "      $why" -ForegroundColor DarkGray }
    }
    Write-Host ''
}
