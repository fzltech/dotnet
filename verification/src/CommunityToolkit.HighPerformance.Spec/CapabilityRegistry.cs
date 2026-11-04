namespace CommunityToolkit.HighPerformance.Spec;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    /// <summary>The bound every property below is proven over. Recorded, never retyped.</summary>
    private static readonly ModelBound Bound = new();

    private static readonly PropertyDefinition Seed = new(
        Id: "seed-invariant",
        Kind: PropertyKind.Safety,
        ClassId: "safety.inductive-invariant",
        Meaning: "SEED trivial invariant (true). Replace with a real property; see verified-properties.",
        Invariant: (f, s) => f.Invariant(s),
        Safety: (f, s) => f.Safety(s),
        Scope: new PropertyScope(
            Bound: Bound.Describe(),
            Preconditions: "none - the model has no guards",
            ArithmeticDomain: "integer",
            Notes: "Trivially true (Invariant = Safety = true). This proves nothing about any real system; it exists to make the proof pipeline green from the start."),
        Backing: TrustBacking.ModelOnly);

    public IReadOnlyList<PropertyDefinition> Properties { get; } = new[] { Seed };

    // No liveness class is advertised for this state kind, and the asymmetry with the multiset
    // registry is deliberate. The seed transition leaves the counter unchanged, so no measure over
    // this model strictly decreases, `liveness.ranking-function` could not discharge its
    // ProofOfCapability, and the descriptor would exit non-zero — which is the honesty mechanism
    // working, not a bug. Do not copy the class across to make the two registries look alike: give
    // this model a transition that makes progress first, then add the class.
    public IReadOnlyList<PropertyClass> SupportedClasses { get; } = new[]
    {
        new PropertyClass(
            Id: "safety.inductive-invariant",
            Kind: PropertyKind.Safety,
            Meaning: "A state predicate proven in every reachable state by an inductive invariant (Init non-vacuity, Initiation, Consecution ∀label, Safety, non-vacuity ∀label).",
            ObligationShape: "InitNonVacuous; Initiation; Consecution(forall label); Safety; NonVacuity(forall label)",
            TemplateId: "safety-inductive-invariant",
            ProofOfCapability: () => FullDischarge(Seed)),
    };

    public IReadOnlyList<EncodedProperty> AlreadyEncoded =>
        Properties.Select(p => new EncodedProperty(p.Id, p.Kind, p.Meaning, p.ClassId, p.Scope, p.Backing)).ToArray();

    private static VcResult FullDischarge(PropertyDefinition p)
    {
        var results = new Verifier().VerifyAll(p);
        var firstUnproven = results.FirstOrDefault(r => r.Outcome != Outcome.Proven);
        return firstUnproven ?? new VcResult($"AllObligations[{p.Id}]", Outcome.Proven, null);
    }
}
