# The C# catalog port, and the cutover that lands it

Janet's surfaces are PowerShell, which binds the audience to PowerShell 7 on Windows. That
is the constraint on open-sourcing: the people who want tooling that makes coding agents
reliable are a much larger set than the people who will install PowerShell 7. So the core
surfaces are moving behind MCP, with `Janet.Core` as the single implementation and two thin
front ends over it.

## The objection to compiling, and why it does not hold

`note.razorgraph-cli` exists because a running MCP server holds its DLLs open: a code change
reaches the tools only after closing every session, re-publishing, and restarting. That is
real, and it is **specific to stdio**. Verified against the Claude Code MCP documentation:

> If an HTTP or SSE server disconnects mid-session, Claude Code automatically reconnects with
> exponential backoff: up to five attempts, starting at a one-second delay and doubling each
> time. [...] Stdio servers are local processes and are not reconnected automatically.

An HTTP-transport server is a *separate process the client dials*, not a child it owns. Kill
it, rebuild, restart: the session reconnects itself inside a ~31-second window, or on one
`/mcp` retry past it. So `janet-mcp` ships both — stdio as the zero-config default consumers
get, HTTP as the development loop. Two further facts from the same source: `list_changed` is
supported, so a restarted server can announce a changed tool surface in-session; and tool
search is the default, so MCP tool schemas are deferred rather than billed every turn, which
retires most of `note.startup-brief-budget`'s argument against a second server.

## Where it stands

Done, with parity proven against the PowerShell:

- `Janet.Core` — graph model, ranked query, and the textual-splice writer.
- `Janet.Cli` (`janet`) and `Janet.Mcp` (`janet-mcp`), both packaged as .NET global tools so
  client config is a bare command name rather than a machine-specific absolute path.
- 41 tests. Twenty-eight compare the C# against `Get-Research.ps1` on the live graph. Six run
  both writers against identical copies and assert the files are **byte-identical** after add
  and update, including reverse-linking, with prose carrying quotes, apostrophes, ampersands,
  arrows and commas. Three assert a refused write leaves the file untouched. Four pin the
  candidate-first default below.

Not done: `research rename`; thread items; API/assembly introspection; dotnet check; the
shim pass that turns the scripts into thin wrappers; the documentation pass.

## The staging discipline, and the one rule that enforces it

Two implementations write two files while the port is built. `research.json` stays under the
PowerShell scripts; `research.candidate.json` is the only thing `Janet.Core` writes. **With no
explicit `--graph`, the candidate wins while it exists** — targeting the live file has to be
said out loud (`--graph research.json`), and the special case retires itself once the swap
removes the candidate. That default is not a convenience: a single forgotten flag would merge
the two sides and destroy the arithmetic the cutover depends on.

Test writes use a reserved `sandbox.` id prefix, and `Swap-ResearchGraph.ps1` refuses to run
while any survives — otherwise they become catalog entries at cutover.

`Invoke-EditGuard.ps1` guards all four graph files, not just the live one.

## Three files, because a hash cannot describe a change

- `research.json` — live, written by the scripts.
- `research.candidate.json` — written by the port.
- `research.candidate.base.json` — the common ancestor, byte-frozen at seed time, written by
  nothing. **This is the one that is easy to omit and fatal to omit.** The seed record's hash
  can only say *that* the live graph moved; integrating its changes forward needs to know
  *which* nodes moved, and that is a three-way comparison. The first cut of the plan stored
  only the hash and would not have been implementable.

## `Swap-ResearchGraph.ps1`

Preserve by rename, swap by rename, then integrate the live-side delta forward through the
**new** writer — its first run against real accumulated content, with `research.previous.json`
sitting beside it unmodified. Where a node changed on both sides the preserved side wins:
those are nodes real sessions authored, the candidate's copy is at best a stale seed, and
every conflict is reported.

It is manual and approval-gated because its precondition is stopping every competing writer,
and **a session cannot quiesce itself** — an agent running it from inside one of two concurrent
sessions is a writer trying to prove there are no writers. The script verifies the quiesce;
the user establishes it.

Rehearsed end to end on a copied directory with both sides diverged: 94 nodes out of 92 base
plus one addition per side, the conflict resolved to the live version, the candidate-only node
untouched. Refusals exercised: a surviving `sandbox.` node, an existing `research.previous.json`,
an unparseable candidate. Recovery exercised: a failing integration reverses both renames and
restores both files byte-exact.

## Two operational traps that already bit

**A stale installed tool silently ignores the candidate-first default.** The `.nupkg` is built
from source at pack time, so changing `GraphLocator` and forgetting to repack leaves the global
tool on the old behaviour — which wrote two nodes into the live graph before anyone noticed.
`dotnet tool update` **no-ops on an identical version**: uninstall and install, or bump the
version. The same staleness hit the Release build output earlier in the same session. Repack
and reinstall after any change to defaults, or do not trust what `janet` does.

**Nothing can delete a node.** `update --remove` drops a *field*, not a node; there is no
delete in either implementation. Removing one means restoring the file. That is why a stray
`sandbox.` write is expensive rather than trivial to undo, and why `Swap-ResearchGraph.ps1`
refuses on one rather than stripping it for you — stripping would mean writing a delete path
that does not otherwise exist, on the [SAFETY] script, for the convenience of not being tidy.

## Two things learned the hard way

**An exit code is a claim, not proof.** A stand-in that exited 0 without writing anything was
reported as integrated; only the end-of-run re-read caught it and reversed. The per-node line
now says "integrating", and the re-read is the authority. Same shape as
`note.debugger-displays-are-claims`: derive ground truth from what was actually emitted.

**Check what a modification *is* before reverting it.** A `git checkout -- research.json` was
run on a file showing as modified, on the assumption the modification was mine. It was — but
the check came after the command, not before. The scare that followed (a hash mismatch that
looked like destroyed work) turned out to be `core.autocrlf` normalising 779 CRLF + 257 LF
into 1036 CRLF; content was identical and `git diff HEAD` was empty. The arithmetic settled it,
not the plausible reading.

## Open questions for whoever picks this up

- `Add-ResearchNode.ps1` and `Update-ResearchNode.ps1` **disagree on field order** — add emits
  `tags` before `caveats`, update emits `caveats` before `tags`. Both are reproduced exactly
  for parity, so a node added by one path and later touched by the other has its fields
  reordered: a whole-node diff arriving through the back door. Worth settling once, deliberately.
- The catalog carries one genuine dangling link, `note.janet-origin -> file.readme`, where
  `file.readme` was never created. The swap warns rather than blocks, because
  `Add-ResearchNode.ps1` warns rather than blocks — a cutover stricter than the writer that
  produced the data is a cutover that can never run.
- `janet` does not write the research trace that `Invoke-ResearchGuard.ps1` reads. The shim
  must keep writing it, or the guard silently stops firing at cutover.
