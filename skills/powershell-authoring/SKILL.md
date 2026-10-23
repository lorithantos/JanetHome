---
name: powershell-authoring
description: Write, edit or run PowerShell here without the mistakes agents keep repeating. Use whenever you are about to write or change a .ps1, run a PowerShell command that passes text to a CLI (janet research, janet thread, git commit), parse a script's or CLI's output, or write a prompt that delegates PowerShell work to another agent.
---

# PowerShell authoring

This skill is a pointer, not a store. The script rules are `notes\powershell-house-rules.md`
(`note.powershell-house-rules`); the checkable subset is `scripts\Test-PowerShellRules.ps1`
(`script.test-powershell-rules`). New rules go into that note or, better, into the linter --
not into this file. What lives here is the part neither covers: running PowerShell from a
tool call, where the failures are in the command line rather than the script.

```powershell
$janet = if ($env:JanetBase) { $env:JanetBase } else { 'C:\repos\lorithantos\JanetHome' }
```

## Before you write a .ps1

Read the house rules note (it is short), then after every change:

```powershell
pwsh "$janet\scripts\Test-PowerShellRules.ps1" -Path <file>
```

The edit hook runs it when it is wired; run it yourself when it is not.

## Running PowerShell from a tool call

Each entry below cost a real session something. Dates are when it was seen.

1. **Text with a `$` goes in single quotes.** A double-quoted string interpolates, and an
   unknown variable becomes an empty string with no error. Two agents lost text this way
   on consecutive days writing catalog caveats -- `$janet` (2026-10-21) and
   `$mcpDependents` (2026-10-22) vanished from the stored text, and each had to rewrite a
   whole caveat list to repair it. For multi-line text, a single-quoted here-string
   (`@'` ... `'@`, closing marker at column 0). For a commit message, write a file and
   `git commit -F <file>`.

2. **A list or notes flag usually REPLACES.** `janet thread update --notes` replaces the
   notes; `--append-notes` appends. `janet research update --caveat` replaces the caveat
   list; `--append` adds. Run `--dry-run` first where the verb offers it -- the result
   shows every field's before and after.

3. **A failed `-match` does not clear `$Matches`.** It keeps the previous successful
   match, or is `$null` if there never was one. Branch on the `-match` result, or use
   `[regex]::Match(...)` and check `.Success`.

4. **Check `.error` on every janet CLI result.** A refusal comes back as JSON carrying an
   `error` field, which reads like any other output once it is piped onward. `$r = janet ... | ConvertFrom-Json; if ($r.error) { ... }`.

5. **Leave the file parseable after every single Edit.** The lint hook runs after each
   Edit call, not after a sequence. Opening a block in one Edit and closing it in the next
   reports a ParseError in between (seen 2026-10-22). Make each edit whole, or make the
   whole change in one Edit.

6. **Edit with Edit/Write, search with the graph.** A shell rewrite (`sed`, `Set-Content`
   on a source file) routes around every hook; a PowerShell or Bash file search is denied.
   Text search is `mcp__razorgraph__grep`, which also covers .ps1, .md and .json.

7. **An `[ordered]` dictionary indexed with an int is positional.** `$o[20]` on
   `[ordered]@{ 20 = 'a' }` asks for the 21st entry and returns `$null`, silently; a plain
   `@{}` hashtable looks the key up. A loop over `$o.Keys` that indexes with each key
   therefore reads nothing, or a neighbour's value when the position happens to exist.
   Seen 2026-10-23: two scripts wrote 16 thread notes this way, 15 empty and one carrying
   another item's text, and both printed success. Use `@{}`, string keys, or
   `$o.GetEnumerator()` and read `.Key` / `.Value`. `Test-PowerShellRules.ps1` now flags
   an `[ordered]` literal with an integer key (rule OrderedIntKey, house rules section 9).

8. **A write that reports success can still have written the wrong thing.** After a
   scripted batch of writes, read one back (`janet thread show --topic ... --full`,
   `janet research query --id ...`) before reporting it done. Rule 7's 16 bad notes
   survived a day because every write said it succeeded.

## Delegating PowerShell work

Quote the rules above into the prompt -- an agent inherits the tools, none of the habits.
`agent-kickoff` carries the template; this skill is what its PowerShell lines refer to.

## Everything else: query the catalog

```powershell
& $janet\scripts\Get-Research.ps1 -Id note.powershell-house-rules
& $janet\scripts\Get-Research.ps1 -Query 'powershell'
```

New lessons: a script rule goes into the note (or the linter); an invocation lesson goes
here, with the date it was seen.
