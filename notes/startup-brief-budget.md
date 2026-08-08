# Startup brief budget

The session brief emitted by `Invoke-JanetStartup.ps1` grew until 84% of it was prose
that is not the startup contract. Measured 2026-08-01, before the fix.

## The measurement

Total: **4525 characters** of compressed JSON, on every session start.

| Section | Chars | Share | Contract? |
|---|---|---|---|
| `retrieval` | 1986 | 44% | 42 chars of it (`graph` + `via`) |
| `rules` | 1829 | 40% | the imperatives are; the rationale is not |
| `read` | 359 | 8% | yes |
| `run` | 133 | 3% | yes |
| `captured` | 60 | 1% | yes, but duplicated — see below |
| `manifest` + `janetBase` | 67 | 1% | yes |
| `problems` | 0 | 0% | yes |

Breakdown of the 1986-char `retrieval` block, which is nominally *a pointer*:

| Field | Chars | |
|---|---|---|
| `graph` + `via` | 42 | the actual pointer |
| `$comment.json` | 303 | **a comment**, shipped to the model |
| `usage` | 492 | six example invocations |
| `update` | 259 | full parameter list |
| `caveats` | 257 | |
| `add` | 227 | full parameter list |
| `envelope` | 191 | |
| `fallback` | 120 | |

And rule #8 (the provenance rule) was 449 characters on its own — 25% of the rules
block, 10% of the entire brief.

## What went wrong

**Progressive disclosure was applied to file content but not to the manifest's own
prose.** `-IncludeContent` is off by default and the `read` list carries paths and
reasons only, exactly as DESIGN-NOTES section 2 argues. That discipline stopped at the
manifest boundary. `retrieval` and `rules` were passed through verbatim, at whatever
length someone had last typed into the JSON.

The retrieval pointer exists *because* eagerly loading the tool inventory was rejected
as too expensive. It then became an eagerly-loaded manual for the retrieval tool —
`add` and `update` reproduce parameter lists that `Get-Research.ps1 -?` and the graph
node already hold, and `envelope` describes a shape that every response to a query
visibly demonstrates. The pattern was defeated one level up from where it was applied.

**A `$comment` field reached the model.** `$comment.json` is written for a person
editing the manifest. Nothing stripped it, so 303 characters of editorial note were
billed to every session. Any `$`-prefixed key is by convention not payload; the brief
had no such convention.

**`captured` and `run[].output` are byte-identical.** Confirmed: for a `run` entry with
a `captureAs`, the same string is emitted twice. Only 36 characters at present, because
the thread stack is small — but it scales with the output of every future startup
command, and the duplicate is pure loss.

## The rule-rationale tension

Rules were long because each carries its own justification, and the manifest's
`$comment.rules` says plainly: *"Do not rationalise past an advisory rule on the grounds
that this particular case is small."* The rationale is load-bearing for compliance. An
advisory rule stripped to a bare imperative is easier to argue past, which is the exact
failure the comment was written after.

So the fix is not "delete the rationale." It is to split each rule into `text` (the
imperative, always emitted) and `why` (the justification, on disk, emitted under
`-Full`). The reasoning stays reviewable and retrievable; it just stops being billed
every session. Plain-string rules still work, so the schema change is additive.

## Fix

`Invoke-JanetStartup.ps1` now trims by default and takes `-Full` to restore everything:

- strips `$`-prefixed keys from the brief at every level
- `retrieval` reduces to `graph`, `via`, and a one-line `usage` hint
- `rules` accept `{ text, why }` objects; only `text` is emitted by default
- `run[].output` is omitted when the same string is already in `captured`

Measured after: **4525 to 2105 characters, a 54% reduction**, with no loss of anything
the session cannot retrieve on demand. `-Full` (5379) and `-Text` remain complete,
because a person reading at a terminal scrolls and a person debugging the manifest wants
everything.

Where the remaining 2105 sits: `rules` 956 (45%), `read` 359 (17%), `captured` 319
(15%), `retrieval` 232 (11%), the rest structural. That is the contract and little else.
`captured` is inflated here by an unusually long thread-stack topic; it is normally ~60.

## Landmine: a switch parameter collides with any same-named local

Adding `-Full` broke the script twice, in a way worth writing down because nothing warns
about it and the symptom names the wrong culprit.

PowerShell variable names are **case-insensitive**, so the new `[switch]$Full` parameter
and the pre-existing local `$full` (a resolved path, used in both validation loops) are
*the same variable*. Assigning a path string to a typed `[switch]` throws:

```
Cannot convert the "D:\Repos\JanetHome\README.md" value of type "System.String"
to type "System.Management.Automation.SwitchParameter".
```

The message points at a *file path* and at the call site of the whole script. Nothing in
it suggests a parameter-name collision, and the line it blames is code that has worked
unchanged for weeks. Then `-Text` broke the same way against a local `$text` holding
rule text.

Locals are now `$fullPath` and `$ruleText`. The general rule: **adding a parameter to an
existing script is a rename of every same-named local in it**, and the compiler will not
tell you. Grep for the new parameter name, case-insensitively, before adding it. A
function-local shadows rather than collides, so it survives — but only by accident, and
the same name in script scope later will not.

Candidate for `Test-PowerShellRules.ps1`: flag any assignment to a variable whose name
matches a `param()` entry of a different type. Cheap AST check, and this class of bug is
invisible on review.

A second, smaller one from the same edit: `[string[]]$Only` with no default is `$null`,
and `$null.Count` is terminating under StrictMode (house rule 2). Default it to `@()`.

## `-Text` is not uniformly the expensive form

The convention reads as "JSON for the model, `-Text` for the terminal." Measured, the
cost goes in opposite directions depending on the script:

| Call | JSON | `-Text` | |
|---|---|---|---|
| `Get-Research.ps1 -Query`, 6 nodes | 3541 | 2655 | `-Text` is 75% |
| `Invoke-JanetStartup.ps1` | 1844 | 4627 | `-Text` is 251% |

They are different *kinds* of projection wearing one flag name. `Get-Research -Text` is
**denser** than its JSON: the text form drops the field names JSON repeats for every
node, and structure is not worth paying for when the output is a shortlist to choose
from. `Invoke-JanetStartup -Text` is **fuller** than its JSON: it adds every rule's
`why`. So for a model reader, `-Text` is the better call on one and the worse call on
the other, and the flag name predicts nothing.

Choose by what the projection *contains*, not by which reader the flag was named for.
For a pass/fail gate, take the JSON regardless -- `Test-PowerShellRules.ps1` returns
`{"violations":0,...}` in 79 characters, which is checkable; its `-Text` form is prose
that has to be interpreted to reach the same verdict.

**`-Text` output cannot be captured.** It is written with `Write-Host`, which goes to the
information stream, so `| Out-String`, `> file`, and `$x = ...` all yield *nothing* --
the text appears on the console and the variable is empty. Use `6>&1` to capture it. This
is why the mode-coverage test above reports `-Text` as 0 chars: not a failure, a stream.
Anything scripting against these tools must use the JSON form or redirect stream 6.

## The transport was the real defect

Length was the symptom. The brief reached the session as **text pasted on the command
line**, and that pathway fails in three ways trimming cannot fix:

1. **It truncates.** It did, mid-entry, in the session that prompted this work.
2. **It cannot be re-run.** The reader is stuck with whatever text arrived — no
   `-Full`, no re-query, no recovery.
3. **It carries the text without the context.** This is the serious one.

On 2026-08-01 a pasted brief asserted two `ENFORCED` rules in a session where neither
backing hook was loaded. The session's project dir was a different repo, so
`.claude\settings.json` was never read. Worse, both hooks resolve their script through
the project dir and are wrapped in a `Test-Path` guard:

```powershell
$s = Join-Path $d 'scripts\Invoke-EditGuard.ps1'; if (Test-Path $s) { & $s }
```

Started elsewhere, the path does not resolve and the `if` swallows it. The guards were
absent and nothing said so. `$env:JanetBase` was inherited through the shell; the project
settings were not. **A pasted brief describes an environment it is not connected to.**

So the linter never ran on any `.ps1` edited that session, and `Write`/`Edit` against
`research.json` would have gone straight through. Both rules read `ENFORCED`.

### Fix

- Rules may declare `enforcedBy` — the hook script backing them, resolved the same way
  the hook resolves it. Startup tests it against the current project dir.
- An unwired hook is reported, never fatal. It is listed under the brief's own
  `enforcement` field and raised on the warning stream, deliberately outside both the
  set `onMissing` governs and the `problems` field consumers gate on.
- The rule itself is emitted as `ADVISORY (claims ENFORCED; hook not wired here)` rather
  than keeping a label it has not earned. That relabelling is the actual fix; the
  `problems` entry just names which scripts were missing and where it looked.
- `CLAUDE.md` tells the session to run startup itself and not to accept a pasted brief.
- Every run writes `.janet\last-brief.json` — including `-Text`, which for a while
  silently did not, leaving the file on disk stale whenever someone read the formatted
  view. `-OutFile` moves it, `-OutFile ''` skips it. Ingestion is a file read, not a paste.

### Correction, later the same day

The first cut of this made an unwired hook a `problems` entry under `onMissing: fail`,
so startup refused to start from another repo's project dir. That was an over-correction
and it was wrong on its own terms.

The bug was **a brief asserting a guarantee it did not have**, and the repair for that is
to stop asserting it — which the relabelling already does completely. Refusing to start
buys nothing on top: a session reading `ADVISORY (claims ENFORCED; hook not wired here)`
is not deceived about anything. Meanwhile the cost is real. Working on this repo from
another project dir is legitimate, `Invoke-JanetStartup.ps1` resolves everything from
`$PSScriptRoot` and otherwise does not care where it was launched, and a hard stop makes
the one tool whose entire job is orienting a session the one tool that will not run for a
session that needs orienting.

It also mislabelled the failure. The manifest was intact and every path in it resolved;
what was false was one rule's label. Folding that into the same bucket as a missing
`read` entry made `onMissing` govern two unrelated conditions, and the ENFORCED rule
"startup fails if any manifest entry does not resolve" started meaning something wider
than it says. The two are now separate lists: manifest resolution stays a hard stop,
enforcement mismatch reports and continues.

Worth noting the manifest's own `$comment.rules.enforcedBy` had argued for exactly this
from the start — *"a brief that tells the truth is worth more than one that refuses to
start"* — and the code shipped contradicting it. The comment was right. A design note and
the code it describes drifting apart is the same class of failure as section 1's prose
context file, one layer up.

### Correction to the correction

The first cut of the fix above put enforcement notes *into* `problems`, reasoning that
fatal and non-fatal were "one list to the reader." They are not one list to every
reader: `Start-Janet.ps1` gates launch on `problems.Count` — correctly, for a manifest
that half-resolved under `onMissing: warn` — and its documented primary case is
launching from some other repo, which is exactly when the hooks are unwired. So the
hard stop that was removed from startup reappeared verbatim in the launcher, found the
same day by trying to launch from `C:\Users\lori_`.

Enforcement notes now live in their own `enforcement` field. The lesson is sharper than
"don't merge lists": **a brief field is a contract with every consumer, not a display
channel for the session model.** `problems` had one meaning to a consumer — do not
proceed — and appending softer content to it changed that meaning silently, in exactly
the way this note keeps warning that prose drifts.

## The general lesson

A brief is a budget, not a bucket. Anything that passes structured config straight to a
model needs an explicit answer to "what does the reader actually need at this moment,"
enforced in the code that emits it — not in the discipline of whoever last edited the
config. Config prose has no natural length limit and no reviewer; the emitter is the
only place the constraint can actually live.

Related: DESIGN-NOTES section 1 (manifest-driven startup), section 2 (progressive
disclosure).
