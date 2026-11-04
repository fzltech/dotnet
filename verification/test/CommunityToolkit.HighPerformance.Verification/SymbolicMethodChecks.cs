using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.HighPerformance.Conformance;
using Xunit;

namespace CommunityToolkit.HighPerformance.Verification;

public class SymbolicMethodChecks
{
    [Fact]
    public void Annotated_methods_satisfy_contracts_and_do_not_overflow()
    {
        var targetSource = TargetSourceDirectory();
        if (!System.IO.Directory.Exists(targetSource))
            return;

        var results = SymbolicMethodChecker.CheckAll(targetSource);
        if (results.Count == 0)
            return;

        var failures = results.Where(r => r is not SymbolicCheckResult.Verified).ToList();
        Assert.True(failures.Count == 0, FormatFailures(failures));
    }

    private static string FormatFailures(IReadOnlyList<SymbolicCheckResult> failures)
    {
        var lines = failures.Select(f => f switch
        {
            SymbolicCheckResult.ContractViolation cv =>
                $"[ContractViolation] {cv.MethodName}: violated postcondition '{cv.ViolatedEnsures}'\n" +
                $"  Inputs: {FormatMap(cv.Inputs)}\n" +
                $"  Actual Result: {cv.ActualResult}",
            SymbolicCheckResult.OverflowDivergence od =>
                $"[OverflowDivergence] {od.MethodName}: 32-bit machine arithmetic diverges from mathematical arithmetic\n" +
                $"  Inputs: {FormatMap(od.Inputs)}\n" +
                $"  Real Result: {od.RealResult}\n" +
                $"  Ideal Result: {od.IdealResult}",
            SymbolicCheckResult.PreconditionContradiction pc =>
                $"[PreconditionContradiction] {pc.MethodName}: precondition '{pc.Precondition}' is unsatisfiable (contradiction / vacuous)",
            SymbolicCheckResult.Unsupported u =>
                $"[Unsupported] {u.MethodName}: {u.Reason}",
            _ => f.ToString() ?? "Unknown failure"
        });

        return $"Symbolic verification found {failures.Count} defect(s):\n\n" + string.Join("\n\n", lines);
    }

    private static string FormatMap(IReadOnlyDictionary<string, object> map) =>
        string.Join(", ", map.Select(kv => $"{kv.Key} = {kv.Value}"));

    private static string TargetSourceDirectory()
    {
        var relPath = @"../../../src/CommunityToolkit.HighPerformance"
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .Replace('/', System.IO.Path.DirectorySeparatorChar);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.csproj")) && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.csproj")))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, relPath));
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
            else if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, "test", "CommunityToolkit.HighPerformance.Verification", relPath));
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
        }

        var fromCurrent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relPath));
        if (System.IO.Directory.Exists(fromCurrent)) return fromCurrent;

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, relPath));
    }
}
