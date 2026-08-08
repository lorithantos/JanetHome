# Skill: crash-escape analysis

How to find why an app dies without a trace — the process exits, no dialog, no
log, often intermittently — by treating the crash as a reachability question:
somewhere a throw can reach a process boundary without passing through a catch
that handles it. Proven on the RetirementCore.App first-run crash (2026-08-07):
graph queries found an empty exception surface, a catch-shape mismatch, and a
render-loop callback calling a deliberately-throwing guard, in under an hour.

**Trigger:** "the app just closes", "crashes sometimes / only on first run /
only outside the debugger", "works in tests, dies in the UI", any exit with no
stack trace to start from. Also fires *proactively* when reviewing an app shell:
before shipping a UI host, ask where its escapes go.

**Not the trigger:** a reproducible exception with a stack trace — that is
ordinary debugging; start at the trace. Failing tests. Hangs (no throw is
escaping; see circuit-breakers, DESIGN-NOTES §8).

## Workflow

1. **Check the process-level surface first — it is one query.** `get_node` on
   the `Application`/host class: if it contains no handler methods, every
   escape is an exit, and everything found later is fatal until a backstop
   exists. WPF's roll call: `DispatcherUnhandledException`,
   `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException`
   (the last logs but cannot save the process). WinForms:
   `Application.ThreadException`. Generic host: unhandled = death.
2. **Run `exception_escapes` if the graph has it.** The RazorGraph tool
   precomputes throw → entry-point chains that no catch stops; spend reading
   time only on the reported chains, and mind its `caveats` array — what it
   cannot see (BCL, lambdas, virtual dispatch) still needs step 4's manual eye.
3. **Enumerate what the risky operation reaches, then rule sites *out*.**
   `trace_data_flow` from the operation's entry point gives the closed list of
   methods a throw could start from; read only those, and record each one
   eliminated (guarded, clamped, total). Ruling out is the point — suspicion
   that only accumulates never terminates.
4. **Hunt catch-shape mismatches.** A catch can be present, sincere, and
   unmatchable: `catch (OperationCanceledException)` never sees the
   `AggregateException` that `Parallel.For` wraps a body throw in; async-void
   and async command wrappers rethrow onto the dispatcher behind the caller's
   back; `await` unwraps aggregates but `.Wait()`/`.Result` do not.
5. **Hunt callback surfaces that run outside every user catch.** Render-loop
   callbacks, chart labelers, converters, property-changed cascades, timer
   ticks. Then check the code they call is *total*: a deliberate
   throw-on-bad-input guard (RetirementCore's `Money.FromDouble` rejecting
   NaN) is correct engineering that becomes a crash the moment a framework
   hands it garbage mid-render. The guard is never the bug; the unguarded room
   it fires in is.
6. **Intermittent means race or first-frame.** Ask what is different the first
   time: first render, first layout pass, uninitialized bounds, empty
   collections, device init. "Sometimes on first run" pointed at LiveCharts
   handing NaN to a labeler before axis bounds settled.
7. **Fix in layers, all of them.** Guard at the throw site (make the callback
   total), catch at the operation (turn a failed run into a message), backstop
   at the process (handled + continue — the autosave protects the document, so
   exiting costs more than continuing risks), and append to a crash log so
   "somewhere" has a name next time. One layer is a patch; four is an
   architecture.

## Knowing the blind spots

`exception_escapes` inherits RazorGraph's scope: BCL/out-of-solution throwers
are invisible, dispatch is static, lambdas and local functions are not
followed, catch-with-`when` counts as conditional. Steps 4–5 exist because the
tool cannot do them; the tool exists so steps 4–5 start from a shortlist.
Native/interop crashes (access violations, GPU device loss) never throw at
all — no catch reaches them; that investigation is dumps and event logs, not
this skill.

Related: [[skill.graph-first-code-analysis]] for the query discipline this
leans on, [[pattern.thread-items]] for keeping the descent honest,
[[note.razorgraph-mcp-server]] for tool caveats.
