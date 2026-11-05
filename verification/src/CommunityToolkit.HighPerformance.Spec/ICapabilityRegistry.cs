namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// A project's code-sourced declaration of what its verification engine can prove
/// (SupportedClasses) and what it already proves (Properties / AlreadyEncoded).
/// This is the single source of truth the descriptor emitter serialises.
/// </summary>
public interface ICapabilityRegistry
{
    IReadOnlyList<PropertyClass> SupportedClasses { get; }
    IReadOnlyList<PropertyDefinition> Properties { get; }
    IReadOnlyList<EncodedProperty> AlreadyEncoded { get; }
}
