# What else in RetirementCore belongs in Janet.Shared

Surveyed 2026-09-16. Janet.Shared exists to hold machinery extracted from apps
that had already built it twice; RetirementCore is the app that supplied
Janet.Coalescing. This is the sweep for what ELSE it holds, done once so it is
not done again from scratch.

## Method, and why it was two passes

Two lenses ran concurrently against the same repository.

A PROSE pass read the design documents (`OVERVIEW.md`, `DESIGN.md`,
`DESIGN-COALESCING.md`, `DESIGN-DRIFT.md`, `DESIGN-UX.md`, `improvements.md`,
`CLAUDE.md`), the catalog and the thread items. Authors who write down WHY a
technique exists have already done most of the extraction judgement, and the
failure attached to a pattern is the part worth carrying.

A STRUCTURAL pass built a RazorGraph graph (`graphId retirementcore`, 4,002
nodes and 51,966 edges over 7 projects) and censused which non-test types carry
no dependency on retirement-domain types, reading method node ids because those
carry parameter types and parameter types are what settle coupling.

**Running both is the finding.** A document says what a thing IS; a compiler
says what it DEPENDS ON, and those are different questions. Two candidates the
documents described as clean general machinery turned out to be welded to the
domain at the signature level (below). A single pass in either direction would
have been confidently wrong in one direction or blind in the other.

## Candidates, ranked

**1. `PageTemplateHarness` + `BindingFailureListener` + `WpfApplicationFixture`**
(`RetirementCore.App.Tests\PageTemplateHarness.cs` 18-171,
`AccountsPageTemplateTests.cs` 236-301). Realises a `DataTemplate` against a
view model off a `ContentControl`, pumps the dispatcher, walks descendants and
fails the test on any WPF binding error. Signatures are `ContentControl`,
`FrameworkElement` and `DependencyObject` only -- fully domain-free. Would be a
new `Janet.Wpf.Testing`.

This is the strongest candidate because of what it is ABOUT. A WPF binding
error is written to debug output and nowhere else: the app runs, the control
renders, the value is silently absent. That is the same failure class
`CrashHardening` exists for -- WPF's default for an unhandled dispatcher
exception is silent process exit -- and Janet.Wpf is already the home for
turning WPF's silent failures loud. It also ships with
`TheHarnessActuallyDetectsABrokenBinding()`, a mutation check on the harness
itself, so its author had already internalised that a gate which has never
fired is not a gate.

**2. `StreamSeed`** (`RetirementCore\Engine\StreamSeed.cs` 32-80). Derives an
RNG seed from scenario seed, path index and stream NAME via FNV-1a plus a
SplitMix64 finaliser. Stream identity is name-keyed and value-independent, so
editing an input reshapes a stream's transform but never its underlying draws:
common random numbers. `OVERVIEW.md` section 3.6 states the payoff -- "Comparing
two runs shows you the effect of your edit, not the effect of your edit plus a
different draw of the dice." The finaliser is not decoration; `DESIGN.md`
section 9 records that seeded `Random` uses a subtractive generator whose
sequences correlate on adjacent raw seeds. Fully domain-free despite living in
`Engine`; currently `internal`. Both passes ranked it top-two independently.
Would be a new small package.

**3. The deterministic-testing diff.** RetirementCore's `FakeTime` and
`DeterministicPump` (`RetirementCore.App.Tests\DeterministicScheduling.cs`)
carry `ScheduledTimerCount`, `Remove(FakeTimer)`, `Run<T>(Func<T>)` and
`RunUntilIdle()`, plus a `CoalescerHarness` (lines 10-48) wiring pump, clock and
coalescer together. Janet.Coalescing.Testing may lack these. Cheap, concrete,
and improves a package that already ships.

**4. Charting.** `ChartPalette` (`RetirementCore.App\Theming\ChartPalette.cs`
14-80) resolves semantic theme tokens into SkiaSharp colours, because LiveCharts
paints cannot consume `DynamicResource`. Every member is
`(string|int|SKColor) -> SKColor`. With it: the nine domain-free members of
`ChartBuilders` (`CountAxis`, `MoneyAxis`, `MoneySpanAxis`, `MonthAxis`,
`MoneyLabel`, `TooltipMoney`, `TooltipMonth`, `LabelMonth`, `Marker`),
`HistogramChart`, and `LiveChartsWarmup` -- a one-liner guarding a real defect,
recorded in `DESIGN-COALESCING.md` section 8 as found rather than suspected:
LiveCharts' global init is not thread-safe, parallel first-series construction
corrupts it, and the resulting `TypeInitializationException` poisons every
chart-touching test in the run.

**BLOCKED, and this is the reason to read this entry before starting.**
`ChartPalette` reacts to `ThemeService.SkinChanged`, and Janet.Wpf's
`ThemeService` DELIBERATELY REMOVED that static event -- its own documentation
says "The reliable way not to leak a static event is not to have one", and
`DESIGN-COALESCING.md` section 7 logged the same leak independently. Extraction
must carry the replacement mechanism, which section 8 describes as invalidating
only the cells whose artifacts carry palette brushes. It also resolves keys out
of XAML dictionaries (`Themes/*.xaml`) that nobody has diffed against
Janet.Wpf's, so a move needs those dictionaries or a stated fallback contract.

**5. `Money`, `TimePoint` and `MonthRange`** (`RetirementCore\Models\`).
Domain-free but domain-adjacent: integer-cent money with a rounding-bias
argument worth carrying verbatim -- "round-to-nearest leaves a mean error of
zero... Truncation is the failure mode -- its errors all point one way and
accumulate" -- and a guard recording that casting infinity or NaN to `long`
yields an unspecified value, so a bad rate previously corrupted a balance with
no signal at all. `MonthRange.Collapse` requires strictly ascending input and
enforces it rather than assuming, because out-of-order ordinals "would not fail
naturally, they would quietly yield overlapping or misordered ranges that still
look plausible".

The practical caveat is blast radius: 269 incoming edges on `Money`, 204 on
`TimePoint`. This is a lift-and-reference-back job, not a cut. `MoneyFormat` and
`MonthText` become liftable for free if these move.

**6. Smaller items.** `StatTile` (record struct, 12 incoming references, pairs
with `PageScaffold`), `TimelineLane` + `LaneSegment` (a custom
`FrameworkElement` that `OnRender`-draws labelled segments), `RecentFiles` (a
capacity-bounded MRU persisted as JSON), and `AutosaveStore` generalised to
`AutosaveStore<T>` with an injected serializer -- its only coupling is a typed
payload and a call to `ScenarioLoader.Load`.

## Where the two passes disagreed

**`FaultRouting`** reads in the documents as a general fault-to-page router, and
the reasoning is good: `OVERVIEW.md` section 3.7 says faults route through a
single table "so the badge count and the page's rendered explanation cannot
disagree -- a test asserts they agree", and `DESIGN-UX.md` section 2 records the
failure that motivated severity gating, where conflating two kinds of fault
"used to disable Run and blank the results page over an authoring nicety".

The graph read its signatures: `BadgeFor(PageId, ScenarioValidation?)`,
`Describe(ScenarioFault)`, `DescribeCoverage(Engine.TimelineFault)`,
`Owner(ScenarioFaultKind)`. Every one names the domain. The PATTERN is
liftable; the TYPE is not. `ScenarioFault` itself looks like a generic fault
envelope and is a domain coordinate -- its nine constructor parameters are
account, strategy, draw, band and sleeve indices.

**`ScenarioSession`** tells the same story. The documents describe undo/redo as
"a stack of immutable Scenario snapshots -- the records are already immutable,
so undo is a pointer move, not a command pattern", which is general. The type is
welded to `Scenario`, so it is an `UndoStack<T>` rewrite rather than a move. Its
`AutosaveStore.Write` also does a synchronous `File.WriteAllText` on the UI
thread on every adoption, so extracting it as-is would move a known defect into
a library.

## A correction worth keeping

The coalesced `IProgress<T>` adapter that `DESIGN-COALESCING.md` section 2
describes -- folding progress with `Max()` because `Interlocked.Increment` hands
out unique values that parallel workers post out of order, so a displayed count
can run BACKWARDS (500, then 499) -- appears NOT TO BE BUILT. The structural
census found no such type, and the document describes the dispatcher-starving
path as what runs today ("the per-path `progress.Report` currently marshals
~10,000 dispatcher operations per run").

The gap it names is real and sits in Janet.Coalescing: that package's README
argues at length against `Progress<T>` on a hot path and ships no replacement.
But closing it is a BUILD, not an extraction, and the invariant to honour is
stated in the same section -- the final delivered value equals the path count,
so conflation must not lose the last report.

## Direction reversal

RetirementCore has `CrashLog` and NO `CrashHardening`; its hardening is inline
in `App.xaml.cs`. It is a CONSUMER candidate for Janet.Wpf, not a source of one.

## Not candidates, so nobody re-checks

All of `improvements.md` (tax engine, income streams, market model, RMDs, LTCG
stacking, IRMAA) is retirement-domain, and the file is a gap analysis rather
than a pattern catalogue. All of `DESIGN-DRIFT.md` is financial AND unbuilt --
its sections D0 and D1 are unstarted -- however general its reasoning reads. The
`Curve` expression language is unspecified and unbuilt, and `improvements.md`
recommends dropping the DSL for named curves. `PathExecutor` and `PathLedger`
implement a genuinely general shape -- confined mutation behind a pure boundary,
pimpl-style -- but it is a shape, not a type to lift.

`RetirementCore.Cli` is 97 lines of top-level statements with hand-rolled
argument switching and no classes; notably it calls INTO `RetirementCore.Web`,
which shares `Responses.cs` between the two front ends. `RetirementCore.Web` is
a 77-line minimal API with two reusable ideas and no reusable types: an API-key
gate that engages only when the setting is non-empty, and a `ProcessorCount - 1`
parallelism default overridable by config.

There is NO golden, snapshot or approval testing anywhere in that repository,
and no built drift-detection machinery. Nothing there extends Janet.Goldens.

## Not verified

Whether RetirementCore's `ThemeService`, `GridSortMemory`, `ButtonChrome`,
`PageScaffold` and the two converters differ from Janet.Shared's -- they were
identified as present and not diffed. The XAML resource dictionaries are not
indexed as code, so the theme-key contract that `ChartPalette` and
`TimelineLane` depend on is unchecked. Neither pass read the C# of candidates 1,
4 and 6 line by line; domain-freedom there rests on signatures and file headers.
