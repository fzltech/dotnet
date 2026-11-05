using Microsoft.Z3;
using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Maps the target's concrete state onto the model's symbolic state. You implement this.
///
/// <para>This mapping and the <see cref="IStepper{TScenario,TState}"/> implementation are the two
/// places a mistake silently weakens every conformance-backed claim in the layer. Nothing verifies
/// them. Give them their own unit tests.</para>
/// </summary>
public interface IConcreteEncoding<TState>
{
    /// <summary>
    /// A constraint tying the symbolic state to this concrete state. Conjoin equalities for each
    /// state variable the model has.
    ///
    /// <para><b>Assert one equality per state variable and nothing that can conflict.</b> An
    /// under-constrained encoding produces a false <i>failure</i>, which announces itself. A
    /// self-contradictory one makes every check over it pass vacuously — a false <b>pass</b>, and
    /// the only way a wrong encoding leaves the gate green while proving nothing. The checker tests
    /// for it explicitly, but it is cheaper not to write one.</para>
    /// </summary>
    BoolExpr Encode(SmtContext context, StateVars symbolic, TState concrete);

    /// <summary>
    /// The model transition corresponding to a step label, or <c>null</c> if the model has none.
    ///
    /// <para><b>Returning null is a finding, not a fallback.</b> It means the target can do
    /// something the model does not describe, so every proof over that model is narrower than it
    /// appears. The checker fails on it.</para>
    ///
    /// <para><b>Return a label the model actually has.</b> <c>Label</c> is an ordinary type you can
    /// construct, but only the labels in <c>TransitionRelation.Labels</c> have had a consecution and
    /// a non-vacuity obligation discharged for them. One you build yourself that is not in that list
    /// is backed by no proof at all, so the checker tests membership rather than null — constructing
    /// a label is not the same as the model having one.</para>
    /// </summary>
    Label? MapLabel(string stepLabel);
}
