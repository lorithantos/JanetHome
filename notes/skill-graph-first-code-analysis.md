# Skill: graph-first code analysis with RazorGraph

How to answer structural and semantic questions about a C#/Razor codebase from the
compiler's model instead of text search — the operating skill for the RazorGraphTool
cluster.

**Trigger:** the task asks a question source text cannot answer reliably — who calls
this, who implements this, what breaks if this signature changes, what do the tests
actually reach, where is the deep nesting, did this refactor preserve the flow, what
does this page depend on. If the plan starts "open the file and read", check this
skill first.

**Not the trigger:** single-fact lookups where the file and symbol are already known —
grep is cheaper than a compile.

## Workflow

1. **Build once, query many.** `build_solution` for anything crossing a project
   boundary — coverage, DI, "what breaks" — since those edges cannot exist in a
   single-project graph. `save_graph` immediately; future sessions `load_graph` in
   milliseconds instead of recompiling.
2. **Orient before drilling.** `graph_summary`, then `find_nodes` — and check
   `truncated` in every envelope before concluding you have seen everything.
3. **Mind direction.** Several edge types point opposite the question asked of them
   (`InjectedInto` runs service → consumer). Default is outgoing; ask `incoming` for
   "who reaches this", `both` for "are these related at all".
4. **Ids are exact.** `m:Type.Name(paramTypes)` with the full parameter-type list —
   parameterless is `m:Type.Name()`. Solution graphs scope `page:`/`js:` ids by
   project.
5. **Coverage claims are reachability claims.** Full call closure, cross-project
   only, depth carried on each edge — filter depth 1 for direct exercise. A
   "covered" method is reachable, not asserted-on.
6. **Descend into methods when the question is inside one.** `deep_methods` finds
   the nesting; `method_body_graph` shows one method's CFG; `method_body_diff`
   proves a rewrite flow-equivalent. The move table for acting on what you find —
   including where extraction is compiler-forbidden — is
   [[note.christmas-tree-flattening]].
7. **Gate refactors mechanically.** Flow-preserving rewrites: prover against a saved
   baseline. Extractions: the prover refuses honestly — gate with the suite plus a
   before/after edge-set diff where every delta must mention the new symbol.
8. **Read source only to spot-check.** The graph turns speculation into evidence;
   the file read is the audit, not the investigation ([[pattern.graph-first-analysis]]).

## Knowing the tool's blind spots

Caveats live on [[note.razorgraph-mcp-server]] and retrieval prints them — the ones
that change conclusions: no type-usage edges (DTO consumers are invisible; fall back
to grep for those), record-ctor params misclassified as injected services, saved-graph
JSON field names differ from the in-memory model (use [[script.search-json]] for
serverless graph queries).

Related: [[note.razorgraph-js-scoping]] for the client-tier roadmap and what the JS
extraction deliberately does not claim.
