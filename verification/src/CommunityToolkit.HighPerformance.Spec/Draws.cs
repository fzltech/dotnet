using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// A value the model reads and never controls: a random draw, an external input, a nondeterministic
/// abort. Free at every step, and deliberately not part of the state.
///
/// <para>fmverify supplies this whole. There is nothing to fill in, and nothing in the seeded model
/// uses it — a draw is a domain decision, and a seeded free variable that nothing constrains is the
/// fastest route to a vacuous proof somebody trusts.</para>
///
/// <para><b>What it is for.</b> A guard or an effect sometimes depends on a value the code does not
/// carry between steps. Two workarounds suggest themselves and both are bad: unrolling every choice
/// into a transition label makes the label count the product of every choice's range, and putting
/// the value in <c>StateVars</c> lands it on the invariant surface, so every property has to say
/// something about it and the conformance encoding is forced to pin it to whatever the target
/// happened to draw.</para>
///
/// <para><b>What a proof over one means, and what it does not.</b> The variable is unconstrained, so
/// the solver is free to choose it — the adversary picks the draw, and the property has to hold
/// anyway. That covers <em>every sequence the generator could produce</em>, which is a **stronger**
/// claim than a probabilistic one and is **not** a probabilistic one. "Holds for every draw" and
/// "holds in expectation" are different claims needing different engines, and conflating them is the
/// main hazard in this area: a property proven here must never be described as covering the
/// distribution. Mean fitness per generation, expected convergence time, probability of a result
/// within k steps — none of those is this.</para>
///
/// <para><b>Usage.</b> Inside <c>Guard</c> or <c>Effects</c>, ask for one by name:</para>
///
/// <code>
/// var pivot = Draws.Value(_c, "pivot");
/// return _c.Z3.MkAnd(
///     _c.Z3.MkGe(pivot, _c.Z3.MkInt(0)),               // constrain its range if the code does
///     _c.Z3.MkLt(pivot, _c.Z3.MkInt(Bound.Items)),
///     _c.Z3.MkEq(post.Counts[label.Container][0], pivot));
/// </code>
///
/// <para>Constrain its range where the code does. An entirely free integer makes the adversary
/// stronger than the real generator, and a property that fails only for a draw the code cannot
/// produce is a false failure — the same over-strong-abstraction hazard as an unbounded clock.</para>
///
/// <para><b>Earning a backing over a draw.</b> Report the value the code drew from
/// <c>Step.Draws</c>, keyed by the same name, and the checker pins the symbolic draw to it — so the
/// transition is compared against the run that happened. Without a reported value the comparison
/// asks whether <em>some</em> draw explains the step, which is weaker than it looks and is named in
/// <c>bridge-coverage.txt</c> rather than failing the gate. **A property resting on a draw that
/// appears there should not be <c>ConformanceBacked</c>**: the proof covers every draw and the check
/// covered none in particular.</para>
/// </summary>
public static class Draws
{
    /// <summary>
    /// The prefix every draw's name carries, and it is load-bearing rather than cosmetic: it is how
    /// <see cref="OccursIn"/> recognises one, and it is what keeps a draw from colliding with a
    /// state cell however the model names its cells. <c>FreshState</c> suffixes; this prefixes.
    /// Reserved — do not name a state variable with it.
    /// </summary>
    public const string Prefix = "draw!";

    /// <summary>
    /// The draw called <paramref name="name"/> in this context: an unconstrained integer the solver
    /// chooses.
    ///
    /// <para><b>The same name in the same context is the same value, and Z3 gives that for
    /// free.</b> Terms are hash-consed, so two calls return one AST — a guard and an effect can talk
    /// about the same draw without this class remembering anything. The first version of this file
    /// memoised per context, which was dead weight: nothing could observe the difference, so nothing
    /// could test it either.</para>
    ///
    /// <para>Across contexts the draws are independent, and that is what makes each obligation draw
    /// again. Every obligation in <c>Verifier</c> builds its own context and conjoins <c>Clause</c>
    /// exactly once, so per-context is per-step — the freshness contract is a property of the
    /// Verifier's shape rather than of anything here.</para>
    /// </summary>
    public static IntExpr Value(SmtContext context, string name) =>
        (IntExpr)context.Z3.MkIntConst(Prefix + name);

    /// <summary>
    /// Whether any draw appears in <paramref name="expression"/>.
    ///
    /// <para><b>The guard that keeps a draw out of a property.</b> A draw is not state, so an
    /// invariant that reads one is a claim about the value the solver picked rather than about the
    /// system — provable or refutable for reasons that have nothing to do with the code. The
    /// Verifier asks this before discharging and refuses the property instead, which is the
    /// difference between a rule and a note in a comment.</para>
    ///
    /// <para>The context argument is unused and kept anyway: a draw is recognised by its name, and
    /// a caller should not have to know that. Were draws ever to become context-scoped, every call
    /// site already passes what that would need.</para>
    /// </summary>
    public static bool OccursIn(SmtContext context, Expr expression) =>
        NamesIn(context, expression).Count > 0;

    /// <summary>
    /// Which draws appear in <paramref name="expression"/>, by the name the model asked for.
    ///
    /// <para>What the conformance checker needs, where <see cref="OccursIn"/> is what the Verifier
    /// needs: to pin a draw to the value the target reported it must know which draws the clause
    /// reads, and to say a draw went unpinned it must know which it could not.</para>
    /// </summary>
    public static IReadOnlySet<string> NamesIn(SmtContext context, Expr expression)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        Walk(expression, new HashSet<uint>(), found);
        return found;
    }

    /// <summary>
    /// Depth-first over the term, remembering which nodes have been seen. Z3 terms are
    /// hash-consed — a shared subterm is one node reachable by many paths — so an unguarded walk
    /// over a large frame conjunction revisits the same subtree combinatorially.
    /// </summary>
    private static void Walk(Expr expression, HashSet<uint> seen, HashSet<string> found)
    {
        if (!seen.Add(expression.Id)) return;

        if (expression.IsConst)
        {
            var name = expression.FuncDecl.Name.ToString();
            if (name.StartsWith(Prefix, StringComparison.Ordinal)) found.Add(name[Prefix.Length..]);
            return;
        }

        foreach (var arg in expression.Args) Walk(arg, seen, found);
    }
}
