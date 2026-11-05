using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

// SEED label set. Add your system's transitions here.
public enum Label { Step }

/// <summary>
/// The independent model's transition relation. SEED: one no-op `Step` transition
/// (guard true, counter unchanged) so Consecution and non-vacuity have something to
/// discharge. Replace with your system's real guards/effects.
/// </summary>
public sealed class TransitionRelation
{
    private readonly SmtContext _c;
    public TransitionRelation(SmtContext c) => _c = c;

    public static readonly IReadOnlyList<Label> Labels = new[] { Label.Step };

    /// <summary>
    /// When this transition may fire.
    ///
    /// <para><b>If a guard or an effect depends on a value the code does not carry between steps</b>
    /// — a random draw, an external input, a nondeterministic abort — read it as a draw rather than
    /// adding it to <c>StateVars</c> or unrolling it into more labels:
    /// <c>var pivot = Draws.Value(_c, "pivot");</c>. It is free at every step, so the proof covers
    /// every value the code could produce. See <c>Draws</c> for what that claim is and is not.</para>
    /// </summary>
    public BoolExpr Guard(Label label, StateVars pre) => _c.Z3.MkTrue();

    public BoolExpr Clause(Label label, StateVars pre, StateVars post) =>
        _c.Z3.MkAnd(Guard(label, pre), _c.Z3.MkEq(post.Counter, pre.Counter));
}
