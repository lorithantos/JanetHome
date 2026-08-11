# RazorGraph Lua scoping

Measurements taken 2026-08-10 to decide the Lua extractor's parser strategy and
architecture, before either was chosen. Sibling of `razorgraph-js-scoping.md`, and the
same discipline: measure the corpus before committing to a parsing approach, because the
approach that looks obvious is chosen against imagined code.

## Corpora

Public third-party code, deliberately not Lori's own — own-code testing shares the
author's blind spots, and the extractor would be validated against the very idioms it was
written for. Cloned shallow (`--depth 1`) to `V:\repos`.

| Corpus | Licence | Files | LOC | Role |
|---|---|---|---|---|
| Penlight | MIT | 115 | 20,883 | develop against; small enough for ground truth |
| Kong | Apache-2.0 | 1,309 | 285,665 | stress; not hand-verifiable, which is the point |
| LR-Lua (Lori's Lightroom plugins) | — | 75 | — | second *host*, not a second corpus |

## Idiom census

Regex over raw text, so comments and string literals are counted too — indicative, not
exact. Format: Penlight / Kong.

```
require "x" WITHOUT parens ....... 249 / 2497
require("x") with parens .......... 65 / 1370
require(<expression>) ............... 3 /   43
require [[x]] long string ........... 9 /    0
local M = {} ...................... 211 / 1170
module(...) 5.1 legacy .............. 0 /    0
local function f() ................ 234 / 2355
M.f = function() .................. 118 / 1346
function M.f() .................... 389 /  662
function M:f() method ............. 147 /  846
setmetatable( ...................... 67 /  305
goto / ::label:: .................... 0 /  148
```

## What the census settled

**Paren-less `require` dominates ~2:1 in both corpora.** An extractor keyed on
`require(` misses roughly 65% of the module graph, and misses it *silently*, producing a
plausible partial graph. This single finding is what the measurement bought.

**Function definitions spread across four forms with no dominant one.** In Kong the
largest bucket is `local function` (2,355) and the second is the assignment form
`M.f = function()` (1,346) — the two that a naive `function <name>(` pattern misses
entirely.

**Lua 5.1 grammar is insufficient.** Kong uses `goto`/`::label::` 148 times (5.2+, and a
LuaJIT extension). A 5.1-only parser fails on the stress corpus outright.

**`module(...)` is dead** — zero occurrences across 1,424 files. Do not build for the old
global-namespace idiom.

**Dynamic `require` exists and cannot be resolved statically** — 46 sites. Direct
analogue of the `@Url.Action` finding in `razorgraph-js-scoping.md`, where the
literal-URL rule scored 0% on real code. These must be *reported* as unresolved rather
than silently omitted, or the module graph claims a completeness it does not have.

Conclusion: a real parser, not regex. Reached by measurement, and independently matching
the Razor precedent (internal `Razor.Language` syntax API kept; regex text analysis is
fallback only).

## The host finding, which changed the architecture

Lightroom plugins use `import 'LrView'` **191 times against 22 `require`**. A
`require`-only extractor finds almost nothing in them. They are organised as 17
`.lrdevplugin` units each carrying an `Info.lua` manifest, and their imports resolve to
the Lightroom SDK rather than to plugin source.

That is not an edge case. **Lua is predominantly an embedded language** — the standalone
library (Penlight, LuaRocks) is the minority case, and most Lua in the world is scripts
for a host application. So the *host*, not the module system, is the primary abstraction.

| Host | Unit | Mechanism | Resolution |
|---|---|---|---|
| LuaRocks / plain | `.rockspec` | `require` | rockspec map, else path convention |
| Lightroom (and Adobe kin) | `Info.lua` | `import` | SDK namespace → external |
| Neovim | plugin dir | `require` | runtimepath; `vim.*` external |
| LÖVE2D | `main.lua` | `require` | path; `love.*` external, callbacks are entry points |
| Garry's Mod | — | `include` / `AddCSLuaFile` | path, plus client/server realm |
| WoW addon | `.toc` | **none** | no static module graph exists at all |
| Roblox | — | `require(script.Parent.X)` | instance tree, not on disk |

Kong's `kong-latest.rockspec` carries 605 explicit `["kong.cache"] = "kong/cache/init.lua"`
mappings — authoritative where present, covering 605 of 1,309 `.lua` files, so `spec/` and
`bin/` still need convention resolution. Kong uses the `init.lua` idiom 70 times, Penlight
once.

## Design consequences

Validating the host interface against the two hard cases on paper — before writing it —
produced three elements that would otherwise have been painful retrofits:

1. **Resolution is three-way, and `Unresolved` carries a reason.** `InGraph` /
   `External` / `Unresolved`. `LrDialogs` is not a missing module, it is someone else's;
   collapsing external into unresolved reports ~191 phantom failures on a healthy plugin,
   and collapsing it into success hides the genuinely dynamic requires. The reason string
   separates Roblox's "instance-tree reference" from Kong's "dynamic expression".
2. **A host declares whether a static module graph exists at all.** WoW forces this: a
   `.toc` lists load *order*, there is no `require`, and coupling is via a shared global
   namespace. A WoW graph with zero module edges is correct but reads identically to a
   broken extractor, so such a host must stamp a caveat. Same discipline as the coverage
   guard that refuses to answer against a test-less graph rather than reporting everything
   uncovered.
3. **Hosts may annotate and mark entry points.** Realms (GMod client/server) and load
   stages (Factorio) are a dimension, not a resolution outcome. Entry points reuse the
   existing `entryPointKind` property, so escape analysis works on Lua once they are marked.

The general lesson, which is DESIGN-NOTES section 3 in a new setting: an abstraction
shaped around the operation first imagined — `require` — quietly forbids the operations
actually needed: `import`, `include`, `.toc` load order, instance paths.
