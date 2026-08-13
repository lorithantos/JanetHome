<#
.SYNOPSIS
    Shared read/write/lock helpers for the thread item list.

.DESCRIPTION
    Dot-sourced by Add/Set-Active/Update/Complete/Show-ThreadItem(s). Not a
    standalone script -- it defines functions and does nothing on its own.

    Deliberately does NOT set Set-StrictMode: strict mode set in a dot-sourced
    file leaks into the caller's scope. Each entry-point script sets its own
    (house rules 5 and 7).

    The list replaced a push/pop stack on 2026-08-08. The stack's failure was
    that "record a topic" and "descend into a topic" were the same operation,
    so noting work displaced whatever was active, and completing an item
    dropped it. A list separates those: position carries order, 'status'
    carries focus, and nothing is ever removed.
#>

# Every writer takes this before its read-modify-write. The list is a shared
# mutable file with concurrent writers (two agent sessions is the normal case),
# and on 2026-08-08 an unlocked RMW destroyed another session's notes
# irrecoverably. Session-local scope -- no 'Global\' -- because the contending
# processes run as one user in one session, and Global\ needs rights we would
# rather not require.
$script:ThreadItemsMutexName = 'JanetThreadItems'

function Get-ThreadItemsPath {
    [CmdletBinding()]
    param([string]$Path = '')

    if ($Path -ne '') { return $Path }
    return (Join-Path $env:TEMP 'Janet\thread-stack.json')
}

# Reads one optional property. ConvertFrom-Json returns PSCustomObject and
# absent fields are missing rather than null, so touching one under StrictMode
# is a terminating error (house rule 2). Does not comma-wrap: callers wanting
# an array write @(Get-ThreadProp ...), which is correct precisely because
# this returns the value unwrapped.
function Get-ThreadProp {
    [CmdletBinding()]
    param($Item, [Parameter(Mandatory)][string]$Name, $Default = $null)

    if ($null -eq $Item) { return $Default }
    if ($Item.PSObject.Properties.Name -notcontains $Name) { return $Default }
    if ($null -eq $Item.$Name) { return $Default }
    return $Item.$Name
}

# Normalises to the five-field shape, defaulting anything absent. Migration of
# the old { topic, status, notes } form is therefore just a read followed by a
# write -- no transcription of note bodies, so none can be mangled.
#
# The three content fields are distinct roles, not stages of one migration:
#   refs  -- context that has earned a research.json node
#   next  -- the resume cursor: the one thing to do first on return
#   notes -- detail too small or too fresh to be worth a node
# An item may legitimately carry any combination, including none.
#
# Comma-wrapped: assign the result, never @(...) it (house rule 1).
function Read-ThreadItems {
    [CmdletBinding()]
    param([string]$Path = '')

    $resolved = Get-ThreadItemsPath -Path $Path
    if (-not (Test-Path $resolved)) { return ,@() }

    $raw = Get-Content $resolved -Raw
    if ($null -eq $raw -or $raw.Trim() -eq '') { return ,@() }

    $parsed = $raw | ConvertFrom-Json
    if ($null -eq $parsed) { return ,@() }

    $items = @()
    foreach ($entry in @($parsed)) {
        $items += [PSCustomObject]@{
            topic  = [string](Get-ThreadProp $entry 'topic' '')
            status = [string](Get-ThreadProp $entry 'status' 'parked')
            refs   = @(Get-ThreadProp $entry 'refs' @())
            next   = [string](Get-ThreadProp $entry 'next' '')
            notes  = [string](Get-ThreadProp $entry 'notes' '')
        }
    }
    return ,@($items)
}

function Write-ThreadItems {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [string]$Path = ''
    )

    $resolved = Get-ThreadItemsPath -Path $Path
    $dir = Split-Path $resolved -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # Depth 5: array -> item -> refs -> string still has headroom.
    # WriteAllText with an explicit no-BOM encoder rather than -Encoding
    # utf8NoBOM, which is 7-only and fails on 5.1 *after* the success message
    # prints -- the bug that left Push-ThreadStack silently inert (house rule 8).
    $json = ConvertTo-Json -InputObject @($Items) -Depth 5
    [System.IO.File]::WriteAllText($resolved, $json, (New-Object System.Text.UTF8Encoding $false))
}

# Serialises the whole read-modify-write. Callers pass a scriptblock taking the
# current items and returning the new ones.
function Invoke-ThreadItemsUpdate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [string]$Path = '',
        [int]$TimeoutMs = 5000
    )

    $mutex = New-Object System.Threading.Mutex($false, $script:ThreadItemsMutexName)
    $held = $false
    try {
        try {
            $held = $mutex.WaitOne($TimeoutMs)
        }
        catch [System.Threading.AbandonedMutexException] {
            # A writer died holding the lock. We now own it, and the file may be
            # mid-write -- surfaced rather than swallowed, because a torn list is
            # exactly the failure this lock exists to prevent.
            $held = $true
            Write-Warning 'Thread-items lock was abandoned by another process; the list may be inconsistent.'
        }

        if (-not $held) {
            throw "Timed out after ${TimeoutMs}ms waiting for the thread-items lock. Another session is writing."
        }

        $current = Read-ThreadItems -Path $Path
        $updated = & $Action $current
        Write-ThreadItems -Items @($updated) -Path $Path
        return ,@($updated)
    }
    finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

# Resolves a selector to exactly one index, or throws. Ambiguity is an error
# rather than a first-match guess: the operations that follow rewrite the file,
# and silently amending the wrong item is how notes get lost.
function Find-ThreadItemIndex {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [string]$Topic = '',
        [int]$Index = -1
    )

    $all = @($Items)

    if ($Index -ge 0) {
        if ($Index -ge $all.Count) {
            throw "Index $Index is out of range; the list holds $($all.Count) item(s)."
        }
        return $Index
    }

    if ($Topic -eq '') {
        for ($i = 0; $i -lt $all.Count; $i++) {
            if ($all[$i].status -eq 'active') { return $i }
        }
        throw 'No item is active, so there is nothing to act on. Pass -Topic or -Index.'
    }

    $matched = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        if ($all[$i].topic -like "*$Topic*") { $matched += $i }
    }

    if ($matched.Count -eq 0) { throw "No item matches topic '$Topic'." }
    if ($matched.Count -gt 1) {
        $names = ($matched | ForEach-Object { $all[$_].topic }) -join '; '
        throw "Topic '$Topic' is ambiguous -- it matches $($matched.Count) items: $names"
    }
    return $matched[0]
}
