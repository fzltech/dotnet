using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>The predicates the Verifier proves. SEED values are trivial; replace them.</summary>
public sealed class Formulas
{
    private readonly SmtContext _c;
    public Formulas(SmtContext c) => _c = c;

    public BoolExpr And(params BoolExpr[] conjuncts) => _c.Z3.MkAnd(conjuncts);

    /// <summary>
    /// What must be true of the run's frozen configuration — the parameters declared with
    /// <c>SmtContext.ConfigInt</c> / <c>ConfigBool</c>. SEED: no configuration, nothing to constrain.
    ///
    /// <para><b>Conjoined into both <see cref="Init"/> and <see cref="Invariant"/>, and it has to
    /// be.</b> Consecution starts from the invariant and never sees <c>Init</c>, so a constraint
    /// written only into <c>Init</c> is invisible to every transition obligation: the solver is free
    /// to pick a capacity of zero, or a configuration your code could never produce, and Consecution
    /// fails with a counterexample that looks exactly like a defect in your property. Putting it
    /// here puts it in both places at once, which is why this method exists rather than a note
    /// telling you to remember.</para>
    ///
    /// <para>Constrain only what must hold. Every conjunct here narrows what your proofs cover.</para>
    /// </summary>
    public BoolExpr ConfigurationWellFormed() => _c.Z3.MkTrue();

    // SEED: initial state is counter == 0.
    public BoolExpr Init(StateVars s) =>
        And(_c.Z3.MkEq(s.Counter, _c.Z3.MkInt(0)), ConfigurationWellFormed());

    // SEED: the trivially-true inductive invariant. TODO: model your system's invariant here.
    public BoolExpr Invariant(StateVars s) => ConfigurationWellFormed();

    // SEED: the trivially-true safety property. TODO: state what should always hold.
    public BoolExpr Safety(StateVars s) => _c.Z3.MkTrue();
}
