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

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Drives one slice of CommunityToolkit.HighPerformance through the steps the model describes.
///
/// <para>Replace <c>TScenario</c> and <c>TState</c> with your own types — whatever your target
/// takes as input, and whatever it hands back as the thing that changes. Then implement the two
/// members below against the target's existing public seam.</para>
///
/// <para>If you cannot write this without widening the target's visibility, pick a different slice.
/// An <c>internal</c> added for the benefit of a verification tool is a change to the target's
/// design made for the wrong reason.</para>
/// </summary>
public sealed class Stepper : IStepper<object, object>
{
    public object Start(object scenario) =>
        throw new NotImplementedException(
            "Implement Start: return the concrete state your target begins this scenario in.");

    public Step<object>? Advance(object state) =>
        throw new NotImplementedException(
            "Implement Advance: take one step, and return its label and the resulting state — " +
            "or null when there is no step left. Do not mutate the state passed in.");
}
