# WPF app architecture — plumbing lessons

Platform-specific findings about WPF's notification, threading and rendering model, and
what they imply for how to structure an app. Collected 2026-08-07 from a full thread-affinity
and rebuild-churn audit of a real WPF app (charts over SkiaSharp, CommunityToolkit.Mvvm).

**The main architectural conclusion is `pattern.change-coalescing`.** WPF is the platform
where that pattern pays off hardest, for reasons in §1 and §2 below. Read this note for the
platform facts; read that one for the machine.

---

## 1. The notification model is pull, and that makes laziness free

WPF bindings **read on demand**. A `PropertyChanged` raised against a binding that was never
materialized costs essentially nothing — no getter runs, no value is produced.

That single fact means demand-based computation needs no infrastructure:

```csharp
public IReadOnlyList<ISeries> Series
{
    get { if (this.dirty) this.Rebuild(); return this.series; }
}

private void Invalidate()
{
    this.dirty = true;
    this.OnPropertyChanged(nameof(this.Series));   // no-op when nothing is bound
}
```

An invisible view marks a boolean and produces nothing. Becoming visible materializes the
binding, which reads the getter, which rebuilds exactly once.

**Do not build an `IsActive` / activation-signal mechanism for this.** The obvious
alternative — unsubscribe on hide, re-subscribe on show, recompute on activation — reaches
the same result but needs an activation signal, re-subscription bookkeeping, and a catch-up
rebuild. That catch-up *is* the lazy getter. Same idea, more plumbing to maintain.

Contract on a lazy getter: idempotent, and cheap when clean.

## 2. Template materialization is the demand signal you already have

A shell with `SelectedItem`-driven navigation into a single `ContentControl` materializes
**only the selected page's visual tree**. Non-selected pages have no live bindings at all.

Combined with §1, "only compute what is being looked at" is free. This is why viewmodels
that eagerly recompute on every notification are so wasteful in WPF specifically: they do
work for N pages when the framework has already decided only one exists.

## 3. Compose cells per output; do not hoist a base class

Viewmodels that all subscribe and all call `Recompute()` look like a base class waiting to
happen. Resist it. A base class carries one dirty flag and one `rebuild()`, which models
*"one signal → rebuild this whole page"* — usually the very problem, relocated. Real pages
have several outputs invalidated by *different* sources.

One lazy cell per bindable output, each registered for exactly what dirties it, gives the
granularity a single flag flattens. Inheritance is not the obstacle — multi-level viewmodel
inheritance works fine with source-generated properties — the semantics are.

## 4. `[ObservableProperty]` routes through one overridable raise point

CommunityToolkit's generated setters all call `ObservableObject.OnPropertyChanged`, which is
`protected virtual`. Overriding it lets an app observe or augment **every** property change
centrally without touching a single setter — the hook that makes change-coalescing additive
rather than invasive:

```csharp
protected override void OnPropertyChanged(PropertyChangedEventArgs e)
{
    base.OnPropertyChanged(e);      // bindings: entirely unchanged
    this.PostChange(e.PropertyName!);
}
```

Also note `[NotifyPropertyChangedFor]` multiplies the raise count — a source with seven
properties and two dependent ones raises nine notifications per logical operation, not seven.
Notification count is not property count.

## 5. What WPF marshals, and what it does not

- **Does marshal**: simple property-binding updates. A `PropertyChanged` raised off-thread
  for a scalar bound to a control is marshalled by the binding engine.
- **Does not marshal**: `CollectionChanged` on a collection bound to a `CollectionView`
  (throws), and — critically — **the object graph a control reads during rendering**.

That second gap is the dangerous one. Handing a control a freshly built object graph from a
background thread "works" as far as the binding engine is concerned, while the render pass
walks that graph concurrently. Prefer replacing whole immutable collections over mutating
bound ones; it removes the collection-marshalling problem entirely.

## 6. `DispatcherPriority` gives ordering, never a latency bound

`Background` sits below `Input` and `Normal`. A flood of `Normal`-priority posts starves a
`Background` continuation for as long as the flood lasts — unboundedly.

Concretely: reporting progress once per work item from parallel workers through
`Progress<T>` posts one dispatcher operation *per item*. Ten thousand of them starve the
render pump for the whole run.

**Prefer an explicit time window to a priority** when you need a guarantee. A priority
answers "before or after what else"; a window answers "within how long", which is what a
responsive UI actually needs to promise.

## 7. `Progress<T>` captures its context at construction, and never conflates

- Construct it on the UI thread (before the first `await`) or its callbacks will not land
  there.
- It marshals every single report. It has **no** conflation, so N reports become N dispatcher
  operations.
- Reports can be **delivered out of order** even when the values were generated in order:
  `Interlocked.Increment` hands out unique increasing values, but two workers can post them
  in either order, so a displayed counter can visibly run backwards.

Routing progress through a coalescing queue and letting the destination take `Max` fixes the
volume and the ordering together.

## 8. Make thread affinity structural, not manual

Start a long-running consumer loop **on the UI thread** and never use
`ConfigureAwait(false)`. Every continuation then resumes on the dispatcher and no explicit
marshalling exists anywhere in the app.

The invariant is enforced by *where the loop was started*. That deserves a comment at the
call site, because adding `ConfigureAwait(false)` looks like tidying and would silently move
UI-object mutation onto a pool thread.

Corollary: a `System.Threading.Timer` or `System.Timers.Timer` fires on a pool thread. In a
WPF app, use `DispatcherTimer` — or better, an awaited delay inside a loop started on the UI
thread, which needs no timer type at all.

## 9. Static events leak handlers

A static event (a theme service, a settings singleton) holds every subscriber forever.
Viewmodel handlers attached at construction and never removed keep those viewmodels alive and
cause phantom work after they should be dead. Harmless with exactly one shell; a real leak the
moment that assumption changes. Prefer an injected instance over a static.

## 10. Native-backed rendering turns churn into a lifetime hazard

When rendering is native-backed (SkiaSharp, Direct2D interop, GL controls), excess rebuild
churn stops being merely wasteful. Each rebuild orphans native-backed resources whose
reclamation is scheduled independently of the render pass, so replacing them faster than the
canvas retires them is a plausible use-after-free.

And the failure is invisible to normal diagnostics: **a native access violation is not a
managed exception**. `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`
and `AppDomain.CurrentDomain.UnhandledException` are all bypassed and the process fails fast,
so a crash log written from those handlers stays empty. An empty log is the signature of *any*
native fault — it is not evidence about threading.

Diagnosis needs a WER dump (`HKLM\...\Windows Error Reporting\LocalDumps\<exe>`); the
Application event log gives the exception code and the loaded-module list, which is usually
enough to identify the faulting subsystem but not the frame.

Also: before adding `Dispose` calls to native-backed objects a charting/rendering library
handed you, check whether the library already reclaims them. Doubling up is the same class of
bug you are trying to fix.

---

## Checklist for a new WPF app

1. One coalescing queue between source changes and derived rebuilds (`pattern.change-coalescing`).
2. Lazy getters per bindable output; no activation plumbing.
3. Cells per output, not a base class per page.
4. Subscriptions filter on what they need, or receive keys — never a discard lambda over
   every notification.
5. Consumer loops started on the UI thread; no `ConfigureAwait(false)`; no pool-thread timers.
6. Replace bound collections wholesale rather than mutating them.
7. Injected services, not static events.
8. A crash log, plus WER dumps configured, if anything renders natively.
