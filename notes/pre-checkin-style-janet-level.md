# Pre-checkin style fixes are Janet-level tooling

Decision (2026-08-02): automatic code-style fixing before checkin is a Janet
framework capability that applies to all code, in every language -- not a
feature any single tool grows for itself.

## The decision

Recorded from a working session in D:\Repos\RazorGraphTool. The originating
discussion (a "btw" chain) could not be located -- it is not in this repo,
not in research.json, not in D:\Repos\Janet, and the E:\ handoff drive was
not mounted -- so this note is the durable record.

The operator's framing: "we don't care that this tool has the right shape so
much as all code does." The value is uniform shape across everything checked
in, which puts the capability at the framework level:

- One gate, language-plugged backends: Roslyn + .editorconfig resolution for
  C#; StyLua / luacheck are the expected first Lua backends; PowerShell is
  already served by Test-PowerShellRules.ps1. Same plug-in shape as
  graph-first analysis (DESIGN-NOTES section 4) and RazorGraphTool's
  coverage-tool direction.
- A style verdict must come from the language's own resolver, not regex over
  text: report the resolved rule and where it came from, then fix or fail
  loudly.
- The gate runs before every checkin regardless of which repo it is.

## Existing pieces

- script.test-pre-commit -- the host, repaired (lib recreated 99f8329) and made
  repo-agnostic 2026-08-03: staged files resolve against `git rev-parse
  --show-toplevel` from the CWD, not the toolkit root, so one gate serves every
  repo. git found via JANET_GIT / PATH / VS-bundled probe.
- script.test-deep-nesting -- the PS nesting backend (below), wired in as a
  gate step.
- script.test-powershell-rules -- working, PowerShell house rules only.
- script.test-file-encoding -- working, encoding and line-ending audit only.

## Rule candidates (collected 2026-08-02/03, RazorGraphTool sessions)

- **Deep nesting (christmas-tree code).** DONE for both current languages
  (2026-08-03). C#: RazorGraph bodyDepth / deep_methods / `query --deep`
  (repo-level; no fast per-commit path yet). PowerShell: Test-DeepNesting.ps1
  by AST, wired into Test-PreCommit failing at depth >= 6 -- threshold from
  calibration (35 containers flag at >=3 across the toolkit, 3 at >=6) plus
  the RazorGraphTool precedent that 5 is case-by-case. Flattening moves and
  their proofs: skill.christmas-tree-flattening; architectural destination:
  DESIGN-NOTES section 12.
- **Anonymous blocks over N lines.** A 140-line SetAction lambda was
  unreadable, untestable, and invisible to the code graph (compiled name
  unrecoverable from grep). Named methods fix all three at once. Distinct
  from the nesting rule -- that lambda was flat.

  Second justification (2026-08-03): this rule is the *companion the nesting
  rule requires*. C# bodyDepth deliberately exempts lambda bodies, which is
  what makes SelectMany pair-flattening metric-real -- but the same exemption
  means a statement lambda is a blind spot where a christmas tree can hide
  invisibly (thought experiment: ClassifySymbol's five-branch dispatch inside
  a lambda would vanish from deep_methods while costing the reader full
  price). Principle: depth exemptions belong to containers that cannot hold
  logic; the moment a container can hold logic it needs either a name or a
  limit. Expression lambdas are exempt by construction -- they cannot nest;
  statement lambdas get this rule.
- **Tuples at method boundaries.** `(string FromId, string ToId)` across a
  signature is positionally typed strings: element names are compiler fiction
  (erased, unenforced), so a swapped pair compiles and quietly reverses every
  call edge. RazorGraphTool `c201a58` replaced its call-edge and disposal
  tuples with `readonly record struct` (CallEdge, DisposedResource) -- same
  layout, value equality preserved (Distinct keeps working), deconstruction
  call sites compile unchanged, and the swap becomes unwritable. Rule form:
  a tuple whose lifetime exceeds one expression gets a name; inside the
  expression that deconstructs it, it is plumbing. Same principle as
  anonymous blocks -- a tuple is to a type what a lambda is to a method.
  Checkable by Roslyn: flag tuple types in method signatures. Tolerable end
  of the spectrum: natural ordered pairs deconstructed immediately
  (GetLines' (start, end)).
- **Floating-point equality comparison.** Operator-stated: "no one should do
  equals comparisons on them." No stock warning exists -- the C# compiler and
  .NET SDK analyzers accept `double == double` silently (CA2242 only catches
  comparison against NaN literally; general flagging is third-party, e.g.
  Sonar S1244). So the gate's C# backend must carry the rule itself.
  Extra weight for the Lua backend: Lua numbers are floats by default.
  Prior art in-repo: RazorGraph's equivalence prover refuses to fold ordering
  comparisons over floating operands because NaN breaks complementarity.

  Origin story (why this rule earns its place): a pre-release Xbox game
  crashed intermittently; two days of tracking traced it to std::sort over
  float keys. A NaN in the keys makes every comparison return false, which
  silently violates strict weak ordering, and the small-range insertion-sort
  pass walks its pointer off the front of the array -- memory corruption that
  detonates far from the sort, with a callstack that says nothing about
  floating point. The rule is the two days, compressed into a warning at
  checkin. Scope note: NaN poisons *ordering* contracts too (comparators,
  binary search), not just equality -- the rule should cover float sort keys,
  not only ==.

  How the NaN got in: startup model loading computed pow via the
  exp(n * log(val)) identity, valid only on the positive reals, with val able
  to go negative -- log(negative) is a quiet NaN that rode inside model data
  all session before meeting the sort. Hardware detail that made it worse: on
  the x87 FPU (Pentium III, original Xbox), an unordered compare sets ALL
  condition flags, so naive single-flag predicates read NaN as YES to <, <=,
  and == simultaneously -- the comparator says a < b and b < a at once. And
  the indefinite QNaN that log(negative) produces has its sign bit set: a
  literally negative NaN (0xFFC00000). Two rules fall out, one per end of
  the pipe: (a) source -- flag domain-unchecked math identities (log, sqrt,
  asin/acos, exp-log pow) where the operand's range is not proven; (b) load
  boundary -- NaN/inf-screen float data at asset ingest, loudly. (b) is
  DESIGN-NOTES section 1 in a game-engine costume: startup was the one
  moment the bad value was cheap to catch.

## Open questions

- Fix-vs-verify split: does the gate rewrite the code or only reject it?
- Where per-repo style configuration lives when the language's resolver
  draws on machine-level settings the repo does not carry.
- Fast per-commit C# nesting: bodyDepth is syntax-only, so a parse-without-
  compile CLI mode in RazorGraphTool would make the C# rule commit-speed.
- ~~Machine setup~~ resolved 2026-08-03: PSScriptAnalyzer 1.25.0 and Pester
  6.0.1 installed CurrentUser; gate verified green end-to-end.
