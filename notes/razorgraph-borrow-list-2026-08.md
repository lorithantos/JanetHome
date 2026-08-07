# Borrow list: concepts worth taking from the MCP code-graph field

Follow-up to [[note.razorgraph-competitive-field]] (2026-08-07). The Janet
growth pattern applied outward: borrow concepts from the field, contribute the
deterministic/discovery layer back. Best borrows are the ones that fuse with a
capability only this tool has — borrowed-cheap × our-expensive.

## Tier 1 — borrowed concept × unique asset

1. **Diff-aware test impact.** (DeusData detect_changes; roslyn-codelens-mcp
   analyze_change_impact.) git diff → changed methods → Covers edges walked
   backward → "run exactly the tests that reach this commit." The field stops
   at affected-symbols; the reachability layer completes it. Integration
   target: Test-PreCommit picks its own tests.
2. **Similarity → proof pipeline.** (DeusData MinHash SIMILAR_TO.) Cheap clone
   detection shortlists candidate pairs; method_body_diff proves merge
   candidates vs. legitimate divergence. Makes DESIGN-NOTES §12's pairwise
   catalog sweep tractable — bisimulation only on plausible pairs.
3. **Measured-coverage import.** (NDepend coverage import; goldbergyoni LCOV
   parser.) Ingest Coverlet/VS/LCOV runs; annotate Covers edges measured vs.
   reachable. Closes the documented "reachable ≠ asserted" caveat; the delta
   (reachable-but-never-executed) is a new report.
4. **graph_diff as a first-class tool.** (Borrowed from ourselves — hand-rolled
   Compare-Object over saved graph JSONs twice as an extraction gate, 2026-08;
   roslyn-codelens-mcp breaking-change-vs-baseline confirms demand.)
5. **Content-hash incremental indexing + syntax-only fast tier.** (sdsrss
   Merkle/BLAKE3; roslyn-codelens-mcp stale-project hot reload — both MIT,
   implementations studyable.) Also solves the open style-gate item: fast
   per-commit C# nesting without a full compile.

## Tier 2 — table stakes worth matching

6. Token budgets / detail tiers per tool response (CodeFathom, sdsrss). The
   Janet envelope reports truncation; make it steerable.
7. Lexical FTS over the graph (field-wide BM25/FTS) — the discovery pillar
   applied to code; port Get-Research's scored-query design. Skip embeddings
   for now.
8. Dead-code report: zero incoming Calls AND zero Covers — graph answers it
   today; needs a named tool (sdsrss module_overview).
9. Conformance rules as queries (Code Atlas violation detection) — §12's
   invariant, user-declarable.
10. Mermaid subgraph rendering: render_tree × ConvertTo-MermaidEmbed (already
    in scripts\).

## Tier 3 — synthesis and long-term

11. **Plan → SurgicalEdit → prover verify.** The field pairs analysis with
    action; the house doctrine splits decide/perform. Composing
    Invoke-SurgicalEdit with method_body_diff yields analysis that gates its
    own edits — no one in the field has the closed loop.
12. Composable query language (openCypher subset / CQLinq) — leverage, big
    lift, later.

## Deliberately not borrowed

Cloud hosting, breadth-first 100+-language indexing, vector search as
identity. Each trades away local-first / compiler-grade / prove-don't-guess —
the identity the competitive survey says to protect.

Related: [[note.razorgraph-competitive-field]], [[note.razorgraph-mcp-server]],
[[pattern.discriminator-backends]], [[script.invoke-surgical-edit]].
