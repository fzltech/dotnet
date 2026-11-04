using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Builds the scenarios the checker drives. fmverify supplies this whole; you decide what an item
/// is and which orderings matter.
///
/// <para><b>Permuting is the default, not an option.</b> A greedy, order-dependent target reaches
/// states that one fixed input order never produces, so checking a single ordering says less than
/// it appears to. An adopter who does not need permutations loses only time; one who needs them and
/// does not get them gets a conformance result that means much less than it reads as.</para>
///
/// <para><b>Enumeration is the default.</b> Everything above <see cref="Sampled"/> is exhaustive up to
/// a stated cap and deterministic — the same reasoning as the unrolled, quantifier-free encoding in
/// the model. Past that cap the space cannot be enumerated at all, and sampling is the honest way
/// past it rather than pretending a truncated subset is complete. What makes that safe is that
/// <see cref="Sampled"/> takes a required seed and no other source of randomness: a sampled run
/// without a recorded seed makes a red gate irreproducible, which is how a real failure gets
/// dismissed as flakiness, and the mandatory seed is what stops that from happening.</para>
/// </summary>
public static class ScenarioEnumerator
{
    /// <summary>
    /// Above this many items, permuting is refused rather than truncated. Nine items is 362,880
    /// orderings per scenario and would dominate the gate's runtime; eight is 40,320, which is
    /// already the point to think. Refusing beats silently returning a subset, because a truncated
    /// permutation set still reads as "we tried every order".
    /// </summary>
    public const int MaxPermutableItems = 8;

    /// <summary>
    /// Every ordering of <paramref name="items"/>, in a deterministic order.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// More than <see cref="MaxPermutableItems"/> items. Take a smaller slice, or choose the
    /// orderings you care about by hand and pass them as scenarios directly.
    /// </exception>
    public static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count > MaxPermutableItems)
            throw new ArgumentException(
                $"permuting {items.Count} items is {Factorial(items.Count):N0} orderings, past the " +
                $"cap of {MaxPermutableItems}. Truncating would leave a subset that still reads as " +
                "exhaustive, so this refuses instead. Use a smaller slice, or pass chosen orderings " +
                "as scenarios yourself.",
                nameof(items));

        return Permute(items.ToArray(), 0);

        static IEnumerable<IReadOnlyList<T>> Permute(T[] buffer, int first)
        {
            if (first == buffer.Length - 1)
            {
                yield return buffer.ToArray();
                yield break;
            }

            for (var i = first; i < buffer.Length; i++)
            {
                (buffer[first], buffer[i]) = (buffer[i], buffer[first]);
                foreach (var permutation in Permute(buffer, first + 1)) yield return permutation;
                (buffer[first], buffer[i]) = (buffer[i], buffer[first]);
            }
        }
    }

    /// <summary>
    /// The first <paramref name="max"/> of <paramref name="source"/>, and whether that was all of
    /// it.
    ///
    /// <para>The source is the point. "Conformance checked" means nothing without "over how many, and
    /// was that all of them", and a truncated run that does not say so is a bounded claim presented
    /// as a complete one. It travels into the report and from there into the property's recorded
    /// scope.</para>
    /// </summary>
    public static (IReadOnlyList<T> Items, ScenarioSource Source) Bounded<T>(IEnumerable<T> source, int max)
    {
        // One extra taken deliberately: it is the only way to tell "exactly max" from "more than
        // max" without enumerating the whole sequence, which may be expensive or unbounded.
        var taken = source.Take(max + 1).ToList();
        return taken.Count > max
            ? (taken.Take(max).ToArray(), ScenarioSource.Truncated)
            : (taken, ScenarioSource.Exhaustive);
    }

    /// <summary>
    /// <paramref name="count"/> scenarios drawn from a seeded generator.
    ///
    /// <para>For a scenario space too large to enumerate. <see cref="Permutations"/> refuses past
    /// <see cref="MaxPermutableItems"/> because a truncated permutation set still reads as
    /// exhaustive; sampling is the honest way past that cap, and the report says it sampled.</para>
    ///
    /// <para><b>The seed is required, and that is the whole design.</b> A sampled run that cannot be
    /// replayed produces a red gate nobody can reproduce, which gets dismissed as flakiness rather
    /// than investigated. With the seed recorded, a failure is replayed by pinning it.</para>
    ///
    /// <para>You supply the generator because you own the scenario type — item counts, durations,
    /// graphs, capacities. Draw every varying part of a scenario from the <see cref="Random"/> handed
    /// to you and nothing else, or the run stops being reproducible from its seed.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is below 1. A run over no scenarios compares nothing and already
    /// fails <c>ConformanceReport.Passed</c>; refusing here says why.
    /// </exception>
    public static IEnumerable<T> Sampled<T>(Func<Random, T> generate, int count, int seed)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "a sampled run needs at least one scenario to compare anything.");

        return Draw();

        IEnumerable<T> Draw()
        {
            // One generator for the whole run, so the sequence is a function of the seed alone.
            var random = new Random(seed);
            for (var i = 0; i < count; i++) yield return generate(random);
        }
    }

    /// <summary>
    /// A fresh seed, for a hunt.
    ///
    /// <para>Safe to be unpredictable ONLY where the run cannot earn a backing: hunts live in
    /// <c>ConformanceRegistry.Hunts</c>, which is never registered as evidence. A red hunt names its
    /// seed in the report, and pinning that seed into <c>Checks</c> turns the finding into a
    /// deterministic regression test. Never use this in <c>Checks</c> — it would make the proof gate
    /// flaky by construction.</para>
    /// </summary>
    public static int Fresh() => Random.Shared.Next();

    private static long Factorial(int n)
    {
        var result = 1L;
        for (var i = 2; i <= n; i++) result *= i;
        return result;
    }
}
