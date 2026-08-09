# Cross-language conversion as the long-term target (2026-08-09)

Lori's stated north star: **"convert this JavaScript into Razor"** — with the graph
supplying a JavaScript model that can be compared, as best as possible, against the
C# side, so that LLM reasoning has a good chance of making the conversion *correct*
rather than plausible. Not competing with anyone; the requirement is robustness.

## The premise this rests on (why verification, not generation)

Distilled from the 2026-08-09 Modelcode.ai comparison and the model-promise
argument, both had live demonstrations this week:

- **The better the model gets, the more verification — not generation — is the
  scarce resource.** Models supply judgment (classification, design, translation);
  they do not emit audit trails. Evidence composes: the graph caught its own bugs
  twice this week (zero-Property gap, partial-class edge doubling) because its
  wrongness was detectable and statable. A model's wrong answer has no caveats
  field.
- **Morph (modelcode.ai) validates the market premise while marking the gap.**
  Their enterprise migration product independently rediscovered the Janet shape —
  spec-first, milestone gates, automated review, knowledge accumulation — but
  their entire correctness story is behavioral sampling (side-by-side E2E tests),
  because C++→Go / Python→Java have **no shared compiler model**, so proof is not
  available to them. Within-.NET, proof IS available, and `method_body_diff`
  already does it. Cross-language sits between: proof of full equivalence is out
  of reach, but far more than test-sampling is achievable. That between-space is
  the target.

## JS→Razor is tier migration, not transpilation

"Convert this JavaScript into Razor" usually means: client-side DOM construction
and state handling becomes server-rendered markup plus a smaller client remainder.
The correctness question is therefore not "does the Go function equal the C++
function" but **"does the page still honor every contract the remaining client
code depends on."** RazorGraph already models exactly those contracts — that is
its unique-in-field capability, not an add-on:

- `DomSelectedBy` — element ids the composition renders vs. ids scripts reach.
- `unboundSelectorIds` — the rename-that-broke-one-side defect, already detected.
- `ViewDataReadBy` / data-* keys — server state crossing to client by name.
- `UrlGeneratedBy` — the literal-URL vs @Url.Action finding (note.razorgraph-js-scoping).

So contract preservation is a **static graph diff**: build the graph before and
after conversion; every selector/key/URL the surviving JS consumes must still be
produced. That check is buildable today with no new extraction.

## Three verification planes, strongest applied where each reaches

1. **Contract plane (exists now):** the boundary edges above. Preservation is a
   graph-to-graph comparison; failures name the broken contract. This is the
   plane Morph does not have and cannot cheaply build.
2. **Structural plane (the build):** a JS-side extractor emitting the SAME
   BodyGraph IR the C# side already produces (blocks, branch structure, call
   sites, canonicalized conditions). Then a **relaxed cross-language comparator**:
   not bisimulation-as-proof but scored structural correspondence with a mismatch
   taxonomy — the prover's conservative report-why-not shape, downgraded honestly
   from "equivalent" to "corresponds / diverges at...". JS parsing candidates:
   TypeScript compiler API (best binding), acorn/ESTree (lightest), tree-sitter
   (already the field's commodity choice, weakest semantics). The 2026-07-31
   scoping note's deferred "function nodes" item is the first brick.
3. **Behavioral plane (borrowed from Morph):** side-by-side rendering — original
   page + JS vs. converted Razor, compare rendered DOM and network traffic.
   Catches what structure cannot model (timing, dynamic selectors, runtime
   data). Complements, never replaces, the other two.

LLM reasoning sits **between** the planes: it proposes the conversion; the planes
grade it; the mismatch reports feed the next attempt. Same loop as
plan → SurgicalEdit → prover-verify (the borrow-list tier-3 synthesis), with the
comparator relaxed to match what cross-language can honestly claim.

## "Any .NET language — correct?"

Mostly, with one asterisk and one floor:

- **Roslyn covers C# and VB** — MSBuildWorkspace compiles both, so the existing
  extractor reaches any C#/VB project already. Razor's code-behind is C#, so the
  JS→Razor target is concretely JS→C#.
- **F# is NOT Roslyn.** It has its own compiler service (FSharp.Compiler.Service);
  supporting it means a second extractor front end, same graph.
- **The universal floor is IL.** Every .NET language compiles to it;
  System.Reflection.Metadata/Cecil can extract CFGs from assemblies for a
  language-blind structural plane. Costs source fidelity (line mapping, names) —
  a fallback plane, not the primary.

## Ordered steps, when this activates

1. Contract-preservation query over two graphs (graph diff scoped to boundary
   edges) — no new extraction, immediate value for ANY page refactor.
2. JS function nodes + call sites (scoping note's deferred item) — makes the JS
   side navigable at all.
3. JS BodyGraph emission behind the shared IR (requires the formatVersion stamp
   from the parked visualization item first — same serialized-contract concern).
4. Cross-language comparator with mismatch taxonomy.
5. Behavioral side-by-side harness last — it needs a running app and is the
   least reusable.

Related: [[note.razorgraph-competitive-field]], [[note.razorgraph-borrow-list]],
[[note.razorgraph-js-scoping]], [[note.razorgraph-mcp-server]],
[[pattern.graph-first-analysis]].
