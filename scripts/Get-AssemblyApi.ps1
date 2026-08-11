<#
.SYNOPSIS
    Report a compiled assembly's real API surface: types and their declared members.

.DESCRIPTION
    Answers "what is this library actually called" without a guess-and-compile
    loop. Reading a NuGet package's types from source is often impossible, the
    docs are often thinner than the surface, and guessing a node type's member
    names costs a build per wrong guess.

    Two things make the naive attempt fail, and both are handled here.

    SIBLINGS. Loading the DLL straight out of
    ~/.nuget/packages/<pkg>/lib/<tfm>/ throws ReflectionTypeLoadException on
    GetTypes(), because that folder holds one assembly and its dependencies are
    elsewhere. This resolves references from the folder the assembly sits in, so
    pointing at a build or publish output -- where the whole closure is already
    laid out side by side -- just works. A folder with no siblings is reported as
    the likely cause rather than left as a wall of identical load errors.

    PARTIAL LOADS. When some types still fail to load, the ones that did load are
    used rather than the whole call failing: a partial answer beats none, and the
    count of what could not be loaded is reported instead of being swallowed.

    Output is filtered before it is printed. GetTypes() on a compiler library
    returns thousands of names, and an unfiltered dump is the reason this is a
    script and not a one-liner.

.PARAMETER Assembly
    Path to the .dll, or a bare assembly name to find under -SearchRoot.

.PARAMETER SearchRoot
    Directory to search when -Assembly is a name rather than a path. Preference
    goes to the candidate whose folder holds the most sibling assemblies, since
    that is the one whose dependencies will resolve.

.PARAMETER Type
    Regex selecting type names. Omit to list every public type, which for a large
    library is usually not what you want.

.PARAMETER Member
    Regex selecting member names within the matched types.

.PARAMETER Inherited
    Include members declared on base types. Off by default: a syntax-node class
    inherits dozens of members and the interesting ones are its own.

.PARAMETER Static
    Include static members, which are excluded by default.

.OUTPUTS
    JSON: { contract, assembly, folder, siblings, typesLoaded, typesUnloadable,
    matched, types: [ { name, kind, baseType, members: [ { kind, type, name } ] } ] }.
    -Text prints the same thing for a human.

.EXAMPLE
    # What is a member access node called, and what does it carry?
    & "$env:JanetBase\scripts\Get-AssemblyApi.ps1" -Assembly Loretta.CodeAnalysis.Lua `
        -SearchRoot D:\Repos\RazorGraphTool -Type 'MemberAccess|MethodCall' -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Get-AssemblyApi.ps1" -Assembly .\bin\Release\net10.0\MyLib.dll -Type 'Options$'

.NOTES
    LoadFrom pins each assembly for the life of the PROCESS, and an agent shell
    typically reuses one process across commands even though variables do not
    survive. Two consequences, both observed while writing this:

    - Rebuilding the target and re-running in the same session returns the OLD
      surface. Start a new pwsh to see changes.
    - Once a dependency has been loaded from anywhere, a later load from a
      folder MISSING that dependency succeeds, because the assembly is already
      in the process. A sibling problem therefore reproduces only in a fresh
      process: pwsh -NoProfile -Command '...'. The bare nuget lib folder that
      threw 251 loader errors cold reported no error at all once a build output
      had been read earlier in the same session.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Assembly,

    [string]$SearchRoot = '.',

    [string]$Type = '',

    [string]$Member = '',

    [switch]$Inherited,

    [switch]$Static,

    [int]$MaxTypes = 40,

    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-AssemblyPath {
    param([string]$NameOrPath, [string]$Root)

    if (Test-Path -LiteralPath $NameOrPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $NameOrPath).Path
    }

    $fileName = if ($NameOrPath.EndsWith('.dll')) { $NameOrPath } else { "$NameOrPath.dll" }
    $candidates = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter $fileName -File -ErrorAction SilentlyContinue)

    if ($candidates.Count -eq 0) {
        throw "No assembly named '$fileName' under '$Root'. Give a full path, or a -SearchRoot that contains a build output."
    }

    # The copy with the most neighbours is the one whose dependencies resolve:
    # a publish folder beats a nuget lib folder, which holds the DLL alone.
    return ($candidates |
        Sort-Object -Property @{ Expression = { @(Get-ChildItem -LiteralPath $_.DirectoryName -Filter '*.dll' -File).Count } } -Descending |
        Select-Object -First 1).FullName
}

$assemblyPath = Resolve-AssemblyPath -NameOrPath $Assembly -Root $SearchRoot
$folder = Split-Path -Parent $assemblyPath
$siblings = @(Get-ChildItem -LiteralPath $folder -Filter '*.dll' -File).Count

# Resolve dependencies out of the same folder. Registering this BEFORE the load
# is what turns "could not load file or assembly" into a working reflection
# session; doing it by hand for each dependency is the loop this replaces.
$resolver = {
    param($sender, $eventArgs)
    $wanted = ($eventArgs.Name -split ',')[0].Trim()
    $candidate = Join-Path $folder "$wanted.dll"
    if (Test-Path -LiteralPath $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

try {
    $loaded = [System.Reflection.Assembly]::LoadFrom($assemblyPath)

    $unloadable = 0
    try {
        $types = @($loaded.GetTypes())
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        # Partial answer beats none, but say how partial it is.
        $types = @($_.Exception.Types | Where-Object { $null -ne $_ })
        $unloadable = @($_.Exception.LoaderExceptions).Count

        if ($siblings -le 1) {
            Write-Warning "'$folder' holds $siblings assembly: dependencies cannot resolve there. Point -SearchRoot at a build or publish output instead of a nuget lib folder."
        }
    }

    $matched = @($types | Where-Object { $_.IsPublic -and ($Type -eq '' -or $_.Name -match $Type) } | Sort-Object Name)
    $shown = @($matched | Select-Object -First $MaxTypes)

    $binding = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance
    if ($Static) { $binding = $binding -bor [System.Reflection.BindingFlags]::Static }
    if (-not $Inherited) { $binding = $binding -bor [System.Reflection.BindingFlags]::DeclaredOnly }

    $report = foreach ($t in $shown) {
        $members = foreach ($m in $t.GetMembers($binding)) {
            if ($Member -ne '' -and $m.Name -notmatch $Member) { continue }
            if ($m.MemberType -eq 'Constructor') { continue }

            # get_X / set_X / op_Equality and friends: the compiler's spelling of
            # members already listed properly. Printing both doubles the output
            # and hides the real surface in accessor noise.
            if ($m.MemberType -eq 'Method' -and $m.IsSpecialName) { continue }

            $memberType = switch ($m.MemberType) {
                'Property' { $m.PropertyType.Name }
                'Field'    { $m.FieldType.Name }
                'Method'   { $m.ReturnType.Name }
                default    { '' }
            }

            [pscustomobject]@{ kind = "$($m.MemberType)"; type = $memberType; name = $m.Name }
        }

        [pscustomobject]@{
            name     = $t.Name
            kind     = if ($t.IsInterface) { 'interface' } elseif ($t.IsEnum) { 'enum' } elseif ($t.IsAbstract) { 'abstract class' } else { 'class' }
            baseType = if ($t.BaseType) { $t.BaseType.Name } else { $null }
            members  = @($members | Sort-Object kind, name)
        }
    }

    $result = [pscustomobject]@{
        contract        = 1
        assembly        = Split-Path -Leaf $assemblyPath
        folder          = $folder
        siblings        = $siblings
        typesLoaded     = $types.Count
        typesUnloadable = $unloadable
        matched         = $matched.Count
        # Truncation is stated, never silent: a capped list that looks complete
        # is the failure this whole catalog exists to avoid.
        returned        = $shown.Count
        truncated       = ($matched.Count -gt $shown.Count)
        types           = @($report)
    }

    if (-not $Text) {
        $result | ConvertTo-Json -Depth 6 -Compress:$false
        return
    }

    Write-Output "$($result.assembly) -- $($result.typesLoaded) types loaded, $($result.matched) matched$(if ($result.truncated) { ", showing $($result.returned)" })"
    if ($result.typesUnloadable -gt 0) {
        Write-Output "  ($($result.typesUnloadable) type(s) could not be loaded; $($result.siblings) assemblies in the folder)"
    }
    foreach ($t in $result.types) {
        Write-Output ""
        Write-Output "$($t.kind) $($t.name)$(if ($t.baseType) { " : $($t.baseType)" })"
        foreach ($m in $t.members) { Write-Output "    $($m.kind.PadRight(8)) $($m.type) $($m.name)" }
    }
}
finally {
    [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
