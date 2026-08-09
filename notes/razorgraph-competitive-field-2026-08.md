# RazorGraphTool vs. the MCP code-graph field (2026-08-07 survey)

Web survey of competing MCP code-analysis servers, run while renaming the tool.
Question: is the tooling adding something the field doesn't already have?
Answer: yes — two capabilities with no competitor found anywhere, one near-unique,
a crowded commodity layer, and named gaps. Details and URLs below.

## Apparently unique (no MCP-accessible equivalent found)

- **The flow-equivalence prover (`method_body_diff`) as a refactoring gate.**
  Bisimulation equivalence checking exists only in research tooling — SymDiff
  (Boogie IR), DiffKemp (LLVM IR), REM2.0 (Rust) — none MCP, none C#/Roslyn,
  none positioned inside an agent loop. NDepend's `diff-sources` is baseline
  text/rule comparison, not a proof. This is the clearest moat.
- **Razor/PageModel/JS cross-boundary correlation.** Page→PageModel
  correlation, `page_context`, server-to-JS mismatch detection, wwwroot/JS
  assets and inline scripts as graph nodes. Everyone else's ASP.NET support
  stops at HTTP-route extraction (Code Atlas, sdsrss).

## Rare (few players; we're at the strong end)

- **Coverage-as-reachability.** One other implementer of the static idea:
  roslyn-codelens-mcp (`find_tests_for_symbol` / `find_uncovered_symbols`,
  transitive). Everything else is dynamic instrumentation (NCrunch, dotCover)
  or LCOV parsing (goldbergyoni/test-coverage-mcp). Depth-per-Covers-edge and
  the covered/uncovered/covering_tests triad over a persistent graph appear
  unique as a formulation.
- **Compiler-grade .NET binding.** Not unique: roslyn-codelens-mcp does full
  MSBuildWorkspace compilation (~67 tools, DI registrations, change impact,
  data/control flow, refactoring execution, IL, breaking-change detection);
  CodeGraphContext gets Roslyn-grade symbols via scip-dotnet; NDepend analyzes
  compiled output. Our edge inside this club: the graph itself (DI edges,
  cross-project calls, Razor/JS nodes) is the persistent queryable product.

## Commodity (crowded; no differentiation)

Call graphs, find-references, impact analysis, path tracing, persistence,
dependency/structure queries — sdsrss/code-graph-mcp (tree-sitter, 19 langs),
DeusData/codebase-memory-mcp (158 grammars + heuristic type layer; its
"coverage" is parse coverage, not test coverage), CodeGPT DeepGraph (cloud),
CodeFathom (retrieval/embeddings only), Code Atlas (diagram-first, 51 tools).
Tree-sitter competitors cannot resolve overloads/interface dispatch/generics —
DeusData's own README concedes this.

## Where the field is ahead

- Language breadth (158/23/19/40+ vs. our C#/Razor/JS) — consistent with the
  Lua-next direction being the right one.
- Incremental indexing speed (sdsrss: Merkle-hash re-index <250 ms; DeusData:
  28M LOC in 3 min). Full Roslyn compile can't match; hot-reload of stale
  projects (roslyn-codelens-mcp) is the mitigation to copy.
- Semantic/vector search — we have none; the field treats it as table stakes.
- Token-economy engineering (budgeted responses, context compression tiers).
- Composable query languages (DeusData openCypher subset; NDepend CQLinq) vs.
  our fixed tool surface.
- Actionability: others apply renames/fixes; we observe and prove only.

## Closest competitor

MarcelRoozekrans/roslyn-codelens-mcp — full Roslyn compilation, testing
intelligence, refactoring execution. Lacks: Razor/JS analysis, equivalence
prover, persistent graph, depth-annotated Covers edges. Watch it.

## Key URLs

codefathom.ai · codeatlas.live · github.com/sdsrss/code-graph-mcp ·
github.com/DeusData/codebase-memory-mcp · github.com/JudiniLabs/mcp-code-graph ·
github.com/MarcelRoozekrans/roslyn-codelens-mcp ·
github.com/ndepend/NDepend.MCP.Server · github.com/oraios/serena ·
github.com/codegraphcontext/codegraphcontext ·
github.com/carquiza/RoslynMCP · github.com/goldbergyoni/test-coverage-mcp

## Openness audit (same day)

Of 16 surveyed: **9 genuinely open** (MIT/Apache, full source, nothing gated):
sdsrss, DeusData (full C source in-repo — the "single static binary" phrasing
was just convenience releases), giauphan/codeatlas-mcp (npm; possibly a lighter
cousin of the closed codeatlas.live site product), carquiza/RoslynMCP,
**roslyn-codelens-mcp (MIT — the closest competitor is fully open, all 67
tools)**, codegraphcontext, goldbergyoni/test-coverage-mcp, SymDiff (MIT,
dormant), DiffKemp (Apache-2.0, active, C/LLVM only). **3 open-core**: NDepend
(MIT shell over mandatory commercial engine), Serena (MIT core, paid JetBrains
backend), JudiniLabs (MIT client, closed DeepGraph cloud does the actual graph
building). **3 closed**: CodeFathom (proprietary, $15–29/seat, no free tier),
Horokhov VS extension (closed freemium, "Public" repo is docs-only),
codeatlas.live site product (no license or repo visible). **1 trap**:
egorpavlikhin/roslyn-mcp has no license file — source-visible but legally
all-rights-reserved.

Prover-specific: the only prover-like code anywhere is permissively licensed
research (SymDiff MIT, DiffKemp Apache) but none is MCP-shaped or C#-capable;
REM2.0 — the only modern refactoring-gate prover — is paper-only, no released
code (arXiv 2601.19207). **No usable open implementation of the equivalence-
prover capability exists in the field.**

## Strategic read

Invest to stay distinct: the prover and the Razor correlation. Invest to catch
up: incremental indexing, language breadth. The naming exercise converged with
this: Plumbline (chosen candidate, collision-free 2026-08-07) names the
verification instrument, and verification is the moat.

## Added 2026-08-09: Modelcode.ai — the process-wrapper pole

Different category (enterprise migration *service*, not a queryable substrate),
but premise-adjacent and worth keeping in view. Their Morph product independently
rediscovered the Janet operating shape: reviewable Project Spec before
generation (§5's plan-then-execute), milestone PRs with human gates, automated
review before human review, "Project Knowledge" that compounds across milestones
(§10's handoff corpus, productized), on-prem ModelDaemon. Meta's Second Brain hit
§9 independently; Modelcode hits §5+§10 — convergent evolution is evidence the
patterns are load-bearing.

The instructive gap: their entire correctness story is **behavioral sampling**
(side-by-side E2E of original vs. migrated app, functional tests) — forced,
because C++→Go / Python→Java have no shared compiler model, so proof is
unavailable to them. Inside .NET the prover strictly dominates that layer.
Their existence validates verification-as-product without closing the prover
gap. Borrowable: the side-by-side behavioral comparison as a *complement* to
structural proof, and spec-frozen-after-chat-refinement as a migration ritual.
Caution: "knowledge compounds, reducing corrections" with no re-validation story
is the drift trap at enterprise scale.

Long-term direction this feeds: [[note.razorgraph-cross-language]] — JS→Razor
conversion with contract/structural/behavioral verification planes.

Related: [[note.razorgraph-mcp-server]], [[pattern.graph-first-analysis]].
