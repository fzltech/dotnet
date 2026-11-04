using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Which properties this bridge can produce evidence for. You fill this in.
///
/// <para>It lives here rather than beside the properties themselves because a check has to drive the
/// real target, and this is the only project that can reach it. <c>CapabilityRegistry</c> is in Spec,
/// which must stay independent of the code it models.</para>
///
/// <para><b>An empty list is the honest default.</b> A property may declare
/// <c>TrustBacking.ConformanceBacked</c> only once a check registered here passes for it — until
/// then the gate rejects the claim, which is the behaviour you want if you forget to come back.</para>
/// </summary>
public sealed class ConformanceRegistry
{
    public IReadOnlyList<ConformanceCheck> Checks =>
    [
        // One entry per property whose claim you can back with a run against the real target:
        //
        //   new ConformanceCheck("your-property-id", () => ConformanceChecker.Check(
        //       new Stepper(settings),
        //       new ConcreteEncoding(itemOrder),
        //       ScenarioEnumerator.Permutations(items),
        //       new CapabilityRegistry().Properties.Single(p => p.Id == "your-property-id"),
        //       ScenarioSource.Exhaustive)),
        //
        // If your target runs to completion rather than stepping — one call in, a result and a list
        // of what happened out — call CheckRun instead and hand it an IRunObserver. Which entry
        // point you call is the declaration of your target's shape; nothing infers it. The model
        // comparison is identical either way, and the replay the Stepper produces is still what the
        // model is compared against; what the observer changes is where the code's own annotations
        // are read. ADOPTION.md has the receivers.
        //
        //   new ConformanceCheck("your-property-id", () => ConformanceChecker.CheckRun(
        //       new Stepper(settings),
        //       new ConcreteEncoding(itemOrder),
        //       ScenarioEnumerator.Permutations(items),
        //       new CapabilityRegistry().Properties.Single(p => p.Id == "your-property-id"),
        //       ScenarioSource.Exhaustive,
        //       new RunObserver(settings))),
        //
        // Record what the run covered in that property's Scope.Bound — ConformanceReport carries a
        // BoundDescription for exactly this. A conformance-backed claim whose bound is unrecorded
        // reads as broader than it is.
    ];

    /// <summary>
    /// Runs that explore rather than gate. **Never evidence for a backing.**
    ///
    /// <para>A check in <see cref="Checks"/> must be deterministic, because the proof gate runs it
    /// and a conformance failure that comes and goes gets dismissed rather than investigated. That
    /// rules out the thing most likely to find a defect nobody has stated: a large sample over a
    /// scenario space too big to enumerate, with a different seed each time.</para>
    ///
    /// <para>Hunts are where that belongs. They are not registered as evidence — the proof gate's
    /// backing rule never sees them — and the generated verify script excludes them, so they cannot
    /// make the gate flaky and cannot promote a property however they are written.</para>
    ///
    /// <para><b>The workflow.</b> A hunt goes red and its report names the seed it drew. Pin that
    /// seed into a <see cref="Checks"/> entry and the finding becomes a deterministic regression
    /// test, in a commit somebody reviewed. Exploration turns into a gate deliberately.</para>
    ///
    /// <para>Run them with
    /// <c>dotnet test CommunityToolkit.HighPerformance.Verification.slnx -c Release --filter Category=hunt</c>.
    /// Large sample counts belong here rather than in <see cref="Checks"/>: the gate's runtime is
    /// your own to spend, and a hunt is where spending it is free. A registered check still runs
    /// twice per gate -- once from <c>TrustBoundary.UnjustifiedBackings</c> and once from
    /// <c>ConformanceProofs.Every_registered_conformance_check_passes</c> -- so a sampled check's
    /// cost is double what its count suggests.</para>
    /// </summary>
    public IReadOnlyList<ConformanceCheck> Hunts =>
    [
        // One entry per property worth exploring beyond what you can enumerate:
        //
        //   new ConformanceCheck("your-property-id", () =>
        //   {
        //       var seed = ScenarioEnumerator.Fresh();
        //       return ConformanceChecker.Check(
        //           new Stepper(settings),
        //           new ConcreteEncoding(itemOrder),
        //           ScenarioEnumerator.Sampled(random => YourScenario(random), count: 500, seed),
        //           new CapabilityRegistry().Properties.Single(p => p.Id == "your-property-id"),
        //           ScenarioSource.Sampled(seed));
        //   }),
    ];
}
