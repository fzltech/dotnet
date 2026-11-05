using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// Owns the Z3 context and the symbols of the model's state. SEED: the state is a
/// single integer `counter`. Replace this with your system's real state (see
/// TRUST-BOUNDARY.md and the verified-properties skill).
/// </summary>
public sealed class SmtContext : IDisposable
{
    public SmtContext() => Z3 = new Context();
    public Context Z3 { get; }

    public StateVars FreshState(string suffix) =>
        new((IntExpr)Z3.MkIntConst($"counter_{suffix}"));

    /// <summary>
    /// A frozen parameter of the run — a capacity, a limit, a mode — as opposed to state. Use
    /// <see cref="ConfigInt"/> / <see cref="ConfigBool"/> rather than declaring constants yourself.
    ///
    /// <para><b>These are deliberately not suffixed.</b> <see cref="FreshState"/> suffixes every
    /// symbol it creates, so pre and post get different constants; that is exactly right for state
    /// and exactly wrong for configuration. Unsuffixed, pre and post share one symbol, so
    /// "configuration does not change mid-run" holds by construction and there is nothing for
    /// <c>Frame</c> to freeze. Suffix these and they silently become unframed free variables the
    /// solver may change under you — every consecution obligation weakens and nothing reports it.
    /// That is why this is a method and not a convention you reinvent.</para>
    ///
    /// <para><b>Leaving one unconstrained is a feature.</b> A free symbolic capacity means a proof
    /// covers every capacity, not the one you picked. Constrain only what must be true — and put
    /// that in <c>Formulas.ConfigurationWellFormed</c>, never in <c>Init</c> alone; see the remarks
    /// there for why.</para>
    /// </summary>
    public IntExpr ConfigInt(string name) => (IntExpr)Z3.MkIntConst($"config_{name}");

    /// <inheritdoc cref="ConfigInt"/>
    public BoolExpr ConfigBool(string name) => (BoolExpr)Z3.MkBoolConst($"config_{name}");

    public void Dispose() => Z3.Dispose();
}

// SEED state. Add fields here as you model your system.
public sealed record StateVars(IntExpr Counter);

/// <summary>
/// The finite bounds this model is proven over, as data. Every property's recorded scope takes
/// its bound from here rather than restating it, because a hand-written bound is a number that
/// can disagree with the model it describes.
/// </summary>
public sealed record ModelBound
{
    public string Describe() => "one integer state variable";
}
