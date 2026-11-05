namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// Claims proven for every size by the unbounded (Dafny) engine. Empty: this layer has not opted in.
///
/// <para>Run <c>fmverify add-unbounded</c> to add the engine. It seeds this file with a capability
/// class and a claim, and the gate, the descriptor and <c>TRUST-BOUNDARY.md</c> read it alongside
/// <see cref="CapabilityRegistry"/> — so nothing else has to be edited to enrol a claim.</para>
/// </summary>
public static class UnboundedRegistry
{
    public static IReadOnlyList<PropertyClass> Classes { get; } = [];

    public static IReadOnlyList<UnboundedProperty> Properties { get; } = [];
}
