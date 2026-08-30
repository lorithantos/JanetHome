# A green suite that ran a quarter of its tests

Found 2026-08-30 in RetirementCore. `janet check` reported **426/426 passing, exit 0,
`succeeded: true`** — and one of the three test assemblies had run 4 of its 131 tests.

**The first diagnosis in this note was wrong and is corrected below.** It claimed the
envelope structurally could not detect this. It can. The runner says so loudly and
`janet check` drops it on the floor, which makes this a defect rather than a limitation.

## What happened

Two xUnit classes each declared `IClassFixture<WpfApplicationFixture>`. xUnit builds one
fixture instance **per class**, so the second constructed a second
`System.Windows.Application` — which WPF refuses, one per AppDomain. The refusal killed
the test host mid-run.

## What the runner reports

Everything you would want, reproduced deliberately on 2026-08-30:

```
The active test run was aborted. Reason: Test host process crashed :
Unhandled exception. System.InvalidOperationException: Cannot create more than
one System.Windows.Application instance in the same AppDomain.
   at System.Windows.Application..ctor()
   at RetirementCore.App.Tests.WpfApplicationFixture...b__0() in
      ...\AccountsPageTemplateTests.cs:line 249

Test Run Aborted.
```

Exit code **1**. The message names the cause and the source line.

## What `janet check` reports for the same run

```
janet exit code:   0
succeeded:         True
tests.succeeded:   True
tests:             423 total, 423 passed, 0 failed
assemblies:
  lori__LORI-VIDEO_2026-08-30_14_59_52_net10.0.trx : 0/0
  RetirementCore.Tests                             : 417/417
  RetirementCore.Web.Tests                         : 6/6
stderr:            (empty)
```

Three separate failures, all in the check rather than the contract:

1. **The runner's non-zero exit is swallowed.** `dotnet test` exited 1; `janet check`
   exited 0 and asserted `succeeded: true`. The documented meaning of that exit code —
   "0 iff the build succeeded and every test passed" — is violated, because the run was
   aborted rather than passed.
2. **The abort message vanishes entirely.** Not in the envelope, not on stderr. The one
   channel carrying the diagnosis is discarded.
3. **The crashed assembly is mis-parsed into a phantom.** `RetirementCore.App.Tests` does
   not appear at all. In its place is an entry named after the **TRX filename** with
   `0/0`, because the partial TRX lacks whatever the assembly name is normally read from
   and the file name is used as a fallback.

## The tell that was already in the envelope

An assembly entry whose name ends in `.trx` with `0/0` is not noise. It is a crashed
assembly. Those entries had appeared in several runs earlier that same session and were
read as harmless clutter, which is the second half of why this went unnoticed for hours:
the signal was present, unlabelled, and looked like formatting debris.

## Fix

The cheap and correct one, in order:

1. **Propagate the runner's exit code.** If `dotnet test` exits non-zero, `succeeded`
   is false. This alone would have caught it.
2. **Surface the abort.** Detect `Test Run Aborted.` / `Test host process crashed` and
   carry it as a first-class field, with the message, rather than dropping the only text
   that names the cause.
3. **Stop emitting filename-named assemblies.** A TRX that yields no assembly name is a
   partial result and should be reported as one — `status: "aborted"` — not as an
   assembly that ran zero tests.

A discovery pass (`dotnet test --list-tests --no-build`, measured at 2.8s for 131 tests
on an already-built assembly) remains worth having, but for a **different** failure: tests
that were never discovered at all, through a filter typo or a missing attribute, where
nothing aborts and nothing is non-zero. It is not the fix for this one and conflating them
was part of the original wrong diagnosis.

## The general shape, restated

The original claim — "a count reported by the thing being measured cannot detect that
thing failing to run" — is too strong. Here the runner reported the failure accurately at
every level available to it, and the wrapper reported success. The transferable lesson is
narrower and less flattering: **a tool that summarises another tool must propagate its
verdict, not recompute one from the parts it managed to parse.** `succeeded` was derived
from "no failures found in the results I could read", which is true of an empty result set
and of a crash.

Related: `script.invoke-dotnet-check`, DESIGN-NOTES section 1 — contracts that fail loudly
rather than degrade quietly. This one degraded quietly *because* the wrapper turned a loud
failure into a quiet one.

## Fixed 2026-08-30, contract 5

All three, in `Janet.Core`, in the order proposed:

1. `DotnetCheck.RunTests` no longer discards the runner's exit code —
   `DotnetTests.WithRunnerVerdict` folds it into the run, so `succeeded` is false and the
   process exits 1 whenever `dotnet test` did, whatever the counters say.
2. The envelope's `tests` gained `runnerExitCode` and `abort`; the abort banner is read from
   the runner's console output (`DotnetTests.ReadAbort`) — deliberate scraping, because the
   exception that kills a test host is written nowhere structured.
3. Assembly entries gained `status: "complete" | "aborted"`. A TRX whose summary says
   Aborted, that cannot name its assembly, or that lacks counters entirely (previously
   *skipped*, a fourth way to vanish) is labelled a crashed run's leftovers rather than an
   assembly that ran zero tests.

**Corrected same day, from a field report.** The first cut of item 3 keyed only on the
missing-name/missing-counters/outcome="Aborted" signs, and a crashed TRX slipped through
labelled `complete`: it had a name, plausible counters (3 of 131), and outcome **"Failed"**
— a value healthy runs with failing tests also use. How much of the TRX exists is a race
against the dying host (three reproductions flushed 0, 3, and 10 results). The signal that
is there regardless, measured on the real artifact: the runner writes the abort into the
TRX itself as `<RunInfo outcome="Error">` under `ResultSummary/RunInfos`, full exception
text included. Detection now keys on that Error RunInfo too — the attribute is
locale-independent where the console banner is not — and `tests.abort` is read from the
RunInfo text first, with console scraping demoted to the fallback for a host that dies
before any TRX is written.

Verified by re-running the reproduction above: `AllPageTemplatesTests` reverted to
`IClassFixture`, and the same run that had reported 426/426-passing/exit-0 now reports
exit 1, `succeeded: false`, `runnerExitCode: 1`, the full abort banner with the
`Application..ctor` source line, and the partial TRX marked `aborted`. Restored, 554/554,
exit 0. The 423 the aborted run counted was 131 tests short of the 554 that exist — the
size of what the old envelope called a pass.
