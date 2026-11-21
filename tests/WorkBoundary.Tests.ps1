#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the work boundary: scripts\lib\WorkBoundary.ps1 and its three entry
    points, Get-WorkBoundary.ps1, Set-WorkBoundary.ps1 and Invoke-WorkBoundaryHook.ps1.

.DESCRIPTION
    Every branch that decides something: the slot lookup, the closed window on both sides of
    midnight and at its edges, the six-hour end (one crossing midnight), no start, a new day
    forgetting yesterday's start, record / correct / clear, swapping a slot, a waiting slot,
    malformed settings and day state, and each line the per-prompt hook injects -- the silent
    case asserted EMPTY, and exit 0 on every path.

    NO TEST READS THE SYSTEM CLOCK (house rule 7). Every instant is a literal passed as -Now,
    to the library and to the entry scripts alike. Settings and day state live under
    TestDrive, so the real .janet\work-boundary.json and work-day.json are never touched.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test fixture builders writing under TestDrive, where -WhatIf has no meaning.')]
param()

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:scripts = Join-Path $script:repoRoot 'scripts'
    . (Join-Path $script:scripts 'lib\WorkBoundary.ps1')
    $script:getScript = Join-Path $script:scripts 'Get-WorkBoundary.ps1'
    $script:setScript = Join-Path $script:scripts 'Set-WorkBoundary.ps1'
    $script:hookScript = Join-Path $script:scripts 'Invoke-WorkBoundaryHook.ps1'
    $script:utf8 = New-Object System.Text.UTF8Encoding $false

    function At {
        # A fixed instant from its text: the only clock these tests have.
        param([Parameter(Mandatory)][string]$Text)
        return [DateTimeOffset]::Parse($Text, [System.Globalization.CultureInfo]::InvariantCulture)
    }

    function New-Paths {
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        return [pscustomobject]@{ Settings = (Join-Path $dir 'work-boundary.json'); State = (Join-Path $dir 'work-day.json') }
    }

    function Write-Text {
        param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][AllowEmptyString()][string]$Text)
        [System.IO.File]::WriteAllText($Path, $Text, $script:utf8)
    }

    function New-SettingsBody {
        # The decided values, with any field overridden.
        param([hashtable]$Override = @{})
        $body = [ordered]@{
            stopHour   = 23
            resumeHour = 5
            spanHours  = 6
            slots      = [ordered]@{
                owed   = [ordered]@{ project = 'client-site'; waitingUntil = '2026-11-26'; note = 'feedback session first' }
                chosen = [ordered]@{ project = 'ImageSelectorV2'; waitingUntil = $null; note = $null }
            }
        }
        foreach ($key in $Override.Keys) { $body[$key] = $Override[$key] }
        return $body
    }

    function New-Fixture {
        # Paths with settings written, and optionally a day state.
        param([hashtable]$Override = @{}, [string]$Date, [string]$Start)
        $paths = New-Paths
        Write-Text $paths.Settings ((New-SettingsBody $Override) | ConvertTo-Json -Depth 5)
        if ($Date) { Write-Text $paths.State (@{ date = $Date; start = $Start } | ConvertTo-Json) }
        return $paths
    }

    function Get-Decision {
        param([Parameter(Mandatory)]$Paths, [AllowEmptyString()][string]$Project = 'ImageSelectorV2', [Parameter(Mandatory)][string]$Now)
        return Get-WorkBoundaryDecision -Project $Project -Now (At $Now) -SettingsPath $Paths.Settings -StatePath $Paths.State
    }

    function Get-Envelope {
        param([Parameter(Mandatory)]$Paths, [AllowEmptyString()][string]$Project = 'ImageSelectorV2', [Parameter(Mandatory)][string]$Now)
        $decision = Get-Decision -Paths $Paths -Project $Project -Now $Now
        return (ConvertTo-WorkBoundaryEnvelope -Decision $decision | ConvertTo-Json -Depth 5 | Read-Json)
    }

    function Read-Json {
        # -DateKind String: pwsh would otherwise turn every timestamp back into a DateTime,
        # and the assertions are about the text the envelope carries.
        param([Parameter(Mandatory, ValueFromPipeline)][string[]]$Text)
        begin { $all = [System.Collections.Generic.List[string]]::new() }
        process { foreach ($t in $Text) { $all.Add($t) } }
        end { return (($all -join "`n") | ConvertFrom-Json -DateKind String) }
    }

    function Invoke-Hook {
        # Runs the hook in-process with the project dir it should see, and returns its
        # output lines and exit code. CLAUDE_PROJECT_DIR is restored afterwards.
        param([Parameter(Mandatory)]$Paths, [Parameter(Mandatory)][string]$ProjectDir, [Parameter(Mandatory)][string]$Now,
            [string]$Hook = $script:hookScript)
        $saved = $env:CLAUDE_PROJECT_DIR
        try {
            $env:CLAUDE_PROJECT_DIR = $ProjectDir
            $global:LASTEXITCODE = 99
            $out = @(& $Hook -InputJson '{"hook_event_name":"UserPromptSubmit","prompt":"x"}' -Now $Now -SettingsPath $Paths.Settings -StatePath $Paths.State)
            return [pscustomobject]@{ Lines = $out; Exit = $LASTEXITCODE }
        }
        finally { $env:CLAUDE_PROJECT_DIR = $saved }
    }
}

Describe 'slot lookup' {
    BeforeAll { $script:paths = New-Fixture -Date '2026-11-21' -Start '09:00' }

    It 'finds the owed project' {
        (Get-Envelope $script:paths -Project 'client-site' -Now '2026-11-27T10:00:00-08:00').slot | Should -Be 'owed'
    }
    It 'finds the chosen project, and new work is allowed there' {
        $envelope = Get-Envelope $script:paths -Project 'ImageSelectorV2' -Now '2026-11-21T10:00:00-08:00'
        $envelope.slot | Should -Be 'chosen'
        $envelope.newWorkAllowed | Should -BeTrue
        $envelope.reason | Should -BeNullOrEmpty
    }
    It 'reads any other project as shelved, naming what is in flight' {
        $envelope = Get-Envelope $script:paths -Project 'gamehub' -Now '2026-11-21T10:00:00-08:00'
        $envelope.slot | Should -Be 'shelved'
        $envelope.newWorkAllowed | Should -BeFalse
        $envelope.reason | Should -Be 'shelved project: in flight are client-site (owed) and ImageSelectorV2 (chosen)'
    }
    It 'reads an unknown or empty project as shelved without an error' {
        foreach ($name in @('no-such-repo', '')) {
            $envelope = Get-Envelope $script:paths -Project $name -Now '2026-11-21T10:00:00-08:00'
            $envelope.slot | Should -Be 'shelved'
            $envelope.error | Should -BeNullOrEmpty
        }
    }
    It 'matches the project name case-insensitively' {
        (Get-Envelope $script:paths -Project 'imageselectorv2' -Now '2026-11-21T10:00:00-08:00').slot | Should -Be 'chosen'
    }
    It 'lists the in-flight slots with any waitingUntil and note' {
        $envelope = Get-Envelope $script:paths -Now '2026-11-21T10:00:00-08:00'
        @($envelope.inFlight).Count | Should -Be 2
        $envelope.inFlight[0].waitingUntil | Should -Be '2026-11-26'
        $envelope.inFlight[0].note | Should -Be 'feedback session first'
        $envelope.inFlight[1].PSObject.Properties.Name | Should -Not -Contain 'waitingUntil'
    }
}

Describe 'the closed window' {
    BeforeAll { $script:paths = New-Fixture -Date '2026-11-21' -Start '06:00' }

    It 'is open at 22:59 and closed at 23:00' {
        (Get-Decision $script:paths -Now '2026-11-21T22:59:00-08:00').Closed | Should -BeFalse
        $late = Get-Envelope (New-Fixture -Date '2026-11-21' -Start '20:00') -Now '2026-11-21T23:00:00-08:00'
        $late.newWorkAllowed | Should -BeFalse
        $late.reason | Should -Be 'closed window: it is 23:00; no new work starts from 23:00 to 05:00'
    }
    It 'stays closed after midnight and opens at 05:00' {
        (Get-Decision $script:paths -Now '2026-11-22T00:30:00-08:00').Closed | Should -BeTrue
        (Get-Decision $script:paths -Now '2026-11-22T04:59:00-08:00').Closed | Should -BeTrue
        (Get-Decision $script:paths -Now '2026-11-22T05:00:00-08:00').Closed | Should -BeFalse
    }
    It 'does not wrap when the stop hour is the earlier one' {
        $paths = New-Fixture -Override @{ stopHour = 1; resumeHour = 5 }
        (Get-Decision $paths -Now '2026-11-21T23:30:00-08:00').Closed | Should -BeFalse
        (Get-Decision $paths -Now '2026-11-22T01:00:00-08:00').Closed | Should -BeTrue
        (Get-Decision $paths -Now '2026-11-22T05:00:00-08:00').Closed | Should -BeFalse
    }
}

Describe 'the six-hour day' {
    It 'ends spanHours after the recorded start, and not a minute sooner' {
        $paths = New-Fixture -Date '2026-11-21' -Start '09:00'
        $before = Get-Envelope $paths -Now '2026-11-21T14:59:00-08:00'
        $before.day.started | Should -Be '2026-11-21T09:00:00-08:00'
        $before.day.endsAt | Should -Be '2026-11-21T15:00:00-08:00'
        $before.newWorkAllowed | Should -BeTrue
        $at = Get-Envelope $paths -Now '2026-11-21T15:00:00-08:00'
        $at.newWorkAllowed | Should -BeFalse
        $at.reason | Should -Be 'past endsAt: the day started at 09:00 and ended at 15:00'
    }
    It 'carries endsAt across midnight for a late start' {
        $envelope = Get-Envelope (New-Fixture -Date '2026-11-21' -Start '20:30') -Now '2026-11-21T22:00:00-08:00'
        $envelope.day.endsAt | Should -Be '2026-11-22T02:30:00-08:00'
        $envelope.newWorkAllowed | Should -BeTrue
    }
    It 'reports no start as null and does not stop work for it' {
        $envelope = Get-Envelope (New-Fixture) -Now '2026-11-21T10:00:00-08:00'
        $envelope.day.started | Should -BeNullOrEmpty
        $envelope.day.endsAt | Should -BeNullOrEmpty
        $envelope.newWorkAllowed | Should -BeTrue
    }
    It "forgets yesterday's start on a new calendar day" {
        $envelope = Get-Envelope (New-Fixture -Date '2026-11-20' -Start '09:00') -Now '2026-11-21T16:00:00-08:00'
        $envelope.day.started | Should -BeNullOrEmpty
        $envelope.newWorkAllowed | Should -BeTrue
    }
    It 'reports the decided numbers' {
        $envelope = Get-Envelope (New-Fixture) -Now '2026-11-21T10:00:00-08:00'
        @($envelope.stopHour, $envelope.resumeHour, $envelope.spanHours) | Should -Be @(23, 5, 6)
        $envelope.now | Should -Be '2026-11-21T10:00:00-08:00'
    }
}

Describe 'a waiting slot' {
    It 'is not worked before its date, and is on the date' {
        $paths = New-Fixture -Date '2026-11-25' -Start '09:00'
        $before = Get-Envelope $paths -Project 'client-site' -Now '2026-11-25T10:00:00-08:00'
        $before.newWorkAllowed | Should -BeFalse
        $before.reason | Should -Be 'waiting until 2026-11-26: feedback session first'
        $on = Get-Envelope (New-Fixture -Date '2026-11-26' -Start '09:00') -Project 'client-site' -Now '2026-11-26T10:00:00-08:00'
        $on.newWorkAllowed | Should -BeTrue
    }
}

Describe 'recording, correcting and clearing the start' {
    It 'records now to the minute, and a second -Start leaves it alone' {
        $paths = New-Fixture
        $first = Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T10:15:42-08:00')
        $first.changed | Should -BeTrue
        (Get-Content $paths.State -Raw | ConvertFrom-Json).start | Should -Be '10:15'
        $second = Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T12:00:00-08:00')
        $second.changed | Should -BeFalse
        $second.message | Should -BeLike '*already recorded at 10:15*'
        (Get-Envelope $paths -Now '2026-11-21T12:00:00-08:00').day.started | Should -Be '2026-11-21T10:15:00-08:00'
    }
    It 'records a fresh start on a new day over yesterday''s' {
        $paths = New-Fixture -Date '2026-11-20' -Start '09:00'
        (Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T11:00:00-08:00')).changed | Should -BeTrue
        (Get-Content $paths.State -Raw | ConvertFrom-Json).date | Should -Be '2026-11-21'
    }
    It 'corrects the start with -At, and refuses a start in the future' {
        $paths = New-Fixture -Date '2026-11-21' -Start '10:15'
        $fix = Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T11:00:00-08:00') -At '9:05'
        $fix.changed | Should -BeTrue
        $fix.message | Should -Be "today's start corrected from 10:15 to 09:05"
        (Get-Envelope $paths -Now '2026-11-21T11:00:00-08:00').day.endsAt | Should -Be '2026-11-21T15:05:00-08:00'
        $future = Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T11:00:00-08:00') -At '11:01'
        $future.error | Should -BeLike '*cannot be in the future*'
        (Get-Content $paths.State -Raw | ConvertFrom-Json).start | Should -Be '09:05'
    }
    It 'clears today''s start and says how to restore it' {
        $paths = New-Fixture -Date '2026-11-21' -Start '07:40'
        $cleared = Clear-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T08:00:00-08:00')
        $cleared.changed | Should -BeTrue
        $cleared.message | Should -Be "today's start (07:40) cleared; restore it with -Start -At 07:40"
        (Get-Envelope $paths -Now '2026-11-21T08:00:00-08:00').day.started | Should -BeNullOrEmpty
        (Clear-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T08:00:00-08:00')).changed | Should -BeFalse
    }
    It 'records through Set-WorkBoundary.ps1 and names the end time' {
        $paths = New-Fixture
        $out = & $script:setScript -Start -Project 'ImageSelectorV2' -SettingsPath $paths.Settings -StatePath $paths.State -Now '2026-11-21T10:00:00-08:00' | Read-Json
        $LASTEXITCODE | Should -Be 0
        $out.message | Should -Be "today's start recorded at 10:00; the day ends at 16:00"
        $out.boundary.day.endsAt | Should -Be '2026-11-21T16:00:00-08:00'
    }
}

Describe 'swapping a slot' {
    It 'puts a new project in a slot without inheriting the old wait, and names the undo' {
        $paths = New-Fixture
        $result = Set-WorkSlot -SettingsPath $paths.Settings -Slot owed -Project 'gamehub'
        $result.changed | Should -BeTrue
        $result.message | Should -BeLike "*undo with: Set-WorkBoundary.ps1 -Slot owed -Project 'client-site' -WaitingUntil 2026-11-26 -Note 'feedback session first'"
        $envelope = Get-Envelope $paths -Project 'gamehub' -Now '2026-11-21T10:00:00-08:00'
        $envelope.slot | Should -Be 'owed'
        $envelope.inFlight[0].PSObject.Properties.Name | Should -Not -Contain 'waitingUntil'
        (Get-Envelope $paths -Project 'client-site' -Now '2026-11-21T10:00:00-08:00').slot | Should -Be 'shelved'
    }
    It 'sets and clears a wait on the current occupant' {
        $paths = New-Fixture
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot chosen -WaitingUntil '2026-12-01' -Note 'on hold').changed | Should -BeTrue
        $waiting = Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00'
        $waiting.reason | Should -Be 'waiting until 2026-12-01: on hold'
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot chosen -NoWait).changed | Should -BeTrue
        (Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00').newWorkAllowed | Should -BeTrue
    }
    It 'refuses a project that already holds the other slot, and a bad date' {
        $paths = New-Fixture
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot chosen -Project 'Client-Site').error | Should -BeLike '*already holds the other slot*'
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot owed -WaitingUntil '26/11/2026').error | Should -BeLike '*not a yyyy-MM-dd date*'
    }
    It 'creates a missing settings file with the decided numbers' {
        $paths = New-Paths
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot chosen -Project 'ImageSelectorV2').changed | Should -BeTrue
        $envelope = Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00'
        @($envelope.stopHour, $envelope.resumeHour, $envelope.spanHours) | Should -Be @(23, 5, 6)
        $envelope.slot | Should -Be 'chosen'
        @($envelope.inFlight).Count | Should -Be 1
    }
    It 'refuses to overwrite a malformed settings file' {
        $paths = New-Paths
        Write-Text $paths.Settings '{ not json'
        (Set-WorkSlot -SettingsPath $paths.Settings -Slot chosen -Project 'x').error | Should -BeLike '*not overwritten*'
        Get-Content $paths.Settings -Raw | Should -Be '{ not json'
    }
}

Describe 'settings and state that cannot be read' {
    It 'reports a missing settings file as an error, stops new work, and exits 1' {
        $paths = New-Paths
        $raw = & $script:getScript -Project 'ImageSelectorV2' -SettingsPath $paths.Settings -StatePath $paths.State -Now '2026-11-21T10:00:00-08:00'
        $LASTEXITCODE | Should -Be 1
        $envelope = $raw | Read-Json
        $envelope.error | Should -BeLike 'work-boundary settings not found at *'
        $envelope.newWorkAllowed | Should -BeFalse
        $envelope.slot | Should -BeNullOrEmpty
    }
    It 'reports invalid JSON' {
        $paths = New-Paths
        Write-Text $paths.Settings '{ "stopHour": 23, '
        (Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00').error | Should -BeLike '*are not valid JSON*'
    }
    It 'reports a malformed field by name: <Field>' -ForEach @(
        @{ Field = 'stopHour 24'; Override = @{ stopHour = 24 }; Expect = '*stopHour must be a whole hour*' }
        @{ Field = 'resumeHour equal to stopHour'; Override = @{ resumeHour = 23 }; Expect = '*stopHour and resumeHour must differ*' }
        @{ Field = 'spanHours 0'; Override = @{ spanHours = 0 }; Expect = '*spanHours must be a number above 0*' }
        @{ Field = 'a slot missing'; Override = @{ slots = @{ owed = $null } }; Expect = '*slots.chosen is missing*' }
        @{ Field = 'an unknown slot'; Override = @{ slots = @{ owed = $null; chosen = $null; spare = $null } }; Expect = "*unknown slot 'spare'*" }
        @{ Field = 'one project in both slots'; Override = @{ slots = @{ owed = @{ project = 'A' }; chosen = @{ project = 'a' } } }; Expect = '*cannot hold both slots*' }
        @{ Field = 'waitingUntil not a date'; Override = @{ slots = @{ owed = @{ project = 'A'; waitingUntil = 'soon' }; chosen = $null } }; Expect = '*waitingUntil must be null or a yyyy-MM-dd date*' }
    ) {
        $envelope = Get-Envelope (New-Fixture -Override $Override) -Now '2026-11-21T10:00:00-08:00'
        $envelope.error | Should -BeLike $Expect
        $envelope.newWorkAllowed | Should -BeFalse
    }
    It 'reports a malformed day state, and -Clear repairs it' {
        $paths = New-Fixture
        Write-Text $paths.State '{ "date": "2026-11-21", "start": "late" }'
        $envelope = Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00'
        $envelope.error | Should -BeLike '*the day state at * must be*'
        $envelope.newWorkAllowed | Should -BeFalse
        (Write-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T10:00:00-08:00')).error | Should -Not -BeNullOrEmpty
        (Clear-WorkDayStart -StatePath $paths.State -Now (At '2026-11-21T10:00:00-08:00')).changed | Should -BeTrue
        (Get-Envelope $paths -Now '2026-11-21T10:00:00-08:00').error | Should -BeNullOrEmpty
    }
}

Describe 'the per-prompt hook' {
    It 'says NOTHING when the project is in flight, the window is open and the day is running' {
        $hook = Invoke-Hook (New-Fixture -Date '2026-11-21' -Start '09:00') -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T12:00:00-08:00'
        $hook.Lines.Count | Should -Be 0
        $hook.Exit | Should -Be 0
    }
    It 'names the closed window and the hard rule' {
        $hook = Invoke-Hook (New-Fixture -Date '2026-11-21' -Start '20:00') -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T23:30:00-08:00'
        $hook.Exit | Should -Be 0
        $hook.Lines | Should -Be @("Work boundary: it is 23:30, inside the closed window (23:00-05:00). No new work starts after 23:00, by Lori's hard rule; work already in flight may be finished. Say so and do not start anything new -- the rule is changed in the settings, not by asking.")
    }
    It 'names the recorded start and the end once the day is over' {
        $hook = Invoke-Hook (New-Fixture -Date '2026-11-21' -Start '09:30') -ProjectDir 'C:\repos\ImageSelectorV2\' -Now '2026-11-21T16:00:00-08:00'
        $hook.Exit | Should -Be 0
        $hook.Lines | Should -Be @("Work boundary: the day started at 09:30 and its 6-hour span ended at 15:30; it is 16:00. No new work starts after the day's end, by Lori's hard rule; work already in flight may be finished. Say so and do not start anything new -- the rule is changed in the settings, not by asking.")
    }
    It 'says a shelved project is shelved and what is in flight' {
        $hook = Invoke-Hook (New-Fixture) -ProjectDir 'C:\repos\gamehub' -Now '2026-11-21T12:00:00-08:00'
        $hook.Exit | Should -Be 0
        $hook.Lines | Should -Be @('Work boundary: gamehub is shelved; in flight are client-site (owed) and ImageSelectorV2 (chosen). Say so and make no changes here; questions and lookups are fine.')
    }
    It 'gives the exact command when no start is recorded today' {
        $hook = Invoke-Hook (New-Fixture -Date '2026-11-20' -Start '09:00') -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T09:00:00-08:00'
        $hook.Exit | Should -Be 0
        $command = "pwsh -NoProfile -File '" + $script:setScript + "' -Start"
        $hook.Lines | Should -Be @("Work boundary: no start is recorded today. If this prompt begins real work, run: $command -- then tell Lori the end time in one line. A quick question or lookup does not start the clock.")
    }
    It 'says a waiting project is waiting, with its note' {
        $hook = Invoke-Hook (New-Fixture -Date '2026-11-21' -Start '09:00') -ProjectDir 'C:\repos\client-site' -Now '2026-11-21T12:00:00-08:00'
        $hook.Exit | Should -Be 0
        $hook.Lines | Should -Be @('Work boundary: client-site is waiting until 2026-11-26; its list is not worked before then. Note: feedback session first')
    }
    It 'reports missing or malformed settings in one line and still exits 0' {
        $missing = Invoke-Hook (New-Paths) -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T12:00:00-08:00'
        $missing.Exit | Should -Be 0
        $missing.Lines.Count | Should -Be 1
        $missing.Lines[0] | Should -BeLike 'Work boundary could not be read: work-boundary settings not found at *'
        $bad = New-Fixture -Override @{ spanHours = -1 }
        $malformed = Invoke-Hook $bad -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T12:00:00-08:00'
        $malformed.Exit | Should -Be 0
        $malformed.Lines.Count | Should -Be 1
        $malformed.Lines[0] | Should -BeLike 'Work boundary could not be read: *spanHours must be*'
    }
    It 'reports an internal error in one line and still exits 0' {
        # A copy of the hook beside a library that throws as it loads.
        $root = Join-Path $TestDrive 'broken'
        New-Item -ItemType Directory -Path (Join-Path $root 'scripts\lib') -Force | Out-Null
        Copy-Item $script:hookScript (Join-Path $root 'scripts\Invoke-WorkBoundaryHook.ps1')
        Write-Text (Join-Path $root 'scripts\lib\WorkBoundary.ps1') "throw 'library failed to load'"
        $hook = Invoke-Hook (New-Fixture) -ProjectDir 'C:\repos\ImageSelectorV2' -Now '2026-11-21T12:00:00-08:00' -Hook (Join-Path $root 'scripts\Invoke-WorkBoundaryHook.ps1')
        $hook.Exit | Should -Be 0
        $hook.Lines | Should -Be @('Work boundary could not be read: library failed to load')
    }
    It 'reads the payload on stdin and falls back to its cwd, under <Exe>' -ForEach @(
        @{ Exe = 'pwsh' }
        @{ Exe = 'powershell' }
    ) {
        $exe = Get-Command $Exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $exe) { Set-ItResult -Skipped -Because "$Exe is not installed"; return }
        $paths = New-Fixture
        $payload = '{"hook_event_name":"UserPromptSubmit","cwd":"C:\\repos\\gamehub","prompt":"hello"}'
        $saved = $env:CLAUDE_PROJECT_DIR
        try {
            $env:CLAUDE_PROJECT_DIR = $null
            $out = @($payload | & $exe.Source -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script:hookScript -Now '2026-11-21T12:00:00-08:00' -SettingsPath $paths.Settings -StatePath $paths.State)
            $code = $LASTEXITCODE
        }
        finally { $env:CLAUDE_PROJECT_DIR = $saved }
        $code | Should -Be 0
        $out | Should -Be @('Work boundary: gamehub is shelved; in flight are client-site (owed) and ImageSelectorV2 (chosen). Say so and make no changes here; questions and lookups are fine.')
    }
}
