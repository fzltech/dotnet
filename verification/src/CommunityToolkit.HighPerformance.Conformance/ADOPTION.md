# Bridging a slice of CommunityToolkit.HighPerformance

Read this before writing `Stepper.cs` or `ConcreteEncoding.cs`. It is short on purpose.

Do not bridge CommunityToolkit.HighPerformance as a whole. Pick one slice, and make the first one embarrassingly small
— it is where the adapter's shape and the encoding's conventions get decided, and both are much
harder to change once several slices depend on them.

## Is this slice suitable?

The first four are requirements, not preferences.

**1. Drivable through a public seam you already have.** If `IStepper` cannot be written without
making something `internal` visible, pick a different slice. Widening visibility for the benefit of a
verification tool is a change to your design made for the wrong reason.

**2. Deterministic given its inputs.** No wall-clock, no shared random source, no parallel fan-out
anywhere beneath the seam. A scenario that yields a different trace on a second run cannot be
conformance-checked at all, and a conformance failure that comes and goes gets dismissed rather than
investigated. If the slice is not deterministic, making it so is prerequisite work here, not
something to encode around.

**3. Its state fits the model's state sort.** A slice needing a richer state than the model has is
waiting on the model, not on this command.

**4. One transition label, two at most.** Every extra label multiplies the label-mapping surface,
which is hand-written and unverified.

**5. Small enough that a wrong encoding is visible by reading it.** Nothing downstream can tell you
the mapping was wrong, so reviewability is the only defence there is.

## What else does the slice expose that you are not looking at?

Answer this before writing the encoding, in one sitting, and write the answer down. It is the one
question in this file that nothing in the layer can ask on your behalf.

**List every output the seam produces.** Not the one you are bridging — all of them. A returned
result object and each of its properties, an event stream, a status, a completion time, a diagnostic
collection, anything mutated through a parameter. Then say, for each, whether the model has a cell
for it. Where it does not, note whether that is a decision or an oversight.

**Why this and not something else.** A defect found in a blind mutation exercise left a completion
timestamp unset. The bridge was correct, the encoding was correct, the property was correct, and the
gate was green — because the bridge replayed the *event stream* and the defect lived in the final
progress record. Nothing looked there. Six defects were injected by someone who had never seen the
verification layer; the proof gate as it stood caught none of them, and this is why one of the misses
happened.

**The gate reports the near miss but not this one.** `bridge-coverage.txt` names the model cells your
`Encode` left free, so a cell the model *has* and you forgot to pin shows up on every run. A part of
the output the model has no cell for at all cannot show up there — there is no variable to report —
so the only place that gets caught is here, by asking.

It is a fair answer that a slice deliberately reads one projection. What is not fair is not knowing
which.

## When the code reads something it does not carry

A guard or an effect sometimes depends on a value that is neither state nor configuration: a random
draw, an external input, a nondeterministic abort. Read it as a **draw**:

```csharp
var pivot = Draws.Value(_c, "pivot");
```

It is an unconstrained integer, so the solver chooses it — and a property that survives that holds
for **every** value the code could produce. That is stronger than modelling the generator, and it
costs nothing: the draw is not in `StateVars`, so no property has to mention it and your encoding
never has to pin it.

Constrain its range where your code does. An entirely free integer makes the adversary stronger than
the real generator, and a property that fails only for a value the code cannot produce is a false
failure.

**"Holds for every draw" is not "holds in expectation", and the two need different engines.** Mean
fitness per generation, expected convergence time, probability of a result within k steps — none of
those is provable here, and none should be described as if it were. A property proven over a draw
must not be written as covering the distribution.

**To earn a backing over a draw, say what the code drew.** Report it from `Step.Draws`, keyed by the
same name:

```csharp
return new Step<MyState>(label, post, new Dictionary<string, int> { ["pivot"] = drawn });
```

The checker then pins the model's draw to that value, so the transition is compared against the run
that happened. Report nothing and the comparison asks whether *some* draw explains the step — which
it usually can, so the run stays green while proving less. That is named in `bridge-coverage.txt`
rather than failing the gate, because a bridge written before this existed reports none and an
upgrade must not turn it red. **A property resting on a draw listed there should not be
`ConformanceBacked`**: the proof covers every draw and the check covered none in particular.

One thing the layer enforces rather than asks of you: **a property may not read a draw.** An
invariant over a value the solver picks is a claim about the solver, so the Verifier refuses it by
name.

## Stepping must produce a new state

`Advance` returns the state after the step. If your code mutates in place, an adapter over it hands
back the object it was given — and then there is no pre/post pair to compare, because both are the
same thing.

This is the most expensive mistake available here, so it is worth saying plainly: **the fix belongs
in the slice, not in the adapter.** Give the slice a value type for its in-progress state and a way
to advance it one step, then implement the method your callers already use on top of that. A second
copy of the logic written just for stepping would be the one nothing checks, while the code your
callers actually run stayed unverified and the gate stayed green.

## What "done" looks like

Exactly one property flips to `ConformanceBacked`, its scope records the realised scenario count and
whether the coverage was exhaustive, truncated, or sampled with a seed, and every other property stays
`ModelOnly`. A trust boundary
listing one conformance-backed property and eleven model-only ones is an honest document; one
claiming the layer is connected is not.

Resist widening in the same change. The second slice is cheap once the first exists.

## When the checker goes red

Each failure names a different thing, and they are not interchangeable:

| Failure | What it means |
| --- | --- |
| `model-incomplete` | Your code did something no transition describes. Add the transition, or narrow the property's scope to exclude that behaviour. It is a finding about the model, not a bug in the bridge. |
| `label-not-in-model` | `MapLabel` returned a label that is not in `TransitionRelation.Labels`. Constructing a `Label` is not the same as the model having one — nothing was proven about that one. |
| `transition-not-modelled` | The transition exists but cannot describe the states your code produced. Either the encoding is wrong, or the transition's guard, effects or footprint do not match what the code does. |
| `encoding-unsatisfiable` | `Encode` asserts something contradictory. Assert one equality per model state variable and nothing that can conflict. |
| `annotation-run-contract` | A `[Requires]` was false on the driver `Configure` returned, or an `[Ensures]`/`[Invariant]` on what `Observe` handed back. The precondition is read before the run, so a driver that repairs its own entry condition no longer passes. |
| `adapter-did-not-terminate` | Steps kept coming past the bound. Usually `Advance` returning the state it was given — see above. |

## When enumeration runs out

`ScenarioEnumerator.Permutations` refuses past eight items, because a truncated permutation set still
reads as exhaustive. Past that cap, sample instead:

```csharp
ScenarioEnumerator.Sampled(random => YourScenario(random), count: 500, seed: 20260829)
```

The seed is required. A sampled run that cannot be replayed produces a red gate nobody can reproduce,
and an irreproducible failure gets dismissed rather than investigated.

**Put a fixed, committed seed in `Checks`, and fresh seeds only in `Hunts`.** A check runs in the
proof gate, so it must give the same answer every run; `ScenarioEnumerator.Fresh()` in `Checks` makes
your gate flaky by construction. Hunts are excluded from the gate for exactly that reason, and are
never registered as evidence, so nothing they do can promote a property.

The loop worth building a habit around:

1. Run the hunts: `dotnet test CommunityToolkit.HighPerformance.Verification.slnx -c Release --filter Category=hunt`.
2. One goes red and its report names the seed it drew.
3. Pin that seed into a `Checks` entry.
4. The finding is now a deterministic regression test, in a commit somebody reviewed.

A backing earned over a sample records that in its scope — *"500 scenario(s), 6200 step(s), sampled
with seed 20260829"* — which is a materially different claim from an exhaustive one, and readers of
`TRUST-BOUNDARY.md` can now tell them apart.

## Annotating your slice

The conformance checker reads a small set of attributes from your code at every gate run and
cross-checks the hand-written bridge against them. They are optional: an unannotated slice behaves
exactly as before, and an annotation that is read but not enforced changes nothing.

The attributes are six small types in namespace `Fmverify.Annotations`. There is no published
package, and the file has to live in a project of your own (the checker reads attributes off your
assembly), so get it with the tool rather than by hand:

```bash
fmverify add-annotations <path> --source   # copies Fmverify.Annotations.cs into the target project
fmverify add-annotations <path> --source --what-if
```

**Source copy is the supported route today.** The `Fmverify.Annotations` NuGet package is packable
but not published, so `--package` and `--apply` are deferred; the command's no-flag default still
suggests the package, so pass `--source` explicitly. The source copy currently requires C# 12 syntax
and does not include the build-time predicate analyzer; if either blocks you, see `docs/STATUS.md` in
the fmverify repository rather than editing the copy. The checker itself only
cares about the attribute names, so the source copy and a future package reference are
interchangeable at gate time.

**The namespace is the load-bearing part, not where the file came from.** The checker reflects
your built assembly and matches attributes by full type name, never by assembly identity, so a
copied file and a referenced package are indistinguishable to it. Nothing in the generated layer
references the attributes either, so there is no version here that has to agree with anything —
except the one the copy itself carries.

**A copy can drift, and drift is a named failure, not silence.** The file declares
`Fmverify.Annotations.Allowance.Version`, and the checker compares it against the pin this layer
was generated with on every gate run. An allowance copied before the marker existed is not
compared (failing a copy for a field it never had would be the false red this layer refuses);
a copy whose marker disagrees is a failure that names `fmverify add-annotations <path> --force` as
the fix.

Then declare:

- `[Step("Place")]` on the seam method — names a transition the model must have a label for. Checked
  against the labels the run produced, and only where the run hands over the declaring type: a
  `[Step]` on a seam this bridge does not drive is another bridge's, and holding this run to it
  reddens a gate over correct code.
- `[Deterministic]` on the seam method — the checker drives it twice and compares the traces.
- `[Invariant(nameof(IsWellFormed))]` on the state type — a parameterless `bool` method that must
  hold in every observed state.
- `[Requires(nameof(...))]` / `[Ensures(nameof(...))]` on the seam method — `bool` predicates checked
  before and after each step.
- `[Measure(nameof(PendingCount))]` on the state type — an `int` member that must decrease on every
  progress step, anchoring the model's ranking function to a real decreasing quantity.

Each is a checked obligation, not a comment. A violated annotation turns the gate red with a failure
kind that names which one.

**What a green run does and does not tell you.** It does not mean every annotation was checked. It
means none of the ones this run could reach went unapplied — and the difference is worth knowing
before you trust it.

An annotation is applied only where the run hands over an object of the type that declares it. Where
it hands one over and no receiver reads that kind of annotation on it, you get
`annotation-none-applied` and can act on it. Where the run never hands over such an object at all,
nothing is reported: on that shape the annotation is indistinguishable from one belonging to a
different bridge in the same assembly, and reporting it would redden a gate over code that is
correct.

The case to know about is a member-level `[Requires]` or `[Ensures]` on a stepping bridge whose
replay state is not the seam's own type. Answer `IStepper.SeamOwner` and it is checked; leave it and
it is silent.

**So `verify.sh` prints what the run reached**, and this is the number to look at rather than
inferring anything from a green gate:

```
== Annotation coverage ==
Annotation coverage: 6 of 7 reached by a registered check.
Reached by no check:
  [Ensures(CanPlace)] on Scheduler.Run
These were read out of the target and checked by nothing. Either they belong to a seam no
registered check drives, or the bridge that would reach them does not declare the object that
owns it.
Seams driven twice and compared:
  [Deterministic] on Scheduler.Run
A run drives the seam its bridge names, so which of these declarations that seam is cannot be
said here — they are named rather than counted for that reason.
Only registered checks are counted; a bridge nobody registered contributes nothing.
```

Two things about it. **It is a report, never a verdict** — an annotation reached by no check does not
fail the gate, because nothing here can tell a misconfigured bridge from a slice you deliberately
left unannotated, and a red build you can only clear by deleting a correct annotation is worse than
no report. And **the count is across every registered check**, so an annotation one bridge misses and
another applies is not listed: what appears was reached by nothing at all.

The number moves for reasons outside your bridge, too. "Of 7" counts the annotations in the whole
target assembly, so it grows when your code gains an annotation anywhere; the useful figure is the
list, not the ratio.

**`[Deterministic]` is named rather than counted**, and it is worth knowing why the report is shaped
that way. Every other kind is checked per declaration: a predicate is read on a receiver, a `[Step]`
is compared against a label the run produced. A `[Deterministic]` is not — the run drives *the seam
its bridge names* twice and never learns which of your declarations that seam is. Counting it as
reached would claim an attribution nothing here has; listing it as unreached would deny a check that
did run. So it is named, and the line above says what happened to it.

If your target carries only a `[Deterministic]` — a reasonable place to start, since it is the one
annotation that needs no predicate — the count is `0 of 0` and the seam line is the whole report.
That is correct rather than a sign nothing was read.

None of that tells you a predicate is *meaningful* — only that something read it. For a specific
annotation you have just written, make it false once and watch the gate go red. That is still the
only way to be sure, and it takes a minute.

### Writing a predicate as an expression

The four contract and measure attributes take an expression as well as a member name. `nameof(P)` is
the one-identifier case of one, so an annotation you already wrote keeps working unchanged — there is
no second form to choose between:

```csharp
[Requires("Placed < Capacity")]
[Ensures("PendingCount == old(PendingCount) - 1")]
[Invariant("IsWellFormed")]
[Measure("Pending + Retrying")]
```

The grammar is deliberately small, and the boundary is the point — anything outside it is a named
`annotation-malformed` failure rather than a guess:

- a member of the receiver, by name: a public property or a public parameterless method, returning
  `bool` or `int`;
- a **dotted path** — `Detail.Depth`. Only the last segment has to be `bool` or `int`; the ones before
  it are the objects the path travels through. A path through a `null` is a failure naming the
  segment, never `false`;
- an **enum member by name** — `State == TaskState.Done`. An enum-valued member reads as its
  underlying int, so comparing against the number works too and is worse to read. The enum is looked
  for in the receiver's own assembly and nowhere else, so an annotation cannot mean different things
  depending on what else is loaded; two of the same name are refused with both named rather than
  picked between. A member the receiver actually has always wins over an enum of the same name;
- integer literals, `true`, `false`;
- integer `+` and `-`;
- `<`, `<=`, `>`, `>=`, `==`, `!=`, one per expression — `a < b < c` is a mistake, as it is in C#;
- `&&`, `||`, `!`, and parentheses;
- `old(<member>)`, in an `[Ensures]` on a stepping seam only;
- **`all(<path>, <predicate>)` and `any(<path>, <predicate>)`** over a collection.

**Quantifying is how you say something about what a run produced**, which is where most true claims
about a result object live:

```csharp
[Ensures("all(Events, Remaining >= 0)")]
[Invariant("!Finished || all(Items, Settled)")]
```

**The element is the receiver inside the body**, so there is no name to introduce: `Remaining`
resolves against each event. That is the same rule as everywhere else here — a bare identifier
resolves against the current receiver — and a quantifier changes the current receiver to the element.
The cost is that the body cannot reach the outer receiver, which is why the second example puts
`Finished` outside.

`old(...)` inside a quantifier is refused. There is no earlier value of an element the run produced
for it to name.

**`all` over an empty collection is `true`, and that is worth knowing.** It has to be — the grammar
has no short-circuit, so `Count == 0 || all(...)` would evaluate the quantifier anyway, and failing
would leave you unable to write the claim at all. So instead: a predicate whose `all(...)` was empty
on **every** observed state is reported as `annotation-vacuous`. It held without ever looking at
anything, so it could not have failed and is not evidence. One empty evaluation is fine; all of them
means the slice never populates that collection and you want a different slice or a different claim.

Not in it: arithmetic beyond `+` and `-`, dotted access like `a.B.C`, method arguments, indexing,
quantifiers. Quantifiers belong in the model, where the solver discharges them instead of sampling
them; the rest are absent because no annotation has needed them yet, and each one added is a decision
about what a predicate may reach.

**A name the checker cannot read is a failure, never `false`.** An identifier that resolves to
nothing, resolves to the wrong type, or throws when read, fails the gate. An annotation nobody could
evaluate has not been satisfied. For the same reason `&&` and `||` do not short-circuit: a predicate
whose other side names nothing must not pass because this side settled it.

**Which object an expression is read on depends on the shape**, and that is the thing to know when a
predicate fails on a name you can see in your code:

| Annotation | Stepping seam | Runs to completion |
|---|---|---|
| `[Requires]` | the state before the step | the driver the run was performed with |
| `[Ensures]` | the state after the step, with `old(...)` reading the state before it | the result; `old(...)` is refused — no pre-run object exists |
| `[Invariant]` | every observed state | the result |
| `[Measure]` | the state, per progress step | each event of the annotated type, in order |

### Telling the checker which object owns your seam

A member-level `[Requires]` or `[Ensures]` belongs to the class that declares the seam. On a stepping
bridge that is often not the type your replay state is — the seam may be `Run.Next(State)`, while the
state is a `State`. Where they differ, the checker cannot attribute the contract to your bridge until
you say so:

```csharp
public object? SeamOwner => _run;   // the object whose method Advance drives
```

Overriding it is optional and a bridge that does not compiles unchanged. Without it, a contract on a
type other than your state's is not this bridge's to check — so if a `[Requires]` or `[Ensures]` you
wrote seems to be doing nothing, this is the first thing to look at.

### If your target runs to completion rather than stepping

The list above assumes a seam that is one step: you call it, it hands back the state after that step,
and the checker calls it again. Not every target has such a method. One call runs to completion and
returns a result carrying a list of what happened — a scheduler's `Run()`, a batch process, a
solver. On that shape there is no object that is the state part-way through the run, so the
annotations above have nothing to be checked against until you say where to read them.

**Which shape you have is something you declare, not something the tool works out.** Nothing the tool
can read off a target says which it is, so the declaration is the entry point your registry calls:
`ConformanceChecker.Check` for a stepping seam, `ConformanceChecker.CheckRun` for one that runs to
completion. `CheckRun` takes one extra argument, an `IRunObserver<TScenario>`.

**The model comparison does not change.** A run-shaped bridge still writes `Stepper.cs`, the replay
that Stepper produces is still what the model is compared against, and every conformance failure in
the table above still means what it means there. What the observer changes is only *where the code's
own annotations are read*: on objects the target itself produced, instead of on the replay your
Stepper reconstructed.

`Observe` runs the target for one scenario and hands back three of the target's own objects — the
driver it configured, the result the run returned, and the events the run produced. Each annotation
then has a receiver:

| Annotation | Receiver on the run shape | What must hold |
| --- | --- | --- |
| `[Requires(nameof(P))]` | the driver the run was performed with, once per run | `P` resolves as a parameterless `bool` method on it and returns true |
| `[Ensures(nameof(P))]`, `[Invariant(nameof(P))]` | the result, after the run | the same, read on the result |
| `[Measure(nameof(M))]` | each event of `M`'s declaring type, in order | `M` reads as an `int`; the readings are non-increasing, and at least one of them falls |
| `[Step("X")]` | the replay's observed labels, exactly as on the step shape | `X` is the first `:`-segment of some observed label |
| `[Deterministic]` | two `Observe` calls on the same scenario | the two event sequences are equal |

Five things in that table are worth spelling out.

**`[Requires]` is a precondition read after the fact, and that is an obligation on your observer.**
`Observe` runs the target and hands back the driver it ran, so the run has already happened by the
time the predicate is invoked on it; nothing here can see the driver as it was on entry. On a driver
that accumulates state, a precondition violated before the run can therefore be repaired by the run
and pass. **Hand back a driver whose entry conditions the run did not destroy.** A driver whose
configuration the run leaves alone satisfies that; one that consumes its own input does not, and
there a `[Requires]` naming an input the run has already eaten proves nothing.

**A measure that never falls is a failure, not a pass.** Strict decrease on every event would be the
wrong rule — a run that emits both a start and a finish per unit of work only falls on the finishes —
so the rule is non-increasing with at least one strict decrease. There are then four ways a
`[Measure]` goes red, and they are different findings: its member yields no readable `int` on any
event of its declaring type, so it went unread and unread is not satisfied; the readings **rose**;
the readings never fell, which anchors no termination claim and would make the annotation
decoration; or there was only **one** reading, which cannot show a decrease either way and so is
rejected rather than accepted for want of evidence. An observation that carries no events at all is
its own failure, `run-observed-nothing`: no events were compared, and no `[Measure]` was read on it.

**Whether an annotation applies here and what it is read on are different questions.** A type-level
`[Invariant]` or `[Measure]` is a claim the declaring type makes about its own instances, so it is
this run's obligation only if the object it would be read on is one of them. A member-level
`[Requires]` or `[Ensures]` sits on a seam method, so what decides whether it applies is who owns
that seam — the driver — while the object it is read on is whichever one the shape supplies, which
for `[Ensures]` is the result. An annotated seam inherited by several classes is one obligation, not
one per subclass.

**Events must be value-equal for `[Deterministic]` to mean anything** — make the event a record, or
override `Equals`. Reference equality fails on an honest second run.

**`[Step]` reads differently on this shape.** On a stepping seam it says "this method is one
transition"; here it says "this run produces these transitions", and several may be stacked on the
one seam method. The check itself is unchanged.

**No implementation is generated for the observer**, and not a throwing skeleton either: a file that
throws for a reason that does not apply to your target teaches nothing. You write it — one class,
usually a dozen lines. A bridge on this shape therefore has three hand-written semantic files; every
other bridge still has two.

**On this shape your Stepper produces its own replay, and four consequences follow.** The checker has
no step API to call on a run-shaped target, so the model is compared against a replay your Stepper
builds by driving the target itself (usually once, recording what it did) and advancing through that
record. Getting the replay right is the whole bridge, so four things are worth stating plainly:

- **The Stepper runs the target.** There is no shared run the observer hands over; your Stepper and
  your observer each run the seam for a scenario, and that is expected. Determinism is what makes
  the two agree.
- **Only events that change the model's state are steps.** A target's log usually carries events
  that are not transitions — a periodic tick, an announcement whose state change the target applied
  at an earlier instant. Skip them in `Advance`; emit a step only when the lifecycle state moves.
- **The initial replay state is the target's state after its entry-time actions**, not the model's
  `Init`. A scheduler that opens its workers before the first event has them open in the very first
  replay state. That state still has to satisfy the property's invariant, which is the check that
  catches a wrong reconstruction.
- **Scenarios must fit the model's bound.** The encoding asserts one equality per model cell, so a
  scenario whose population exceeds the bound (more customers than the model has item cells) makes
  `Encode` throw. That is now a named failure (`encoding-threw`) rather than a crash, and the fix is
  a scenario that fits — the model proves over its bound, and a conformance run is over that bound
  too.

**Two methods, and the order is what makes `[Requires]` mean anything.** `Configure` builds the
object that will run and does not run it. `Observe` runs it. The checker reads every member-level
`[Requires]` on the configured driver **before** calling `Observe`, so nothing the run does can
affect it.

It used to be one call that configured and ran, which left a precondition being read off a driver
the run had already finished with — so a driver that violated its entry condition and then repaired
it passed, and the obligation to hand back an undisturbed driver was yours and unenforced. If your
observer predates the split, `Configure` is usually the object construction your old `Observe`
already did, and `Observe` keeps the rest.

`Observe` takes the scenario as well as the driver, so your observer needs no state of its own.
Where your driver takes its inputs at construction, ignore that argument; where it takes them at the
call, that is how they reach the run.

`RunObservation`'s two members are `object`, deliberately: every annotation check is already
reflective, so typing them would buy nothing. They are the result and the events, and being
different types they cannot be exchanged — the driver used to be here too, and that is the swap this
document used to warn about at length.

What remains is handing back the **driver** where the result belongs, and it is narrower:

- **Caught, if your target declares any member-level `[Ensures]`.** A predicate naming a member of
  the result type does not resolve on the driver, and an unresolvable name is a failure and never
  `false`. The `[Requires]` is unaffected — it is read on the driver before the run, and the driver
  is still the driver, so the two hand-offs are now independent.
- **Not caught, if your only annotations are type-level `[Invariant]`s.** Those are filtered against
  the object they would be read on, so one that matches neither is skipped consistently and the gate
  stays green over a check that never ran. The defences are to declare one `[Ensures]` on the seam
  method, or to read the observer once by eye. Nothing in the layer will tell you.

**What a green run on this shape says.** The code's own contract predicates held on the objects the
code produced for those runs, and a quantity the code declares fell across the events the code
emitted. **It says nothing about the states part-way through a run** — on this shape there is no such
object to check. That claim is available only to a target that exposes a per-step snapshot, which is
a real change to your design and yours to choose; the tool does not ask for one. Where a property's
backing rests on a run-shaped check, write its `Meaning` so it cannot be read as the per-step claim.

**If your layer was generated before this existed**, regenerate `src/CommunityToolkit.HighPerformance.Spec/Contract.cs`
before calling `CheckRun`. The three failure kinds the run shape reports �?"`
`annotation-run-contract`, `annotation-run-measure` and `run-observed-nothing` �?" are declared there,
and `Contract.cs` comes from `init`'s scaffold rather than from `add-bridge`, so a layer whose spec
project predates them will not compile against them.

**Upgrading the layer.** `fmverify update <path>` brings the whole layer up to the running fmverify
in one command: the model always, this bridge if it exists, and the annotation allowance if a stale
copy exists, then a build to confirm the adapter, encoding and registry still satisfy the refreshed
interfaces. `fmverify status <path>` reports the layer's currency first, read-only. `update` never
creates a bridge or an allowance that was not already there, and refuses a downgrade by an older
fmverify.
