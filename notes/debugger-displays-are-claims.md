# Debugger displays are claims with validity windows

The rule, then the story that earned it, then the modern occurrences.

## The rule

A debugger's display is a claim, not a fact. Every displayed value has a
validity window -- the program point at which some convention guaranteed it --
and the display does not tell you whether you are still inside that window.
Trust a convention only at its guaranteed boundary; past it, derive ground
truth from what was actually emitted. And when the derivation matters, wire
it into a tool so it runs every time: discipline that lives in a document
degrades, discipline that lives in a tool executes.

Corollary for reports: a plausible reading of a stale display produces a
confident, wrong bug report -- worse than no report, because it sends the fix
to the wrong owner. The cost of the false classification, not the cost of the
lookup, is what justifies automating the derivation.

## The story (Watson, ~2005-2008)

x86-era crash triage. `__thiscall` puts `this` in ECX -- but the convention
guarantees it only at the call boundary. In optimized builds the compiler
reuses ECX within instructions of the prolog, and WinDbg would happily
display the register's CURRENT value as if it answered "what is `this`?".
Engineers filed false "`this` was null" reports off a stale register --
debug builds seemed reliable only because unoptimized codegen spills `this`
to a fixed stack slot and leaves it there.

The real recovery: read the method's PROLOG -- the compiler's own record of
where the argument actually went -- and fetch `this` from the spill site.
After enough false reports, that derivation became a WinDbg extension that
walked the prolog automatically. The fix for a display that lies is not
remembering that it lies; it is a tool that computes the truth.

## Modern occurrences of the same shape

- A single-frame call stack (stack walker gave up politely) read as "there
  are no callers" rather than "the walk failed" -- absence as finding.
- A UI status banner ("not run yet") read as process state while the run was
  mid-flight -- the banner's validity window was the last settle, not now.
- A register/variable pane in optimized managed code showing hoisted or
  dead values -- same ECX lie, JIT edition.
- verifyheap as the prolog-walk of 2026: the derivation (enumerate the heap)
  overturning the plausible display reading (heap must be corrupt) --
  RetirementCore AV hunt, 2026-08-10, where the clean heap redirected the
  investigation from native suspects to the JIT.

Related: DESIGN-NOTES section 1 (contracts that fail loudly over documents
that degrade quietly) is this rule's authoring-time sibling; the RazorGraph
absence-as-finding caveats are its query-time sibling.
