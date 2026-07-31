# RazorGraph JS graphing — scoping measurements and revised priorities

Measured (2026-07-31) what RazorGraph's client-side extraction actually recovers on
real codebases before building JavaScript graphing. The numbers overturned the
intuited plan: the dominant coupling is DOM selectors, not `data-*`; most client
code is inline in `.cshtml`, not in `.js` files; and the API-call rule that scored
84% on small personal projects scored **0%** on nopCommerce.

## Method

Regex counts over first-party JS and `.cshtml`. Codebases: ImageSelector and
ImageSelectorV2 (personal, classic jQuery, D:\Repos — **V2 is a successor of V1,
so they are one source, not two independent ones**), nopCommerce and OrchardCore
(shallow clones under `D:\Repos\_corpus` — kept for repeatability). Counts include
comments and string literals, so they are indicative rather than exact; the gaps
are an order of magnitude, which is enough.

## What the current extractor recovers

Today's `ClientAssetExtractor` is seven regexes, built around `data-*` attributes
and literal-prefix `fetch('/...')` URLs. Files are single nodes — no functions, no
internal structure.

| Coupling | ImgSel | ImgSelV2 | nopCommerce | Recovered |
|---|---|---|---|---|
| DOM selectors (`getElementById`, `querySelector`, `$('#…')`) | 13 | 62 | **2,362** | **0** |
| fetch/ajax call sites | 5 | 19 | **188** | 4 / 16 / **0** |
| Event handlers | 13 | 18 | 253 | 0 |
| Functions | 22 | 80 | 162+ | 0 (file = node) |
| `data-*` access | 21 | 26 | **5** | all |
| `import`/`export`/`require` | 0 | 0 | 16 | 0 |
| TypeScript files | 0 | 0 | 0 | — |

Against the markup side: nopCommerce renders 1,741 `id=` attributes and 363
`data-*` attributes across 978 `.cshtml` files.

## Findings

1. **The client tier is inline-first.** nopCommerce: 24 first-party `.js` files
   (3,716 LOC) vs **575 inline `<script>` blocks (14,533 LOC)**. ~80% of client
   code lives in the markup. Any design that only improves `.js` file analysis
   misses most of the problem.

2. **Inline blocks are not parseable JavaScript.** 1,220 Razor interpolations
   (`@Model...`, `@Url.Action(...)`, `@T(...)`) sit inside those blocks.
   `var u = '@Url.Action("X","Y")';` happens to survive a JS parser (inside a
   string); `@if (...) { ... }` wrapping a block does not. The inline tier needs
   Razor-expression handling *before* an AST is reachable — so the established
   architecture (real parser primary, regex fallback when it throws, equivalence
   tests between modes) is load-bearing here, and the fallback will fire often.

3. **The API-call rule is a Razor problem, not a constant-propagation problem.**
   0 of 188 ajax/fetch sites in nopCommerce use a literal `/...` URL. Real code
   writes `$.ajax({ url: '@Url.Action("GetItems","Catalog")' })` — 53 such sites,
   each an *exact* server→client edge (controller + action, named on the server
   side). Highest-value edges in the graph; currently all rejected.

4. **`data-*` is the rarest mechanism, and the extractor is built around it.**
   5 uses in an 18k-LOC client tier, vs 2,362 selector sites. Keep it, demote it.

5. **The vendor filter is ~97% wrong on real layouts.** The `\lib\` + `.min.`
   rule admits 105k LOC as "first-party" in nopCommerce, of which ~102k is
   `wwwroot\lib_npm` (moment locales ×137, elfinder, summernote, globalize).
   One unmatched directory name. Must be fixed before anything downstream, or
   every stage burns its budget on vendor code.

6. **Scale is a non-issue; modules are unjustified so far.** ~18k LOC of real
   client code total — no lazy parsing needed. 16 import/export/require sites and
   zero TypeScript across all three codebases; ESM resolution buys nothing yet.
   Revisit when the corpus grows (OrchardCore is the planned counter-example).

## Revised priority order

1. Fix vendor detection (`lib_npm`, general "directory of vendor packages"
   heuristic). Cheap, blocks everything.
2. Selector ↔ `id=`/class contracts, JS side and markup side. The dominant
   coupling everywhere measured; extends the mismatch report the same way
   unbound `data-*` keys work today.
3. `@Url.Action`/`@Url.Page` inside inline scripts → controller/action edges.
   53 exact edges in nopCommerce alone; needs finding 2's inline handling.
4. Function-level nodes in JS (real parser — Acornima is the candidate, verify
   before committing). Fixes "somewhere in this file" granularity.
5. `data-*` stays as-is.
6. Modules/ESM/TypeScript: deferred until the corpus justifies them.

Parser decision follows the Razor precedent already on record: syntax API
primary, text scan fallback, never rewrite back to regex-only.

## History

2026-07-31: initial scoping. Corpus = 2 personal apps + nopCommerce.

## OrchardCore (added 2026-07-31, the deliberately-modern pole)

581 js / 25 ts / 1,611 cshtml. The structural opposite of nopCommerce, as intended:

- **Sources live outside wwwroot.** 159 JS + 20 TS under per-module `Assets\`
  directories, each with its own `package.json`; `wwwroot` holds *generated*
  triplets (`x.js` / `x.map` / `x.min.js`). Graphing the unminified shipped copy
  is a workable proxy for classic modules, but it is not the source for TS- or
  Vue-compiled files — "graph sources vs graph shipped" is now a live design
  question for the bundled pole, parked with ESM.
- **The modern pole moves code out of the markup.** Inline tier is 209 blocks /
  8k LOC against 41k LOC of sources — the inverse of nopCommerce's 80% inline.
  Inline handling still matters (598 selectors, 434 Razor interpolations there),
  but file-side analysis carries more of the weight here.
- **Selector-first holds at both poles.** Sources: 339 selectors vs 81 `data-*`
  vs 35 fetch/ajax (0 caught by the literal-URL rule — that finding also holds).
  With V1/V2 counted as one source, this is now three independent codebases
  agreeing, priority 2 confirmed.
- **ESM/TS deferral survives, weakened.** 42 import/export, 25 TS files, 21
  Vue/Alpine mounts — real but modest, and all of it compiles into scannable
  shipped JS. Deferral stands for graphing shipped output; revisit if
  sources-not-shipped becomes the target.
- **Vendor detection found its first generalization gap.** The Resources module
  re-ships bootstrap/codemirror/jquery from `wwwroot\Scripts`, with the manifest
  in `Assets\package.json` — the root-only manifest search read nothing and 176
  vendor files sailed through. Fixed (manifests now discovered one level down);
  all three corpus layouts verified clean afterwards.

## History

2026-07-31: initial scoping. Corpus = 2 personal apps + nopCommerce.

2026-07-31 (evening): **priority item 2 landed** — the selector ↔ id contract, five
commits (`39da965`…`edd1d44`). JS side: literal ids from getElementById /
querySelector / jQuery, with three honesty rules (whole-literal arguments only,
dynamic call sites counted not guessed, self-created ids excluded from the
contract). Markup side: literal `id=` values plus asp-for's generated ids; dynamic
`id="@..."` counted. Graph: `DomSelectedBy` edges per page composition,
`unboundSelectorIds` on script nodes with three suppression rules, mismatch report
extended. JS comments stripped before all scanners after a fixture comment created
a phantom contract (one commit went in red over this; fixed forward in `edd1d44`).
Acceptance on the corpus: nopCommerce 497 selector ids extracted / 384-of-496
foreign ids bindable against 2,382 rendered ids; ImageSelectorV2 31/31 bindable
with zero dynamic sites — a clean codebase closes its contract completely. Items
3 (@Url.Action edges) and 4 (function nodes / real parser) still open.

2026-07-31 (later): priority item 1 landed in RazorGraphTool. Vendor detection now
matches whole path segments (the substring bug), plus npm `@scope` dirs, manifests
shipped inside wwwroot, and package-drop evidence from the root package.json
(children matching ≥2 dependency names — the manifest-less lib_npm case). Detection
is separate from policy: `--include-vendor` / `includeVendor` keeps vendor files as
nodes marked `vendor:true` for bug-hunting inside shipped bundles, and every drop is
reported (stderr + `skippedVendorAssets` in MCP build responses). Verified against
the live nopCommerce layout: 21 first-party assets kept, 481 vendor dropped, zero
lib_npm leak. Items 2+ (selector contracts, @Url.Action edges, function nodes)
still open.

Related: [[note.razorgraph-mcp-server]], [[pattern.graph-first-analysis]].
