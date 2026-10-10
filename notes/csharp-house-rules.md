# C# and Blazor house rules

Rules for C# and Blazor code in the house repos, each one written because it broke
something or because the owner decided it. Started 2026-10-09 from the Neelam campaign
tool, where the owner said: "there are a bunch of things I'm concerned about with the
coding style and I want to get some house rules in."

As with `note.powershell-house-rules`: prefer a test or an analyzer that enforces a rule
over a paragraph here. A style guide nobody runs degrades quietly.

**Status of this file.** Part 1 holds rules the owner has decided. Part 2 holds
candidates gathered for the owner to decide; nothing in Part 2 is a rule yet, and no
code is refactored to it until it moves to Part 1.

---

## Part 1 -- Decided

### 1. A flaky test is a priority-1 fix

Decided 2026-10-09: "We should treat flaky tests as pri-1. This may seem trivial, but
letting tests go is how we get disasters." A test that fails and then passes on rerun is
not a pass. Stop, find the root cause, fix it, and sweep for the same pattern. Never
retry, skip or quarantine it away.

Why it bit: a Neelam page test asserted that "PDT" appeared nowhere in a rendered page.
Every Blazor page carries about 2,000 characters of fresh random base64 (the prerender
descriptor and component-state comment), so a three-letter needle matched by chance in
about 1 page in 130 and the test failed about once in 70 runs. Fixed in Neelam 021992b.

### 2. Page checks target named elements

Decided 2026-10-09: "Checks on pages should know what element they are looking for.
These should have individual names that can be located." An assertion about a page reads
one element found by a stable, unique name (`data-testid="last-saved"`, kebab-case,
unique per page), never a substring of the whole HTML. Whole-page "nowhere on the page"
checks read the visible text, not the markup. Neelam's `RenderedPage` test helper
(`Named`, `Text`, `Links`) is the reference shape. Accessible names double as locators,
so this rule and accessibility work pull the same way.

---

## Part 2 -- Candidates (the owner decides)

### A. Text and catalogues are data, with one source each

Example: Neelam `BlockGuide.All` -- the owner: "looks like data disguised as code". It
holds user-facing block descriptions as C# strings, prose restating each rule with
nothing tying it to the rule's logic, and rule ids as hand-copied strings policed by a
test. It was edited four times in one day, once per new rule. Suggested shape: each rule
carries its own id, plain description and the block types it applies to, beside its
logic, and guides are generated from the rules; user-facing descriptions become content
(a validated data file, or operator data editable without a deploy); behaviour stays in
code.

### B. Results that cross a method boundary are named records, not tuples

The owner: "I have grown to love records because they are small and read only", and
"I'm okay with some callers not needing things" -- so a caller discarding part of a
result is fine. See `note.tuples-do-not-cross-boundaries` for why tuples fail silently
in JSON. Neelam example: `ClientForm.ToRecord` returns a tuple.

### C. JavaScript only where a browser feature demands it

Hand-written JavaScript is fragile and hard to read. Use it only where no Blazor/.NET
feature can do the job (the clipboard, for example), keep it tiny, and consider
TypeScript behind one typed C# wrapper. Focus (`ElementReference.FocusAsync`) and
fragment navigation are built in. Neelam today: `copy.js` (clipboard, needed),
`arrive.js` and a `document.title` interop (replaceable).

### D. Layout is server-rendered; update on change, not per keystroke

Layout is Razor markup, never built in JavaScript at runtime. Interactivity is set per
page, where needed. Bind on change by default; per-keystroke updates only by deliberate
exception, with a debounce. The owner: "we don't want to hammer the server for every
character edit, but we also don't want page layouts to be entirely coded on the fly
using js." Neelam today: 59 per-keystroke bindings, no debounce, and every keystroke on
the campaign editor reruns every check and rebuilds the export JSON. When per-keystroke
updates are wanted on a page is a later conversation.

### E. Differences found between Neelam and ImageSelectorV2 (2026-10-09 survey)

Each is a candidate for one house choice:
- one type per file, versus several per file (Neelam `Campaign.cs` holds 15);
- folders by layer, versus by kind (`Interfaces/`, `Models/`);
- explicit DI registration, versus attribute scanning (`Janet.DependencyInjection`);
- xunit 2.9.3 everywhere (Neelam is on 2.4.2);
- test names as sentences, versus `Method_Scenario_Result` with Arrange/Act/Assert;
- hand-written fakes, versus NSubstitute;
- `.slnx`, versus `.sln` (Neelam is the only `.sln`);
- `var`, braceless one-line `if` and records, versus explicit types, braces everywhere
  and `this.`;
- `internal` by default with compiler-checked doc comments (Neelam has 115 public
  members that could be internal, and no doc-comment checking).

The full survey lives in the Neelam session scratchpad
(`style-survey-imageviewer-vs-neelam.md`); copy what the owner wants kept into this
note when deciding.
