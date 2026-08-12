# Tuples are method-local only

**Rule.** A tuple may exist inside a method body. It may not appear in any
signature that another piece of code binds to: return types, parameters, record
or class members, collection element types. Not even private ones — "private"
limits who can call it, not whether the shape has to survive a boundary.

Where a shape crosses a boundary, name it. A record costs one line.

## Why, with the failure that produced the rule

RazorGraph's drive-survey tool (2026-08-11) modelled a duplicate-file ruling as
`IReadOnlyList<(DeletionSubject Subject, DeletionAssessment Assessment)>` on a
public record. Everything compiled. Every test passed. The text output rendered
correctly, because C# destructures tuples happily.

Then the JSON output — the DEFAULT output mode, the one a model or a script
consumes — came back with the field silently empty. `ValueTuple` exposes `Item1`
and `Item2` as **fields**, and `System.Text.Json` serialises properties, not
fields. No error, no warning, no exception: just a report whose most important
content had quietly vanished. It was found only because a downstream aggregation
produced no rows and the absence was noticed by hand.

That is the shape of the hazard. A tuple is a convenience for the compiler at
the point of use, and it carries no contract outward:

- **Serialisers ignore it.** System.Text.Json emits nothing. Silently.
- **The element names are a lie.** `(int Volume, string Path)` names are erased
  to `Item1`/`Item2` in metadata — which is why assigning differently-named
  tuples raises CS8123 warnings about names being "ignored".
- **Nothing can be added to it.** A third field means touching every call site,
  where a record takes a new member.
- **It documents nothing.** `(long, long, string)` at a call site is a puzzle.

## How to apply

Inside a method, freely:

```csharp
var (files, bytes) = CountCandidates();   // local, never escapes
```

Crossing a boundary, never — name it instead:

```csharp
public sealed record DuplicateRuling(DeletionSubject Subject, DeletionAssessment Assessment);
```

Deconstruction still works on records if the call sites want it, so the
convenience is not actually lost.

**Suspect first when:** a JSON field is empty but the object is populated in
memory; a serialised payload is missing exactly one member; CS8123 warnings
appear about ignored tuple element names.

## Not yet enforced

There is no C# rule-checker in this repo (`Test-PowerShellRules.ps1` covers
PowerShell only). Until there is, this is ADVISORY — an honest label, per the
manifest's own stance that a rule claiming enforcement it does not have is worse
than an honest suggestion. A checker would look for tuple syntax in signatures:
`ValueTuple<`, or a `(` immediately following a return-type position.
