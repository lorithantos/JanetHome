# Thread-item projection

Thread-item output has no projection contract. Every consumer gets every field of every
live item, so the startup capture is now **127,155 characters -- 97% of the entire
brief**, and 92% of that is parked items' `notes`.

Designed 2026-08-12, not built. This note describes an intended contract; check it
against the tool before relying on any of it.

## The measurement

Measured 2026-08-12 against the live list (26 items on disk: 1 active, 22 parked,
3 done; `Show-ThreadItems` prints the 23 live ones).

| Part | Chars | Share of brief |
|---|---|---|
| Whole brief | 131,098 | 100% |
| `captured.threadStack` | 127,155 | 97.0% |
| -- item `notes` | 117,365 | 89.5% |
| -- item `next` | 4,291 | 3.3% |
| -- item `topic` | 1,987 | 1.5% |
| Everything else in the brief | 3,943 | 3.0% |

~32,000 tokens, on every session start, before the session has been asked to do
anything. The topics and the active item's `next` -- 2,368 characters between them --
answer the question startup is actually asking, which is *what is parked and where was
I*.

**The durable claim is the ratio, not the number.** 89% of the brief is notes; the
absolute figure grows with every item added. Re-measure rather than quoting it: it was
123,059 earlier the same day, against 21 items.

## Third recurrence of a pattern already written down

`note.startup-brief-budget` records the same failure twice before this:

- **2026-08-01** -- the retrieval *pointer* had become a manual. 4,525 -> 2,105.
- **2026-08-08** -- the *read list* the trimmed brief pointed at had become a corpus.
  28,499 -> 12,300.

That note closes with the rule this one is the third instance of: *"Progressive
disclosure has to be re-applied at every layer that expands. A pointer becomes a
manual, a manual becomes a read list, a read list becomes a corpus. Each layer looks
small when you edit it and none of them has a reviewer."* It measured `captured` at 319
characters and called it "normally ~60". Eleven days later, 127,155.

`Invoke-JanetStartup.ps1` carries a comment predicting it too. Neither the note nor the
comment prevented it, which is the point: **a prediction is not a mechanism.** Nothing
in the emitter bounds what a `run` entry may capture, so the constraint lived only in
the discipline of whoever last added a thread item -- and adding a thread item is
supposed to be the cheap operation.

## The design

### 1. `area` is a stored field, never derived

Measured on the live list: **4 of 20 topics carry no colon at all**, and splitting the
other 16 on the first colon yields **12 groups** -- RazorGraph fragments into 4,
JanetHome into 3. Prefix-derivation does not group this data; it shreds it.

Unassigned items group under a literal `(unfiled)` and are never guessed into a
neighbour. A wrong area is worse than an absent one, because the roster is the only
thing a session sees by default.

### 2. Four projections, and the startup slot is not the default

The first cut of this design gave every consumer one summary. That was wrong: **the
startup capture and a direct call are different consumers.**

| Projection | Contains | Chars |
|---|---|---|
| `areas` (roster) | `{count, active, areas[]}` | ~162--400 |
| `summary` | every live topic + area + status + `hasNotes`/`refs` count, plus the active item's `next` | 4,444 |
| `area` | one area's items, at summary depth | -- |
| `item` | one item, in full, `notes` included | -- |

Measured against the current list: the roster is a **99.9% cut**, the summary **96.5%**.

- **Startup takes the roster.** Its job is a tripwire -- make the session aware
  something is parked and roughly where. Content comes from the tool on demand.
- **A direct call defaults to `summary`.**

### 3. The invariant is structural, not editorial

**Notes are returned one item at a time.** `-Expand` requires `-Topic` and errors without
it; `-Area X -Expand` is refused.

Written as a rule ("don't expand across a set") it would be re-broken by the next flag
someone adds, which is exactly how the current state arose. Written as a signature that
cannot express the bad call, the 117KB regression cannot come back.

### 4. The envelope self-describes

Every response carries `projection: "summary" | "area" | "item" | "areas"`, and every
summarised row carries `hasNotes` and a `refs` count.

A summary that silently drops notes is indistinguishable from items that have none.
That is the same absence-reads-as-real trap as RazorGraph's missing Property nodes: an
empty field means "not extracted" but reads as "nothing there". A projection that does
not name itself is a truncation that does not report itself.

## It subsumed the `-Index` bug -- RESOLVED 2026-08-14, by removal

`Show-ThreadItems` filtered `done` before printing; `Set-ActiveThread -Index` counted into
the unfiltered file. With 3 done items, `-Index` was live-wrong by up to 3 -- it silently
selected, and then rewrote, a different item than the one displayed.

**This section's proposed fix was not taken.** It was to emit the true unfiltered index on
every row, which the projection work had to touch anyway. That makes the number correct
without making it identity: the next change to ordering or filtering re-opens the same bug,
and the failure stays silent because a plausible number is indistinguishable from a right
one.

Selection by position was removed instead, across `Janet.Core`, the CLI, the MCP tools and
the three shims. Items are addressed by topic, as a dictionary. **Do not add a row index to
the projection** -- there is nothing left that consumes one, and printing a position next to
an item invites exactly the call this removed.

The projection design is otherwise untouched by that change, since it selects by `-Topic`
throughout.

## The dependency, and why it is not optional

The manifest justifies eager loading as *"small, stateful, and useless if fetched
late."*

- **"Small" is dead** -- 60 characters to 127,155.
- **"Useless if fetched late"** only holds if nothing prompts the query. But the catalog
  *is* the discovery mechanism, and `pattern.thread-items` and `script.show-thread-items`
  are already nodes. What is missing is not discovery; it is a **trigger**.

That trigger is the parked `research.json` trigger/`whenToUse` item. It is what would
let the eager `run` entry be dropped entirely. **Do not build the startup projection as
though it were independent of that item** -- built alone, the roster is doing the
trigger's job.

Which raises the stakes on the stored-area decision rather than lowering them: **a stale
or mostly-`(unfiled)` roster is a tripwire that does not trip, and it fails silently.**

## Sequence

1. Stored `area` field. Schema change -- do it while the shape is being touched.
2. Roster for the startup slot; `summary` for direct calls; `item`/`area` behind an
   explicit selector. Emit the true unfiltered index per row.
3. Revisit dropping the manifest `run` entry once trigger/`whenToUse` lands.

## What the MCP port changed, and what it did not

The port to `Janet.Core` was in flight when this was designed, which is why it was
planned and not built.

Deferred-tool loading means MCP schemas are **not** billed per turn in Claude Code --
RazorGraph's 24 tools arrive as names only, a few hundred tokens, and schemas load via
`ToolSearch` on demand. So the transport is cheaper than first assumed and the tool
surface is not the problem.

**It is the eager startup capture that costs, not the tool surface.** Moving the
implementation behind an MCP tool does nothing about it on its own.

---

Related: `note.startup-brief-budget`, DESIGN-NOTES section 2 (progressive disclosure),
section 3 (thread items).
