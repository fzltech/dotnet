using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Spec.Descriptor;

/// <summary><c>Engine</c> is the <c>ProofEngine</c> name of the machinery the class runs.</summary>
public sealed record CapabilityDto(string Id, string Kind, string Engine, string Meaning, string ObligationShape, string TemplateId);

/// <summary>How far a property's proof reaches, as data a consumer can read.</summary>
public sealed record ScopeDto(string? Bound, string? Preconditions, string? ArithmeticDomain, string? Notes);

/// <summary>
/// <c>Scope</c> is null when the property did not state one — which is a different claim from
/// a scope whose fields are all empty, so it is serialised as null rather than as an object.
/// <c>Backing</c> is the <c>TrustBacking</c> name; it is <c>ModelOnly</c> unless the layer
/// could justify otherwise. <c>Engine</c> is the <c>ProofEngine</c> name of what discharged it.
/// </summary>
public sealed record EncodedPropertyDto(
    string Id,
    string Kind,
    string Engine,
    string Meaning,
    string ClassId,
    ScopeDto? Scope,
    string Backing);

/// <summary>
/// The document consumers parse. <c>SchemaVersion</c> comes first and exists so a consumer can
/// tell which shape it is reading; see <c>docs/descriptor-schema.md</c> in the fmverify
/// repository for the documented contract.
/// </summary>
public sealed record DescriptorDocument(
    int SchemaVersion,
    IReadOnlyList<CapabilityDto> Capabilities,
    IReadOnlyDictionary<string, string> ProjectMap,
    IReadOnlyList<EncodedPropertyDto> ExistingProperties);

public static class Descriptor
{
    /// <summary>Bump when the document's shape changes. Consumers parse this.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Serialise the registry, INCLUDING a capability class only if its ProofOfCapability
    /// currently returns Proven (honesty by construction — spec §6.3).
    /// </summary>
    public static DescriptorDocument Build(ICapabilityRegistry reg, IReadOnlyDictionary<string, string> projectMap)
    {
        var caps = reg.AllClasses()
            .Where(cls => cls.ProofOfCapability().Outcome == Outcome.Proven)
            .Select(cls => new CapabilityDto(cls.Id, cls.Kind.ToString(), cls.Engine.ToString(), cls.Meaning, cls.ObligationShape, cls.TemplateId))
            .ToArray();

        var props = reg.AllEncoded()
            .Select(e => new EncodedPropertyDto(
                e.Id,
                e.Kind.ToString(),
                e.Engine.ToString(),
                e.Meaning,
                e.ClassId,
                e.Scope is null
                    ? null
                    : new ScopeDto(e.Scope.Bound, e.Scope.Preconditions, e.Scope.ArithmeticDomain, e.Scope.Notes),
                e.Backing.ToString()))
            .ToArray();

        return new DescriptorDocument(CurrentSchemaVersion, caps, projectMap, props);
    }

    /// <summary>True if every advertised class actually proved (none were dropped).</summary>
    public static bool AllAdvertisedClassesProved(ICapabilityRegistry reg) =>
        reg.AllClasses().All(cls => cls.ProofOfCapability().Outcome == Outcome.Proven);
}
