namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// One run of the target, as the target itself reports it. You build one of these in
/// <see cref="IRunObserver{TScenario}.Observe"/>.
///
/// <para><b>Both members are the target's own objects, never the bridge's.</b> That is the whole
/// point of this shape: the code's annotations get checked on things the code produced, so a
/// predicate that passes says something about the target rather than about a reconstruction.</para>
///
/// <para>They are <c>object</c> deliberately. Every annotation check is already reflective — the
/// conformance project matches attribute types by full name because it must not reference
/// <c>Fmverify.Annotations</c> — so typing them would buy nothing and would push type parameters
/// through every signature that carries one.</para>
///
/// <para><b>The driver used to be here too, and removing it removed a defect class.</b> The checker
/// holds the driver already — it is what <c>Configure</c> returned — so handing it back was
/// redundant, and the redundancy is what made a driver-and-result swap expressible: two
/// <c>object</c> members, no build error, every contract check silently skipped, green gate. These
/// two cannot be swapped, because they are not the same type.</para>
///
/// <para>What is left is narrower and is caught rather than designed away: an implementation can
/// pass the <em>driver</em> where the result belongs. An <c>[Ensures]</c> naming a member of the
/// result type does not resolve on the driver, and an unresolvable name is a failure and never
/// <c>false</c>.</para>
///
/// <para><paramref name="Events"/> must be value-equal to compare — a record, or a type that
/// overrides <c>Equals</c>. A <c>[Deterministic]</c> seam is checked by observing twice and
/// comparing the two event sequences, and reference equality would fail that on an honest run.</para>
/// </summary>
/// <param name="Result">What the run returned. <c>[Ensures]</c> and <c>[Invariant]</c> are checked
/// on it after the run.</param>
/// <param name="Events">The ordered events the run produced. <c>[Measure]</c> is read from each in
/// turn.</param>
public sealed record RunObservation(object Result, IReadOnlyList<object> Events);

/// <summary>
/// Declares that this bridge's target runs to completion rather than stepping, and hands back what
/// one run did. You implement this — but only if your target has this shape.
///
/// <para><b>When to reach for it.</b> Your seam is one call that runs to completion and returns a
/// result carrying a list of what happened — a scheduler's <c>Run()</c>, a batch process, a solver.
/// There is no method that is one transition, and no object that is the state part-way through, so
/// the annotations on your code have nothing to be checked against. This gives them something.</para>
///
/// <para><b>What it does not change.</b> The model is still compared against the replay your
/// <c>IStepper</c> produces, exactly as on a stepping bridge. This shape changes what the code's own
/// annotations are checked on, not what the model is checked against — you still write a Stepper,
/// and it is still the thing the proof rests on.</para>
///
/// <para><b>What a green run then says, and does not.</b> The contract predicates held on the
/// objects the code produced for these runs, and a declared quantity fell across the events the code
/// emitted. Nothing about states part-way through the run: there is no such object to check. That
/// claim is available only if your target exposes a per-step snapshot, and offering one is your
/// choice, not something this layer asks for.</para>
/// </summary>
public interface IRunObserver<TScenario, TDriver>
{
    /// <summary>
    /// Build the object that will run this scenario, and do not run it.
    ///
    /// <para><b>This method exists so that <c>[Requires]</c> means something.</b> It used to be one
    /// call that configured and ran, which left a precondition on the seam being read off a driver
    /// the run had already finished with — so a driver that accumulates state could violate its
    /// entry condition and have the run repair it, and the check passed. The obligation sat with
    /// the adopter to hand back a driver the run had not disturbed, and nothing enforced it: the one
    /// annotation in this layer whose green was honour-based.</para>
    ///
    /// <para>Return it configured and untouched. The checker reads every member-level
    /// <c>[Requires]</c> on it before calling <see cref="Observe"/>.</para>
    /// </summary>
    TDriver Configure(TScenario scenario);

    /// <summary>
    /// Run the seam on a driver <see cref="Configure"/> produced, and hand back what it did.
    ///
    /// <para>Called once per driver. A <c>[Deterministic]</c> seam is checked by configuring twice
    /// and observing each — two independent runs, rather than two runs of one object, which is what
    /// the declaration actually claims.</para>
    ///
    /// <para><b>The scenario comes back too, so that this interface needs no state of its own.</b>
    /// Where a target takes its inputs at construction — a scheduler given its task set — the driver
    /// carries them and this argument is unused. Where it takes them at the call — an allocator given
    /// its items at <c>Allocate</c> — they have to reach the run somehow, and the alternative was an
    /// observer that remembered the scenario between its two calls. A stateful observer is a poor
    /// thing to put on the trust boundary: two configures in a row would overwrite it, which is
    /// exactly what the determinism check does.</para>
    /// </summary>
    RunObservation Observe(TScenario scenario, TDriver driver);
}
