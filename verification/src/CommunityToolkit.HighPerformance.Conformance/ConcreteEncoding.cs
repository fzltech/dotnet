// ===========================================================================
// TRUST BOUNDARY — this file is the trust boundary of the verification layer.
//
// Nothing verifies this file. Every conformance-backed property in this layer
// depends on it being right, and a subtle mistake here makes those proofs
// meaningless while the gate stays green.
//
// fmverify deliberately does not generate a working implementation: a plausible
// wrong one is worse than an empty one.
// ===========================================================================

using Microsoft.Z3;
using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Ties CommunityToolkit.HighPerformance's concrete state to the model's symbolic state, and its step labels to the
/// model's transitions.
///
/// <para>Replace <c>TState</c> with the same type <c>Stepper</c> uses. Keep this small enough that a
/// wrong encoding is visible by reading it — reviewability is the only defence there is here, since
/// nothing downstream can tell you the mapping was wrong.</para>
/// </summary>
public sealed class ConcreteEncoding : IConcreteEncoding<object>
{
    public BoolExpr Encode(SmtContext context, StateVars symbolic, object concrete) =>
        throw new NotImplementedException(
            "Implement Encode: conjoin one equality per model state variable, tying each symbolic " +
            "variable to the corresponding value in the concrete state. Nothing that can conflict — " +
            "a contradictory encoding makes every check over it pass vacuously.");

    public Label? MapLabel(string stepLabel) =>
        throw new NotImplementedException(
            "Implement MapLabel: return the TransitionRelation.Labels entry describing this step, " +
            "or null if the model has no transition for it. Null is a finding, not a fallback.");
}
