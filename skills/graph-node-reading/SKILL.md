---
name: graph-node-reading
description: Read a code-graph node economically -- counts first, rows only when you need them. Use whenever you are about to call get_node, or are following edges to answer "who calls this", "what tests reach this", "what breaks if I change this". Also use when a graph answer came back far larger than the question, or when you are writing a prompt that tells another agent to query the graph.
---

# Reading a graph node without paying for it

## The currency rule, before anything else

**Never trade context to save seconds.** The two costs are not the same
resource, and only one of them is scarce.

| | wall clock | context |
|---|---|---|
| `build_solution`, RazorGraphTool / gamehub | 11.2s / 15.1s | ~700 chars, whatever the size |
| `load_graph` from a saved graph | 0.4s | ~300 chars |
| `find_nodes` for one name | 0.01s | ~4,200 chars, with exact file and line |
| the same question by text search | 0.02s | ~13,200 chars, and then you still have to read the files |

Measured 2026-09-05. A rebuild is nearly free in the currency that runs out,
because its answer is a fixed-size summary no matter how big the solution is.
Text search is instant and expensive: it hands you lines instead of structure,
so it is never the last call -- it is a cheap call followed by several file
reads that the graph would have made unnecessary.

So when a graph is missing, **rebuild it**. Fifteen seconds of waiting is not a
reason to spend thousands of tokens and accept a worse answer. This exact
mistake was made twice in one session, both times right after a server rotation
dropped the loaded graphs, and both times the reasoning was "rebuilding is
expensive" -- which was measured in seconds, the resource that was not running
out.


`get_node` is narrow on purpose. The default gives you the node, its outgoing
edges as rows, and its incoming edges **summarised by type with counts**. The
summary is usually the answer: "130 tests reach this" is what a caller used to
pay for 130 rows to learn.

Depth lives in `note.razorgraph-mcp-server` (blind spots) and
`skill.graph-first-code-analysis` (when to reach for the graph at all). Query
those rather than trusting this file's age.

## The loop

1. **Call `get_node` plainly.** No `edges`, no `edgeType`. You get the shape of
   both sides for about a kilobyte.
2. **Read `incoming.byType`.** Very often you are done. A count answers "is this
   tested", "is this used", "how many implementations" without a single row.
3. **Read `availableFields`.** It is a menu derived from *this* node: the edge
   types it actually carries. It is not a static list, so it also tells you what
   the node does *not* have.
4. **Expand one thing.** `edges: "incoming"` plus `edgeType: "Calls"` when the
   counts say there is one Calls edge among 130 Covers. Ask for the list you
   named, not for everything.

## What the fields mean

| Field | Reading |
|---|---|
| `total` | The whole side, always. `edgeType` never changes it. |
| `byType` | The whole side, always. Use it to decide what to expand. |
| `expanded` | Whether `edges` rows are present on this side. |
| `filteredTo` / `matching` | Present only when `edgeType` narrowed the rows. `matching` of `total`. |
| `returned` / `truncated` | Rows you got, and whether `limit` bit. |

`total` and `byType` describing the **whole** side is deliberate and worth
trusting: a narrowing argument must never change what the unasked-for half
claims about itself. An early build applied the filter before the summary, and
the first real call reported `outgoing.total: 0` on a node with an outgoing
edge. A smaller answer is fine; a confidently wrong one is not.

## Reading the source the node points at

The node tells you what to read, not just where to look. Take
`lineDocs ?? lineStart` through `lineEnd` and you have the declaration and its
documentation, exactly -- no opening a file at a line number and guessing how
far down to go.

- `lineDocs` absent means **undocumented**, on a graph built 2026-09-05 or
  later. On an older graph it means nothing was recorded. Check `loadedAt`
  before reading absence as a fact.
- Attributes sit between `lineDocs` and `lineStart`, because `lineStart` points
  at the declaration rather than at the `[`. An attributed but undocumented
  member therefore has attributes just above `lineStart` that no line names.
- `fileWrittenSince: true` means the file changed after the graph was built.
  The span is then a guess about a file that has moved. Rebuild rather than
  reading it.
- `builtAt` on the response is the warrant for the whole span. Code in progress
  breaks a span, so check it against your own edits before opening a file --
  and read `builtAt`, not `list_graphs`' `loadedAt`, which only says when the
  graph entered the server.
- `freshnessCaveat` present means the graph carries no build stamp at all.
  Then an absent `fileWrittenSince` proves nothing rather than meaning
  "unchanged", and no span on that graph is warranted. Rebuild.

There is deliberately no tool that hands back the source. The harness's own
Read already returns line-numbered text with permissioning and file tracking,
and a second reader inside the graph server would be a worse copy of it. The
graph is an index; the span is what makes the index usable.

## Rules

- **Never open with `edges: "all"`.** That is the old behaviour the narrowing
  replaced. If you find yourself reaching for it, you have not read the counts.
- **A count is evidence; a zero is not proof.** `covering_tests` and the
  coverage counts are reachability. Zero means "no test reaches this by Calls or
  interface dispatch", and the tool's `caveat` names what it cannot follow.
- **An unknown `edgeType` is refused, not answered empty.** The refusal lists
  what the node has. Read it rather than guessing again.
- **`limit` caps rows, not truth.** `total` still tells you the size of what you
  did not take.

## When you are writing a prompt for another agent

Say this, because a subagent inherits the tool surface and none of the habit:

```
get_node is narrow by default: outgoing rows, incoming counts by type. Read
availableFields and incoming.byType FIRST, then expand exactly one side with
edges/edgeType. Do not call it with edges="all".
```

## Related

`pattern.graph-first-analysis` for the ordering rule, `file.skill-agent-kickoff`
for the delegation template that carries the line above,
`note.razorgraph-mcp-server` for what the graph cannot see.
