# RazorGraph JS graphing — scoping measurements and revised priorities

Measured (2026-07-31) what RazorGraph's client-side extraction actually recovers on
real codebases before building JavaScript graphing. The numbers overturned the
intuited plan: the dominant coupling is DOM selectors, not `data-*`; most client
code is inline in `.cshtml`, not in `.js` files; and the API-call rule that scored
84% on small personal projects scored **0%** on nopCommerce.

## Method

Regex counts over first-party JS and `.cshtml`, three codebases: ImageSelector and
ImageSelectorV2 (personal, classic jQuery, D:\Repos), and nopCommerce (shallow
clone, `D:\Repos\_corpus\nopCommerce` — kept for repeatability; corpus will grow).
Counts include comments and string literals, so they are indicative rather than
exact; the gaps are an order of magnitude, which is enough.

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
