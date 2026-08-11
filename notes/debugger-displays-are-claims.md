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

## The same rule, applied one level up (the vtable extension)

Lori wrote several WinDbg extensions; another recovered an object's real
type and origin from MSVC vtable layout. This is the ECX problem for
*types*: the static type in the symbols is a promise about what lives at
an address, and a dump is where promises stop holding. Worse, a pointer
in hand may aim at a base subobject rather than the object's start --
routine under multiple inheritance, where `this` is offset into the
middle.

The derivation, as she built it (deliberately NOT the RTTI route -- see
below):

1. Read the candidate vfptr at offset 0 and resolve that ADDRESS TO A
   SYMBOL. MSVC emits every vtable as `??_7Foo@@6B@`, so the symbol names
   the type. Reliable, not perfect.
2. Take that type's layout from the PDB. The layout fixes where every
   vfptr must sit within a complete object (offset 0 for the primary,
   plus a slot per MI base).
3. Slide the assumed object start backward until the expected vfptr slots
   line up with actual vtable pointers in memory.
4. Alignment IS the answer: the complete object's start and its true
   type, recovered together.

Constraint satisfaction rather than lookup -- the index-array move again
(don't fetch the answer, arrange the data so consistency produces it).

**Why this beats the RTTI route.** MSVC does carry a Complete Object
Locator at `vtable[-1]`, whose `offset` field would hand you step 3
directly. But RTTI is frequently compiled out (`/GR-`), and every
COL-based tool goes blind on those images. Vftable SYMBOLS are emitted
regardless, so the alignment method keeps working where the documented
shortcut fails. Choosing the artifact that is always present over the
metadata that is usually present is the whole lesson, twice over.

Known rare failure: `/OPT:ICF` folds byte-identical vtables, so unrelated
types with identical virtual signatures collapse to one symbol and the
name is whichever won the fold -- unfalsifiable from the vtable alone.

Triage payoffs, and they are different bug owners: a vfptr that does not
point into a module's `.rdata` means a smashed object; one pointing at a
valid vtable of the WRONG type means type confusion.

Two independent instances, same author, same decade -- which is what
makes this a rule rather than an anecdote.

**Provenance caution (2026-08-10):** the first draft of this section
asserted the COL mechanism, which Lori did not use; she corrected it. The
note's own rule caught its own author: a plausible mechanism was written
where the actual artifact belonged. Verify technique with the person who
built it.

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
