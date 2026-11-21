<#
.SYNOPSIS
    The work boundary: which projects are in flight, and whether new work may start now.

.DESCRIPTION
    One implementation of Lori's 2026-11-21 working rules, shared by three entry points so
    they cannot disagree: Get-WorkBoundary.ps1 (the read startup runs, captured as
    workBoundary), Set-WorkBoundary.ps1 (record, correct or clear today's start; swap a slot)
    and Invoke-WorkBoundaryHook.ps1 (the per-prompt reminder).

      1. Two slots. At most two projects are in flight: 'owed' (someone else is waiting on
         it) and 'chosen'. Every other project is 'shelved'. A slot may be waiting on
         someone until a date; its list is not worked before then.
      2. No new work after the stop hour (23). The closed window runs from the stop hour to
         the resume hour (5) -- a CALL, not her words: it makes a session after midnight get
         the same answer as one at 23:30. Work already in flight may be finished.
      3. A six-hour day: no new work once spanHours have passed since the start RECORDED
         today. Wall-clock span, not summed hours. The start is recorded deliberately when
         real work begins, never inferred from the first session, because one-off bits early
         in the day do not start the clock.

    THE CLOCK IS AN ARGUMENT. Every function that decides anything takes -Now as a mandatory
    [DateTimeOffset] and reads no clock of its own (house rule 7, notes\csharp-house-rules.md,
    applied to PowerShell). Only the three entry scripts read the system clock, once, as a
    composition root does; tests pass -Now to them too.

    Times are local to the offset -Now carries. The day state stores the calendar date and an
    HH:mm start rather than a timestamp, because pwsh 7's ConvertFrom-Json turns an ISO
    timestamp into a DateTime and drops its offset, and Windows PowerShell 5.1 (which a hook's
    "shell": "powershell" may resolve to) leaves it a string. Date-only and HH:mm text survive
    both hosts unchanged.

    Dot-source this file. It is Windows PowerShell 5.1 compatible on purpose -- the hook runs
    it -- so no ternaries, no null-coalescing, no -AsHashtable. It does not Set-StrictMode at
    file scope (that would leak into the caller); each function sets its own.
#>

$script:WorkSlotNames = @('owed', 'chosen')
$script:WorkInvariant = [System.Globalization.CultureInfo]::InvariantCulture

function Get-WorkProp {
    # A property of a parsed JSON object, or $null when it is absent (house rule 2).
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object -is [System.Management.Automation.PSCustomObject] -and
        $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

function Test-WorkProp {
    param($Object, [string]$Name)
    return ($null -ne $Object -and $Object -is [System.Management.Automation.PSCustomObject] -and
        $Object.PSObject.Properties.Name -contains $Name)
}

function Get-WorkBoundaryPath {
    <#
    .SYNOPSIS
        The default settings and day-state paths under <JanetBase>\.janet, which is git-ignored.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$JanetBase)
    $dir = Join-Path $JanetBase '.janet'
    return [pscustomobject]@{
        Settings = (Join-Path $dir 'work-boundary.json')
        State    = (Join-Path $dir 'work-day.json')
    }
}

function Get-ProjectNameFromDir {
    # The leaf of a project dir, the way startup's {projectName} token reads it.
    param([AllowEmptyString()][string]$Dir)
    if ([string]::IsNullOrWhiteSpace($Dir)) { return '' }
    $trimmed = $Dir.TrimEnd('\', '/')
    if (-not $trimmed) { return '' }
    return (Split-Path $trimmed -Leaf)
}

function ConvertTo-WorkDateText {
    # yyyy-MM-dd text, or $null when the value is not one. A [datetime] is accepted because a
    # JSON reader may have converted the string already.
    param($Value)
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-dd', $script:WorkInvariant) }
    if ($Value -isnot [string]) { return $null }
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParseExact($Value, 'yyyy-MM-dd', $script:WorkInvariant,
            [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
        return $parsed.ToString('yyyy-MM-dd', $script:WorkInvariant)
    }
    return $null
}

function ConvertTo-WorkTimeOfDay {
    # A [timespan] for H:mm / HH:mm text from 00:00 to 23:59, or $null.
    param($Value)
    if ($Value -isnot [string]) { return $null }
    $parsed = [datetime]::MinValue
    # [string[]] is load-bearing: a bare @() is object[], which pwsh 7 binds to another
    # overload, and every time then reads as unparseable.
    if ([datetime]::TryParseExact($Value, [string[]]@('HH:mm', 'H:mm'), $script:WorkInvariant,
            [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
        return $parsed.TimeOfDay
    }
    return $null
}

function Format-WorkTime {
    # Local time with its offset, to the second: 2026-11-21T23:30:00-08:00.
    param([Parameter(Mandatory)][DateTimeOffset]$Value)
    return $Value.ToString('yyyy-MM-ddTHH:mm:sszzz', $script:WorkInvariant)
}

function Format-WorkClock {
    # HH:mm, with the date in front only when it is not -Today's.
    param([Parameter(Mandatory)][DateTimeOffset]$Value, [Parameter(Mandatory)][DateTimeOffset]$Today)
    $clock = $Value.ToString('HH:mm', $script:WorkInvariant)
    if ($Value.Date -ne $Today.Date) { return $Value.ToString('yyyy-MM-dd', $script:WorkInvariant) + ' ' + $clock }
    return $clock
}

function Test-WorkHour {
    param($Value)
    return (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0 -and $Value -le 23)
}

function Read-WorkBoundarySettings {
    <#
    .SYNOPSIS
        Reads and validates the settings file. Never throws.

    .OUTPUTS
        [pscustomobject] Settings (normalised: stopHour, resumeHour, spanHours, slots with
        owed and chosen each $null or {project, waitingUntil, note}) and Error ($null, or one
        line saying what is wrong). A missing file is an error: silence would read as "no
        rules", which is the one answer the rules never give.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    Set-StrictMode -Version Latest
    $result = [pscustomobject]@{ Settings = $null; Error = $null; Raw = $null }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.Error = "work-boundary settings not found at $Path (create them with Set-WorkBoundary.ps1 -Slot)"
        return $result
    }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $result.Error = "work-boundary settings at $Path are not valid JSON: $($_.Exception.Message)"
        return $result
    }

    $problem = $null
    if ($raw -isnot [System.Management.Automation.PSCustomObject]) {
        $problem = 'the file must hold one JSON object'
    }
    else {
        $stop = Get-WorkProp $raw 'stopHour'
        $resume = Get-WorkProp $raw 'resumeHour'
        $span = Get-WorkProp $raw 'spanHours'
        $slots = Get-WorkProp $raw 'slots'

        if (-not (Test-WorkHour $stop)) { $problem = 'stopHour must be a whole hour from 0 to 23' }
        elseif (-not (Test-WorkHour $resume)) { $problem = 'resumeHour must be a whole hour from 0 to 23' }
        elseif ($stop -eq $resume) { $problem = 'stopHour and resumeHour must differ' }
        elseif (-not ($span -is [int] -or $span -is [long] -or $span -is [double] -or $span -is [decimal]) -or
            $span -le 0 -or $span -gt 24) { $problem = 'spanHours must be a number above 0 and at most 24' }
        elseif ($slots -isnot [System.Management.Automation.PSCustomObject]) { $problem = "slots must be an object holding 'owed' and 'chosen'" }
    }

    $normalSlots = [ordered]@{}
    if (-not $problem) {
        foreach ($name in $slots.PSObject.Properties.Name) {
            if ($script:WorkSlotNames -notcontains $name) { $problem = "slots: unknown slot '$name' (the slots are owed and chosen)"; break }
        }
    }
    if (-not $problem) {
        foreach ($name in $script:WorkSlotNames) {
            if (-not (Test-WorkProp $slots $name)) { $problem = "slots.$name is missing (null means the slot is empty)"; break }
            $slot = Get-WorkProp $slots $name
            if ($null -eq $slot) { $normalSlots[$name] = $null; continue }
            $project = Get-WorkProp $slot 'project'
            if ($slot -isnot [System.Management.Automation.PSCustomObject] -or $project -isnot [string] -or
                [string]::IsNullOrWhiteSpace($project)) { $problem = "slots.$name must be null or an object with a non-empty 'project'"; break }
            $waitRaw = Get-WorkProp $slot 'waitingUntil'
            $wait = $null
            if ($null -ne $waitRaw) {
                $wait = ConvertTo-WorkDateText $waitRaw
                if (-not $wait) { $problem = "slots.$name.waitingUntil must be null or a yyyy-MM-dd date"; break }
            }
            $note = Get-WorkProp $slot 'note'
            if ($null -ne $note -and $note -isnot [string]) { $problem = "slots.$name.note must be null or text"; break }
            if ($note -is [string] -and [string]::IsNullOrWhiteSpace($note)) { $note = $null }
            $normalSlots[$name] = [pscustomobject]@{ project = $project.Trim(); waitingUntil = $wait; note = $note }
        }
    }
    if (-not $problem -and $null -ne $normalSlots['owed'] -and $null -ne $normalSlots['chosen'] -and
        $normalSlots['owed'].project -ieq $normalSlots['chosen'].project) {
        $problem = "one project cannot hold both slots ($($normalSlots['owed'].project))"
    }

    if ($problem) {
        $result.Error = "work-boundary settings at ${Path}: $problem"
        return $result
    }

    $result.Raw = $raw
    $result.Settings = [pscustomobject]@{
        stopHour   = [int]$stop
        resumeHour = [int]$resume
        spanHours  = $(if ([double]$span -eq [math]::Floor([double]$span)) { [int]$span } else { [double]$span })
        slots      = $normalSlots
    }
    return $result
}

function Read-WorkDayState {
    <#
    .SYNOPSIS
        Reads the per-day state ({date, start}). Never throws.

    .OUTPUTS
        [pscustomobject] State ($null when there is no file, else {date, start}) and Error.
        A state from another day is returned as it is; deciding that it is stale is
        Get-WorkBoundaryDecision's job, because it needs -Now.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    Set-StrictMode -Version Latest
    $result = [pscustomobject]@{ State = $null; Error = $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $result.Error = "the day state at $Path is not valid JSON: $($_.Exception.Message)"
        return $result
    }

    $date = ConvertTo-WorkDateText (Get-WorkProp $raw 'date')
    $startText = Get-WorkProp $raw 'start'
    if ($raw -isnot [System.Management.Automation.PSCustomObject] -or -not $date -or
        $null -eq (ConvertTo-WorkTimeOfDay $startText)) {
        $result.Error = "the day state at $Path must be {`"date`": `"yyyy-MM-dd`", `"start`": `"HH:mm`"}; fix it or run Set-WorkBoundary.ps1 -Clear"
        return $result
    }
    $result.State = [pscustomobject]@{ date = $date; start = [string]$startText }
    return $result
}

function Test-WorkClosedWindow {
    <#
    .SYNOPSIS
        True when -Now falls in the closed window [StopHour, ResumeHour), which wraps
        midnight when StopHour is the later hour.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][DateTimeOffset]$Now,
        [Parameter(Mandatory)][int]$StopHour,
        [Parameter(Mandatory)][int]$ResumeHour
    )
    $hour = $Now.Hour
    if ($StopHour -gt $ResumeHour) { return ($hour -ge $StopHour -or $hour -lt $ResumeHour) }
    return ($hour -ge $StopHour -and $hour -lt $ResumeHour)
}

function Get-WorkDayStart {
    # Today's recorded start as a [DateTimeOffset], or $null: a state from any other calendar
    # day is forgotten. The offset is -Now's, since both lie on the same local day.
    param($State, [Parameter(Mandatory)][DateTimeOffset]$Now)
    if ($null -eq $State) { return $null }
    $today = $Now.ToString('yyyy-MM-dd', $script:WorkInvariant)
    if ($State.date -ne $today) { return $null }
    $time = ConvertTo-WorkTimeOfDay $State.start
    if ($null -eq $time) { return $null }
    return (New-Object DateTimeOffset -ArgumentList ($Now.Date.Add($time)), $Now.Offset)
}

function Get-WorkBoundaryDecision {
    <#
    .SYNOPSIS
        Every fact the rules decide on, for one project at one instant. The envelope and the
        hook's reminder are both rendered from this, so neither restates a rule.

    .PARAMETER Project
        The session's project name (the leaf of its project dir). Unknown or empty is simply
        shelved.

    .PARAMETER Now
        The instant to decide for, with the local offset. Mandatory: nothing here reads a clock.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Project = '',
        [Parameter(Mandatory)][DateTimeOffset]$Now,
        [Parameter(Mandatory)][string]$SettingsPath,
        [Parameter(Mandatory)][string]$StatePath
    )

    Set-StrictMode -Version Latest
    $settingsRead = Read-WorkBoundarySettings -Path $SettingsPath
    $stateRead = Read-WorkDayState -Path $StatePath
    $errors = @(@($settingsRead.Error, $stateRead.Error) | Where-Object { $_ })

    $decision = [pscustomobject]@{
        Project      = $Project
        Now          = $Now
        Settings     = $settingsRead.Settings
        Slot         = $null
        InFlight     = @()
        Started      = $null
        EndsAt       = $null
        Closed       = $false
        PastEnd      = $false
        WaitingUntil = $null
        Note         = $null
        Waiting      = $false
        Allowed      = $false
        Reason       = $null
        SettingsBad  = ($null -ne $settingsRead.Error)
        Error        = $null
    }
    if ($errors.Count -gt 0) { $decision.Error = ($errors -join '; ') }
    if ($null -eq $stateRead.Error) { $decision.Started = Get-WorkDayStart -State $stateRead.State -Now $Now }

    $settings = $settingsRead.Settings
    if ($null -eq $settings) {
        $decision.Reason = 'the work-boundary settings could not be read (see error)'
        return $decision
    }

    $decision.Slot = 'shelved'
    $inFlight = @()
    foreach ($name in $script:WorkSlotNames) {
        $slot = $settings.slots[$name]
        if ($null -eq $slot) { continue }
        $inFlight += [pscustomobject]@{ slot = $name; project = $slot.project; waitingUntil = $slot.waitingUntil; note = $slot.note }
        if ($Project -and $slot.project -ieq $Project.Trim()) {
            $decision.Slot = $name
            $decision.WaitingUntil = $slot.waitingUntil
            $decision.Note = $slot.note
        }
    }
    $decision.InFlight = $inFlight

    $today = $Now.ToString('yyyy-MM-dd', $script:WorkInvariant)
    $decision.Waiting = ($null -ne $decision.WaitingUntil -and [string]::CompareOrdinal($today, $decision.WaitingUntil) -lt 0)
    $decision.Closed = Test-WorkClosedWindow -Now $Now -StopHour $settings.stopHour -ResumeHour $settings.resumeHour
    if ($null -ne $decision.Started) {
        $decision.EndsAt = $decision.Started.AddHours($settings.spanHours)
        $decision.PastEnd = ($Now -ge $decision.EndsAt)
    }

    # One reason, the most fundamental first: a shelved project is not worked at any hour,
    # and the window is Lori's hard stop whatever the day's clock says.
    $clock = $Now.ToString('HH:mm', $script:WorkInvariant)
    if ($null -ne $stateRead.Error) {
        $decision.Reason = "today's recorded start could not be read (see error)"
    }
    elseif ($decision.Slot -eq 'shelved') {
        $decision.Reason = 'shelved project: in flight are ' + (Format-WorkInFlight $inFlight)
    }
    elseif ($decision.Closed) {
        $decision.Reason = ('closed window: it is {0}; no new work starts from {1:00}:00 to {2:00}:00' -f $clock, $settings.stopHour, $settings.resumeHour)
    }
    elseif ($decision.PastEnd) {
        $decision.Reason = ('past endsAt: the day started at {0} and ended at {1}' -f
            (Format-WorkClock $decision.Started $Now), (Format-WorkClock $decision.EndsAt $Now))
    }
    elseif ($decision.Waiting) {
        $decision.Reason = "waiting until $($decision.WaitingUntil)" + $(if ($decision.Note) { ": $($decision.Note)" } else { '' })
    }
    $decision.Allowed = ($null -eq $decision.Reason)
    return $decision
}

function Format-WorkInFlight {
    param([object[]]$InFlight)
    $parts = @($InFlight | ForEach-Object { "$($_.project) ($($_.slot))" })
    if ($parts.Count -eq 0) { return 'none' }
    return ($parts -join ' and ')
}

function ConvertTo-WorkBoundaryEnvelope {
    <#
    .SYNOPSIS
        The captured 'workBoundary' envelope, as an ordered dictionary.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Decision)

    Set-StrictMode -Version Latest
    $inFlight = @(foreach ($entry in @($Decision.InFlight)) {
            $row = [ordered]@{ slot = $entry.slot; project = $entry.project }
            if ($entry.waitingUntil) { $row['waitingUntil'] = $entry.waitingUntil }
            if ($entry.note) { $row['note'] = $entry.note }
            $row
        })
    $started = $null
    $endsAt = $null
    if ($null -ne $Decision.Started) { $started = Format-WorkTime $Decision.Started }
    if ($null -ne $Decision.EndsAt) { $endsAt = Format-WorkTime $Decision.EndsAt }

    $stop = $null; $resume = $null; $span = $null
    if ($null -ne $Decision.Settings) {
        $stop = $Decision.Settings.stopHour
        $resume = $Decision.Settings.resumeHour
        $span = $Decision.Settings.spanHours
    }

    return [ordered]@{
        project        = $Decision.Project
        slot           = $Decision.Slot
        inFlight       = $inFlight
        now            = (Format-WorkTime $Decision.Now)
        day            = [ordered]@{ started = $started; endsAt = $endsAt }
        newWorkAllowed = $Decision.Allowed
        reason         = $Decision.Reason
        stopHour       = $stop
        resumeHour     = $resume
        spanHours      = $span
        error          = $Decision.Error
    }
}

function Get-WorkBoundaryReminder {
    <#
    .SYNOPSIS
        The lines the per-prompt hook injects. Empty when all is well -- an in-flight project,
        the window open, a start recorded and the day not over -- because the hook runs on
        every prompt in every project and each line is billed every time.

    .PARAMETER StartCommand
        The exact command that records today's start, named in the no-start line.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Decision, [Parameter(Mandatory)][string]$StartCommand)

    Set-StrictMode -Version Latest
    $lines = [System.Collections.Generic.List[string]]::new()
    $now = $Decision.Now
    $clock = $now.ToString('HH:mm', $script:WorkInvariant)
    $settings = $Decision.Settings
    $hard = "by Lori's hard rule; work already in flight may be finished. Say so and do not start anything new -- the rule is changed in the settings, not by asking."

    if ($Decision.SettingsBad -or ($Decision.Error -and $null -eq $settings)) {
        $lines.Add("Work boundary could not be read: $($Decision.Error)")
        return , $lines.ToArray()
    }
    if ($Decision.Error) {
        $lines.Add("Work boundary could not be read in full: $($Decision.Error)")
    }

    if ($Decision.Slot -eq 'shelved') {
        $lines.Add("Work boundary: $($Decision.Project) is shelved; in flight are $(Format-WorkInFlight $Decision.InFlight). Say so and make no changes here; questions and lookups are fine.")
        return , $lines.ToArray()
    }

    if ($Decision.Closed) {
        $lines.Add(("Work boundary: it is {0}, inside the closed window ({1:00}:00-{2:00}:00). No new work starts after {1:00}:00, {3}" -f $clock, $settings.stopHour, $settings.resumeHour, $hard))
    }
    elseif ($Decision.PastEnd) {
        $lines.Add(("Work boundary: the day started at {0} and its {1}-hour span ended at {2}; it is {3}. No new work starts after the day's end, {4}" -f
                (Format-WorkClock $Decision.Started $now), $settings.spanHours, (Format-WorkClock $Decision.EndsAt $now), $clock, $hard))
    }

    if ($Decision.Waiting) {
        $line = "Work boundary: $($Decision.Project) is waiting until $($Decision.WaitingUntil); its list is not worked before then."
        if ($Decision.Note) { $line += " Note: $($Decision.Note)" }
        $lines.Add($line)
    }

    if (-not $Decision.Closed -and $null -eq $Decision.Started -and -not $Decision.Error) {
        $lines.Add("Work boundary: no start is recorded today. If this prompt begins real work, run: $StartCommand -- then tell Lori the end time in one line. A quick question or lookup does not start the clock.")
    }
    return , $lines.ToArray()
}

function Write-WorkJsonFile {
    # Writes JSON through a temp file and a rename, so a reader never sees half a file.
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $json = $Value | ConvertTo-Json -Depth 6
    $temp = "$Path.tmp-" + [guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding $false))
    # [NullString]::Value, not $null: PowerShell hands a .NET string parameter '' for $null,
    # and Replace refuses an empty backup path.
    if (Test-Path -LiteralPath $Path -PathType Leaf) { [System.IO.File]::Replace($temp, $Path, [NullString]::Value) }
    else { [System.IO.File]::Move($temp, $Path) }
}

function Write-WorkDayStart {
    <#
    .SYNOPSIS
        Records today's start: now (truncated to the minute), or -At HH:mm to correct it.

    .DESCRIPTION
        Once per calendar day. Without -At, a start already recorded today is LEFT ALONE and
        reported -- a second session running the same command must not move the start later
        and so lengthen the day. -At replaces it (the correction), and may not lie in the
        future. Returns [pscustomobject] changed, message, error.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][DateTimeOffset]$Now,
        [AllowEmptyString()][string]$At = ''
    )

    Set-StrictMode -Version Latest
    $result = [pscustomobject]@{ changed = $false; message = $null; error = $null }
    $today = $Now.ToString('yyyy-MM-dd', $script:WorkInvariant)
    $read = Read-WorkDayState -Path $StatePath

    if ($At) {
        $time = ConvertTo-WorkTimeOfDay $At
        if ($null -eq $time) { $result.error = "-At '$At' is not a time of day as HH:mm"; return $result }
        if ($time -gt $Now.TimeOfDay) {
            $result.error = "-At $At is later than now ($($Now.ToString('HH:mm', $script:WorkInvariant))); a start cannot be in the future"
            return $result
        }
        $start = $Now.Date.Add($time).ToString('HH:mm', $script:WorkInvariant)
        $was = Get-WorkDayStart -State $read.State -Now $Now
        Write-WorkJsonFile -Path $StatePath -Value ([ordered]@{ date = $today; start = $start })
        $result.changed = $true
        if ($null -ne $was) { $result.message = "today's start corrected from $($was.ToString('HH:mm', $script:WorkInvariant)) to $start" }
        else { $result.message = "today's start recorded at $start" }
        return $result
    }

    if ($read.Error) { $result.error = $read.Error; return $result }
    $existing = Get-WorkDayStart -State $read.State -Now $Now
    if ($null -ne $existing) {
        $result.message = "today's start was already recorded at $($existing.ToString('HH:mm', $script:WorkInvariant)); unchanged (correct it with -Start -At HH:mm)"
        return $result
    }
    $start = $Now.ToString('HH:mm', $script:WorkInvariant)
    Write-WorkJsonFile -Path $StatePath -Value ([ordered]@{ date = $today; start = $start })
    $result.changed = $true
    $result.message = "today's start recorded at $start"
    return $result
}

function Clear-WorkDayStart {
    <#
    .SYNOPSIS
        Forgets today's start (it was a one-off after all). Undoable: the message names the
        command that puts it back.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][DateTimeOffset]$Now
    )

    Set-StrictMode -Version Latest
    $result = [pscustomobject]@{ changed = $false; message = $null; error = $null }
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        $result.message = 'no start is recorded today; nothing to clear'
        return $result
    }
    $read = Read-WorkDayState -Path $StatePath
    $was = $null
    if ($null -eq $read.Error) { $was = Get-WorkDayStart -State $read.State -Now $Now }
    if ($null -eq $read.Error -and $null -eq $was) {
        $result.message = 'no start is recorded today; nothing to clear'
        return $result
    }
    if ($PSCmdlet.ShouldProcess($StatePath, 'clear today''s start')) {
        Remove-Item -LiteralPath $StatePath -Force
        $result.changed = $true
        if ($null -ne $was) {
            $hhmm = $was.ToString('HH:mm', $script:WorkInvariant)
            $result.message = "today's start ($hhmm) cleared; restore it with -Start -At $hhmm"
        }
        else { $result.message = 'the unreadable day state was cleared' }
    }
    return $result
}

function Set-WorkSlot {
    <#
    .SYNOPSIS
        Puts a project in a slot, or sets or clears the slot's waitingUntil and note. Lori's
        act only: sessions run it when she asks.

    .DESCRIPTION
        A new project in a slot starts with no waitingUntil or note unless they are given --
        the old occupant's wait is not inherited. Without -Project the slot's current project
        keeps its place and only its wait or note changes. A missing settings file is created
        with the decided numbers (stop 23, resume 5, span 6); a malformed one is refused, not
        overwritten, so a hand edit gone wrong is fixed by hand. The message names the command
        that undoes the change. Returns [pscustomobject] changed, message, error.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$SettingsPath,
        [Parameter(Mandatory)][ValidateSet('owed', 'chosen')][string]$Slot,
        [AllowEmptyString()][string]$Project = '',
        [AllowEmptyString()][string]$WaitingUntil = '',
        [switch]$NoWait,
        [AllowNull()][string]$Note = $null
    )

    Set-StrictMode -Version Latest
    $result = [pscustomobject]@{ changed = $false; message = $null; error = $null }
    $Slot = $Slot.ToLowerInvariant()

    if (Test-Path -LiteralPath $SettingsPath -PathType Leaf) {
        $read = Read-WorkBoundarySettings -Path $SettingsPath
        if ($read.Error) { $result.error = "$($read.Error) -- fix the file by hand; it is not overwritten"; return $result }
        $settings = $read.Settings
        $raw = $read.Raw
    }
    else {
        $settings = [pscustomobject]@{ stopHour = 23; resumeHour = 5; spanHours = 6; slots = [ordered]@{ owed = $null; chosen = $null } }
        $raw = $null
    }

    $current = $settings.slots[$Slot]
    $other = $settings.slots[@($script:WorkSlotNames | Where-Object { $_ -ne $Slot })[0]]
    if ($WaitingUntil -and $NoWait) { $result.error = 'give -WaitingUntil or -NoWait, not both'; return $result }
    if ($WaitingUntil -and -not (ConvertTo-WorkDateText $WaitingUntil)) { $result.error = "-WaitingUntil '$WaitingUntil' is not a yyyy-MM-dd date"; return $result }

    $next = $null
    if ($Project) {
        $Project = $Project.Trim()
        if ($null -ne $other -and $other.project -ieq $Project) {
            $result.error = "$Project already holds the other slot; move it out of that slot first"
            return $result
        }
        if ($null -ne $current -and $current.project -ieq $Project) {
            $next = [pscustomobject]@{ project = $current.project; waitingUntil = $current.waitingUntil; note = $current.note }
        }
        else { $next = [pscustomobject]@{ project = $Project; waitingUntil = $null; note = $null } }
    }
    elseif ($null -eq $current) {
        $result.error = "the $Slot slot is empty; name its project with -Project"
        return $result
    }
    else { $next = [pscustomobject]@{ project = $current.project; waitingUntil = $current.waitingUntil; note = $current.note } }

    if ($NoWait) { $next.waitingUntil = $null; $next.note = $null }
    if ($WaitingUntil) { $next.waitingUntil = ConvertTo-WorkDateText $WaitingUntil }
    if ($PSBoundParameters.ContainsKey('Note')) {
        if ([string]::IsNullOrWhiteSpace($Note)) { $next.note = $null } else { $next.note = $Note }
    }

    $before = Format-WorkSlotText $current
    $after = Format-WorkSlotText $next
    if ($before -ceq $after) { $result.message = "$Slot is already $after; unchanged"; return $result }

    # Rebuilt rather than patched: every slot value is written back from its normalised form,
    # so a date the JSON reader turned into a DateTime goes back out as yyyy-MM-dd text.
    # Other top-level keys (a $comment, say) are kept as they were.
    $slotsOut = [ordered]@{}
    foreach ($name in $script:WorkSlotNames) {
        $value = $settings.slots[$name]
        if ($name -eq $Slot) { $value = $next }
        if ($null -eq $value) { $slotsOut[$name] = $null }
        else { $slotsOut[$name] = [ordered]@{ project = $value.project; waitingUntil = $value.waitingUntil; note = $value.note } }
    }
    $out = [ordered]@{}
    if ($null -ne $raw) {
        foreach ($property in $raw.PSObject.Properties) { $out[$property.Name] = $property.Value }
    }
    else {
        $out['$comment'] = "Lori's work boundary (decided 2026-11-21). Read by scripts\Get-WorkBoundary.ps1; change the slots with scripts\Set-WorkBoundary.ps1 -Slot, the numbers by hand."
        $out['stopHour'] = $settings.stopHour
        $out['resumeHour'] = $settings.resumeHour
        $out['spanHours'] = $settings.spanHours
    }
    $out['slots'] = $slotsOut

    if ($PSCmdlet.ShouldProcess($SettingsPath, "set the $Slot slot")) {
        Write-WorkJsonFile -Path $SettingsPath -Value $out
        $result.changed = $true
        $result.message = "$Slot was $before, is now $after; undo with: " + (Format-WorkSlotUndo $Slot $current)
    }
    return $result
}

function Format-WorkSlotText {
    param($Value)
    if ($null -eq $Value) { return '(empty)' }
    $text = $Value.project
    if ($Value.waitingUntil) { $text += " waiting until $($Value.waitingUntil)" }
    if ($Value.note) { $text += " (note: $($Value.note))" }
    return $text
}

function Format-WorkSlotUndo {
    param([string]$Slot, $Value)
    if ($null -eq $Value) { return "edit the settings file and set slots.$Slot back to null" }
    $command = "Set-WorkBoundary.ps1 -Slot $Slot -Project '$($Value.project)'"
    if ($Value.waitingUntil) { $command += " -WaitingUntil $($Value.waitingUntil)" } else { $command += ' -NoWait' }
    if ($Value.note) { $command += " -Note '" + $Value.note.Replace("'", "''") + "'" }
    return $command
}
