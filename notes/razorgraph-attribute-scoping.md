# RazorGraph attribute graphing — census, oracle method, and one known defect

Measured (2026-08-17) how C# attributes are actually used across five corpora
before designing the attribute vocabulary that shipped the same day (DecoratedBy,
Registers, ExternalType, Parameter — formatVersion 1.1). Unlike the JS and Lua
scoping notes, these counts are **not regex**: they come from a Roslyn
syntax-only parse, so they are exact over what they count — which is what later
let the census graduate from scoping input to **verification oracle**, with every
increment reconciled against exact expected numbers rather than eyeballed.

## Method

`scripts\AttributeCensus.cs` (preserved beside this note): a .NET 10 file-based
app on Roslyn 5.6.0 — deliberately the same version the extractor compiles with —
parsing every `.cs` under a root (`bin`/`obj` excluded by path) and walking
`AttributeListSyntax` with its target specifier, argument shapes, and a
same-corpus declared-attribute match. Needs
`#:property JsonSerializerIsReflectionEnabledByDefault=true`. It never builds a
compilation, which is the point twice over: it runs on anything checkout-shaped,
and it is a completely independent route from the extractor it later verifies —
agreement between the two is evidence, not tautology.

## Census

8,047 usages over 5,927 files:

| corpus | usages | distinct types | method | property | class | param | assembly | field |
|---|---|---|---|---|---|---|---|---|
| RazorGraphTool | 553 | 13 | 456 | 13 | 13 | 69 | 0 | 0 |
| RetirementCore | 646 | 7 | 546 | 0 | 0 | 0 | 0 | 100 |
| DriveSurvey | 479 | 11 | 414 | 0 | 4 | 0 | 0 | 49 |
| ImageSelectorV2 | 577 | 27 | 462 | 6 | 70 | 38 | 0 | 0 |
| OrchardCore | 5,792 | 113 | 3,791 | 789 | 608 | 55 | 483 | 52 |

## Findings that drove the design

1. **Distinct types are tiny; usages are not** (7–113 types buy 479–5,792
   usages). Node-per-type + edge-per-usage confirmed as the cheap shape.
2. **50–80% of attributes are xUnit plumbing** (`Fact`+`Theory`+`InlineData`),
   and `[InlineData]` is the worst payload (1,300 in OrchardCore, all test
   data). This is why argument-payload suppression exists as *policy data*
   (attribute-policy.json) rather than a hardcoded denylist — default is
   uniform emission; narrowing is a visible line in a file.
3. **Parameter attributes are rare but entirely signal** (0–69 per corpus, zero
   test noise): binding surfaces (`FromQuery`/`FromForm`), and RazorGraphTool's
   own 69 are all `[Description]` on its MCP tool parameters — the tool's
   published schema. This justified Parameter nodes emitted *only when
   decorated*: absence means undecorated, never unmodelled.
4. **Constructor parameters are essentially never attributed** (2 primary-ctor
   usages in one corpus, 0 in four). The DI conclusion this measurement first
   produced was wrong and was corrected by Lori: attributes still help DI, but
   via *registration lookup* (class-level `[RegisterDependency<T>]`-style
   declarations + registration call sites) plus the container's documented
   constructor-selection rules — not via attributed parameters. The corrected
   mechanism lives in note.razorgraph-mcp-server's caveat.
5. **Assembly attributes are an OrchardCore story and an obj\ trap.** 483 there
   (the `[assembly: Module]`/`[assembly: Feature]` manifest), 0 hand-written in
   all four local repos — but the SDK generates ~10 per project into `obj\` as
   plain `.cs` (not `.g.cs`), so a compile-based extractor must gate on the
   `obj` path segment, not the suffix. Shipped as
   `GeneratedCodeMap.IsGeneratedSite`; the census's own `obj` exclusion is what
   made "zero" an honest oracle.
6. **Arguments are overwhelmingly strings; the tail is the value.** OrchardCore:
   positional string 3,579 (61%), named string 925, enum member-access 591,
   arrays 59 + C# 12 collection expressions 149, `typeof` 27, generic
   attributes in real use (`RegisterDependency<T>` ×35 in ImageSelectorV2).
   Extraction went semantic (`TypedConstant`), which dissolves two of the three
   hard cases: both array spellings are one Array kind, and enums arrive
   resolved. `typeof`/generic type arguments became the `Registers` edge.
7. **Multi-attribute `[A, B]` lists are real but rare** (0/0/0/8/73). Handled
   (dedup key carries line + source + type args), not designed around.

## The census as verification oracle

Every shipped increment reconciled exactly against census numbers, and the two
divergences found were both *findings*, not rounding:

- **Whole-tree vs solution scope.** The first oracle (13 types / 482 edges) was
  wrong because the census scanned the tree while the build compiles only .slnx
  projects — `tests\fixtures\` holds the repo's only ApiController/Route/
  HttpGet/BindProperty usages. Rescoped to `src\`, the count closed exact:
  481 = 474 + 2 (record `[property:]` attributes land on the PROPERTY at the
  semantic layer — syntax cannot see that) + 5 (compiler-generated sources: the
  census excluded `obj\` by path; the compiler does not).
- **ImageSelectorV2 field check of Registers**: 40 edges = 35
  `RegisterDependency<TInterface>` (census figure exact) + 5 the census's
  matcher defect hid (3 non-generic `RegisterDependency` via named
  `ServiceType = typeof(...)`, 2 from one `RegisterFactory<,>` usage).

## Known defect (recorded, not smoothed over)

The corpus-declared matcher appends `"Attribute"` to a name that already
carries `<...>`, so generic attributes never match the same-corpus declared
set. ImageSelectorV2's `usagesOfCorpusDeclaredAttributes` reads **9**; the
truth is ~45 (the RegisterDependency/RegisterFactory family alone accounts for
40 field-verified usages). The per-target and per-type counts are unaffected —
only the "declared in this corpus" split undercounts. Fix is a name
normalization before the suffix append, in `scripts\AttributeCensus.cs`.
