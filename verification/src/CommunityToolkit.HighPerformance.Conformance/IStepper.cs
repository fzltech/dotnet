namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Drives the target's real code one observable step at a time. You implement this.
///
/// <para><b>Advance returns the post-state; it must not mutate the state passed in.</b> Domain
/// objects are commonly immutable-by-copy, and an adapter shaped around in-place mutation cannot
/// report a pre/post pair at all — which is what the conformance checker compares.</para>
///
/// <para>The scaffold never constructs your target. Hold whatever dependencies you need — a
/// composed service graph, a settings object, a factory — in your implementation.</para>
///
/// <para>What counts as one step is your judgement about the domain. Pick the smallest unit the
/// model has a transition for.</para>
///
/// <para><b>The slice you drive through this must be deterministic.</b> No wall-clock, no shared
/// random source, no parallel fan-out beneath the seam. A scenario that yields a different trace on
/// a second run cannot be conformance-checked at all, and a flaky conformance failure gets dismissed
/// rather than investigated.</para>
/// </summary>
public interface IStepper<TScenario, TState>
{
    /// <summary>The concrete state the target begins this scenario in.</summary>
    TState Start(TScenario scenario);

    /// <summary>
    /// Advance one step. Returns the label of what the target did and the resulting state, or
    /// <c>null</c> when the target has no step left to take.
    /// </summary>
    Step<TState>? Advance(TState state);

    /// <summary>
    /// The target object whose method <see cref="Advance"/> drives — the one that owns the seam.
    /// Return <c>null</c> where the state itself is that object.
    ///
    /// <para>Only <c>[Requires]</c> and <c>[Ensures]</c> need it, and only to decide whether they
    /// are <em>this</em> bridge's obligation: a member-level contract belongs to the class that
    /// declares the seam, which is often not the type the state is. Where those two differ and this
    /// returns null, such a contract cannot be attributed to this bridge and is reported rather than
    /// checked — so answering here is what turns those annotations on. The predicates themselves are
    /// still read on the state, before and after the step; this changes only which annotations
    /// apply.</para>
    ///
    /// <para>Overriding is optional and a bridge that does not compiles unchanged. An unannotated
    /// target needs nothing here.</para>
    /// </summary>
    object? SeamOwner => null;
}

/// <summary>
/// One observed step: what the target did, and the state it produced.
/// </summary>
/// <param name="Label">The transition the target took, which <c>MapLabel</c> turns into a model label.</param>
/// <param name="Post">The state after the step.</param>
/// <param name="Draws">
/// The values this step read that the state does not carry — a random draw, an external input, a
/// nondeterministic abort. Keyed by the name the model asks for with <c>Draws.Value(_c, name)</c>.
///
/// <para><b>Only needed if your model reads one.</b> Optional and defaulted, so a bridge that does
/// not report draws compiles unchanged and an unannotated model needs nothing here.</para>
///
/// <para><b>What reporting one buys.</b> The model's draw is free, so without a value the transition
/// check asks whether <em>some</em> draw explains the step — which is weaker than it looks, and is
/// how a transition can be judged against a run the code never made. Report the value the code
/// actually drew and the checker pins the symbolic draw to it, so the comparison is about this run.
/// A draw the model reads and a step does not report is named in the coverage summary rather than
/// failing the gate: an existing bridge must not go red on an upgrade for a capability it predates.</para>
/// </param>
public sealed record Step<TState>(
    string Label,
    TState Post,
    IReadOnlyDictionary<string, int>? Draws = null);
