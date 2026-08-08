# PowerShell house rules

Rules for scripts in this repo, each one written because it actually broke something.
The checkable subset is enforced by `scripts\Test-PowerShellRules.ps1` -- prefer adding
a check there over adding a paragraph here. A style guide nobody runs degrades quietly,
which is the exact failure mode DESIGN-NOTES section 1 argues against.

**Target: PowerShell 7 (pwsh).** Installed 2026-07-27; 7.6.4. Sections 1-7 below are
edition-independent -- 7 does not rescue you from any of them. Section 8 is
backward-compatibility only, for scripts that may land on a machine with just the
shipped-in-Windows 5.1.

---

## Part 1 -- Rules that hold regardless of edition

### 1. `return @()` does not return an empty array

PowerShell unrolls collections on output. `return @()` emits *nothing*, so the caller
gets `$null`, and `$result.Count` then throws under StrictMode. This is core pipeline
semantics, unchanged in 7.

Use the unary comma to wrap, **and assign the result** -- assignment unrolls exactly one
layer, which is what makes the wrap work:

```powershell
function Get-Section { ...; if ($null -eq $v) { return ,@() }; return ,@($v) }

$items = Get-Section $obj 'things'      # correct: count is 0 or N
foreach ($i in @(Get-Section $obj 'x')) # WRONG: one element holding the real array
```

If a helper returns a comma-wrapped array, say so in its comment. Inline `@(...)` around
the call is the trap, and it is a quiet one: the loop runs once and stringifies the whole
array instead of failing.

### 2. Probe every optional property before reading it

Under `Set-StrictMode -Version Latest`, reading a property that does not exist is a
terminating error, in 5.1 and 7 alike. Two common sources:

- **`ConvertFrom-Json`** returns `PSCustomObject`. Optional JSON fields are absent, not
  null. Reading `$entry.why` on an entry that omits `why` throws -- and it throws
  *before* your validation code can report the real problem, replacing a useful message
  with a useless one.
- **`Get-Help`** returns a plain string for a script with no comment-based help. Reading
  `.parameters` on it throws.

```powershell
if ($obj.PSObject.Properties.Name -contains 'why' -and $null -ne $obj.why) { ... }
```

Wrap it in a `Get-Prop`-style helper rather than repeating the incantation.

### 3. Put `$null` on the left of comparisons

`$x -eq $null` returns an array when `$x` is an array, which is not what the `if` reads.
`$null -eq $x` is scalar and correct.

### 4. Never `New-Item -ItemType File -Force` on a path that may exist

`-Force` truncates existing content. Test first:

```powershell
if (-not (Test-Path $p)) { New-Item -ItemType File $p }
```

### 5. Set `Set-StrictMode -Version Latest` in new scripts

It converts silent `$null` propagation into a loud failure at the point of the mistake.
Rules 1 and 2 exist *because* StrictMode is on, and that trade is worth it: the failures
it surfaces are real bugs that would otherwise reach production as wrong output. Note
that turning it on in a caller also affects scripts invoked in the same scope, which is
how the `Get-Help` bug in `Get-ScriptCatalog.ps1` finally surfaced.

### 6. Two structural rules with no syntax tell

- **Catch, do not propagate, in session-startup paths.** A script that runs at startup
  must not be able to take the session down. Capture the failure, report it, keep going
  (DESIGN-NOTES section 8). `Invoke-JanetStartup.ps1` runs each manifest command inside a
  `try` for exactly this reason, and that is how the `Get-ScriptCatalog` breakage showed
  up as one legible line instead of a stalled session.
- **Resolve paths from `$PSScriptRoot`, not a hardcoded layout.** `Get-ScriptCatalog.ps1`
  hardcoded `$env:JanetBase\.github\scripts`, the *old* repo's layout, and silently
  listed nothing here. Default to `$PSScriptRoot` and let a parameter override it. Stale
  `.github\scripts` paths still appear in eight scripts' `.EXAMPLE` blocks -- harmless to
  run, wrong to copy.

### 7. Adding a parameter renames every same-named local

Variable names are case-insensitive, so a new `[switch]$Full` parameter and an existing
local `$full` are **the same variable**. Assigning a string to a typed switch throws, and
the message names the wrong culprit entirely:

```
Cannot convert the "D:\Repos\JanetHome\README.md" value of type "System.String"
to type "System.Management.Automation.SwitchParameter".
```

That points at a *file path*, and at the call site of the whole script, for a line that
has worked unchanged for weeks. `Invoke-JanetStartup.ps1` hit it twice in one edit:
`-Full` against a local `$full` holding a resolved path, then `-Text` against a local
`$text` holding rule text.

**Before adding a parameter, grep the script for its name, case-insensitively.** Treat
every hit as a rename. A function-local shadows rather than collides so it survives, but
only by accident -- the same name at script scope later will not.

Worth adding to `Test-PowerShellRules.ps1`: flag any assignment to a variable whose name
matches a `param()` entry of a different type. Cheap AST check, and this class of bug is
invisible on review. Full write-up in `notes\startup-brief-budget.md`.

---

## Part 2 -- Backward compatibility (only if 5.1 must run it)

Now optional. `Test-PowerShellRules.ps1 -Target Ps51` turns these on. Worth running
before sharing a script, since 5.1 is what a stock Windows box has.

### 8. 5.1-only hazards

- **PowerShell 7-only encoding names.** `utf8NoBOM`, `utf8BOM`, `ansi`, `oem` are not
  valid `-Encoding` values in 5.1. The binding error is thrown at the pipeline element,
  so a script can print its success message and still not have written anything.

  Not hypothetical: `Push-ThreadStack.ps1` used `-Encoding utf8NoBOM`, printed
  `Pushed: <topic>` on every call, and never once persisted the stack under 5.1. The
  toolkit's most-used tool was silently inert. Its replacement (the thread *item*
  scripts, 2026-08-08) centralises the single write in `ThreadItems.Common.ps1`, which uses
  `[System.IO.File]::WriteAllText($p, $json, (New-Object System.Text.UTF8Encoding $false))`,
  which is correct on either edition -- keep it that way rather than reverting to
  `utf8NoBOM` now that 7 is the default.

- **`&&`, `||`, ternary `? :`, `??`, `?.`** are all 7+. In 5.1 they are *parser* errors:
  the whole file fails to load, not just that line.

- **`somenative.exe 2>&1`** in 5.1 wraps each stderr line in an `ErrorRecord` and sets
  `$?` to `$false` even on exit code 0. Check `$LASTEXITCODE` explicitly rather than
  trusting `$?`. 7 handles this correctly.

---

## What 7 gives you that is worth adopting

Since pwsh is now the default, these are fine to use in repo scripts -- just not in
anything tagged for 5.1 compatibility: `&&`/`||` chaining, ternary and null-coalescing
operators, `ConvertFrom-Json -AsHashtable`, and `-Encoding utf8NoBOM` as a default rather
than an error.
