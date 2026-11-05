namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// The solver this layer pins. A proof is a proof by a particular solver at a particular
/// version, built from particular bytes — so all three travel together wherever any of them
/// appears, and the trust-boundary document records all three.
/// </summary>
public static class SolverInfo
{
    public const string PinnedZ3Version = "5.3.3";

    /// <summary>
    /// Hash of the solver package this layer was generated against. It travels with the
    /// version everywhere: a version says which solver was asked for, the pair says which
    /// bytes discharged the proof. fetch-solver.sh / fetch-solver.ps1 verify it before use
    /// and refuse a mismatch.
    /// </summary>
    public const string PinnedZ3Sha256 = "3ece54cefce4d58be1ce7c00bcff34b691e8076169cba095a057284bfb0b7822";

    /// <summary>
    /// Where the package came from. Not nuget.org: publishing of Microsoft.Z3 there stopped at
    /// 4.12.2 in 2023, and that version ships no linux-x64 native library at all.
    /// </summary>
    public const string PinnedZ3SourceUrl = "https://github.com/Z3Prover/z3/releases/download/z3-5.3.3/Microsoft.Z3.5.3.3.nupkg";
}
