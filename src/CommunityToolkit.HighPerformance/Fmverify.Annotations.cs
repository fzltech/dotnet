using System;

namespace Fmverify.Annotations;

/// <summary>
/// The allowance's own version, so a copy that has drifted from the fmverify that generated the
/// surrounding layer can be told apart from one that has not.
///
/// <para>The conformance checker reads this off the target assembly and compares it with the
/// layer's pin (<c>PinnedVersions.Annotations</c> in the fmverify repository); a mismatch is a
/// named gate failure rather than silent drift. <c>fmverify add-annotations --force</c> is the
/// refresh path. Bump this whenever an attribute's shape or semantics change — the checker
/// matches attribute types by full name, so a renamed attribute in an old copy would otherwise
/// go unread without a sound.</para>
/// </summary>
public static class Allowance
{
    /// <summary>The version fmverify pins and the checker compares against.</summary>
    public const string Version = "2026.09.1";
}

/// <summary>
/// Marks a method as an observable step of the slice under verification, and names the model
/// transition it produces. AllowMultiple: one method can produce several transitions.
///
/// <para>The checker requires every declared name to be a transition some observed run actually
/// produced. What the declaration means depends on the shape of the seam it sits on: on a stepping
/// seam, "this method is one transition"; on a seam that runs to completion, "this run produces
/// these transitions", with one attribute per transition.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class StepAttribute(string name) : Attribute
{
    /// <summary>The transition name, matched against the model's label kinds.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// Declares that driving the seam twice on the same scenario does the same thing. Checked by the
/// conformance checker, not taken on trust.
///
/// <para>On a stepping seam the checker drives it twice and compares the two step-label sequences.
/// On a seam that runs to completion it observes the run twice and compares the two event sequences
/// the target emitted, which requires the event type to be value-equal — a record, or a type that
/// overrides <c>Equals</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DeterministicAttribute : Attribute { }

/// <summary>
/// A predicate that must hold where the seam is entered, written either as the name of a public
/// parameterless <c>bool</c> member or as an expression over the receiver's members.
///
/// <para>The two are one thing, not two: <c>nameof(CanPlace)</c> is the one-identifier case of an
/// expression and resolves to the same member it always did. An expression may compare members and
/// integer literals, add and subtract, and combine with <c>&amp;&amp;</c>, <c>||</c> and <c>!</c> —
/// <c>[Requires("Placed &lt; Capacity")]</c>. It may not name the entry state with <c>old(...)</c>:
/// a precondition holds at a single state, so there is no earlier one to name. The generated
/// <c>PredicateExpression</c> carries the full grammar.</para>
///
/// <para>The receiver depends on the shape. On a stepping seam it is the state before the step,
/// checked on every observed step. On a seam that runs to completion it is the driver the run was
/// performed with, checked once per run — and the run has already happened by then, because the
/// observation that carries the driver is built from a completed run. So this is an obligation on
/// the bridge as much as on the code: hand back a driver whose entry conditions the run did not
/// destroy, or the predicate judges a state the seam was not entered in.</para>
///
/// <para>The predicate is resolved on whichever object the shape supplies, while the annotation
/// itself belongs to the class that declares the seam. On a stepping bridge whose adapter defines
/// its own replay state, no object the code produced is available to receive it, so it is not
/// checked — that gap is what the run shape closes.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresAttribute(string methodName) : Attribute
{
    public string MethodName { get; } = methodName;
}

/// <summary>
/// A predicate that must hold after the seam runs. Same shape as <see cref="RequiresAttribute"/>, on
/// the other side of the seam: the state after the step, or the result the run returned.
///
/// <para><b>This is the one annotation that may name the state the seam was entered in</b>, with
/// <c>old(...)</c> around a member name: <c>[Ensures("PendingCount == old(PendingCount) - 1")]</c>
/// says one step settles exactly one item. That is a relation between before and after, which no
/// helper method can express, because none of them receives both.</para>
///
/// <para><c>old(...)</c> works on a stepping seam only. A seam that runs to completion hands its
/// driver back afterwards, so no pre-run object ever reaches the checker, and an expression that
/// reaches for one there is a named failure rather than a quiet read of the after-value. State such
/// a postcondition over the result alone, or drive the seam with a Stepper.</para>
///
/// <para>On a stepping seam, whether this annotation is checked at all depends on the bridge
/// answering <c>IStepper.SeamOwner</c>: a member-level contract belongs to the class that declares
/// the seam, which is usually not the type the replay state is.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnsuresAttribute(string methodName) : Attribute
{
    public string MethodName { get; } = methodName;
}

/// <summary>
/// A predicate that must hold on each instance of the annotated type the shape offers as a receiver
/// — the name of a public parameterless <c>bool</c> member, or an expression over the type's members.
///
/// <para>No <c>old(...)</c>: an invariant holds at a single state, so there is no earlier one to
/// name. A claim about how one state relates to the next is a <see cref="EnsuresAttribute"/>.</para>
///
/// <para>On a stepping seam that is every observed state. On a seam that runs to completion it is
/// the result the run returned, and only that — the events the run emitted are observed but never
/// invariant-checked. Checked once per run, which says nothing about the states part-way through
/// that run: on that shape there is no such object to check.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class InvariantAttribute(string methodName) : Attribute
{
    public string MethodName { get; } = methodName;
}

/// <summary>
/// An integer quantity of the annotated type that must fall as the run proceeds, anchoring the
/// model's ranking function to a real, observed decreasing quantity. Either the name of an
/// <c>int</c> member or an expression yielding one — <c>[Measure("Pending + Retrying")]</c>.
///
/// <para>On a stepping seam it is read from the state and must decrease on every progress step. On
/// a seam that runs to completion it is read from each event of the annotated type in order, and
/// the rule is non-increasing across that sequence with at least one strict decrease: a run emits
/// starts as well as finishes, so per-event decrease would be the wrong rule, and a sequence that
/// never falls anchors no termination claim.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method)]
public sealed class MeasureAttribute(string memberName) : Attribute
{
    public string MemberName { get; } = memberName;
}
