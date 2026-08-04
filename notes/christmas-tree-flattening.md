# Christmas-tree flattening — the move table, and what proves each move

The legal moves for flattening deeply nested method bodies, which ones a
flow-equivalence prover can bless, and where each one is forbidden. Written after
the first production run of RazorGraph's `method_body_diff` (2026-08-03,
RazorGraphTool `bc6693f`).

## Detection

- C#: `deep_methods` (MCP) / `query --deep N` (CLI) in RazorGraphTool. `bodyDepth`
  is syntactic nesting stamped at build time; an else-if chain adds nothing, and
  brace-less stacked loops still count — the metric measures structure, not
  indentation, so it cannot be styled away.
- PowerShell: AST-based detection still open (the style-gate thread on the Janet
  stack).
- Constructors count. They are Method nodes with `bodyDepth` since RazorGraphTool
  `1fb662d`, so deep ctors surface in the same report — and ctors are exactly
  where the strongest move is restricted (below).

## The moves

**Guard inversion** — `if (cond) { deep }` becomes `if (!cond) continue/return;
deep`. Universally legal, including in constructors (nothing leaves scope).
Prover-blessable: `method_body_diff` canonicalizes branch conditions, so `!c`
with swapped targets folds back to `c` — the rewrite proves `equivalent:true`
against a saved baseline with no test in the loop. Only reduces depth when the
guarded body itself nests; a leaf `if (c) yield x` vs `if (!c) continue; yield x`
has the same max depth, because the `continue` occupies the level the payload
vacated.

**Extract-method** — the only real reduction for loop pyramids, because loop
nesting is structural and no styling removes it. The prover will (correctly)
refuse to bless it: flow moved, calls changed. Its refusal is still diagnostic —
on a clean extraction, `callsOnlyInRight` is exactly the new helper,
`callsOnlyInLeft` is exactly the plumbing that moved, and the diverging block is
the seam. Gate it with the test suite plus a before/after edge-set diff of the
code graph: every delta line must mention the new helper. 52/52 did on the first
production run. Second run (RazorGraphTool `affac31`, 2026-08-03) sharpened the
rule: **removed** edges cite the *old* caller, not the helper, so the check is
pairwise — every removed `Old -> X` must match an added `NewHelper -> X`, and
every added edge must mention a helper. A naive "grep every line for the helper"
flags exactly those removed lines as strays.

**Compute-and-return** — the constructor's consolation move when extraction is
blocked: the helper does the deep conditional work and returns values; the
assignments stay behind in the ctor.

**SelectMany pair-flattening** — when the upper levels of a pyramid are pure
iteration product (every A × its Bs, no per-level work), collapse them inline:
`foreach (var (a, b) in xs.SelectMany(a => a.Items.Select(b => (a, b))))`.
Lambdas add no depth in the C# bodyDepth metric — a deliberate calibration
decision — so the level genuinely disappears, with no new members and both
loop variables still in scope. Prover refuses per the LINQ-ification rule
(SelectMany is a new call); seam is one line, gate with tests. Prefer this
over a bespoke pair-iterator helper when the pairing has one consumer — and
especially over widening a *shared* helper's tuple to feed one caller: a
discard (`_`) at the other call sites is the tell that the sharing is forced
(Lori's catch, RazorGraphTool `5733c14`, 2026-08-03).

## Where extract-method is forbidden

A block inside a constructor that assigns `readonly` fields (CS0191: assignable
only in a ctor or field initializer) or `init`-only properties (CS8852: only in
an object initializer or on `this` in a ctor / another `init` accessor) cannot
move to a helper — a helper called from the ctor is an ordinary method and the
extraction does not compile. An auto-flattener must gate the move on
`MethodKind.Constructor` plus a Roslyn check of what the candidate block assigns,
and fall back to guard inversion or compute-and-return. (Lori's catch, 2026-08-03.)

## What the prover will not bless, by design

- Extract-method (above — flow moved).
- switch → if-chain: condition text comes from syntax, and a case pattern
  (`UsingStatementSyntax u`) is not the text of an is-expression
  (`node is UsingStatementSyntax u`). Keep the switch; flatten inside the cases.
- LINQ-ification: `Select`/`OfType` move calls into lambdas and add calls of
  their own. Different, and reported as such.
- Renamed locals: operations compare by normalized source text. Rename in a
  separate, unproven commit if it matters.

## Workflow (the shape that worked)

1. Save baselines: `body <csproj> --method "m:Type.Name(paramTypes)"` → JSON.
   Method ids carry the full parameter-type list; parameterless is `m:Type.Name()`.
2. Transform: guard inversion where it wins depth; extraction where loops pyramid.
3. Gate: `method_body_diff --baseline` per method. `equivalent:true` ships on the
   proof alone; an expected refusal falls back to tests + graph edge-set diff.
4. Verify the metric moved: rebuild, `deep_methods` at the old threshold.

Related: [[note.razorgraph-mcp-server]], [[pattern.graph-first-analysis]].
