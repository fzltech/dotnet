using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

public enum Outcome { Proven, Refuted, Unknown }

public sealed record VcResult(string Name, Outcome Outcome, string? Counterexample);

/// <summary>
/// Discharges the inductive-invariant obligations with Z3, over the independent
/// model. "P is valid" is proven by asserting ¬P and getting UNSAT. UNKNOWN is
/// treated as failure (never a proof).
/// </summary>
public sealed class Verifier
{
    // Every obligation is discharged against a property from the registry, which is the
    // single place a property is defined. There is deliberately no no-argument overload: a
    // second definition of the seed living here would diverge from the registry's the first
    // time either was edited.
    public IReadOnlyList<VcResult> VerifyAll(PropertyDefinition p)
    {
        // A malformed property is reported through the machinery that already exists rather than
        // thrown: the gate renders a Refuted result with its reason like any other failed
        // obligation, and there is no registration hook to fail at.
        var malformed = Validate(p);
        if (malformed.Count > 0) return malformed;

        var results = new List<VcResult> { CheckInitReachable(p), CheckInitiation(p) };
        results.AddRange(TransitionRelation.Labels.Select(l => CheckConsecution(p, l)));
        results.Add(CheckSafety(p));
        results.AddRange(TransitionRelation.Labels.Select(l => CheckTransitionFireable(p, l)));

        if (p.Kind != PropertyKind.Liveness) return results;

        // The safety obligations above are not optional for a liveness property. A measure proven
        // bounded and decreasing under an invariant that was never established proves nothing at
        // all, so the dependency is structural rather than a convention someone has to remember.
        results.Add(CheckBoundedness(p));
        results.AddRange(TransitionRelation.Labels.Select(l =>
            p.IsProgressLabel!(l) ? CheckDecrease(p, l) : CheckNonIncrease(p, l)));

        return results;
    }

    private static IReadOnlyList<VcResult> Validate(PropertyDefinition p)
    {
        var errors = new List<VcResult>();
        void Bad(string why) => errors.Add(new VcResult($"Malformed[{p.Id}]", Outcome.Refuted, why));

        if (p.Kind == PropertyKind.Liveness)
        {
            if (p.Measure is null)
                Bad("a Liveness property must supply a Measure.");
            if (p.IsProgressLabel is null)
                Bad("a Liveness property must say which labels make progress.");
            if (p.Measure is not null && !MeasureIsInteger(p))
                Bad("the Measure must be integer-sorted. A real measure can decrease forever " +
                    "(1, 1/2, 1/4, ...) without crossing a bound, so it is not a well-founded argument.");
            if (p.IsProgressLabel is not null && !TransitionRelation.Labels.Any(p.IsProgressLabel))
                Bad("no label makes progress, so termination would hold vacuously.");
        }
        else
        {
            // The other direction. A measure on a safety property is never checked by anything, so
            // it would sit in the registry reading as though something had proven it.
            if (p.Measure is not null)
                Bad($"a {p.Kind} property must not supply a Measure.");
            if (p.IsProgressLabel is not null)
                Bad($"a {p.Kind} property must not declare progress labels.");
        }

        // A draw is a value the model reads and never controls, so a property that reads one is a
        // claim about the adversary's choice rather than about the system — provable or refutable
        // for reasons that have nothing to do with the code. Refused here rather than described in
        // Draws' remarks, which is the difference between a rule and a note.
        //
        // Read on a fresh state so the check is about what the predicate does with any state, not
        // about a state some obligation happened to build.
        if (ReadsADraw(p.Invariant))
            Bad("the Invariant reads a draw (see Draws). A draw is not state: it is free at every " +
                "step, so an invariant over one asserts something about the value the solver picked. " +
                "Constrain the draw inside Guard or Effects, where the transition can say what the " +
                "code does with it.");

        if (p.Safety is not null && ReadsADraw(p.Safety))
            Bad("the Safety predicate reads a draw (see Draws), which makes it a claim about the " +
                "value the solver picked rather than about a reachable state.");

        if (p.Measure is not null && ReadsADraw((f, s) => p.Measure!(f, s)))
            Bad("the Measure reads a draw (see Draws). A ranking function over a free value is not " +
                "well-founded: the adversary can choose it to grow, and nothing about the code " +
                "changes that.");

        return errors;
    }

    /// <summary>
    /// Whether a predicate reads a draw, asked by building it over a fresh state in a context of its
    /// own and looking at what came out.
    ///
    /// <para>Building it is the only way to ask. A predicate is a delegate, so nothing static can
    /// see which variables it touches — and a draw first exists when the predicate asks for it,
    /// which is what makes the constructed term the evidence.</para>
    /// </summary>
    private static bool ReadsADraw(Func<Formulas, StateVars, Expr> predicate)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        return Draws.OccursIn(c, predicate(f, c.FreshState("validate")));
    }

    private static bool MeasureIsInteger(PropertyDefinition p)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        return p.Measure!(f, c.FreshState("sort")).Sort is IntSort;
    }

    // Invariant ⇒ Measure ≥ 0   ⇔   UNSAT(Invariant ∧ Measure < 0)
    public VcResult CheckBoundedness(PropertyDefinition p)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var s = c.FreshState("s");

        return Discharge($"Boundedness[{p.Id}]", c,
            c.Z3.MkAnd(p.Invariant(f, s), c.Z3.MkLt(p.Measure!(f, s), c.Z3.MkInt(0))));
    }

    // Progress label: Invariant(pre) ∧ Clause ⇒ Measure(post) < Measure(pre)
    public VcResult CheckDecrease(PropertyDefinition p, Label label)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var tr = new TransitionRelation(c);
        var pre = c.FreshState("pre");
        var post = c.FreshState("post");

        var negation = c.Z3.MkAnd(
            p.Invariant(f, pre),
            tr.Clause(label, pre, post),
            c.Z3.MkGe(p.Measure!(f, post), p.Measure!(f, pre)));

        return Discharge($"Decrease[{p.Id}/{label}]", c, negation);
    }

    // Non-progress label: Invariant(pre) ∧ Clause ⇒ Measure(post) ≤ Measure(pre).
    //
    // Easy to omit, and without it a non-progress transition can raise the measure, the system runs
    // forever, and every other obligation still passes. This is the standard hole in a hand-rolled
    // termination argument.
    public VcResult CheckNonIncrease(PropertyDefinition p, Label label)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var tr = new TransitionRelation(c);
        var pre = c.FreshState("pre");
        var post = c.FreshState("post");

        var negation = c.Z3.MkAnd(
            p.Invariant(f, pre),
            tr.Clause(label, pre, post),
            c.Z3.MkGt(p.Measure!(f, post), p.Measure!(f, pre)));

        return Discharge($"NonIncrease[{p.Id}/{label}]", c, negation);
    }

    // Non-vacuity for Init, and it has to come before Initiation to be worth anything.
    //
    // Initiation discharges UNSAT(Init ∧ ¬Invariant). An Init that no state satisfies makes that
    // UNSAT for ANY invariant, so every property in the layer reports a proof over a model with no
    // initial state at all. This is the same hole CheckTransitionFireable closes per label — a
    // transition that can never fire trivially preserves the invariant — one level further back,
    // and it is easier to open than that one: Init is edited whenever the model's starting
    // condition changes, and a conjunct that contradicts another is not visible by reading.
    //
    // Checks satisfiability directly (SAT = good, unlike Discharge's polarity).
    public VcResult CheckInitReachable(PropertyDefinition p)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var s = c.FreshState("init");

        var solver = c.Z3.MkSolver();
        solver.Assert(f.Init(s));
        var name = $"InitNonVacuous[{p.Id}]";
        return solver.Check() switch
        {
            Status.SATISFIABLE   => new VcResult(name, Outcome.Proven, null),
            Status.UNSATISFIABLE => new VcResult(name, Outcome.Refuted,
                "Init is unsatisfiable: no state satisfies it, so Initiation holds vacuously and " +
                "every obligation resting on it proves nothing. Usual cause: a conjunct added to " +
                "Init that contradicts another — often a modelling precondition written as an Init " +
                "constraint rather than as a guard or a narrower state sort."),
            _                    => new VcResult(name, Outcome.Unknown, null),
        };
    }

    // Init => Invariant   ⇔   UNSAT(Init ∧ ¬Invariant)
    public VcResult CheckInitiation(PropertyDefinition p)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var s = c.FreshState("s");
        return Discharge($"Initiation[{p.Id}]", c, c.Z3.MkAnd(f.Init(s), c.Z3.MkNot(p.Invariant(f, s))));
    }

    // Invariant ∧ T ⇒ Invariant'   ⇔   UNSAT(Invariant(pre) ∧ Clause(pre,post) ∧ ¬Invariant(post))
    public VcResult CheckConsecution(PropertyDefinition p, Label label)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var tr = new TransitionRelation(c);
        var pre = c.FreshState("pre");
        var post = c.FreshState("post");

        var negation = c.Z3.MkAnd(p.Invariant(f, pre), tr.Clause(label, pre, post), c.Z3.MkNot(p.Invariant(f, post)));
        return Discharge($"Consecution[{p.Id}/{label}]", c, negation);
    }

    // Invariant => Safety   ⇔   UNSAT(Invariant ∧ ¬Safety)
    public VcResult CheckSafety(PropertyDefinition p)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var s = c.FreshState("s");
        return Discharge($"Safety[{p.Id}]", c, c.Z3.MkAnd(p.Invariant(f, s), c.Z3.MkNot(p.Safety(f, s))));
    }

    // Non-vacuity: Invariant(pre) ∧ Clause must be SATISFIABLE, else the consecution VC for
    // this label is proven vacuously (a transition that can never fire trivially preserves
    // the invariant). Checks satisfiability directly (SAT = good, unlike Discharge's polarity).
    //
    // The whole clause, not the guard alone. A satisfiable guard does not make a transition
    // fireable: Effects and Frame can contradict each other, and when they do, every obligation
    // for this label passes while the transition is dead. A footprint that omits a cell the
    // effects write produces exactly that — Frame freezes what Effects changes — and it is the
    // first mistake the multiset kind invites.
    public VcResult CheckTransitionFireable(PropertyDefinition p, Label label)
    {
        using var c = new SmtContext();
        var f = new Formulas(c);
        var tr = new TransitionRelation(c);
        var pre = c.FreshState("pre");
        var post = c.FreshState("post");

        var solver = c.Z3.MkSolver();
        solver.Assert(c.Z3.MkAnd(p.Invariant(f, pre), tr.Clause(label, pre, post)));
        var name = $"NonVacuous[{p.Id}/{label}]";
        return solver.Check() switch
        {
            Status.SATISFIABLE   => new VcResult(name, Outcome.Proven, null),
            Status.UNSATISFIABLE => new VcResult(name, Outcome.Refuted,
                "transition is unsatisfiable under the invariant, so it can never fire and its " +
                "consecution obligation is vacuous. Usual cause: the footprint omits a cell the " +
                "effects write, so Frame freezes what Effects changes."),
            _                    => new VcResult(name, Outcome.Unknown, null),
        };
    }

    private static VcResult Discharge(string name, SmtContext c, BoolExpr negatedVc)
    {
        var solver = c.Z3.MkSolver();
        solver.Assert(negatedVc);
        return solver.Check() switch
        {
            Status.UNSATISFIABLE => new VcResult(name, Outcome.Proven, null),
            Status.SATISFIABLE   => new VcResult(name, Outcome.Refuted, solver.Model.ToString()),
            _                    => new VcResult(name, Outcome.Unknown, null),
        };
    }
}
