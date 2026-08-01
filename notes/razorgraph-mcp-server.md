# RazorGraph MCP server — graph-first analysis, rebuilt as civilian tooling

The DESIGN-NOTES §4 pattern ("expose the compiler's model as agent tools") now has a
working implementation again: `D:\Repos\RazorGraphTool` gained a `RazorGraph.Mcp`
project (2026-07-27) — an MCP stdio server wrapping the RazorGraph code-graph engine
(Roslyn + Razor extraction over ASP.NET Core apps). Clean-room: built on the public
RazorGraphTool codebase and the official `ModelContextProtocol` NuGet SDK (1.4.1);
nothing derived from the employer MCP implementation.

## Shape

- Many graphs at once, held in a `GraphStore` keyed registry. Every tool takes an
  optional `graphId` and falls back to the most recently added, so a solution graph
  and a single-project graph of the same code can be compared without rebuilding
  either.
- 18 tools: `build_graph` (one csproj via MSBuild+Roslyn, slow) and `build_solution`
  (every project in a solution — slower, and the only way to get edges that cross a
  project boundary), `load_graph` / `save_graph` (JSON round-trip, fast),
  `list_graphs` / `drop_graph`, `graph_summary`, `find_nodes`, `get_node`,
  `render_tree`, `page_context`, `trace_data_flow`, `find_path`, `covering_tests` /
  `covered_methods` / `uncovered_methods`, `find_server_to_js_mismatches`,
  `research` (relevance-scored subgraph, score 1/(1+depth), returned inline).
- Traversal takes a direction (outgoing / incoming / both) and descends containment
  without spending depth — call edges hang off Method nodes, so a class-level trace
  that cannot descend for free reports nothing.
- Janet conventions carried over: compact JSON results (consumer is a model), the
  `{ returned, totalMatches, truncated }` envelope on searches, hard errors instead
  of silent drops (unknown focus ids fail the research call).

## Operational notes

- stdout is the protocol channel; all logging is forced to stderr
  (`LogToStandardErrorThreshold = Trace`). GraphBuilder's warnings already go to
  stderr, so builds don't corrupt the stream.
- MSBuildLocator registration happens in RoslynExtractor's ctor — no special
  startup hook needed in the server.
- Registered for Claude Code via `.mcp.json` in the RazorGraphTool repo root
  (points at the Debug exe; rebuild before use, or switch to a published binary).
- `research`, `trace_data_flow` and `find_path` take a direction. It still defaults
  to outgoing, so a service `InjectedInto` a PageModel is NOT pulled into a
  page-focused research doc unless you ask for `incoming`. Same default as the CLI.
- Coverage is call-graph reachability to depth 3, not runtime coverage: a covered
  method is one some test can reach, not one a test asserted on. Edges are only
  emitted across a project boundary, so `build_solution` is required — a
  single-project graph can never contain one.

## Planned: attribute-driven DI detection (2026-08-01)

Lori's codebases register services via attributes — `[RegisterDependency<TInterface>]`
(multiple attributes = forwarding descriptors, one instance across interfaces),
`[RegisterHostedService]` (forwards IHostedService to the primary registration),
`[RegisterFactory<TFactory,TCreates>]` — wired by
`AddDependenciesFromAttributes(assembly)` in
`ImageSelectorV2\ImageSelectionTools\Attributes\RegisterDependencyAttribute.cs`.

The extractor should read these: a class carrying `RegisterDependency` is a
ServiceImplementation of the attribute's type argument (or its first interface),
with lifetime metadata on the node. Constructor parameters should count as
injections **only when the parameter type is itself registered** — which also
fixes the record-ctor-params-as-injectedServices misclassification as a side
effect, since DTOs are never registered. Per Lori: an *option* in the tool
(attribute conventions are per-codebase), but the default in our repos.

## History

Updated 2026-07-30. The server and the extractor work it depends on sat uncommitted
in the working tree for two days; it is now five commits on `main` (`694377d`
core traversal → `f3fdb12` the server itself), each building and passing its tests.
This note had drifted in the meantime — it claimed 12 tools and one active graph —
which is DESIGN-NOTES §1's own argument about quietly-degrading documents landing on
one of my own notes. The `research.json` node stayed accurate; the prose did not.

Related: [[pattern.graph-first-analysis]], `meta-second-brain-vs-janet.md`.
